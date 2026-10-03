using System;
using System.Collections.Generic;
using System.Reflection;
using Terraria;
using Terraria.ID;

namespace PlayerProjectileRandomizer
{
    /// <summary>
    /// 玩家弹幕池的构建与过滤。
    ///
    /// 目标：挑出「当成玩家弹幕射出去不会出事、看起来也正常」的原版弹幕。
    /// 过滤规则分三层：
    ///   1. 硬性规则：必须有伤害、aiStyle != 0、尺寸/存活时间在阈值内
    ///   2. 黑名单：明确会崩、会连锁生成、是召唤物/坐骑/工具、光束占位等
    ///   3. ProjectileID.Sets 反射：如果这个版本提供了对应集合（如 minion/sentry），就用它兜底
    /// </summary>
    internal static class ProjectilePool
    {
        /// <summary>
        /// 明确排除的弹幕名：出问题风险高，或明显不适合当普通攻击弹幕。
        /// 这些是「保命名单」，宁可少几种花样，也不要搞崩服务器或让玩家凭空召唤出东西。
        ///
        /// 用名字（而非 ProjectileID.XXX 常量）经反射解析，这样同一份源码可以
        /// 在多个 Terraria 版本上编译 —— 某个版本没有的弹幕名会被自动跳过。
        /// </summary>
        private static readonly string[] ExcludedNames =
        {
            // ---- 召唤物 / 宠物 / 坐骑 ----
            "BabySlime", "BabyHornet", "BabySkeletronHead", "BabyWerewolf", "BabyEater",
            "BabySnowman", "BabyTruffle", "BabyDinosaur", "BabyBird", "BabyGrinch",
            "BabyFaceMonster", "BabyPenguin", "BabySkeletronPrime", "BabyImp", "BabyOwl",
            "TinyDeer", "FennecFox", "GlitteryButterfly", "VampireFrog", "StormTiger",
            "SpiderMinion", "DeadlySphere", "Pygmy", "PygmySpear", "Pygmy2", "Pygmy3",
            "UFOMinion", "UFOFriendly", "Raven", "Tempest", "Retanimini", "Spazmamini",
            "MiniRetinaLaser", "MiniSharkron", "Terraprisma", "Flinx", "AbigailCounter",
            "AbigailMinion", "StardustCellMinion", "StardustCellMinionShot",
            "StardustDragon", "StardustGuardian", "StardustGuardianExplosion",
            "DesertTiger", "Smolstar", "VampireFrog", "BatMinion",

            // ---- 鞭子（近战延伸，判定方式特殊）----
            "CoolWhip", "FireWhip", "BoneWhip", "ScytheWhip", "BlandWhip", "ThornWhip",
            "RainbowWhip", "SwordWhip", "VampireWhip", "Whip", "HiveBomb",

            // ---- 哨兵 / 炮塔 ----
            "MiniNukeSnowmanRocket", "ExplosiveBunny", "ExplosiveTrapBunny",
            "DD2FlameBurstTowerT1", "DD2FlameBurstTowerT2", "DD2FlameBurstTowerT3",
            "DD2BallistraTowerT1", "DD2BallistraTowerT2", "DD2BallistraTowerT3",
            "DD2LightningAuraT1", "DD2LightningAuraT2", "DD2LightningAuraT3",
            "DD2ExplosiveTrapT1", "DD2ExplosiveTrapT2", "DD2ExplosiveTrapT3",

            // ---- 光束 / 占位 / 纯特效（没有独立判定，放出去就是空弹幕）----
            "LastPrismLaser", "LastPrism", "Zenith", "LaserMachinegunLaser",
            "ChargedBlasterLaser", "ChargedBlasterOrb", "ChargedBlasterCannon",
            "PhantasmalDeathray", "PhantasmalBolt", "PhantasmalSphere",
            "MoonlordHand", "MoonlordHead", "MoonlordFreeEye", "MoonlordLeechBlob",
            "StarCultistShot", "ShadowBeamHostile", "DD2LightningBugZap",
            "SpiritHeal", "SpiritHealVial", "LifeDrain",
            "NebulaArcanumExplosionShot", "NebulaArcanumExplosionShotShard",
            "MedusaHeadRay", "Ray", "SandnadoFriendly", "SandnadoHostile",
            "FinalFractal", "TerraBlade2Shot", "TerraBlade2Shot", "SolarWhipSword",
            "SolarCounter", "SolarWhipSwordExplosion", "Daybreak", "DaybreakExplosion",
            "StardustGuardianExplosion", "BetsyFury", "NebulaBlaze1", "NebulaBlaze2",

            // ---- 有破坏性的：爆炸类、炸地形类（用户明确要求禁掉）----
            // 雷管 / 炸弹 / 手雷全系列
            "Dynamite", "StickyDynamite", "BouncyDynamite", "BundleDynamite",
            "Bomb", "StickyBomb", "BouncyBomb", "HappyBomb", "BombFish",
            "DirtBomb", "DirtStickyBomb", "ScarabBomb", "BouncyDirtBomb",
            "BouncyDirtStickyBomb", "ExplosiveBunny", "ExplosiveBunny2",
            "ExplosiveJackOLantern", "ExplosiveTrap", "ExplosiveTrapBunny",
            "Grenade", "GrenadeI", "GrenadeII", "GrenadeIII", "GrenadeIV",
            "StickyGrenade", "BouncyGrenade", "PartyGirlGrenade", "Beenade",
            "ProximityMineI", "ProximityMineII", "ProximityMineIII", "ProximityMineIV",
            "ExplosiveBullet", "BombSkeletronPrime", "MiniNukeSnowmanRocket",
            "ClusterRocketI", "ClusterRocketII",
            // 火箭 / 炮弹 / 地雷 / 雷管类工具
            "RocketI", "RocketII", "RocketIII", "RocketIV",
            "ProximityMineLauncherRocket", "StyngerBolt", "StyngerShrapnel",
            "NailFriendly", "Nail", "JackOLantern", "FlamethrowerTrap",
            "FlamesTrap", "GeyserTrap", "SpearTrap", "SpikyBallTrap",
            "Landmine", "FireworkFountain",
            // 炸掉地形的（沙/雪/泥沙等“射出后落成方块”的）
            "DirtBall", "MudBall", "SandBallFalling", "SandBallGun",
            "EbonsandBallFalling", "EbonsandBallGun", "CrimsandBallFalling",
            "CrimsandBallGun", "PearlsandBallFalling", "PearlsandBallGun",
            "SiltBall", "SlushBall", "SnowBallFriendly", "SnowBallHostile",
            "SnowBallSand", "WetSandBallFalling", "WetSandBallGun",
            "WetEbonsandBallFalling", "WetEbonsandBallGun", "WetCrimsandBallFalling",
            "WetCrimsandBallGun", "WetPearlsandBallFalling", "WetPearlsandBallGun",
            "WetSiltBall", "WetSlushBall",

            // ---- 会连锁生成新弹幕 / 召唤爆发的（换过去后行为不可控）----
            "CrystalBullet", "CrystalShard",
            "Bee", "GiantBee", "Hornet", "HornetStinger", "Wasp",
            "ChlorophyteBullet", "ChlorophyteParty",
            "VampireKnife", "Bat", "Clentaminator", "ClentaminatorBlue", "ClentaminatorGreen",
            "ClentaminatorPurple", "ClentaminatorRed", "ClentaminatorDarkBlue",
            "ToxicCloud", "ToxicCloud2", "ToxicCloud3", "ToxicBubble",
            "DryadWard", "BoneGloveProj",
            "SporeGas", "SporeGas2", "SporeGas3", "ShroomiteDiggingClaw",
            "DiggingMoleMinecart",

            // ---- 特殊机制：药水/弹药类工具、钩爪、墓碑 ----
            "GrapplingHook", "SkeletronHand", "GemHook", "Tombstone",
            "Shuriken", "ThrowingKnife", "PoisonedKnife", "Bone",
        };

