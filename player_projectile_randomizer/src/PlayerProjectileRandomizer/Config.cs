using System;
using System.Collections.Generic;
using TShockAPI.Configuration;

namespace PlayerProjectileRandomizer
{
    /// <summary>
    /// 配置（tshock/PlayerProjectileRandomizer.json）。
    ///
    /// 本插件只做一件事：随机化「玩家打出的弹幕」的种类，并且让随机出来的弹幕
    /// 继承玩家手持武器的伤害。
    ///
    /// 伤害规则（默认）：
    ///   · 换种类之前先快照该弹幕当前的伤害（= 服务端按手持武器算出来的武器伤害），
    ///     换完种类再写回去 —— 迷你鲨射出的高速子弹变成魔法导弹，伤害还是迷你鲨的。
    ///   · 武器本身不产生弹幕时（纯近战挥砍等），插件不介入。
    /// </summary>
    public sealed class ProjectileSettings
    {
        /// <summary>总开关。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>是否随机替换弹幕种类（关掉就只剩伤害继承，可用于排查问题）。</summary>
        public bool RandomizeProjectileType { get; set; } = true;

        /// <summary>
        /// 是否让随机出来的弹幕继承「手持武器算出的伤害」（即原弹幕的伤害）。默认 true。
        /// 关掉的话，伤害会变成「新弹幕自身的默认伤害」，那会让迷你鲨变成魔法导弹的伤害。
        /// </summary>
        public bool KeepWeaponDamage { get; set; } = true;

        /// <summary>
        /// 在继承武器伤害的基础上，是否再做一次随机倍率浮动。
        /// 默认 false = 严格等于手持武器伤害。
        /// </summary>
        public bool RandomizeProjectileDamage { get; set; } = false;

        /// <summary>伤害下限倍率（仅 RandomizeProjectileDamage=true 时生效）。</summary>
        public double DamageMinMultiplier { get; set; } = 0.5;

        /// <summary>伤害上限倍率（仅 RandomizeProjectileDamage=true 时生效）。</summary>
        public double DamageMaxMultiplier { get; set; } = 3.0;

        /// <summary>
        /// 强制保证「这次和上次不一样」：同一名玩家连续两次选择弹幕时，绝不重复上一次的种类。
        /// 弹幕池被配置缩到只剩 1 种时该限制自动失效。
        /// </summary>
        public bool AvoidRepeatingType { get; set; } = true;

        /// <summary>
        /// 同一次攻击在多少毫秒内产生的多条弹幕视为「同一发」，共用同一个随机结果。
        /// 霰弹枪一枪 5 颗、魔法导弹一次多发都算同一发，避免自己打自己脸。
        /// 设为 0 表示对每条弹幕独立随机。
        /// </summary>
        public int SameAttackWindowMs { get; set; } = 120;

        /// <summary>随机时是否保留原弹幕的速度大小（false = 用新弹幕类型的默认速度）。</summary>
        public bool KeepOriginalSpeed { get; set; } = true;

        /// <summary>随机时是否保留原弹幕的击退。</summary>
        public bool KeepOriginalKnockback { get; set; } = true;

        /// <summary>是否连「敌对玩家弹幕」（hostile=true，例如 PvP 里射向你的）一起随机。</summary>
        public bool IncludeHostile { get; set; } = true;

        /// <summary>
        /// 构建弹幕池时是否排除「类型标记为 hostile」的弹幕。
        ///
        /// 注意：Terraria 1.4.5.8 里几乎所有攻击型弹幕（子弹、箭、火球）默认都是 hostile=true，
        /// 那是「弹幕本身是不是攻击判定」的标记，与「谁打出来的」无关。真正的归属由
        /// Projectile.owner 决定，插件只处理 owner 是玩家的弹幕。
        /// 把这项设为 true 会砍掉绝大多数花样，一般不要动。
        /// </summary>
        public bool ExcludeHostileTypes { get; set; } = false;

