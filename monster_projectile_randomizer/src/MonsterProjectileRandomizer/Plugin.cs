using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace MonsterProjectileRandomizer
{
    /// <summary>
    /// 怪物弹幕随机化插件（独立版）。
    /// 只做一件事：把怪物弹幕的种类随机替换、伤害随机化。
    /// 不碰怪物血量、不碰接触伤害 —— 那些由 MonsterHealthRandomizer 负责。
    /// </summary>
    [ApiVersion(2, 1)]
    public sealed class MonsterProjectileRandomizerPlugin : TerrariaPlugin
    {
        internal static ProjectileSettings Settings { get; private set; } = new ProjectileSettings();
        internal static ProjectileRandomizer Randomizer { get; private set; }
        internal static string LastError { get; private set; }

        private static Command pluginCommand;
        private static string configPath;
        private static DateTime lastErrorLog = DateTime.MinValue;

        public override string Name { get { return "Monster Projectile Randomizer"; } }
        public override Version Version { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
        public override string Author { get { return "DSH"; } }
        public override string Description { get { return "只随机化怪物弹幕：种类随机替换 + 伤害随机（血量与接触伤害不受影响）"; } }

        public MonsterProjectileRandomizerPlugin(Main game) : base(game)
        {
            Order = 7;
        }

        public override void Initialize()
        {
            configPath = Path.Combine(TShock.SavePath ?? "tshock", "MonsterProjectileRandomizer.json");

            string error;
            if (!ReloadConfig(out error))
            {
                Log("读取配置失败，使用内置默认值：" + error);
            }

            Randomizer = new ProjectileRandomizer();

            ServerApi.Hooks.NpcSpawn.Register(this, OnNpcSpawn);
            ServerApi.Hooks.NetSendData.Register(this, OnNetSendData);
            ServerApi.Hooks.GamePostUpdate.Register(this, OnPostUpdate);
            ServerApi.Hooks.GameWorldConnect.Register(this, OnWorldConnect);

            // TShock 6 环境里 GamePostUpdate 依赖的 OTAPI HookEvents 不会触发，
            // 因此用定时器把心跳投递到游戏主线程（NetSendData 在有玩家时作为高频辅助驱动）。
            Randomizer.StartHeartbeat(Settings.HeartbeatIntervalMs);

            pluginCommand = new Command("monsterprojectile.admin", OnCommand, "monsterprojectile", "mp")
            {
                HelpText = "怪物弹幕随机化：/mp help",
                AllowServer = true,
            };
            Commands.ChatCommands.Add(pluginCommand);

            TryGrantPermission();

            Log(string.Format("已加载（配置：{0}），当前状态：{1}", configPath, Settings.Enabled ? "启用" : "禁用"));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.NpcSpawn.Deregister(this, OnNpcSpawn);
                ServerApi.Hooks.NetSendData.Deregister(this, OnNetSendData);
                ServerApi.Hooks.GamePostUpdate.Deregister(this, OnPostUpdate);
                ServerApi.Hooks.GameWorldConnect.Deregister(this, OnWorldConnect);

                ProjectileRandomizer randomizer = Randomizer;
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
                    configPath = Path.Combine(TShock.SavePath ?? "tshock", "MonsterProjectileRandomizer.json");
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
            string line = "[MonsterProjectile] " + message;
            try
            {
                if (TShock.Log != null)
                {
                    TShock.Log.Info(line);
                    return;
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
            string line = "[MonsterProjectile] " + message;
            try
            {
                if (TShock.Log != null)
                {
                    TShock.Log.Error(line);
                    return;
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
                    TShock.Groups.AddPermissions("superadmin", new List<string> { "monsterprojectile.admin" });
                }
            }
            catch (Exception)
            {
            }
        }

        // ---------------- Hooks ----------------

        private void OnNpcSpawn(NpcSpawnEventArgs args)
        {
            try
            {
                ProjectileRandomizer randomizer = Randomizer;
                if (randomizer != null)
                {
                    // 生成怪物往往紧接着就会有弹幕，顺带立刻检查一次
                    randomizer.TryPulse();
                }
            }
            catch (Exception ex)
            {
                LogError("怪物生成处理出错：" + ex);
            }
        }

        private void OnPostUpdate(EventArgs args)
        {
            try
            {
                ProjectileRandomizer randomizer = Randomizer;
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
        /// 若正是「弹幕创建包」（ProjectileNew），就先把该弹幕的种类与伤害改好，
        /// 客户端从第一条数据起拿到的就是随机值 —— 不依赖心跳时序，也不会因弹幕短命而漏掉。
        /// </summary>
        private void OnNetSendData(SendDataEventArgs args)
        {
            try
            {
                ProjectileRandomizer randomizer = Randomizer;
                if (randomizer == null)
                {
                    return;
                }

                if (args.MsgId == PacketTypes.ProjectileNew)
                {
                    randomizer.RandomizeProjectileByIndex(args.number);
                }

                randomizer.TryPulse();
            }
            catch (Exception ex)
            {
                LogError("网络驱动心跳出错：" + ex);
            }
        }

        private void OnWorldConnect(EventArgs args)
        {
            try
            {
                ProjectileRandomizer randomizer = Randomizer;
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
                    player.SendInfoMessage("[弹幕随机] 本服怪物弹幕会被随机替换，伤害也是随机的。");
                }
            }
            catch (Exception ex)
            {
                LogError("世界加载处理出错：" + ex);
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
                    player.SendSuccessMessage("[弹幕随机] 已禁用（已生成的弹幕保持现状）。");
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
                        player.SendInfoMessage(string.Format("[弹幕随机] 状态：{0}；弹幕伤害 {1}",
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
                    player.SendInfoMessage(string.Format("[弹幕随机] 伤害规则：{0}", Settings.DescribeDamage()));
                    player.SendInfoMessage(string.Format("[弹幕随机] 开关：伤害 {0} / 种类 {1}（池 {2} 种）",
                        Settings.RandomizeProjectileDamage, Settings.RandomizeProjectileType,
                        Randomizer == null ? -1 : Randomizer.ProjectileTypePoolSize));
                    player.SendInfoMessage(string.Format("[弹幕随机] 已随机化弹幕：{0} 条", 
                        Randomizer == null ? 0 : Randomizer.RandomizedProjectileCount));
                    player.SendInfoMessage(string.Format("[弹幕随机] 诊断：心跳 {0} 次 / 运行 {1:F0} 秒 / 最近错误：{2}",
                        Randomizer == null ? 0 : Randomizer.PulseCount,
                        Randomizer == null ? 0 : Randomizer.Uptime.TotalSeconds,
                        string.IsNullOrEmpty(LastError) ? "无" : LastError));
                    break;

                case "rescan":
                {
                    int handled = Randomizer == null ? 0 : Randomizer.RescanProjectiles();
                    player.SendInfoMessage(string.Format("[弹幕随机] 已重新扫描弹幕，本次处理了 {0} 条新的敌对弹幕。", handled));
                    break;
                }

                case "pool":
                    player.SendInfoMessage("[弹幕随机] " + ProjectileRandomizer.DescribePool(Settings));
                    break;

                case "find":
                {
                    if (args.Parameters.Count < 2)
                    {
                        player.SendInfoMessage("[弹幕随机] 用法：/mp find <关键词>（在所有弹幕名里找，含没进池的）");
                        break;
                    }
                    for (int i = 1; i < args.Parameters.Count && i <= 4; i++)
                    {
                        foreach (string line in ProjectileRandomizer.FindByName(args.Parameters[i], Settings))
                        {
                            player.SendInfoMessage("[查找] " + line);
                        }
                    }
                    break;
                }

                default:
                    player.SendInfoMessage("[弹幕随机] 命令：");
                    player.SendInfoMessage("  /mp status  - 查看状态与随机规则");
                    player.SendInfoMessage("  /mp pool    - 重建并查看弹幕池（含被关键词/黑名单拦下的）");
                    player.SendInfoMessage("  /mp find <关键词> - 在所有弹幕名里找（确认某个名字对应哪个 ID）");
                    player.SendInfoMessage("  /mp on|off  - 临时启用/禁用");
                    player.SendInfoMessage("  /mp reload  - 重载 tshock/MonsterProjectileRandomizer.json");
                    player.SendInfoMessage("  /mp rescan  - 立刻重新扫描并随机化场上敌对弹幕");
                    break;
            }
        }
    }
}