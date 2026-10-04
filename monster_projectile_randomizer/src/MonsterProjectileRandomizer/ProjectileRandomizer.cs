using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;

namespace MonsterProjectileRandomizer
{
    /// <summary>
    /// 随机化核心：只负责「怪物弹幕」这一件事（种类 + 伤害）。
    ///
    /// 驱动方式沿用原插件的三路并行（这是踩坑后验证过的组合）：
    ///   1. NetSendData 拦 ProjectileNew 包 —— 弹幕刚创建就把伤害改掉，
    ///      客户端从第一条数据起拿到的就是随机值，不依赖心跳时序
    ///   2. Timer 心跳 + Main.QueueMainThreadAction 兜底扫描场上弹幕
    ///   3. 怪物生成时立刻 pulse 一次（出生往往紧跟着弹幕）
    /// </summary>
    internal sealed class ProjectileRandomizer
    {
        private struct ProjectileMark
        {
            public int Type;
            public int TimeLeft;
        }

        private readonly Random rng = new Random();
        private readonly Dictionary<int, ProjectileMark> seenProjectiles = new Dictionary<int, ProjectileMark>();
        private readonly Dictionary<int, int> lastTypeBySlot = new Dictionary<int, int>();

        private List<int> projectileTypePool;
        private bool typePoolBuilt;

        private Timer heartbeat;
        private int pulseQueued;
        private volatile bool disposed;

        private long startedMs;
        private long pulseCount;
        private long randomizedProjectileCount;
        private long lastPulseMs;

        internal long PulseCount { get { return pulseCount; } }
        internal long RandomizedProjectileCount { get { return randomizedProjectileCount; } }

        internal int ProjectileTypePoolSize
        {
            get { return projectileTypePool == null ? -1 : projectileTypePool.Count; }
        }

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
            if (intervalMs < 10)
            {
                intervalMs = 10;
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
                MonsterProjectileRandomizerPlugin.LogError("心跳处理出错：" + ex);
            }
        }

        internal void TryPulse()
        {
            ProjectileSettings settings = MonsterProjectileRandomizerPlugin.Settings;
            if (settings == null)
            {
                return;
            }

            long now = Environment.TickCount64;
            int interval = settings.HeartbeatIntervalMs < 10 ? 10 : settings.HeartbeatIntervalMs;
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
            if (!settings.Enabled)
            {
                return;
            }

            RandomizeAllProjectiles(settings);
        }

        internal void ClearWorld()
        {
            seenProjectiles.Clear();
            ResetProjectileTypePool();
        }

        internal void ResetProjectileTypePool()
        {
            projectileTypePool = null;
            typePoolBuilt = false;
        }

        // ---------------- 弹幕 ----------------

        internal int RandomizeProjectileByIndex(int index)
        {
            ProjectileSettings settings = MonsterProjectileRandomizerPlugin.Settings;
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
            if (IsPlayerProjectile(projectile))
            {
                // 玩家打出来的弹幕归 PlayerProjectileRandomizer 管，这里不插手。
                // （1.4.5.8 里玩家弹幕默认也是 hostile=true，不排除的话会被两个插件各随机一次）
                return 0;
            }
            if (!projectile.hostile || projectile.damage <= 0)
            {
                return 0;
            }

            // 拦包路径：force=true，每次攻击都重新随机
            return ApplyProjectileRandom(projectile, settings, true) ? 1 : 0;
        }