        /// <summary>是否随机化小兵/哨兵弹幕（仆从、召唤塔）。默认关闭，避免仆从乱飞。</summary>
        public bool IncludeMinions { get; set; } = false;

        /// <summary>是否随机化「长矛/链锤/鞭子」这类近战延伸弹幕。默认关闭，手感和判定都比较怪。</summary>
        public bool IncludeMeleeExtenders { get; set; } = false;

        /// <summary>
        /// 弹幕池白名单。留空 = 自动构建（遍历所有原版弹幕，按下面的规则过滤）。
        /// 填了就以它为准（仍然会做合法性检查），可用来精确控制随机范围。
        /// </summary>
        public List<int> WhitelistProjectiles { get; set; } = new List<int>();

        /// <summary>
        /// ID 黑名单：这些弹幕永远不会被选到。
        /// 871 = hallowBossSplitShotCore（光之女皇的分裂弹幕核心）——
        /// 它会让<strong>发射者自己</strong>吃伤害，而且名字没有中文翻译
        /// （就叫 ProjectileName.HallowBossSplitShotCore），按关键词搜不到，只能按 ID 精确拉黑。
        /// </summary>
        public List<int> BlacklistProjectiles { get; set; } = new List<int> { 871 };

        /// <summary>
        /// 按「弹幕显示名关键词」拉黑（中英文各匹配一遍）。
        ///
        /// 专门用来挡「换出来之后不会自己消失、一直杵在场上」的滞留型弹幕 ——
        /// 典型的就是存钱罐那一类（飞猪存钱罐 / 钱币槽 / 保险箱 / 守卫熔炉）和玩家点名的碎岩龟。
        /// 这类弹幕的 timeLeft 看 SetDefaults 是正常的，靠数值判据筛不掉，只能按名字拦。
        /// 以后玩家报新名字，改这个列表即可，**不用重新编译**。
        /// </summary>
        public List<string> BlacklistNameKeywords { get; set; } = new List<string>
        {
            "存钱罐", "钱币槽", "飞猪", "碎岩龟", "史莱姆气球", "七彩",
            "绳圈", "永恒彩虹", "钩", "HallowBossSplitShot", "VoidLens", "Globe", "Geode", "墙上飞车", "圣骑士锤",
            "MoneyTrough", "PiggyBank", "Safe", "DefenderForge", "Trough",
        };

        /// <summary>
        /// 换完弹幕类型后，是否给「发射者本人」补发一个销毁包（默认开）。
        ///
        /// 原因：1.4.5.8 的客户端对自己射出的弹幕是**本地预测**的（本地已经建了一颗原版弹幕），
        /// 收到服务端的 ProjectileNew 时它会认出「这是我自己那颗」（key 相同），于是保留本地类型，
        /// 只同步位置 —— 表现就是「别人看得见随机弹幕，自己看不见」。
        /// 补发 ProjectileDestroy 让发射者先删掉本地那颗，紧随其后的 ProjectileNew
        /// 就会用随机后的类型重新建一颗，于是自己也看得见了。
        /// </summary>
        public bool ResyncOwnerAfterRandomize { get; set; } = true;

        /// <summary>
        /// 给发射者补发「销毁 + 重建」的延迟（毫秒，默认 200）。
        ///
        /// ⚠️ 为什么必须延迟：客户端开火时先在本地预测一颗弹幕，**稍后**才收到服务端的
        /// ProjectileNew，收到之后才会把自己那颗弹幕按 key 对齐到服务端给的下标。
        /// 如果销毁包跟着原始 ProjectileNew 一起发出去，客户端此刻本地那颗还在自己的下标上，
        /// 销毁包会打空 —— 这正是第一版修复（同步发销毁）无效的原因。
        /// 等一拍（200ms）再发，客户端已经对齐完毕，删掉后用新类型重建即可。
        /// 玩家网络延迟特别高（>250ms）时仍看不到随机弹幕，就把这个值再调大。
        /// </summary>
        public int OwnerResyncDelayMs { get; set; } = 30;

