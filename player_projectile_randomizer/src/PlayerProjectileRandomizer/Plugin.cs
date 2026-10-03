using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace PlayerProjectileRandomizer
{
    /// <summary>
    /// 玩家弹幕随机化插件（TShock 6.x / Terraria 1.4.5.8）。
    ///
    /// 做两件事：
    ///   1. 玩家打出的弹幕会被随机换成另一种弹幕，且「每次都不一样」（不重复上一次）
    ///   2. 换完之后伤害仍然是「手持武器算出来的伤害」（迷你鲨射出的高速子弹变成魔法导弹，
    ///      伤害还是迷你鲨的）
    ///
    /// 不碰怪物弹幕（那是 MonsterProjectileRandomizer 的活）、不碰近战挥砍、不碰召唤物。
    /// </summary>
    [ApiVersion(2, 1)]
    public sealed class PlayerProjectileRandomizerPlugin : TerrariaPlugin
    {
        internal static ProjectileSettings Settings { get; private set; } = new ProjectileSettings();
        internal static PlayerProjectileRandomizer Randomizer { get; private set; }
        internal static string LastError { get; private set; }

        private static Command pluginCommand;
        private static string configPath;
        private static DateTime lastErrorLog = DateTime.MinValue;

        public override string Name { get { return "Player Projectile Randomizer"; } }
        public override Version Version { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
        public override string Author { get { return "DSH"; } }
        public override string Description { get { return "随机化玩家弹幕（每次都不一样），随机出的弹幕继承手持武器的伤害"; } }

        public PlayerProjectileRandomizerPlugin(Main game) : base(game)
        {
            Order = 8;
        }

        public override void Initialize()
        {
            configPath = Path.Combine(TShock.SavePath ?? "tshock", "PlayerProjectileRandomizer.json");

            string error;
            if (!ReloadConfig(out error))
            {
                Log("读取配置失败，使用内置默认值：" + error);
            }

            Randomizer = new PlayerProjectileRandomizer();

            ServerApi.Hooks.NetSendData.Register(this, OnNetSendData);
            ServerApi.Hooks.GamePostUpdate.Register(this, OnPostUpdate);
            ServerApi.Hooks.GameWorldConnect.Register(this, OnWorldConnect);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);

            // TShock 6 环境里 GamePostUpdate 依赖的 OTAPI HookEvents 不会触发，
            // 因此用定时器把心跳投递到游戏主线程；NetSendData 在有玩家时作为高频驱动。
            Randomizer.StartHeartbeat(Settings.HeartbeatIntervalMs);

            pluginCommand = new Command("playerprojectile.admin", OnCommand, "playerprojectile", "ppr")
            {
                HelpText = "玩家弹幕随机化：/ppr help",
                AllowServer = true,
            };
            Commands.ChatCommands.Add(pluginCommand);

            TryGrantPermission();

            Log(string.Format("已加载（配置：{0}），当前状态：{1}；伤害规则：{2}",
                configPath, Settings.Enabled ? "启用" : "禁用", Settings.DescribeDamage()));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.NetSendData.Deregister(this, OnNetSendData);
                ServerApi.Hooks.GamePostUpdate.Deregister(this, OnPostUpdate);
                ServerApi.Hooks.GameWorldConnect.Deregister(this, OnWorldConnect);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);

                PlayerProjectileRandomizer randomizer = Randomizer;
                if (randomizer != null)
                {
                    randomizer.StopHeartbeat();
                }

                if (pluginCommand != null)
                {
                    Commands.ChatCommands.Remove(pluginCommand);
                    pluginCommand = null;
                }
                Randomizer = null;
            }
            base.Dispose(disposing);
        }

        // ---------------- 配置 ----------------

        internal static bool ReloadConfig(out string error)
        {
            try
            {
                if (string.IsNullOrEmpty(configPath))
                {
                    configPath = Path.Combine(TShock.SavePath ?? "tshock", "PlayerProjectileRandomizer.json");
                }

                bool wroteDefaults;
                ProjectileConfigFile file = ProjectileConfigFile.Load(configPath, out wroteDefaults);
                Settings = file.Settings ?? new ProjectileSettings();
                Settings.Normalize();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                Settings = Settings ?? new ProjectileSettings();
                Settings.Normalize();
                error = ex.Message;
                return false;
            }
        }

        internal static void Log(string message)
        {
            string line = "[PlayerProjectile] " + message;
            try
            {
                if (TShock.Log != null)
                {
                    TShock.Log.Info(line);
                }
            }
            catch (Exception)
            {
            }
            Console.WriteLine(line);
        }

        internal static void LogError(string message)
        {
            LastError = message;
            if ((DateTime.UtcNow - lastErrorLog).TotalSeconds < 10)
            {
                return;
            }
            lastErrorLog = DateTime.UtcNow;
            string line = "[PlayerProjectile] " + message;
            try
            {
                if (TShock.Log != null)
                {
                    TShock.Log.Error(line);
                }
            }
            catch (Exception)
            {
            }
            Console.WriteLine(line);
        }

        private static void TryGrantPermission()
        {
            try
            {
                if (TShock.Groups != null)
                {
                    TShock.Groups.AddPermissions("superadmin", new List<string> { "playerprojectile.admin" });
                }
            }
            catch (Exception)
            {
            }
        }

        // ---------------- Hooks ----------------

        private void OnPostUpdate(EventArgs args)
        {
            try
            {
                PlayerProjectileRandomizer randomizer = Randomizer;
                if (randomizer != null)
                {
                    randomizer.TryPulse();
                }
            }
            catch (Exception ex)
            {
                LogError("主循环出错：" + ex);
            }
        }

        /// <summary>
        /// 关键路径：服务器每次向客户端发包都会触发这里。
        /// 若正是「弹幕创建包」（ProjectileNew），就在数据发出之前把那条弹幕换好 ——
        /// 客户端从第一条数据起拿到的就是随机后的种类与继承来的伤害。
        /// </summary>
        private void OnNetSendData(SendDataEventArgs args)
        {
            try
            {
                PlayerProjectileRandomizer randomizer = Randomizer;
                if (randomizer == null)
                {
                    return;
                }

                if (args.MsgId == PacketTypes.ProjectileNew)
                {
                    randomizer.RandomizeProjectileByIndex(args.number);
                }
            }
            catch (Exception ex)
            {
                LogError("网络驱动出错：" + ex);
            }
        }

        private void OnWorldConnect(EventArgs args)
        {
            try
            {
                PlayerProjectileRandomizer randomizer = Randomizer;
                if (randomizer != null)
                {
                    randomizer.ClearWorld();
                }

                if (!Settings.NotifyOnWorldLoad || !Settings.Enabled)
                {
                    return;
                }

                foreach (TSPlayer player in TShock.Players)
                {
                    if (player == null || !player.Active)
                    {
                        continue;
                    }
                    player.SendInfoMessage("[弹幕随机] 你打出的弹幕会被随机替换，但伤害仍然按你手上的武器计算。");
                }
            }
            catch (Exception ex)
            {
                LogError("世界加载处理出错：" + ex);
            }
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            // 玩家离开时清掉他的「上一次弹幕」记录，避免索引被复用后串味
            try
            {
                PlayerProjectileRandomizer randomizer = Randomizer;
                if (randomizer != null)
                {
                    randomizer.ForgetPlayer(args.Who);
                }
            }
            catch (Exception ex)
            {
                LogError("玩家离开处理出错：" + ex);
            }
        }

        // ---------------- 命令 ----------------

        private void OnCommand(CommandArgs args)
        {
            TSPlayer player = args.Player;
            string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "help";

            switch (sub)
            {
                case "on":
                case "enable":
                    Settings.Enabled = true;
                    player.SendSuccessMessage("[弹幕随机] 已启用。");
                    break;

                case "off":
                case "disable":
                    Settings.Enabled = false;
                    player.SendSuccessMessage("[弹幕随机] 已禁用（场上已生成的弹幕保持现状）。");
                    break;

                case "reload":
                {
                    string error;
                    if (ReloadConfig(out error))
                    {
                        player.SendSuccessMessage("[弹幕随机] 配置已重载。");
                        if (Randomizer != null)
                        {
                            Randomizer.ResetProjectileTypePool();
                            Randomizer.StartHeartbeat(Settings.HeartbeatIntervalMs);
                        }
                        player.SendInfoMessage(string.Format("[弹幕随机] 状态：{0}；伤害规则：{1}",
                            Settings.Enabled ? "启用" : "禁用", Settings.DescribeDamage()));
                    }
                    else
                    {
                        player.SendErrorMessage("[弹幕随机] 配置重载失败：" + error);
                    }
                    break;
                }

                case "status":
                    player.SendInfoMessage("[弹幕随机] 状态：" + (Settings.Enabled ? "启用" : "禁用"));
                    player.SendInfoMessage("[弹幕随机] 伤害规则：" + Settings.DescribeDamage());
                    player.SendInfoMessage(string.Format("[弹幕随机] 开关：换种类 {0} / 伤害随机 {1} / 敌对弹幕 {2} / 召唤物 {3}",
                        Settings.RandomizeProjectileType, Settings.RandomizeProjectileDamage,
                        Settings.IncludeHostile, Settings.IncludeMinions));
                    player.SendInfoMessage(string.Format("[弹幕随机] 伤害继承：{0}（1.4.5.8 的弹幕自身无伤害，伤害一律由武器传入）",
                        Settings.KeepWeaponDamage ? "开" : "关"));
                    player.SendInfoMessage(string.Format("[弹幕随机] 弹幕池：{0}",
                        Randomizer == null ? "未初始化" : Randomizer.PoolDescription));
                    player.SendInfoMessage(string.Format("[弹幕随机] 已随机化弹幕：{0} 条（同一发判定窗口 {1} ms）",
                        Randomizer == null ? 0 : Randomizer.RandomizedProjectileCount, Settings.SameAttackWindowMs));
                    player.SendInfoMessage(string.Format("[弹幕随机] 发射者视角补发：{0} 次（待处理 {1} 条，延迟 {2} ms）",
                        Randomizer == null ? 0 : Randomizer.ResyncCount,
                        Randomizer == null ? 0 : Randomizer.PendingResyncCount,
                        Settings.OwnerResyncDelayMs));
                    player.SendInfoMessage(string.Format("[弹幕随机] Projectile.key 类型：{0}（换 key：{1}）",
                        PlayerProjectileRandomizer.DescribeKeyMember(),
                        Settings.RotateProjectileKeyOnRandomize ? "开" : "关"));
                    player.SendInfoMessage(string.Format("[弹幕随机] key 可写性：{0}；换类型是否已停：{1}",
                        PlayerProjectileRandomizer.DescribeKeyWritable(),
                        PlayerProjectileRandomizer.KeyRestoreFailed ? "是（key 写不回去）" : "否"));
                    player.SendInfoMessage(string.Format("[弹幕随机] 诊断：心跳 {0} 次 / 运行 {1:F0} 秒 / 最近错误：{2}",
                        Randomizer == null ? 0 : Randomizer.PulseCount,
                        Randomizer == null ? 0 : Randomizer.Uptime.TotalSeconds,
                        string.IsNullOrEmpty(LastError) ? "无" : LastError));
                    break;

                case "pool":
                {
                    string describe = ProjectilePool.RebuildDescription(Settings);
                    player.SendInfoMessage("[弹幕随机] " + describe);
                    player.SendInfoMessage("[弹幕随机] 保命名单：排除 " + ProjectilePool.ExcludedCount + " 种问题弹幕");
                    player.SendInfoMessage("[弹幕随机] 过滤统计：" + ProjectilePool.DescribeRejectStats());
                    player.SendInfoMessage("[弹幕随机] 抽样：" + ProjectilePool.Sample(ProjectilePool.LastBuiltPool, 12));
                    break;
                }

                case "rescan":
                {
                    int handled = Randomizer == null ? 0 : Randomizer.RescanProjectiles();
                    player.SendInfoMessage(string.Format("[弹幕随机] 已重新扫描，本次处理了 {0} 条玩家弹幕。", handled));
                    break;
                }

                case "audit":
                {
                    foreach (string line in ProjectilePool.Audit(Settings))
                    {
                        player.SendInfoMessage("[池审计] " + line);
                    }
                    List<string> fields = ProjectilePool.ListBoolSetFields();
                    player.SendInfoMessage(string.Format("[池审计] Sets 里的 bool[] 字段（{0} 个）：{1}",
                        fields.Count, string.Join(", ", fields.ToArray())));
                    foreach (string api in PlayerProjectileRandomizer.DescribeNewProjectileApis())
                    {
                        player.SendInfoMessage("[池审计] NewProjectile: " + api);
                    }
                    break;
                }

                case "test":
                {
                    int rounds = 6;
                    if (args.Parameters.Count > 1)
                    {
                        int parsed;
                        if (int.TryParse(args.Parameters[1], out parsed))
                        {
                            rounds = parsed;
                        }
                    }
                    if (Randomizer == null)
                    {
                        player.SendErrorMessage("[弹幕随机] 未初始化。");
                        break;
                    }
                    foreach (string line in Randomizer.SelfTest(Settings, rounds))
                    {
                        player.SendInfoMessage("[自检] " + line);
                        Log("[自检] " + line);
                    }
                    break;
                }

                case "excl":
                {
                    player.SendInfoMessage(string.Format("[封禁名单] 已封禁 {0} 种弹幕（破坏性/召唤物/特效等）",
                        ProjectilePool.ExcludedCount));
                    List<string> missing = ProjectilePool.ListUnresolvedExcludedNames();
                    if (missing.Count == 0)
                    {
                        player.SendInfoMessage("[封禁名单] 名单里的名字全部在本版本解析成功（无拼写错误）。");
                    }
                    else
                    {
                        player.SendInfoMessage(string.Format("[封禁名单] 本版本不存在的名字 {0} 个（不影响运行）：{1}",
                            missing.Count, string.Join(", ", missing.ToArray())));
                    }
                    player.SendInfoMessage("[封禁名单] 抽查：雷管/炸弹/粘性炸弹/手雷/火箭/地雷是否已禁 —— "
                        + ProjectilePool.DescribeBlacklistSample());
                    List<string> left = ProjectilePool.ListDestructiveInPool(Settings);
                    if (left.Count == 0)
                    {
                        player.SendInfoMessage("[封禁名单] 当前弹幕池里没有任何破坏性弹幕 ✔");
                    }
                    else
                    {
                        player.SendInfoMessage(string.Format("[封禁名单] ⚠ 池子里仍有 {0} 种名字含破坏性关键词：{1}",
                            left.Count, string.Join("、", left.ToArray())));
                    }
                    List<string> rejected = ProjectilePool.ListDestructiveRejected();
                    player.SendInfoMessage(string.Format("[封禁名单] 按名字拦下的 {0} 种：{1}",
                        rejected.Count, rejected.Count == 0 ? "无" : string.Join("、", rejected.ToArray())));
                    break;
                }

                case "find":
                {
                    if (args.Parameters.Count < 2)
                    {
                        player.SendInfoMessage("[弹幕随机] 用法：/ppr find <关键词>（在所有弹幕名里找，含没进池的）");
                        break;
                    }
                    for (int i = 1; i < args.Parameters.Count && i <= 4; i++)
                    {
                        foreach (string line in ProjectilePool.FindByName(args.Parameters[i], Settings))
                        {
                            player.SendInfoMessage("[查找] " + line);
                        }
                    }
                    break;
                }

                case "probe":
                {
                    if (args.Parameters.Count < 2)
                    {
                        player.SendInfoMessage("[弹幕随机] 用法：/ppr probe <弹幕ID>（可多个，空格分隔）");
                        break;
                    }
                    for (int i = 1; i < args.Parameters.Count && i <= 8; i++)
                    {
                        int id;
                        if (!int.TryParse(args.Parameters[i], out id))
                        {
                            continue;
                        }
                        player.SendInfoMessage("[探针] " + ProjectilePool.Probe(id, Settings));
                    }
                    break;
                }

                default:
                    player.SendInfoMessage("[弹幕随机] 命令：");
                    player.SendInfoMessage("  /ppr status  - 查看状态、弹幕池与伤害规则");
                    player.SendInfoMessage("  /ppr pool    - 查看弹幕池大小与抽样（重扫池子）");
                    player.SendInfoMessage("  /ppr on|off  - 临时启用 / 禁用");
                    player.SendInfoMessage("  /ppr reload  - 重载 tshock/PlayerProjectileRandomizer.json");
                    player.SendInfoMessage("  /ppr rescan  - 立刻重新扫描场上玩家弹幕");
                    player.SendInfoMessage("  /ppr find <关键词> - 在所有弹幕名里找（确认名字对应哪个 ID、是否在池内）");
                    player.SendInfoMessage("  /ppr probe <ID> - 诊断某个弹幕类型（为什么被过滤）");
                    player.SendInfoMessage("  /ppr audit   - 审计弹幕池内容（存活时间/aiStyle 分布）");
                    player.SendInfoMessage("  /ppr excl    - 查看破坏性/危险弹幕封禁名单状态");
                    player.SendInfoMessage("  /ppr test [次数] - 自检：连续造弹幕，验证「每次不同 + 伤害继承」");
                    break;
            }
        }
    }
}
