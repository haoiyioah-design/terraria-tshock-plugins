using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace PlayerProjectileRandomizer
{
    /// <summary>
    /// 随机化核心：把「玩家打出的弹幕」换成另一种弹幕，并让新弹幕继承手持武器的伤害。
    ///
    /// 驱动方式（沿用上一代插件验证过的组合）：
    ///   1. NetSendData 拦 ProjectileNew 包 —— 在弹幕数据发给客户端之前就把它换好，
    ///      客户端从第一条数据起看到的就是随机后的弹幕，不依赖心跳时序
    ///   2. Timer 心跳 + Main.QueueMainThreadAction 兜底扫描（防止某条弹幕没走发包路径）
    ///
    /// 「每次弹幕都不同」的实现：
    ///   同一名玩家每换一次弹幕，就记住这次用的类型；下一次选择时把上一次的类型排除掉。
    ///   SameAttackWindowMs 内的多条弹幕视为「同一发」（霰弹枪一枪多颗共用同一结果）。
    ///
    /// 「继承手持武器伤害」的实现：
    ///   换种类之前先快照该弹幕当前的 damage —— 玩家弹幕生成时，服务端传进来的
    ///   Damage 已经是「手持武器伤害经过职业加成/套装加成」的结果（例：迷你鲨高速子弹
    ///   的 damage 就是迷你鲨的伤害），换完种类后把它写回去即可。
    /// </summary>
    internal sealed class PlayerProjectileRandomizer
    {
        private sealed class DedupMark
        {
            public int Type;
            public int TimeLeft;
            public object Key;
        }

        private sealed class PlayerShotState
        {
            /// <summary>本次「同一发」选定的弹幕类型（-1 表示还没选过）。</summary>
            public int PickedType = -1;
            /// <summary>本次「同一发」的时间基准（TickCount64）。</summary>
            public long PickedAtMs;
            /// <summary>上一次选定的弹幕类型 —— 下一次必须避开它。</summary>
            public int LastType = -1;
            /// <summary>上次选择时武器使用动画的剩余帧数（同一次攻击的多颗弹幕共用同一个值）。</summary>
            public int LastAnimation = -1;

            /// <summary>结束「同一发」，但保留「上一发用过什么」的记忆（模拟松开扳机再开一枪）。</summary>
            public void EndShot()
            {
                PickedType = -1;
                LastAnimation = -1;
            }
        }

        private readonly Random rng = new Random();
        private readonly Dictionary<int, DedupMark> seenProjectiles = new Dictionary<int, DedupMark>();
        private readonly Dictionary<int, PlayerShotState> shotStateByPlayer = new Dictionary<int, PlayerShotState>();

        /// <summary>待补发「销毁 + 重建」的弹幕（修发射者自己看不到随机弹幕用的，必须延后一拍）。</summary>
        private sealed class PendingResync
        {
            public int Index;
            public int Owner;
            public long DueMs;
        }

        private readonly List<PendingResync> pendingResync = new List<PendingResync>();

        /// <summary>我们主动补发的 ProjectileNew 要跳过随机化，否则会被换第二次。</summary>
        private readonly HashSet<int> suppressResyncOnce = new HashSet<int>();

        private List<int> projectileTypePool;
        private bool typePoolBuilt;
        private string poolDescription = "尚未构建";

        private Timer heartbeat;
        private int pulseQueued;
        private volatile bool disposed;

        private long startedMs;
        private long pulseCount;
        private long randomizedCount;
        private long resyncCount;
        private static bool keyRestoreFailed;
        private long lastPulseMs;

        internal long PulseCount { get { return pulseCount; } }
        internal long RandomizedProjectileCount { get { return randomizedCount; } }

        /// <summary>已经给发射者补发过多少次「销毁 + 重建」（诊断用）。</summary>
        internal long ResyncCount { get { return resyncCount; } }

        /// <summary>还在等延迟的补发条数（诊断用）。</summary>
        internal int PendingResyncCount { get { return pendingResync.Count; } }

        internal int ProjectileTypePoolSize
        {
            get { return projectileTypePool == null ? -1 : projectileTypePool.Count; }
        }

        internal string PoolDescription { get { return poolDescription; } }

        internal TimeSpan Uptime
        {
            get
            {
                long start = startedMs;
                return start == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Environment.TickCount64 - start);
            }
        }

        // ---------------- 心跳 ----------------

        internal void StartHeartbeat(int intervalMs)
        {
            StopHeartbeat();
            if (intervalMs < 20)
            {
                intervalMs = 20;
            }
            disposed = false;
            heartbeat = new Timer(OnHeartbeat, null, intervalMs, intervalMs);
        }

        internal void StopHeartbeat()
        {
            disposed = true;
            Timer timer = heartbeat;
            heartbeat = null;
            if (timer != null)
            {
                try
                {
                    timer.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private void OnHeartbeat(object state)
        {
            if (disposed)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref pulseQueued, 1, 0) != 0)
            {
                return;
            }
            try
            {
                Main.QueueMainThreadAction(OnMainThreadPulse);
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref pulseQueued, 0);
            }
        }

        private void OnMainThreadPulse()
        {
            Interlocked.Exchange(ref pulseQueued, 0);
            if (disposed)
            {
                return;
            }
            try
            {
                TryPulse();
            }
            catch (Exception ex)
            {
                PlayerProjectileRandomizerPlugin.LogError("心跳处理出错：" + ex);
            }
        }

        internal void TryPulse()
        {
            ProjectileSettings settings = PlayerProjectileRandomizerPlugin.Settings;
            if (settings == null)
            {
                return;
            }

            long now = Environment.TickCount64;
            int interval = settings.HeartbeatIntervalMs < 20 ? 20 : settings.HeartbeatIntervalMs;
            if (lastPulseMs != 0 && now - lastPulseMs < interval)
            {
                return;
            }
            lastPulseMs = now;

            pulseCount++;
            if (startedMs == 0)
            {
                startedMs = now;
            }

            // 待补发的「销毁 + 重建」必须按时做完，即使插件被临时关掉
            ProcessPendingResync();

            if (!settings.Enabled)
            {
                return;
            }

            for (int i = 0; i < Main.projectile.Length; i++)
            {
                Projectile projectile = Main.projectile[i];
                if (projectile == null || !projectile.active)
                {
                    continue;
                }
                if (!IsPlayerProjectile(projectile, settings))
                {
                    continue;
                }
                ApplyRandom(projectile, settings, false);
            }
        }

        internal void ClearWorld()
        {
            seenProjectiles.Clear();
            shotStateByPlayer.Clear();
            ResetProjectileTypePool();
        }

        internal void ResetProjectileTypePool()
        {
            projectileTypePool = null;
            typePoolBuilt = false;
            poolDescription = "尚未构建";
        }

        /// <summary>玩家离开：清掉他的状态，避免索引复用后「上一次弹幕」串味。</summary>
        internal void ForgetPlayer(int playerIndex)
        {
            shotStateByPlayer.Remove(playerIndex);
        }

        /// <summary>结束某玩家的「当前这一发」（自检用：等价于松开扳机，下一发要换新的弹幕）。</summary>
        internal void EndShot(int playerIndex)
        {
            PlayerShotState state;
            if (shotStateByPlayer.TryGetValue(playerIndex, out state))
            {
                state.EndShot();
            }
        }

        /// <summary>
        /// 自检：在真实服务器里连续造几条弹幕，走与线上完全相同的随机化路径，
        /// 检查「每次种类都不同」和「伤害继承武器伤害」是否成立。
        /// 用控制台命令 /ppr test 触发；造出来的弹幕会立刻自行消失，不污染世界。
        /// </summary>
        internal List<string> SelfTest(ProjectileSettings settings, int rounds)
        {
            var lines = new List<string>();
            if (rounds <= 0 || rounds > 20)
            {
                rounds = 6;
            }

            List<int> pool = GetProjectileTypePool(settings);
            lines.Add(string.Format("弹幕池 {0} 种（{1}）", pool.Count, poolDescription));
            if (pool.Count < 2)
            {
                lines.Add("池子不足以测试。");
                return lines;
            }

            MethodInfo spawnApi = ResolveNewProjectileApi();
            lines.Add("自检造弹幕方式：直接构造（服务端无客户端连接时 Projectile.NewProjectile 需要 IEntitySource，"
                      + (spawnApi != null ? "该版本有 " + spawnApi.GetParameters().Length + " 参重载" : "未找到重载") + "）");

            const int owner = 0;
            var detail = new List<string>();
            var seq = new List<int>();
            var expectedDamage = new List<int>();
            var actualDamage = new List<int>();
            int spawned = 0;

            for (int round = 0; round < rounds; round++)
            {
                // 模拟「新的一次攻击」：结束本次发，但保留「上一发用了什么」的记忆
                EndShot(owner);

                int seedType = ProjectileID.Bullet;
                if (round % 3 == 1)
                {
                    seedType = ProjectileID.Fireball;
                }
                else if (round % 3 == 2)
                {
                    seedType = ProjectileID.MagicMissile;
                }

                int weaponDamage = 30 + round * 15;   // 模拟「手持武器伤害」逐发变化，便于确认继承

                int index = SpawnProjectile(seedType, weaponDamage, owner);
                if (index < 0)
                {
                    detail.Add(string.Format("第 {0} 发：没有可用的弹幕槽位（场上弹幕太多），跳过", round + 1));
                    continue;
                }

                Projectile projectile = Main.projectile[index];
                int beforeType = projectile.type;
                int beforeDamage = projectile.damage;
                if (beforeType != seedType || beforeDamage != weaponDamage)
                {
                    detail.Add(string.Format("第 {0} 发：造弹幕失败（type={1} damage={2}，期望 {3}/{4}）",
                        round + 1, beforeType, beforeDamage, seedType, weaponDamage));
                    continue;
                }

                spawned++;
                bool changed = ApplyRandom(projectile, settings, true);

                int afterType = projectile.type;
                int afterDamage = projectile.damage;
                seq.Add(afterType);
                expectedDamage.Add(weaponDamage);
                actualDamage.Add(afterDamage);

                detail.Add(string.Format(
                    "第 {0} 发：{1}(武器伤害{2}) -> {3}(实际伤害{4})  换种类={5}",
                    round + 1,
                    ProjectilePool.NameOf(beforeType), beforeDamage,
                    ProjectilePool.NameOf(afterType), afterDamage,
                    changed ? "是" : "否"));

                // 免得测试弹幕留在世界里
                projectile.timeLeft = 2;
            }

            lines.AddRange(detail);

            bool damageOk = actualDamage.Count > 0;
            for (int i = 0; i < actualDamage.Count; i++)
            {
                if (actualDamage[i] != expectedDamage[i])
                {
                    damageOk = false;
                }
            }

            var types = new HashSet<int>(seq);
            lines.Add(string.Format("【结果】成功造出 {0} 发；出现 {1} 种不同弹幕；连续两发相同：{2}",
                spawned, types.Count, repeatedAny(seq) ? "有（不符合预期）" : "无"));
            lines.Add("【结果】伤害继承：" + (damageOk
                ? "每发都等于当时的武器伤害 ✔"
                : "存在不匹配 ✘（见上方逐发对照）"));
            lines.Add("【结果】逐发伤害对照：期望 [" + string.Join(",", expectedDamage.ToArray())
                      + "] 实际 [" + string.Join(",", actualDamage.ToArray()) + "]");
            return lines;
        }

        private static bool repeatedAny(List<int> seq)
        {
            for (int i = 0; i + 1 < seq.Count; i++)
            {
                if (seq[i] == seq[i + 1])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>列出 Projectile.NewProjectile 的所有重载签名（诊断用）。</summary>
        internal static List<string> DescribeNewProjectileApis()
        {
            var lines = new List<string>();
            try
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                foreach (MethodInfo method in typeof(Projectile).GetMethods(flags))
                {
                    if (method.Name == "NewProjectile")
                    {
                        lines.Add(DescribeMethod(method));
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add("异常：" + ex.GetType().Name);
            }
            return lines;
        }

        private static string DescribeMethod(MethodInfo method)
        {
            var parts = new List<string>();
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                parts.Add((parameter.ParameterType.Name) + " " + parameter.Name);
            }
            return method.Name + "(" + string.Join(", ", parts.ToArray()) + ")";
        }

        private static MethodInfo ResolveNewProjectileApi()
        {
            try
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                MethodInfo fallback = null;
                foreach (MethodInfo method in typeof(Projectile).GetMethods(flags))
                {
                    if (method.Name != "NewProjectile")
                    {
                        continue;
                    }
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length < 9)
                    {
                        continue;
                    }
                    if (fallback == null)
                    {
                        fallback = method;
                    }
                    // 优先挑「第一个参数是 IEntitySource、带 float[] ai 或 owner」的那个
                    if (parameters[0].ParameterType.Name.IndexOf("EntitySource", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return method;
                    }
                }
                return fallback;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 自检造弹幕。优先直接构造：在服务端没有真客户端连接时，Projectile.NewProjectile
        /// 会因为缺少 IEntitySource 而静默失败，而「直接填好一条弹幕再让随机化逻辑处理它」
        /// 与玩家开火时服务端的状态完全一致（type/damage/owner/friendly 都已就位）。
        /// </summary>
        private static int SpawnProjectile(int type, int damage, int owner)
        {
            Vector2 position;
            Vector2 velocity = new Vector2(0f, -6f);
            try
            {
                Player player = (owner >= 0 && owner < Main.maxPlayers) ? Main.player[owner] : null;
                position = player != null && player.active
                    ? player.Center
                    : new Vector2(Main.spawnTileX * 16f, Main.spawnTileY * 16f);
            }
            catch (Exception)
            {
                position = new Vector2(Main.spawnTileX * 16f, Main.spawnTileY * 16f);
            }

            int index = SpawnDirect(type, damage, owner, position, velocity);
            if (index >= 0)
            {
                return index;
            }
            return -1;
        }

        /// <summary>直接构造弹幕（不依赖 NewProjectile 的参数名），仅用于自检。</summary>
        private static int SpawnDirect(int type, int damage, int owner, Vector2 position, Vector2 velocity)
        {
            try
            {
                for (int i = 0; i < Main.projectile.Length; i++)
                {
                    Projectile candidate = Main.projectile[i];
                    if (candidate == null || candidate.active)
                    {
                        continue;
                    }
                    candidate.SetDefaults(type);
                    candidate.position = position;
                    candidate.velocity = velocity;
                    candidate.damage = damage;
                    candidate.owner = owner;
                    candidate.friendly = true;
                    candidate.hostile = false;
                    candidate.active = true;
                    candidate.timeLeft = 60;
                    candidate.whoAmI = i;
                    return i;
                }
            }
            catch (Exception ex)
            {
                PlayerProjectileRandomizerPlugin.LogError("自检直接构造弹幕失败：" + ex.Message);
            }
            return -1;
        }

        // ---------------- 弹幕 ----------------

        /// <summary>拦包路径：force=true（来自 ProjectileNew 包，每次必然是刚生成的弹幕，不去重）。</summary>
        internal int RandomizeProjectileByIndex(int index)
        {
            // 我们自己补发的 ProjectileNew 只发一次，不要再随机一遍
            if (suppressResyncOnce.Remove(index))
            {
                return 0;
            }

            ProjectileSettings settings = PlayerProjectileRandomizerPlugin.Settings;
            if (settings == null || !settings.Enabled)
            {
                return 0;
            }
            if (index < 0 || index >= Main.projectile.Length)
            {
                return 0;
            }

            Projectile projectile = Main.projectile[index];
            if (projectile == null || !projectile.active)
            {
                return 0;
            }
            if (!IsPlayerProjectile(projectile, settings))
            {
                return 0;
            }
            return ApplyRandom(projectile, settings, true) ? 1 : 0;
        }

        /// <summary>立刻全量扫描一次场上玩家弹幕（命令用）。</summary>
        internal int RescanProjectiles()
        {
            ProjectileSettings settings = PlayerProjectileRandomizerPlugin.Settings;
            if (settings == null || !settings.Enabled)
            {
                return 0;
            }

            int handled = 0;
            for (int i = 0; i < Main.projectile.Length; i++)
            {
                Projectile projectile = Main.projectile[i];
                if (projectile == null || !projectile.active)
                {
                    continue;
                }
                if (!IsPlayerProjectile(projectile, settings))
                {
                    continue;
                }
                if (ApplyRandom(projectile, settings, false))
                {
                    handled++;
                }
            }
            return handled;
        }

        /// <summary>判断这条弹幕是不是「玩家打出来、并且我们要处理」的弹幕。</summary>
        private static bool IsPlayerProjectile(Projectile projectile, ProjectileSettings settings)
        {
            if (!projectile.active)
            {
                return false;
            }
            // npcProj：由 NPC 打出来的，交给怪物弹幕插件，不归我们管
            if (projectile.npcProj)
            {
                return false;
            }
            if (projectile.owner < 0 || projectile.owner >= Main.maxPlayers)
            {
                return false;
            }
            Player owner = Main.player[projectile.owner];
            if (owner == null || !owner.active)
            {
                return false;
            }
            // 敌对弹幕（射向玩家的，例如 PvP 里被反弹回来的）默认不碰
            if (projectile.hostile && !settings.IncludeHostile)
            {
                return false;
            }
            // 召唤物 / 哨兵 / 宠物的弹幕不动（与 IncludeMinions=false 对齐）
            if (!settings.IncludeMinions)
            {
                if (projectile.minion || projectile.sentry || ProjectilePool.IsMinionLike(projectile.type))
                {
                    return false;
                }
            }
            // 注意：不再用 damage<=0 排除弹幕 —— 1.4.5.8 的弹幕自身伤害就是 0，
            // 伤害全靠武器传入。damage 为 0 的弹幕交给 GetHeldWeaponDamage 兜底。
            return true;
        }

        private bool ApplyRandom(Projectile projectile, ProjectileSettings settings, bool force)
        {
            if (projectile.type >= ProjectileID.Count)
            {
                return false;
            }

            int oldType = projectile.type;
            object key = GetProjectileKey(projectile);
            int originalDamage = projectile.damage;

            // ---- 去重（只有心跳/重扫路径需要）----
            // ⚠️ 原来把 timeLeft 也算进比较条件，而它每帧都在递减 => 去重等于失效，
            // 心跳每 100ms 就把场上同一颗弹幕重新随机一遍（实测 112 秒刷出 8373 次随机化）。
            // 弹幕的身份只看 key 就够。
            if (!force)
            {
                DedupMark mark;
                if (seenProjectiles.TryGetValue(projectile.whoAmI, out mark)
                    && mark.Key != null && key != null
                    && Equals(mark.Key, key))
                {
                    return false;
                }
            }
            seenProjectiles[projectile.whoAmI] = new DedupMark
            {
                Type = oldType,
                TimeLeft = projectile.timeLeft,
                Key = key,
            };

            if (keyRestoreFailed)
            {
                // key 已证明写不回去：继续换类型只会让玩家弹幕全部同步失败（看不见自己的弹幕），
                // 所以直接不干了，等修好再说。
                return false;
            }

            if (!settings.RandomizeProjectileType)
            {
                // 只做伤害校正：把伤害钉在「手持武器伤害」上（种类保持不变）
                if (!settings.KeepWeaponDamage)
                {
                    return false;
                }
                int heldDamage = GetHeldWeaponDamage(projectile.owner);
                if (heldDamage > 0 && heldDamage != projectile.damage)
                {
                    projectile.damage = settings.RandomizeProjectileDamage ? RollDamage(heldDamage, settings) : heldDamage;
                    projectile.netUpdate = true;
                    randomizedCount++;
                    return true;
                }
                return false;
            }

            List<int> pool = GetProjectileTypePool(settings);
            if (pool.Count == 0)
            {
                return false;
            }

            int newType = PickTypeForPlayer(pool, settings, projectile.owner, oldType);
            if (newType == oldType)
            {
                // 随机到了原类型：不算「换过」，但伤害规则仍然要保证
                return false;
            }

            try
            {
                ChangeProjectileType(projectile, newType);
            }
            catch (Exception ex)
            {
                PlayerProjectileRandomizerPlugin.LogError(string.Format(
                    "切换弹幕类型失败（{0} -> {1}）：{2}", oldType, newType, ex.Message));
                return false;
            }

            // ---- 伤害继承：写回快照到的武器伤害 ----
            // 1.4.5.8 的弹幕没有自带伤害，projectile.damage 就是「手持武器算出来的伤害」，
            // 换种类之后再写回去，就实现了「高速子弹变成魔法导弹、伤害还是迷你鲨的」。
            int newDamage = originalDamage;
            if (newDamage <= 0)
            {
                newDamage = GetHeldWeaponDamage(projectile.owner);
            }
            if (!settings.KeepWeaponDamage)
            {
                // 用户主动关掉继承：用新弹幕类型的默认伤害（该版本多为 0，仅作兜底）
                newDamage = GetTypeDefaultDamage(newType);
            }
            if (settings.RandomizeProjectileDamage && newDamage > 0)
            {
                newDamage = RollDamage(newDamage, settings);
            }
            projectile.damage = newDamage > 0 ? newDamage : originalDamage;

            randomizedCount++;

            // 换过 key 之后必须刷新去重标记，否则心跳会把同一颗弹幕再换一遍
            seenProjectiles[projectile.whoAmI] = new DedupMark
            {
                Type = projectile.type,
                TimeLeft = projectile.timeLeft,
                Key = GetProjectileKey(projectile),
            };

            // ---- 让「发射者本人」也能看到随机后的弹幕 ----
            // 服务端广播「客户端发来的弹幕创建包」时本来就 ignoreClient = 发射者本人，
            // 所以发射者**从来没收到过**类型被改过的那个包 —— 这正是「别人看得见、自己看不见」。
            // 现在换了新 key 之后再单独给他补发一份 ProjectileNew（新 key + 新类型），
            // 他的客户端匹配不上本地那颗预测弹幕，就会照服务端这份数据重建。
            if (settings.ResyncOwnerAfterRandomize)
            {
                QueueOwnerResync(projectile, settings);
            }

            if (settings.LogRandomizedProjectiles)
            {
                PlayerProjectileRandomizerPlugin.Log(string.Format(
                    "玩家 #{0} 弹幕 #{1}：{2} -> {3}，伤害 {4} -> {5}",
                    projectile.owner, projectile.whoAmI,
                    ProjectilePool.NameOf(oldType), ProjectilePool.NameOf(newType),
                    originalDamage, projectile.damage));
            }
            return true;
        }

        /// <summary>
        /// 把「发射者视角修复」排进队列，等 OwnerResyncDelayMs 之后再发。
        /// 不能当场发：客户端这时还没把自己本地预测的弹幕按 key 对齐到服务端下标，销毁包会打空。
        /// </summary>
        private void QueueOwnerResync(Projectile projectile, ProjectileSettings settings)
        {
            try
            {
                int owner = projectile.owner;
                if (owner < 0 || owner >= Main.maxPlayers)
                {
                    return;
                }
                int delay = settings.OwnerResyncDelayMs;
                if (delay < 20)
                {
                    delay = 20;
                }
                long due = Environment.TickCount64 + delay;

                for (int i = 0; i < pendingResync.Count; i++)
                {
                    PendingResync existing = pendingResync[i];
                    if (existing.Index == projectile.whoAmI && existing.Owner == owner)
                    {
                        existing.DueMs = due;   // 同一颗弹幕只保留最后一次
                        return;
                    }
                }

                pendingResync.Add(new PendingResync
                {
                    Index = projectile.whoAmI,
                    Owner = owner,
                    DueMs = due,
                });
            }
            catch (Exception ex)
            {
                PlayerProjectileRandomizerPlugin.LogError("排队补发失败：" + ex.Message);
            }
        }

        /// <summary>心跳里按时处理待补发队列（必须在游戏主线程上跑）。</summary>
        private void ProcessPendingResync()
        {
            if (pendingResync.Count == 0)
            {
                return;
            }
            long now = Environment.TickCount64;
            for (int i = pendingResync.Count - 1; i >= 0; i--)
            {
                PendingResync item = pendingResync[i];
                if (now < item.DueMs)
                {
                    continue;
                }
                pendingResync.RemoveAt(i);
                if (now - item.DueMs > 3000)
                {
                    continue;   // 拖太久（弹幕早没了）就丢弃
                }
                SendOwnerResync(item.Index, item.Owner);
            }
        }

        /// <summary>
        /// 只发给发射者一个人的「销毁 + 重建」：
        ///   1) MessageID 29 = ProjectileDestroy（number=弹幕下标，number2=owner）删掉他本地那颗预测弹幕；
        ///   2) MessageID 27 = ProjectileNew，此时服务端这颗已经是随机类型，客户端会照它重建。
        /// 补发的 New 用 suppressResyncOnce 标记跳过随机化，避免被换第二次。
        /// </summary>
        private void SendOwnerResync(int index, int owner)
        {
            try
            {
                if (index < 0 || index >= Main.projectile.Length)
                {
                    return;
                }
                Projectile projectile = Main.projectile[index];
                if (projectile == null || !projectile.active)
                {
                    return;
                }
                TSPlayer player = TShock.Players[owner];
                if (player == null || !player.Active)
                {
                    return;
                }

                if (PlayerProjectileRandomizerPlugin.Settings.DestroyBeforeResync)
                {
                    NetMessage.SendData(29, owner, -1, null, index, owner, 0f, 0f, 0, 0, 0);
                }

                suppressResyncOnce.Add(index);
                int realOwner = projectile.owner;
                object originalKey = GetProjectileKey(projectile);
                bool keyRotated = false;
                try
                {
                    // 把 owner 伪装成 255（无主）：客户端就不会把这份数据认成「自己射的那颗」。
                    // ⚠️ 实测会被 OTAPI 的 Invariant 直接拦掉，所以默认关闭。
                    if (PlayerProjectileRandomizerPlugin.Settings.SpoofOwnerOnResync)
                    {
                        projectile.owner = 255;
                    }

                    // ★ 关键：只在「发这一份补发包」的瞬间换 key。
                    // 客户端因此认不出这是自己那颗预测弹幕，会照服务端数据（随机后的类型）重建；
                    // 包一发出就把 key 恢复原值，服务端这条弹幕始终保持合法的生成者信息，
                    // 后续同步不会再触发 Invariant。
                    if (PlayerProjectileRandomizerPlugin.Settings.RotateProjectileKeyOnRandomize)
                    {
                        keyRotated = RotateProjectileKey(projectile);
                    }

                    NetMessage.SendData(27, owner, -1, null, index, 0f, 0f, 0f, 0, 0, 0);
                }
                finally
                {
                    projectile.owner = realOwner;
                    if (keyRotated)
                    {
                        SetProjectileKey(projectile, originalKey);
                    }
                }
                resyncCount++;

                if (PlayerProjectileRandomizerPlugin.Settings.LogRandomizedProjectiles)
                {
                    PlayerProjectileRandomizerPlugin.Log(string.Format(
                        "已给玩家 #{0} 补发弹幕 #{1} 的销毁+重建（{2}）",
                        owner, index, ProjectilePool.NameOf(projectile.type)));
                }
            }
            catch (Exception ex)
            {
                PlayerProjectileRandomizerPlugin.LogError("补发销毁+重建失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 换类型。换之前把「与几何/手感有关」的属性全部快照下来，换完再恢复：
        /// 位置（按中心对齐，避免体积变化把弹幕卡进方块）、速度、击退、归属。
        /// 1.4.5.8 删掉了 Projectile.identity，改用 key 标识弹幕，必须原样带过去。
        /// </summary>
        private static void ChangeProjectileType(Projectile projectile, int newType)
        {
            Vector2 center = projectile.Center;
            Vector2 velocity = projectile.velocity;
            float knockBack = projectile.knockBack;
            int owner = projectile.owner;
            bool wasHostile = projectile.hostile;
            bool wasNpcProj = projectile.npcProj;
            bool wasFriendly = projectile.friendly;
            int originalWidth = projectile.width;
            int originalHeight = projectile.height;
            object key = GetProjectileKey(projectile);

            float speedMagnitude = velocity.Length();
            Vector2 direction = speedMagnitude > 0.0001f ? velocity / speedMagnitude : new Vector2(0f, 0f);

            projectile.SetDefaults(newType);

            // 新类型的默认速度大小（用于 KeepOriginalSpeed = false 的情况）
            float defaultSpeed = projectile.velocity.Length();

            projectile.width = projectile.width > 0 ? projectile.width : originalWidth;
            projectile.height = projectile.height > 0 ? projectile.height : originalHeight;
            projectile.Center = center;

            ProjectileSettings settings = PlayerProjectileRandomizerPlugin.Settings;
            if (settings != null && !settings.KeepOriginalSpeed && defaultSpeed > 0.0001f)
            {
                projectile.velocity = direction * defaultSpeed;
            }
            else
            {
                projectile.velocity = velocity;
            }

            projectile.knockBack = (settings == null || settings.KeepOriginalKnockback) ? knockBack : projectile.knockBack;
            projectile.owner = owner;
            projectile.friendly = wasFriendly || !wasHostile;
            projectile.hostile = wasHostile && (settings != null && settings.IncludeHostile);

            if (projectile.ai != null)
            {
                for (int i = 0; i < projectile.ai.Length; i++)
                {
                    projectile.ai[i] = 0f;
                }
            }
            if (projectile.localAI != null)
            {
                for (int i = 0; i < projectile.localAI.Length; i++)
                {
                    projectile.localAI[i] = 0f;
                }
            }

            bool keyRestored = SetProjectileKey(projectile, key);
            if (!keyRestored)
            {
                // key 写不回去 => 这条弹幕的 spawner 信息已丢，OTAPI 会拒绝同步它，
                // 与其让玩家「看不见自己的弹幕」，不如把类型改回原样。
                FlagKeyRestoreFailure();
            }

            // ⚠️ 这里**绝对不能**换 key。
            // 换 key 只允许在「给发射者补发那一份包」的瞬间做（见 SendOwnerResync），做完立刻恢复原值。
            // 否则服务端这条弹幕的 key 会永久失去生成者信息，
            // OTAPI 的 Invariant(SyncProjectile owner must match spawner) 会拒绝它后续同步，
            // 玩家看到的就是「自己射的弹幕凭空消失」。

            projectile.netUpdate = true;
        }

        /// <summary>
        /// 为某名玩家挑一个弹幕类型。
        /// 核心要求：这次选出来的必须和「上一次用的」不同。
        ///
        /// 「同一发」的判定（决定霰弹枪一枪多颗是共用一种还是各随机）：
        ///   主判据 = 武器使用动画剩余帧数（player.itemAnimation）。
        ///   同一次攻击里射出的多颗弹幕发生在同一帧或相邻帧，itemAnimation 相同 —— 共用一种。
        ///   下一次开火时动画重新开始，数值不同 —— 换一种。
        ///   辅助判据 = SameAttackWindowMs 时间窗口（动画信息不可用时兜底）。
        /// </summary>
        private int PickTypeForPlayer(List<int> pool, ProjectileSettings settings, int playerIndex, int oldType)
        {
            long now = Environment.TickCount64;
            int animation = GetItemAnimation(playerIndex);

            PlayerShotState state;
            if (!shotStateByPlayer.TryGetValue(playerIndex, out state))
            {
                state = new PlayerShotState();
                shotStateByPlayer[playerIndex] = state;
            }

            bool sameShot = false;
            if (state.PickedType >= 0 && state.PickedType != oldType)
            {
                if (animation > 0 && state.LastAnimation == animation)
                {
                    // 同一次攻击动画内的后续弹幕
                    sameShot = true;
                }
                else if (settings.SameAttackWindowMs > 0
                         && (now - state.PickedAtMs) <= settings.SameAttackWindowMs)
                {
                    // 兜底：动画信息拿不到时，用很短的窗口把同帧多颗弹幕归为同一发
                    sameShot = true;
                }
            }

            if (sameShot)
            {
                return state.PickedType;
            }

            int picked = PickDifferent(pool, settings, state.LastType, oldType);

            state.LastType = picked;
            state.PickedType = picked;
            state.PickedAtMs = now;
            state.LastAnimation = animation;
            return picked;
        }

        /// <summary>读取玩家当前武器使用动画剩余帧数（拿不到返回 -1）。</summary>
        private static int GetItemAnimation(int playerIndex)
        {
            try
            {
                if (playerIndex < 0 || playerIndex >= Main.maxPlayers)
                {
                    return -1;
                }
                Player player = Main.player[playerIndex];
                if (player == null || !player.active)
                {
                    return -1;
                }
                return player.itemAnimation;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// 从池子里挑一个不等于 excluded1 / excluded2 的类型。
        /// 重试若干次仍失败（池子太小）就退而求其次，只保证「不等于上一次用的」。
        /// </summary>
        private int PickDifferent(List<int> pool, ProjectileSettings settings, int lastType, int oldType)
        {
            if (pool.Count == 0)
            {
                return oldType;
            }
            if (pool.Count == 1)
            {
                return pool[0];
            }

            bool avoidLast = settings.AvoidRepeatingType && lastType >= 0;
            bool avoidOld = true;

            const int MaxAttempts = 24;
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                int candidate = pool[rng.Next(pool.Count)];
                if (avoidOld && candidate == oldType)
                {
                    continue;
                }
                if (avoidLast && candidate == lastType)
                {
                    continue;
                }
                return candidate;
            }

            // 兜底：放弃「避开原类型」，只保证「和上一次不同」
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                int candidate = pool[rng.Next(pool.Count)];
                if (avoidLast && candidate == lastType)
                {
                    continue;
                }
                return candidate;
            }

            return pool[rng.Next(pool.Count)];
        }

        private List<int> GetProjectileTypePool(ProjectileSettings settings)
        {
            if (typePoolBuilt && projectileTypePool != null)
            {
                return projectileTypePool;
            }

            string describe;
            projectileTypePool = ProjectilePool.Build(settings, out describe);
            poolDescription = describe;
            typePoolBuilt = true;

            PlayerProjectileRandomizerPlugin.Log(string.Format(
                "弹幕池已构建：{0}（保命名单排除 {1} 种）", describe, ProjectilePool.ExcludedCount));
            if (settings.LogPoolOnStartup)
            {
                PlayerProjectileRandomizerPlugin.Log("池子抽样：" + ProjectilePool.Sample(projectileTypePool, 12));
            }
            return projectileTypePool;
        }

        /// <summary>某个弹幕类型的默认伤害（1.4.5.8 里普遍是 0，仅作兜底）。</summary>
        private static int GetTypeDefaultDamage(int type)
        {
            try
            {
                var probe = new Projectile();
                probe.SetDefaults(type);
                return probe.damage > 0 ? probe.damage : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>手持武器的伤害（仅在原弹幕伤害异常为 0 时兜底使用）。</summary>
        private static int GetHeldWeaponDamage(int playerIndex)
        {
            try
            {
                if (playerIndex < 0 || playerIndex >= Main.maxPlayers)
                {
                    return 0;
                }
                Player player = Main.player[playerIndex];
                if (player == null || !player.active)
                {
                    return 0;
                }
                Item held = player.HeldItem;
                if (held == null || held.IsAir)
                {
                    int selected = player.selectedItem;
                    if (selected < 0 || selected >= player.inventory.Length)
                    {
                        return 0;
                    }
                    held = player.inventory[selected];
                }
                if (held == null || held.IsAir || held.damage <= 0)
                {
                    return 0;
                }
                return held.damage;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private int RollDamage(int baseDamage, ProjectileSettings settings)
        {
            if (baseDamage <= 0)
            {
                return baseDamage;
            }
            double min = baseDamage * settings.DamageMinMultiplier;
            double max = baseDamage * settings.DamageMaxMultiplier;
            double value = min + rng.NextDouble() * (max - min);
            int result = (int)Math.Round(value);
            return result < 1 ? 1 : result;
        }

        // ---------------- 1.4.5.8 兼容：Projectile.key 反射访问 ----------------

        private static System.Reflection.MemberInfo projectileKeyMember;
        private static bool projectileKeyMemberResolved;

        private static System.Reflection.MemberInfo ResolveProjectileKeyMember()
        {
            if (projectileKeyMemberResolved)
            {
                return projectileKeyMember;
            }
            projectileKeyMemberResolved = true;
            try
            {
                Type type = typeof(Projectile);
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
                projectileKeyMember = (System.Reflection.MemberInfo)type.GetProperty("key", flags)
                                      ?? type.GetField("key", flags);
            }
            catch (Exception)
            {
                projectileKeyMember = null;
            }
            return projectileKeyMember;
        }

        private static object GetProjectileKey(Projectile projectile)
        {
            try
            {
                System.Reflection.MemberInfo member = ResolveProjectileKeyMember();
                var property = member as System.Reflection.PropertyInfo;
                if (property != null)
                {
                    return property.GetValue(projectile, null);
                }
                var field = member as System.Reflection.FieldInfo;
                if (field != null)
                {
                    return field.GetValue(projectile);
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>
        /// 把 key 写回弹幕。
        ///
        /// ⚠️ 这一步必须成功：`Projectile.SetDefaults()` 会把 key 重置成 default，
        /// 而 default 的 ProjectileKey 里没有生成者信息（spawner=0）。
        /// 玩家弹幕的 owner 是玩家索引（例如 2），于是 OTAPI 的完整性校验
        /// `Invariant Failed: SyncProjectile owner (2) must match spawner (0)`
        /// 会拒绝同步这条弹幕 —— 表现就是「自己射出去的弹幕直接不见」。
        /// 所以除了属性 setter 和同名字段，还要能写到编译器生成的 backing field。
        /// </summary>
        private static bool SetProjectileKey(Projectile projectile, object key)
        {
            if (key == null)
            {
                return false;
            }
            try
            {
                System.Reflection.MemberInfo member = ResolveProjectileKeyMember();
                var property = member as System.Reflection.PropertyInfo;
                if (property != null)
                {
                    if (property.CanWrite)
                    {
                        property.SetValue(projectile, key, null);
                        return true;
                    }
                    System.Reflection.FieldInfo backing = typeof(Projectile).GetField(
                        "<" + property.Name + ">k__BackingField",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (backing != null)
                    {
                        backing.SetValue(projectile, key);
                        return true;
                    }
                }

                var field = member as System.Reflection.FieldInfo;
                if (field != null)
                {
                    field.SetValue(projectile, key);
                    return true;
                }

                System.Reflection.FieldInfo direct = typeof(Projectile).GetField(
                    "key", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (direct != null)
                {
                    direct.SetValue(projectile, key);
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>
        /// key 写不回去时调一次：立刻停止后续所有「换类型」动作。
        /// 因为换类型必然先 SetDefaults（把 key 重置成没有 spawner 的 default），
        /// 写不回去就会让这条弹幕被 OTAPI 拒绝同步 —— 对玩家来说就是「弹幕凭空消失」。
        /// </summary>
        private static void FlagKeyRestoreFailure()
        {
            if (keyRestoreFailed)
            {
                return;
            }
            keyRestoreFailed = true;
            PlayerProjectileRandomizerPlugin.LogError(
                "Projectile.key 写不回去（SetDefaults 之后无法恢复生成者信息）—— "
                + "已自动停止更换弹幕类型，避免玩家弹幕同步失败；请检查 SetProjectileKey/backing field。");
        }

        /// <summary>是否已经因为 key 写不回去而停止换类型（诊断用）。</summary>
        internal static bool KeyRestoreFailed { get { return keyRestoreFailed; } }

        /// <summary>key 成员的可写性诊断（/ppr status 里显示）。</summary>
        internal static string DescribeKeyWritable()
        {
            try
            {
                System.Reflection.MemberInfo member = ResolveProjectileKeyMember();
                var property = member as System.Reflection.PropertyInfo;
                if (property != null)
                {
                    if (property.CanWrite)
                    {
                        return "属性可写";
                    }
                    System.Reflection.FieldInfo backing = typeof(Projectile).GetField(
                        "<" + property.Name + ">k__BackingField",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    return backing != null ? "只读属性但有 backing field（可写）" : "只读属性且无 backing field（写不进去！）";
                }
                var field = member as System.Reflection.FieldInfo;
                if (field != null && !field.IsInitOnly)
                {
                    return "字段可写";
                }
                if (field != null)
                {
                    return "字段是 readonly";
                }
                System.Reflection.FieldInfo direct = typeof(Projectile).GetField(
                    "key", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return direct != null ? "命中私有字段（可写）" : "找不到可写成员";
            }
            catch (Exception ex)
            {
                return "读取失败：" + ex.GetType().Name;
            }
        }

        private static readonly Random keyRng = new Random();

        /// <summary>Projectile.key 的实际类型（诊断用，顺便确认能不能换）。</summary>
        internal static string DescribeKeyMember()
        {
            try
            {
                System.Reflection.MemberInfo member = ResolveProjectileKeyMember();
                if (member == null)
                {
                    return "未找到 Projectile.key";
                }
                Type t = member is System.Reflection.PropertyInfo
                    ? ((System.Reflection.PropertyInfo)member).PropertyType
                    : ((System.Reflection.FieldInfo)member).FieldType;
                return t == null ? "?" : t.FullName;
            }
            catch (Exception ex)
            {
                return "读取失败：" + ex.GetType().Name;
            }
        }

        /// <summary>
        /// 造一个全新的 key。
        ///
        /// `Projectile.key` 是 OTAPI 里的自定义结构体（运行时实测是
        /// Terraria.DataStructures.ProjectileKey），字段没公开、本机也反射不到它的定义，
        /// 所以不去猜结构：先拿一颗「临时弹幕」上真实分配到的 key，拿不到就退回 default(类型)。
        /// 只要和原来那个不一样，客户端就认不出这是「自己射出的那颗弹幕」，
        /// 于是照服务端这份数据（随机后的类型）重建 —— 这正是发射者能看到随机弹幕的关键。
        /// </summary>
        private static object BuildFreshKey(Type keyType)
        {
            try
            {
                var probe = new Projectile();
                probe.SetDefaults(ProjectileID.WoodenArrowFriendly);
                object fromProbe = GetProjectileKey(probe);
                if (fromProbe != null && fromProbe.GetType() == keyType)
                {
                    return fromProbe;
                }
            }
            catch (Exception)
            {
            }

            try
            {
                return Activator.CreateInstance(keyType);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 给弹幕换一个新 key。客户端靠 key 认「这是我自己射的那颗」并保留本地类型，
        /// 换掉 key 之后它就会照服务端的数据（随机后的类型）重建这颗弹幕。
        /// </summary>
        private static bool RotateProjectileKey(Projectile projectile)
        {
            try
            {
                System.Reflection.MemberInfo member = ResolveProjectileKeyMember();
                if (member == null)
                {
                    return false;
                }
                Type keyType = member is System.Reflection.PropertyInfo
                    ? ((System.Reflection.PropertyInfo)member).PropertyType
                    : ((System.Reflection.FieldInfo)member).FieldType;
                if (keyType == null)
                {
                    return false;
                }

                object newKey = BuildFreshKey(keyType);
                if (newKey == null)
                {
                    return false;
                }
                return SetProjectileKey(projectile, newKey);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