        /// <summary>
        /// 随机换型时是否同时给弹幕换一个新的 Projectile.key（默认关）。
        ///
        /// 客户端靠 key 认「这是我自己射出的那颗弹幕」，认出来就保留本地预测的类型。
        /// 换掉 key 之后它匹配不上，就会把服务端这份数据当成一颗新弹幕来建 —— 即随机后的类型。
        /// 如果延迟补发（ResyncOwnerAfterRandomize）在你的客户端上不生效，把这项打开再试。
        /// </summary>
        public bool RotateProjectileKeyOnRandomize { get; set; } = true;

        /// <summary>
        /// 补发那一下是否把 ProjectileNew 包里的 owner 临时改成一个非本人的值（默认开，用 255）。
        ///
        /// 客户端判断「这是我射的弹幕」看的就是包里的 owner == Main.myPlayer；
        /// 用 255（无主 —— 陷阱弹幕也用这个值）发一份给发射者，他就不会保留本地那套预测数据，
        /// 而是照服务端这份（随机后的类型）重建。这是「销毁 + 重建」之外的第二道保险。
        /// 服务端自己保存的 owner 发完立刻改回来，伤害归属不受影响。
        /// </summary>
        public bool SpoofOwnerOnResync { get; set; } = false;

        /// <summary>
        /// 补发前是否先给发射者发一个「销毁弹幕」包（默认关）。
        /// 关掉的理由：客户端本地那颗弹幕本来就和服务端用同一个下标，
        /// 直接发一份带**新 key** 的 ProjectileNew 就会把它覆盖掉；先销毁反而会闪一下。
        /// 万一你的客户端上覆盖不生效、屏幕上出现两颗弹幕，再把这项打开。
        /// </summary>
        public bool DestroyBeforeResync { get; set; } = false;

        /// <summary>
        /// 是否用「玩家武器/弹药反向收集」来确定弹幕池（推荐开）。
        ///
        /// 做法：遍历所有物品的 Item.shoot，凡是玩家武器/弹药/工具真的会射出的弹幕都收进池子。
        /// 这样得到的池子天然就是「玩家弹幕」的范围，不会混进怪物专用弹幕和纯装饰特效。
        /// 关掉则退回「遍历所有弹幕 ID + 规则过滤」的老做法。
        /// </summary>
        public bool CollectFromItems { get; set; } = true;

        /// <summary>
        /// 禁止破坏性弹幕（雷管、炸弹、手雷、地雷、火箭、集束火箭、爆炸兔…）。
        ///
        /// 双重保险：既查手写的保命名单，也按弹幕显示名里的关键词
        /// （雷管/炸弹/手雷/地雷/爆炸/火箭/集束/bomb/grenade/rocket…）过滤，
        /// 避免漏掉某个版本新增的爆炸物。建议保持开启。
        /// </summary>
        public bool ForbidDestructiveProjectiles { get; set; } = true;

        /// <summary>弹幕池最大体积（像素，宽或高超过就不进池）。</summary>
        public int MaxProjectileSize { get; set; } = 40;

        /// <summary>弹幕池最小存活时间（帧，低于就不进池）。</summary>
        public int MinProjectileTimeLeft { get; set; } = 1;

        /// <summary>心跳间隔（毫秒）：定时兜底扫描「由玩家发出、但没走 ProjectileNew 包」的弹幕。</summary>
        public int HeartbeatIntervalMs { get; set; } = 100;

        /// <summary>调试日志：每条被随机化的弹幕写一行（种类 x->y、伤害 a->b）。</summary>
        public bool LogRandomizedProjectiles { get; set; } = false;

        /// <summary>玩家进服时提示。</summary>
        public bool NotifyOnWorldLoad { get; set; } = true;

        /// <summary>启动时把弹幕池大小与抽样写进日志，方便确认过滤是否合理。</summary>
        public bool LogPoolOnStartup { get; set; } = true;