        private static readonly HashSet<int> alwaysExcluded = BuildExcludedSet();

        private static HashSet<int> BuildExcludedSet()
        {
            var set = new HashSet<int>();
            foreach (string name in ExcludedNames)
            {
                try
                {
                    FieldInfo field = typeof(ProjectileID).GetField(
                        name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    if (field == null || field.FieldType != typeof(short) && field.FieldType != typeof(int))
                    {
                        continue;
                    }
                    object value = field.GetValue(null);
                    if (value == null)
                    {
                        continue;
                    }
                    int id = Convert.ToInt32(value);
                    if (id > 0)
                    {
                        set.Add(id);
                    }
                }
                catch (Exception)
                {
                }
            }
            return set;
        }

        /// <summary>
        /// 破坏性弹幕的关键词（按弹幕显示名匹配，中英文都过一遍）。
        /// 「雷管 / 炸弹 / 手雷 / 地雷 / 火箭 …」这类东西一旦被换出来就会炸地形，
        /// 用户明确要求禁掉，所以这里按名字做第二道保险，不依赖手写 ID 名单。
        /// </summary>
        private static readonly string[] DestructiveKeywords =
        {
            "雷管", "炸弹", "手雷", "地雷", "爆炸", "火箭", "集束", "导弹", "炮弹",
            "炸药", "nuke",
            "dynamite", "bomb", "grenade", "mine", "explosive", "rocket", "missile",
        };

        /// <summary>名字命中破坏性关键词而被拒绝的类型（诊断用）。</summary>
        private static readonly List<int> destructiveRejected = new List<int>();

        /// <summary>名字命中破坏性关键词而被拒绝的弹幕名列表。</summary>
        internal static List<string> ListDestructiveRejected()
        {
            var names = new List<string>();
            foreach (int type in destructiveRejected)
            {
                names.Add(NameOf(type));
            }
            return names;
        }

        /// <summary>名字命中「滞留型」关键词而被拒绝的类型（诊断用）。</summary>
        private static readonly List<int> nameKeywordRejected = new List<int>();

        /// <summary>名字命中「滞留型」关键词而被拒绝的弹幕名列表。</summary>
        internal static List<string> ListNameKeywordRejected()
        {
            var names = new List<string>();
            foreach (int type in nameKeywordRejected)
            {
                names.Add(NameOf(type));
            }
            return names;
        }

        /// <summary>弹幕显示名是否命中关键词表（服务器当前语言的名字，中英文关键词都能撞上）。</summary>
        internal static bool MatchesNameKeyword(int type, List<string> keywords, out string hit)
        {
            hit = null;
            if (keywords == null || keywords.Count == 0)
            {
                return false;
            }
            string name = ProjectileDisplayName(type);
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

        /// <summary>取弹幕显示名（拿不到返回空串）。</summary>
        private static string ProjectileDisplayName(int type)
        {
            try
            {
                string name = Lang.GetProjectileName(type).Value;
                return name ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 是否召唤物 / 哨兵类弹幕。
        ///
        /// ⚠️ 本版本（1.4.5.8）的 `ProjectileID.Sets` 里**没有** `minion`/`sentry`/`turret` 这三个字段
        /// （`ppr audit` 实测），所以必须用实际存在的那些集合，否则判断恒为 false、召唤物照样被随机。
        /// </summary>
        internal static bool IsMinionLike(int type)
        {
            return SetFlag("minion", type)
                || SetFlag("sentry", type)
                || SetFlag("turret", type)
                || SetFlag("MinionShot", type)
                || SetFlag("SentryShot", type)
                || SetFlag("TurretFeature", type)
                || SetFlag("IsADD2Turret", type)
                || SetFlag("StardustDragon", type)
                || SetFlag("MinionTargetingFeature", type)
                || SetFlag("TrackMinionSpawnFromItemUse", type);
        }

        /// <summary>
        /// 判断一件物品是不是「召唤武器 / 哨兵杖 / 宠物 / 照明宠物」——
        /// 这类物品射出的弹幕从源头就不该进随机池（沙漠虎、闪耀史莱姆气球就是这么漏进来的）。
        /// 字段用反射读，避免版本字段名差异导致编译不过。
        /// </summary>
        private static bool IsSummonLikeItem(Item item)
        {
            if (item == null)
            {
                return false;
            }
            if (ItemBoolField(item, "summon") || ItemBoolField(item, "sentry"))
            {
                return true;
            }
            try
            {
                int buff = item.buffType;
                if (buff > 0 && buff < Main.vanityPet.Length
                    && (Main.vanityPet[buff] || Main.lightPet[buff]))
                {
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static readonly Dictionary<string, FieldInfo> itemBoolFields =
            new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

        private static bool ItemBoolField(Item item, string name)
        {
            try
            {
                FieldInfo field;
                if (!itemBoolFields.TryGetValue(name, out field))
                {
                    field = typeof(Item).GetField(name, BindingFlags.Public | BindingFlags.Instance);
                    itemBoolFields[name] = field;
                }
                if (field == null || field.FieldType != typeof(bool))
                {
                    return false;
                }
                return (bool)field.GetValue(item);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 按关键词在「全部弹幕」里找（含没进池的），诊断用。
        /// 输出「名字(ID) 结论 aiStyle 尺寸 timeLeft」，方便确认玩家报的名字到底是哪个弹幕、
        /// 以及它是被哪条规则挡掉的。
        /// </summary>
        internal static List<string> FindByName(string keyword, ProjectileSettings settings)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(keyword))
            {
                lines.Add("用法：/ppr find <关键词>");
                return lines;
            }

            List<int> pool = Build(settings, out _);
            var poolSet = new HashSet<int>(pool);
            const int limit = 25;

            for (int type = 1; type < ProjectileID.Count && lines.Count < limit; type++)
            {
                string name = ProjectileDisplayName(type);
                if (name.Length == 0 || name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string verdict;
                if (poolSet.Contains(type))
                {
                    verdict = "★在池内";
                }
                else if (!settings.IncludeMinions && IsMinionLike(type))
                {
                    verdict = "召唤物/哨兵";
                }
                else if (alwaysExcluded.Contains(type))
                {
                    verdict = "保命名单";
                }
                else if (nameKeywordRejected.Contains(type))
                {
                    verdict = "名字关键词(已拦)";
                }
                else if (settings.ForbidDestructiveProjectiles && IsDestructive(type))
                {
                    verdict = "破坏性";
                }
                else
                {
                    verdict = "其它规则";
                }

                string shape = "";
                try
                {
                    var probe = new Projectile();
                    probe.SetDefaults(type);
                    shape = string.Format(" aiStyle={0} {1}x{2} timeLeft={3} pen={4}",
                        probe.aiStyle, probe.width, probe.height, probe.timeLeft, probe.penetrate);
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

        /// <summary>弹幕名是否命中破坏性关键词。</summary>
        internal static bool IsDestructive(int type)
        {
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
            string lower = name.ToLowerInvariant();
            foreach (string keyword in DestructiveKeywords)
            {
                if (lower.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>列出当前池子里所有命中破坏性关键词的弹幕（诊断/兜底检查用）。</summary>
        internal static List<string> ListDestructiveInPool(ProjectileSettings settings)
        {
            var found = new List<string>();
            foreach (int type in lastBuiltPool)
            {
                if (IsDestructive(type))
                {
                    found.Add(NameOf(type));
                }
            }
            return found;
        }

        /// <summary>保命名单的大小（用于启动日志，确认反射解析成功）。</summary>
        internal static int ExcludedCount
        {
            get { return alwaysExcluded.Count; }
        }

        /// <summary>某个弹幕类型是否已被保命/破坏性名单封禁。</summary>
        internal static bool IsBlacklisted(int type)
        {
            return alwaysExcluded.Contains(type);
        }

        /// <summary>诊断：保命名单里哪些名字在本版本不存在（名字写错的会被列出来）。</summary>
        internal static List<string> ListUnresolvedExcludedNames()
        {
            var missing = new List<string>();
            foreach (string name in ExcludedNames)
            {
                bool found = false;
                try
                {
                    FieldInfo field = typeof(ProjectileID).GetField(
                        name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    found = field != null;
                }
                catch (Exception)
                {
                }
                if (!found)
                {
                    missing.Add(name);
                }
            }
            return missing;
        }

        /// <summary>最近一次构建的拒绝原因统计（诊断用）。</summary>
        private static readonly Dictionary<string, int> lastRejectStats = new Dictionary<string, int>(StringComparer.Ordinal);
        private static int lastScanned;

        internal static string DescribeRejectStats()
        {
            var parts = new List<string>();
            parts.Add("共扫描 " + lastScanned + " 种");
            foreach (KeyValuePair<string, int> pair in lastRejectStats)
            {
                parts.Add(pair.Key + "=" + pair.Value);
            }
            string text = string.Join("；", parts.ToArray());
            if (destructiveRejected.Count > 0)
            {
                var names = new List<string>();
                for (int i = 0; i < destructiveRejected.Count && i < 20; i++)
                {
                    names.Add(NameOf(destructiveRejected[i]));
                }
                text += "；破坏性命中名单=" + string.Join("、", names.ToArray());
            }
            return text;
        }

        private static List<int> lastBuiltPool = new List<int>();

        /// <summary>最近一次构建出的池子（诊断用）。</summary>
        internal static List<int> LastBuiltPool
        {
            get { return lastBuiltPool; }
        }

        private static void Reject(string reason)
        {
            int current;
            lastRejectStats.TryGetValue(reason, out current);
            lastRejectStats[reason] = current + 1;
        }

        /// <summary>
        /// Sets 反射缓存：某些版本的 ProjectileID.Sets 里带了这些集合，
        /// 有就用来兜底过滤（没有就跳过，不影响运行）。
        /// </summary>
        private static readonly string[] SetFieldNames =
        {
            "minion", "sentry", "turret", "noLiquidDistort", "MeleeSpeedSword",
            "IsAGrapple", "Grapple", "IsWhip", "IsAWhip", "Whip",
        };

        private static readonly Dictionary<string, FieldInfo> setFields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        private static bool setFieldsResolved;

        private static void ResolveSetFields()
        {
            if (setFieldsResolved)
            {
                return;
            }
            setFieldsResolved = true;

            Type sets = null;
            try
            {
                Type idType = typeof(ProjectileID);
                sets = idType.GetNestedType("Sets", BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception)
            {
                sets = null;
            }
            if (sets == null)
            {
                return;
            }

            foreach (string name in SetFieldNames)
            {
                try
                {
                    FieldInfo field = sets.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (field != null && field.FieldType == typeof(bool[]))
                    {
                        setFields[name] = field;
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private static bool SetFlag(string name, int type)
        {
            ResolveSetFields();
            FieldInfo field;
            if (!setFields.TryGetValue(name, out field))
            {
                return false;
            }
            try
            {
                var array = field.GetValue(null) as bool[];
                return array != null && type >= 0 && type < array.Length && array[type];
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>判断某个弹幕类型能否作为「玩家随机弹幕」的候选。</summary>
        internal static bool IsSuitable(int type, ProjectileSettings settings)
        {
            if (type <= 0 || type >= ProjectileID.Count)
            {
                Reject("越界");
                return false;
            }
            if (alwaysExcluded.Contains(type))
            {
                Reject("保命名单");
                return false;
            }
            if (settings.ForbidDestructiveProjectiles && IsDestructive(type))
            {
                Reject("破坏性(按名字)");
                if (!destructiveRejected.Contains(type))
                {
                    destructiveRejected.Add(type);
                }
                return false;
            }
            if (settings.BlacklistProjectiles != null && settings.BlacklistProjectiles.Contains(type))
            {
                Reject("配置黑名单");
                return false;
            }
            // 名字关键词拉黑：滞留型弹幕（飞猪存钱罐 / 钱币槽 / 碎岩龟…）——
            // 这类弹幕 timeLeft 看着正常，但换出来会一直挂在场上，只能按名字拦。
            string keywordHit;
            if (MatchesNameKeyword(type, settings.BlacklistNameKeywords, out keywordHit))
            {
                Reject("名字关键词(" + keywordHit + ")");
                if (!nameKeywordRejected.Contains(type))
                {
                    nameKeywordRejected.Add(type);
                }
                return false;
            }

            // 召唤物 / 哨兵 / 宠物类直接排除（Sets 反射 + 弹幕自身字段，双保险）
            if (!settings.IncludeMinions && IsMinionLike(type))
            {
                Reject("召唤物/哨兵");
                return false;
            }
            if (SetFlag("IsAGrapple", type) || SetFlag("Grapple", type))
            {
                Reject("钩爪");
                return false;
            }
            if (!settings.IncludeMeleeExtenders)
            {
                if (SetFlag("IsWhip", type) || SetFlag("IsAWhip", type) || SetFlag("Whip", type))
                {
                    Reject("鞭子");
                    return false;
                }
            }

            Projectile probe;
            try
            {
                probe = new Projectile();
                probe.SetDefaults(type);
            }
            catch (Exception ex)
            {
                Reject("SetDefaults异常:" + ex.GetType().Name);
                return false;
            }
            // 弹幕自身的召唤物标记（SetDefaults 之后才可读）
            if (!settings.IncludeMinions && (probe.minion || probe.sentry))
            {
                Reject("召唤物/哨兵(字段)");
                return false;
            }
            try
            {
                if (probe.type != type)
                {
                    Reject("类型不匹配");
                    return false;
                }
                // 注意：Terraria 1.4.5.8 的 Projectile.SetDefaults 不再给弹幕设置伤害
                //（木箭、子弹、火球的 damage 都是 0），伤害一律由武器/调用方传入。
                // 所以这里不能拿 damage<=0 当「无效弹幕」判据 —— 那会把 98% 的弹幕误杀。
                // 「aiStyle==0」的弹幕在玩家手里不会自己动，才是真正要排除的。
                if (probe.aiStyle == 0)
                {
                    Reject("aiStyle=0");
                    return false;
                }
                // 体积过大的（巨石、月总砸地之类）
                if (probe.width > settings.MaxProjectileSize || probe.height > settings.MaxProjectileSize)
                {
                    Reject("体积过大");
                    return false;
                }
                // 瞬灭的特效弹幕
                if (probe.timeLeft < settings.MinProjectileTimeLeft)
                {
                    Reject("存活过短");
                    return false;
                }
                // 攻击型弹幕在 1.4.5.8 里基本都是默认 hostile=true，这不能作为排除依据。
                // ExcludeHostileTypes=true 时才按它过滤（保守选项）。
                if (settings.ExcludeHostileTypes && probe.hostile)
                {
                    Reject("敌对类型");
                    return false;
                }
                // 穿透无限 + 存活极长的（持续性领域类），也不适合
                if (probe.penetrate == -1 && probe.timeLeft > 3000)
                {
                    Reject("穿透无限且超长存活");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Reject("属性读取异常:" + ex.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 反向收集：遍历所有物品的 Item.shoot，得到「玩家武器/弹药真正会射出的弹幕」集合。
        /// 这是 1.4.5.8 上确定「玩家弹幕」范围最可靠的办法 ——
        /// 弹幕自身没有伤害也没有归属标记，只有射它的物品能说明它属于谁。
        /// </summary>
        internal static HashSet<int> CollectFromItems(out int itemCount)
        {
            var set = new HashSet<int>();
            itemCount = 0;
            int count;
            try
            {
                count = ItemID.Count;
            }
            catch (Exception)
            {
                count = 0;
            }
            for (int id = 1; id < count; id++)
            {
                try
                {
                    var item = new Item();
                    item.SetDefaults(id);
                    itemCount++;
                    // 召唤武器 / 哨兵杖 / 宠物 / 照明宠物射出的弹幕从源头就不收
                    // （沙漠虎、闪耀史莱姆气球这类就是这么混进池子的）
                    if (IsSummonLikeItem(item))
                    {
                        continue;
                    }
                    int shoot = item.shoot;
                    if (shoot > 0 && shoot < ProjectileID.Count)
                    {
                        set.Add(shoot);
                    }
                }
                catch (Exception)
                {
                }
            }
            return set;
        }

        /// <summary>
        /// 构建弹幕池。优先级：
        ///   1. 配置白名单（非空时以它为准）
        ///   2. CollectFromItems：玩家武器/弹药反向收集（推荐）
        ///   3. 全量遍历 + 规则过滤
        /// 三条路径都会再过一遍 IsSuitable（黑名单、召唤物、aiStyle 等）。
        /// </summary>
        internal static List<int> Build(ProjectileSettings settings, out string describe)
        {
            var pool = new List<int>();
            bool useWhitelist = settings.WhitelistProjectiles != null && settings.WhitelistProjectiles.Count > 0;

            lastRejectStats.Clear();
            lastScanned = 0;
            destructiveRejected.Clear();

            if (useWhitelist)
            {
                foreach (int type in settings.WhitelistProjectiles)
                {
                    lastScanned++;
                    if (IsSuitable(type, settings) && !pool.Contains(type))
                    {
                        pool.Add(type);
                    }
                }
                describe = string.Format("白名单模式：配置 {0} 项 -> 通过校验 {1} 种",
                    settings.WhitelistProjectiles.Count, pool.Count);
            }
            else if (settings.CollectFromItems)
            {
                int itemCount;
                HashSet<int> fromItems = CollectFromItems(out itemCount);
                var candidates = new List<int>(fromItems);
                candidates.Sort();
                foreach (int type in candidates)
                {
                    lastScanned++;
                    if (IsSuitable(type, settings))
                    {
                        pool.Add(type);
                    }
                }
                describe = string.Format("武器反向收集模式：扫描 {0} 件物品 -> 玩家弹幕 {1} 种 -> 可用 {2} 种",
                    itemCount, fromItems.Count, pool.Count);
            }
            else
            {
                for (int type = 1; type < ProjectileID.Count; type++)
                {
                    lastScanned++;
                    if (IsSuitable(type, settings))
                    {
                        pool.Add(type);
                    }
                }
                describe = string.Format("自动模式：共 {0} 种弹幕 ID，通过 {1} 种", ProjectileID.Count, pool.Count);
            }

            lastBuiltPool = pool;
            return pool;
        }

        /// <summary>审计当前池子：存活时间分布 + aiStyle 分布，用于判断池子内容是否正常。</summary>
        internal static List<string> Audit(ProjectileSettings settings)
        {
            List<int> pool = Build(settings, out _);
            var lines = new List<string>();

            int veryLong = 0;
            int longish = 0;
            var aiHistogram = new Dictionary<int, int>();
            var worst = new List<string>();

            foreach (int type in pool)
            {
                try
                {
                    var probe = new Projectile();
                    probe.SetDefaults(type);
                    if (probe.timeLeft > 1800)
                    {
                        veryLong++;
                        if (worst.Count < 15)
                        {
                            worst.Add(NameOf(type) + ":" + probe.timeLeft);
                        }
                    }
                    else if (probe.timeLeft > 900)
                    {
                        longish++;
                    }
                    int count;
                    aiHistogram.TryGetValue(probe.aiStyle, out count);
                    aiHistogram[probe.aiStyle] = count + 1;
                }
                catch (Exception)
                {
                }
            }

            lines.Add(string.Format("池内 {0} 种；存活 >1800 帧的 {1} 种，900~1800 帧的 {2} 种",
                pool.Count, veryLong, longish));
            var aiParts = new List<string>();
            foreach (KeyValuePair<int, int> pair in aiHistogram)
            {
                aiParts.Add("ai" + pair.Key + "=" + pair.Value);
            }
            aiParts.Sort();
            lines.Add("aiStyle 分布：" + string.Join(" ", aiParts.ToArray()));
            if (worst.Count > 0)
            {
                lines.Add("超长存活样例：" + string.Join("、", worst.ToArray()));
            }
            return lines;
        }

        /// <summary>列出 ProjectileID.Sets 里所有 bool[] 字段名（诊断用，方便挑过滤器）。</summary>
        internal static List<string> ListBoolSetFields()
        {
            var names = new List<string>();
            try
            {
                Type sets = typeof(ProjectileID).GetNestedType("Sets", BindingFlags.Public | BindingFlags.NonPublic);
                if (sets == null)
                {
                    names.Add("(找不到 ProjectileID.Sets)");
                    return names;
                }
                foreach (FieldInfo field in sets.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (field.FieldType == typeof(bool[]))
                    {
                        names.Add(field.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                names.Add("异常：" + ex.GetType().Name);
            }
            names.Sort();
            return names;
        }

        /// <summary>抽查几个破坏性弹幕名，确认它们确实被封禁（诊断用）。</summary>
        internal static string DescribeBlacklistSample()
        {
            string[] checkNames =
            {
                "Dynamite", "StickyDynamite", "Bomb", "StickyBomb", "BouncyBomb",
                "HappyBomb", "BombFish", "Grenade", "StickyGrenade", "ExplosiveBunny",
                "RocketI", "RocketII", "RocketIII", "RocketIV", "ProximityMineI",
                "ExplosiveBullet", "ExplosiveJackOLantern", "Landmine", "ScarabBomb",
                "DirtBomb", "SandBallGun", "SnowBallFriendly", "Beenade",
            };
            var parts = new List<string>();
            foreach (string name in checkNames)
            {
                try
                {
                    FieldInfo field = typeof(ProjectileID).GetField(
                        name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    if (field == null)
                    {
                        parts.Add(name + "=本版本无此弹幕");
                        continue;
                    }
                    int id = Convert.ToInt32(field.GetValue(null));
                    parts.Add(name + "#" + id + (alwaysExcluded.Contains(id) ? "=已禁" : "=❌未禁"));
                }
                catch (Exception)
                {
                    parts.Add(name + "=解析失败");
                }
            }
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>只统计、不改动，用于 /ppr pool 命令。</summary>
        internal static string RebuildDescription(ProjectileSettings settings)
        {
            string describe;
            Build(settings, out describe);
            return describe;
        }

        /// <summary>诊断一个弹幕类型：SetDefaults 后的关键属性 + ContentSamples 样本 + 过滤结论。</summary>
        internal static string Probe(int type, ProjectileSettings settings)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("ID ").Append(type).Append(" (").Append(NameOf(type)).Append(") ");

            try
            {
                var probe = new Projectile();
                probe.SetDefaults(type);
                sb.Append(string.Format("| SetDefaults: type={0} dmg={1} aiStyle={2} {3}x{4} timeLeft={5} hostile={6} friendly={7} pen={8} name={9}",
                    probe.type, probe.damage, probe.aiStyle, probe.width, probe.height,
                    probe.timeLeft, probe.hostile, probe.friendly, probe.penetrate, probe.Name));
            }
            catch (Exception ex)
            {
                sb.Append("| SetDefaults 异常: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            }

            try
            {
                var sample = Terraria.ID.ContentSamples.ProjectilesByType.ContainsKey(type)
                    ? Terraria.ID.ContentSamples.ProjectilesByType[type]
                    : null;
                if (sample == null)
                {
                    sb.Append(" | ContentSamples: 无");
                }
                else
                {
                    sb.Append(string.Format(" | Sample: dmg={0} aiStyle={1} {2}x{3} timeLeft={4} hostile={5}",
                        sample.damage, sample.aiStyle, sample.width, sample.height, sample.timeLeft, sample.hostile));
                }
            }
            catch (Exception ex)
            {
                sb.Append(" | ContentSamples 异常: ").Append(ex.GetType().Name);
            }

            bool ok = IsSuitable(type, settings);
            sb.Append(" | 可入选=").Append(ok ? "是" : "否");
            return sb.ToString();
        }

        /// <summary>取一个弹幕类型的中文/英文显示名，失败就返回 ID。</summary>
        internal static string NameOf(int type)
        {
            try
            {
                string name = Lang.GetProjectileName(type).Value;
                if (!string.IsNullOrEmpty(name))
                {
                    return name + "(" + type + ")";
                }
            }
            catch (Exception)
            {
            }
            return "#" + type;
        }

        /// <summary>抽样若干类型名，用于启动日志（确认池子的内容是否正常）。</summary>
        internal static string Sample(List<int> pool, int count)
        {
            if (pool == null || pool.Count == 0)
            {
                return "空";
            }
            var picked = new List<string>();
            int step = Math.Max(1, pool.Count / Math.Max(1, count));
            for (int i = 0; i < pool.Count && picked.Count < count; i += step)
            {
                picked.Add(NameOf(pool[i]));
            }
            return string.Join("、", picked.ToArray());
        }
    }
}