        /// <summary>是不是「玩家打出来的」弹幕（按 owner 判断，不看 hostile 标记）。</summary>
        private static bool IsPlayerProjectile(Projectile projectile)
        {
            try
            {
                if (projectile.npcProj)
                {
                    return false;
                }
                if (projectile.owner < 0 || projectile.owner >= Main.maxPlayers)
                {
                    return false;
                }
                Player owner = Main.player[projectile.owner];
                return owner != null && owner.active;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>立即执行一次全量弹幕扫描，返回本次处理的弹幕条数。</summary>
        internal int RescanProjectiles()
        {
            ProjectileSettings settings = MonsterProjectileRandomizerPlugin.Settings;
            if (settings == null)
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
                if (IsPlayerProjectile(projectile))
                {
                    continue;
                }
                if (!projectile.hostile || projectile.damage <= 0)
                {
                    continue;
                }
                if (ApplyProjectileRandom(projectile, settings, false))
                {
                    handled++;
                }
            }
            return handled;
        }

        private void RandomizeAllProjectiles(ProjectileSettings settings)
        {
            for (int i = 0; i < Main.projectile.Length; i++)
            {
                Projectile projectile = Main.projectile[i];
                if (projectile == null || !projectile.active)
                {
                    continue;
                }
                if (IsPlayerProjectile(projectile))
                {
                    continue;
                }
                if (!projectile.hostile || projectile.damage <= 0)
                {
                    continue;
                }
                ApplyProjectileRandom(projectile, settings, false);
            }
        }

        /// <summary>
        /// 弹幕随机：种类 + 伤害。
        /// 去重在 1.4.5.8 上不能依赖 Projectile.identity（已被删除），改用 (whoAmI, type, timeLeft)。
        /// </summary>
        private bool ApplyProjectileRandom(Projectile projectile, ProjectileSettings settings, bool force)
        {
            // force=true：来自 ProjectileNew 拦包路径 —— 每次发包必然是一条新弹幕，
            //             因此不去重，保证「每次攻击都重新随机」。
            // force=false：来自心跳/重扫路径 —— 需要去重，避免同一条弹幕被反复处理。
            if (!force)
            {
                ProjectileMark mark;
                if (seenProjectiles.TryGetValue(projectile.whoAmI, out mark)
                    && mark.Type == projectile.type
                    && projectile.timeLeft <= mark.TimeLeft)
                {
                    return false;
                }
            }

            // 换种类会调用 SetDefaults 把伤害重置成新弹幕的默认值，
            // 所以先把「原版伤害」记下来，结算时再写回去。
            int originalDamage = projectile.damage;
            int baseDamage = projectile.damage;
            int oldType = projectile.type;

            seenProjectiles[projectile.whoAmI] = new ProjectileMark
            {
                Type = projectile.type,
                TimeLeft = projectile.timeLeft,
            };

            int newType = oldType;
            if (settings.RandomizeProjectileType)
            {
                List<int> pool = GetProjectileTypePool(settings);
                if (pool.Count > 0)
                {
                    newType = PickProjectileType(pool, settings, projectile.whoAmI, oldType);
                    ChangeProjectileType(projectile, newType);
                    lastTypeBySlot[projectile.whoAmI] = newType;
                }
            }

            // 伤害结算：默认写回「原版那条弹幕的伤害」（陷阱打 100，射出来的东西就是 100）。
            // 例外：OwnDamageProjectiles 名单里的弹幕，用它们自身的伤害。
            //
            // ⚠️ 巨石类（aiStyle==25）不能走「自身伤害」：1.4.5.8 里滚动巨石的 SetDefaults
            // 根本不设 damage（模板值是 0），换型后取到的就是 0，巨石会变成 0 伤害。
            // 所以巨石单独处理：
            //   · BoulderDamage > 0 → 用这个固定基础值（原版巨石是 140/280/420 = 经典/专家/大师）
            //   · BoulderDamage = 0 → 与其它弹幕一致，继承「原版那条弹幕的伤害」（不会放大低伤害弹幕）
            bool isBoulder = IsBoulderProjectile(projectile);
            bool useOwnDamage = settings.OwnDamageProjectiles != null && settings.OwnDamageProjectiles.Contains(newType);
            int newDamage;
            if (isBoulder && settings.BoulderDamage > 0)
            {
                newDamage = settings.BoulderDamage;
            }
            else if (useOwnDamage)
            {
                newDamage = projectile.damage;
            }
            else if (settings.KeepOriginalDamage && originalDamage > 0)
            {
                newDamage = originalDamage;
            }
            else
            {
                newDamage = projectile.damage;
            }

            if (settings.RandomizeProjectileDamage && newDamage > 0)
            {
                newDamage = RollDamage(newDamage, settings);
            }
            projectile.damage = newDamage;

            projectile.netUpdate = true;
            randomizedProjectileCount++;

            if (settings.LogRandomizedProjectiles)
            {
                MonsterProjectileRandomizerPlugin.Log(string.Format(
                    "弹幕 #{0}：种类 {1}->{2}，伤害 {3}->{4}",
                    projectile.whoAmI, oldType, newType, baseDamage, newDamage));
            }
            return true;
        }

        private void ChangeProjectileType(Projectile projectile, int newType)
        {
            Vector2 position = projectile.position;
            Vector2 velocity = projectile.velocity;
            float knockBack = projectile.knockBack;
            int owner = projectile.owner;
            bool wasNpcProjectile = projectile.npcProj;
            object key = GetProjectileKey(projectile);

            projectile.SetDefaults(newType);

            projectile.position = position;
            projectile.velocity = velocity;
            projectile.knockBack = knockBack;
            projectile.owner = owner;
            projectile.npcProj = wasNpcProjectile;
            SetProjectileKey(projectile, key);

            for (int i = 0; i < projectile.ai.Length; i++)
            {
                projectile.ai[i] = 0f;
            }
            if (projectile.localAI != null)
            {
                for (int i = 0; i < projectile.localAI.Length; i++)
                {
                    projectile.localAI[i] = 0f;
                }
            }
        }

        private List<int> GetProjectileTypePool(ProjectileSettings settings)
        {
            if (typePoolBuilt && projectileTypePool != null)
            {
                return projectileTypePool;
            }

            var pool = new List<int>();
            if (settings.ProjectileTypePool != null && settings.ProjectileTypePool.Count > 0)
            {
                foreach (int type in settings.ProjectileTypePool)
                {
                    if (IsSuitableProjectileType(type, settings))
                    {
                        pool.Add(type);
                    }
                }
            }
            else
            {
                for (int type = 1; type < ProjectileID.Count; type++)
                {
                    if (IsSuitableProjectileType(type, settings))
                    {
                        pool.Add(type);
                    }
                }
            }

            projectileTypePool = pool;
            typePoolBuilt = true;
            MonsterProjectileRandomizerPlugin.Log(string.Format("弹幕种类池已构建：{0} 种可用弹幕", pool.Count));
            return projectileTypePool;
        }

        private static bool IsSuitableProjectileType(int type, ProjectileSettings settings)
        {
            try
            {
                if (settings != null)
                {
                    if (settings.BlacklistProjectiles != null && settings.BlacklistProjectiles.Contains(type))
                    {
                        return false;
                    }
                    string keywordHit;
                    if (MatchesNameKeyword(type, settings.BlacklistNameKeywords, out keywordHit))
                    {
                        return false;
                    }
                }

                var probe = new Projectile();
                probe.SetDefaults(type);
                if (probe.type != type)
                {
                    return false;
                }
                if (!probe.hostile)
                {
                    return false;
                }
                if (probe.aiStyle == 0)
                {
                    return false;
                }
                // 巨石类（滚动巨石 aiStyle==25）：配置关掉时整类排除
                if (settings != null && !settings.AllowBoulderProjectiles && probe.aiStyle == 25)
                {
                    return false;
                }
                if (probe.width > 40 || probe.height > 40)
                {
                    return false;
                }
                if (probe.timeLeft < 60)
                {
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>弹幕显示名是否命中关键词表（中文按服务器语言，英文关键词也能撞上）。</summary>
        internal static bool MatchesNameKeyword(int type, List<string> keywords, out string hit)
        {
            hit = null;
            if (keywords == null || keywords.Count == 0)
            {
                return false;
            }
            string name;
            try
            {
                name = Lang.GetProjectileName(type).Value ?? string.Empty;
            }
            catch (Exception)
            {
                name = string.Empty;
            }
            if (name.Length == 0)
            {
                return false;
            }
            foreach (string keyword in keywords)
            {
                if (string.IsNullOrEmpty(keyword))
                {
                    continue;
                }
                if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit = keyword;
                    return true;
                }
            }
            return false;
        }

        /// <summary>重建一次池子并给出描述（诊断用，/mp pool）。会列出被关键词与 ID 黑名单拦下的类型。</summary>
        internal static string DescribePool(ProjectileSettings settings)
        {
            var pool = new List<int>();
            var byKeyword = new List<string>();
            var byBlacklist = new List<int>();

            for (int type = 1; type < ProjectileID.Count; type++)
            {
                if (settings != null && settings.BlacklistProjectiles != null
                    && settings.BlacklistProjectiles.Contains(type))
                {
                    byBlacklist.Add(type);
                    continue;
                }

                string hit;
                if (MatchesNameKeyword(type, settings == null ? null : settings.BlacklistNameKeywords, out hit))
                {
                    string name;
                    try
                    {
                        name = Lang.GetProjectileName(type).Value ?? type.ToString();
                    }
                    catch (Exception)
                    {
                        name = type.ToString();
                    }
                    byKeyword.Add(name + "(" + type + ")/" + hit);
                    continue;
                }

                if (IsSuitableProjectileType(type, settings))
                {
                    pool.Add(type);
                }
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(string.Format("可用 {0} 种（全量 {1} 种）", pool.Count, ProjectileID.Count));
            if (byBlacklist.Count > 0)
            {
                sb.Append("；ID 黑名单 ").Append(byBlacklist.Count).Append(" 种");
            }
            if (byKeyword.Count > 0)
            {
                sb.Append("；名字关键词拦下 ").Append(byKeyword.Count).Append(" 种：");
                sb.Append(string.Join("、", byKeyword.ToArray()));
            }
            return sb.ToString();
        }

        /// <summary>诊断：按关键词在所有弹幕名里找（含没进池的）。</summary>
        internal static List<string> FindByName(string keyword, ProjectileSettings settings)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(keyword))
            {
                lines.Add("用法：/mp find <关键词>");
                return lines;
            }

            var pool = new List<int>();
            for (int type = 1; type < ProjectileID.Count; type++)
            {
                if (IsSuitableProjectileType(type, settings))
                {
                    pool.Add(type);
                }
            }
            var poolSet = new HashSet<int>(pool);

            const int limit = 25;
            for (int type = 1; type < ProjectileID.Count && lines.Count < limit; type++)
            {
                string name;
                try
                {
                    name = Lang.GetProjectileName(type).Value ?? string.Empty;
                }
                catch (Exception)
                {
                    continue;
                }
                if (name.Length == 0 || name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string verdict;
                if (poolSet.Contains(type))
                {
                    verdict = "★在池内";
                }
                else
                {
                    string hit;
                    if (settings != null && settings.BlacklistProjectiles != null
                        && settings.BlacklistProjectiles.Contains(type))
                    {
                        verdict = "ID 黑名单";
                    }
                    else if (MatchesNameKeyword(type, settings == null ? null : settings.BlacklistNameKeywords, out hit))
                    {
                        verdict = "名字关键词(" + hit + ")";
                    }
                    else
                    {
                        verdict = "其它规则";
                    }
                }

                string shape = "";
                try
                {
                    var probe = new Projectile();
                    probe.SetDefaults(type);
                    shape = string.Format(" aiStyle={0} {1}x{2} timeLeft={3} hostile={4}",
                        probe.aiStyle, probe.width, probe.height, probe.timeLeft, probe.hostile);
                }
                catch (Exception)
                {
                }

                lines.Add(string.Format("{0}({1}) {2}{3}", name, type, verdict, shape));
            }

            if (lines.Count == 0)
            {
                lines.Add("没有名字含「" + keyword + "」的弹幕");
            }
            return lines;
        }

        /// <summary>
        /// 从弹幕池里挑一个类型。开启 AvoidRepeatingType 时会尽量避开「上一次用过的」和「原本的」类型，
        /// 让「每次攻击都是不一样的弹幕」这个体感更明显；池子太小则自动放弃该限制。
        /// </summary>
        private int PickProjectileType(List<int> pool, ProjectileSettings settings, int slot, int oldType)
        {
            if (pool.Count == 0)
            {
                return oldType;
            }
            if (pool.Count == 1 || !settings.AvoidRepeatingType)
            {
                return pool[rng.Next(pool.Count)];
            }

            int last;
            bool hasLast = lastTypeBySlot.TryGetValue(slot, out last);
            for (int attempt = 0; attempt < 12; attempt++)
            {
                int candidate = pool[rng.Next(pool.Count)];
                if (candidate == oldType)
                {
                    continue;
                }
                if (hasLast && candidate == last)
                {
                    continue;
                }
                return candidate;
            }
            return pool[rng.Next(pool.Count)];
        }

        /// <summary>巨石类弹幕：滚动巨石使用 aiStyle==25（1.4.5.8 实测），它们的伤害保留自身数值。</summary>
        private static bool IsBoulderProjectile(Projectile projectile)
        {
            try
            {
                return projectile.aiStyle == 25;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>按「原版弹幕伤害 x [DamageMinMultiplier, DamageMaxMultiplier]」随机。</summary>
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
            if (result < 1)
            {
                result = 1;
            }
            return result;
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

        private static void SetProjectileKey(Projectile projectile, object key)
        {
            if (key == null)
            {
                return;
            }
            try
            {
                System.Reflection.MemberInfo member = ResolveProjectileKeyMember();
                var property = member as System.Reflection.PropertyInfo;
                if (property != null && property.CanWrite)
                {
                    property.SetValue(projectile, key, null);
                    return;
                }
                var field = member as System.Reflection.FieldInfo;
                if (field != null)
                {
                    field.SetValue(projectile, key);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}