        public void Normalize()
        {
            if (SameAttackWindowMs < 0)
            {
                SameAttackWindowMs = 0;
            }
            if (SameAttackWindowMs > 2000)
            {
                SameAttackWindowMs = 2000;
            }
            if (DamageMinMultiplier < 0.0)
            {
                DamageMinMultiplier = 0.0;
            }
            if (DamageMaxMultiplier < DamageMinMultiplier)
            {
                DamageMaxMultiplier = DamageMinMultiplier;
            }
            if (DamageMaxMultiplier > 1000.0)
            {
                DamageMaxMultiplier = 1000.0;
            }
            if (HeartbeatIntervalMs < 20)
            {
                HeartbeatIntervalMs = 20;
            }
            if (MaxProjectileSize < 8)
            {
                MaxProjectileSize = 8;
            }
            if (MinProjectileTimeLeft < 1)
            {
                MinProjectileTimeLeft = 1;
            }
            WhitelistProjectiles = DedupeInts(WhitelistProjectiles);
            BlacklistProjectiles = DedupeInts(BlacklistProjectiles);
            // ⚠️ Newtonsoft 反序列化 List 属性时是「追加到已有集合」而不是替换，
            // 所以每次读配置都会把 JSON 里的项在默认值之上再叠一遍 ——
            // 不去重的话 BlacklistNameKeywords 会 9→18→27 项无限膨胀。
            BlacklistNameKeywords = DedupeStrings(BlacklistNameKeywords);
            // 想彻底关掉名字关键词过滤：把列表写成 ["__none__"]
            if (BlacklistNameKeywords.Count == 1 && BlacklistNameKeywords[0] == "__none__")
            {
                BlacklistNameKeywords.Clear();
            }
        }

        private static List<int> DedupeInts(List<int> source)
        {
            var result = new List<int>();
            if (source == null)
            {
                return result;
            }
            foreach (int value in source)
            {
                if (!result.Contains(value))
                {
                    result.Add(value);
                }
            }
            return result;
        }

        private static List<string> DedupeStrings(List<string> source)
        {
            var result = new List<string>();
            if (source == null)
            {
                return result;
            }
            foreach (string value in source)
            {
                if (!string.IsNullOrEmpty(value) && !result.Contains(value))
                {
                    result.Add(value);
                }
            }
            return result;
        }

        public string DescribeDamage()
        {
            if (RandomizeProjectileDamage)
            {
                return string.Format("手持武器伤害 x{0:0.##} ~ x{1:0.##}", DamageMinMultiplier, DamageMaxMultiplier);
            }
            if (KeepWeaponDamage)
            {
                return "继承手持武器伤害（迷你鲨射什么都是迷你鲨的伤害）";
            }
            return "使用新弹幕自身的伤害（不推荐）";
        }
    }

    public sealed class ProjectileConfigFile : ConfigFile<ProjectileSettings>
    {
        public static ProjectileConfigFile Load(string path, out bool wroteDefaults)
        {
            var file = new ProjectileConfigFile();
            wroteDefaults = false;

            ProjectileSettings settings = file.Read(path, out bool incomplete);
            if (settings != null)
            {
                file.Settings = settings;
            }
            file.Settings = file.Settings ?? new ProjectileSettings();
            // ⚠️ 必须先 Normalize 再决定要不要写盘：
            // Newtonsoft 反序列化 List 属性是「追加到已有集合」，Read 之后内存里的列表
            // 已经比 JSON 里多了一倍（默认 10 项 + 文件 10 项 = 20 项）。
            // 原来的顺序是「先 Write 再 Normalize」，等于把膨胀结果直接落盘 ——
            // 于是每次 /ppr reload 都让 BlacklistNameKeywords 再翻一倍（10→20→30…）。
            file.Settings.Normalize();
            if (incomplete)
            {
                file.Write(path);
                wroteDefaults = true;
            }
            return file;
        }
    }
}
