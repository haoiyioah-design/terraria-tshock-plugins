using System;
using System.Collections.Generic;
using TShockAPI.Configuration;

namespace MonsterProjectileRandomizer
{
    /// <summary>
    /// 配置（tshock/MonsterProjectileRandomizer.json）。
    /// 本插件只做一件事：随机化怪物弹幕（种类 + 伤害）。
    ///
    /// 伤害规则（默认）：
    ///   · 换种类后，伤害仍然保持「原版那条弹幕的伤害」—— 陷阱打 100，射出来的东西就是 100。
    ///   · 例外：巨石类（aiStyle==25 的滚动巨石）以及 OwnDamageProjectiles 名单里的弹幕，
    ///     使用它们自身的伤害，不被覆盖。
    ///   · 若同时开启 RandomizeProjectileDamage，则在这个基准上再乘一个随机倍率。
    /// </summary>
    public sealed class ProjectileSettings
    {
        /// <summary>是否启用插件。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>弹幕种类是否随机替换（会改变外观）。</summary>
        public bool RandomizeProjectileType { get; set; } = true;

        /// <summary>
        /// 是否在「保持原版伤害」的基础上再做随机。
        /// 默认 false = 严格保持原版伤害（陷阱 100 -> 射出来还是 100）。
        /// </summary>
        public bool RandomizeProjectileDamage { get; set; } = false;

        /// <summary>
        /// 换种类后是否把伤害写回原版数值（默认 true）。
        /// 关掉的话，伤害会变成「新弹幕自身的默认伤害」——那就是你反馈的旧行为。
        /// </summary>
        public bool KeepOriginalDamage { get; set; } = true;

        /// <summary>
        /// 伤害例外名单：这些弹幕使用自身的伤害，不被「保持原版伤害」覆盖。
        /// 巨石类（aiStyle==25）已自动例外，这里可以再补充（比如某些爆炸弹幕）。
        /// </summary>
        public List<int> OwnDamageProjectiles { get; set; } = new List<int>();

        /// <summary>弹幕种类池。留空 = 自动构建（只挑原版怪物用的弹幕）。</summary>
        public List<int> ProjectileTypePool { get; set; } = new List<int>();

        /// <summary>
        /// 避免连续两次随机到同一种弹幕（让「每次攻击都不一样」更明显）。
        /// 池子太小时会自动放弃该限制。
        /// </summary>
        public bool AvoidRepeatingType { get; set; } = true;

        /// <summary>是否允许随机到巨石类弹幕（aiStyle==25 的滚动巨石，体积大、会滚动碾压）。</summary>
        public bool AllowBoulderProjectiles { get; set; } = true;

        /// <summary>
        /// 按「弹幕显示名关键词」拉黑（中英文各匹配一遍）：这些弹幕**不会被随机选出来**。
        /// 默认挡掉会破坏物块的爆炸物（炸弹/雷管/手雷/地雷/火箭/集束/爆炸）
        /// 和用户点名的三种巨石（熔岩巨石 / 蜘蛛巨石 / 便便巨石）。
        /// 以后要加减，改这个列表即可，也可以直接 /mp reload。
        /// </summary>
        public List<string> BlacklistNameKeywords { get; set; } = new List<string>
        {
            "熔岩巨石", "蜘蛛巨石", "便便巨石", "七彩", "碎岩龟", "墙上飞车", "圣骑士锤",
            "炸弹", "雷管", "手雷", "地雷", "爆炸", "火箭", "集束",
            "bomb", "dynamite", "grenade", "mine", "explosive", "rocket",
        };

        /// <summary>ID 黑名单：这些弹幕类型永远不会出现在随机池里。</summary>
        public List<int> BlacklistProjectiles { get; set; } = new List<int>();

        /// <summary>弹幕伤害下限倍率（仅在 RandomizeProjectileDamage=true 时生效）。</summary>
        public double DamageMinMultiplier { get; set; } = 0.5;

        /// <summary>弹幕伤害上限倍率（仅在 RandomizeProjectileDamage=true 时生效）。</summary>
        public double DamageMaxMultiplier { get; set; } = 3.0;

        /// <summary>心跳间隔（毫秒）：定时全量扫描场上敌对弹幕。</summary>
        public int HeartbeatIntervalMs { get; set; } = 50;

        /// <summary>调试日志：每条被随机化的弹幕写一行日志。</summary>
        public bool LogRandomizedProjectiles { get; set; } = false;

        /// <summary>玩家进服时提示。</summary>
        public bool NotifyOnWorldLoad { get; set; } = true;

        public void Normalize()
        {
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
            if (HeartbeatIntervalMs < 10)
            {
                HeartbeatIntervalMs = 10;
            }
            ProjectileTypePool = DedupeInts(ProjectileTypePool);
            OwnDamageProjectiles = DedupeInts(OwnDamageProjectiles);
            BlacklistProjectiles = DedupeInts(BlacklistProjectiles);
            // ⚠️ Newtonsoft 反序列化 List 属性是「追加到已有集合」而不是替换 ——
            // 不去重的话每次读配置都会把默认关键词再叠一遍（10→20→30…）。
            BlacklistNameKeywords = DedupeStrings(BlacklistNameKeywords);
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

        /// <summary>弹幕伤害规则描述（用于状态输出）。</summary>
        public string DescribeDamage()
        {
            if (RandomizeProjectileDamage)
            {
                return string.Format("原版伤害 x{0:0.##} ~ x{1:0.##}", DamageMinMultiplier, DamageMaxMultiplier);
            }
            if (KeepOriginalDamage)
            {
                return "严格保持原版伤害（巨石类例外）";
            }
            return "使用新弹幕自身的伤害";
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
            // ⚠️ 必须先 Normalize（去重）再写盘，否则 Newtonsoft 的「List 追加」语义
            // 会把膨胀后的列表直接落盘，每次 reload 都翻一倍。
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