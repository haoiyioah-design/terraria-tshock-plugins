# PlayerProjectileRandomizer —— 玩家弹幕随机化插件

> 适配 **Terraria 1.4.5.8 + TShock 6.x**（在 TShock 6.1 / Protocol 1.4.5.8 上实测通过）

## 一、它做什么

1. **随机化玩家弹幕**：你打出去的弹幕会被换成另一种弹幕 —— 第一次可能是火枪弹，第二次就是魔法导弹。
2. **每次都不一样**：同一名玩家连续两发**绝不会**是同一种弹幕（避开「上一发用过的」+「原本的」类型）。
3. **伤害继承手持武器**：迷你鲨射出的高速子弹即便变成魔法导弹，威力仍然是迷你鲨的威力。
4. **破坏性弹幕一律禁掉**：雷管、炸弹、粘性炸弹、手雷、地雷、火箭/集束火箭、爆炸兔、
   各类「炸弹桶」（湿炸弹/熔岩炸弹/蜂蜜炸弹/干炸弹/冻结炸弹/力量炸弹/粘性力量炸弹…）、
   烈火箭、爆炸水晶、电圈导弹、雷管小猫、以及各种「射出去会落成方块」的沙球/雪球 ——
   **既不会被随机换成玩家弹幕，也不会被换成它们**。做法是双保险：手写 ID 名单 + 按弹幕名关键词匹配
   （雷管/炸弹/手雷/地雷/爆炸/火箭/集束/bomb/grenade/rocket…），所以版本新增的爆炸物也能自动挡掉。

第 3 点不只是「想要」，而是 1.4.5.8 的**硬性事实**：这个版本的 `Projectile.SetDefaults`
已经不再给弹幕设置伤害值（木箭、子弹、火球的 `damage` 全是 0），伤害一律由武器在发射时传入。
所以「随机换种类 + 继承武器伤害」是这个版本唯一正确的实现方式。

## 二、安装

把 `PlayerProjectileRandomizer.dll` 放进服务器的 `ServerPlugins/`，重启服务器。
首次启动会自动生成配置文件 `tshock/PlayerProjectileRandomizer.json`。

```
ServerPlugins/PlayerProjectileRandomizer.dll      ← 插件本体（45 KB）
tshock/PlayerProjectileRandomizer.json            ← 首次启动自动生成
```

## 三、命令

命令别名：`/ppr` 或 `/playerprojectile`；控制台不带斜杠（`ppr status`）。

| 命令 | 说明 |
|---|---|
| `/ppr status` | 查看状态、弹幕池大小、伤害规则、诊断信息 |
| `/ppr on` / `/ppr off` | 临时启用 / 禁用 |
| `/ppr reload` | 重载配置文件（立即生效，不用重启） |
| `/ppr pool` | 查看弹幕池大小、过滤统计、抽样 |
| `/ppr audit` | 审计弹幕池内容（存活时间 / aiStyle 分布、可用 Sets 标记） |
| `/ppr excl` | 查看破坏性弹幕封禁名单状态（含按名字拦下的清单） |
| `/ppr rescan` | 立刻重扫场上玩家弹幕 |
| `/ppr probe <ID>` | 诊断某个弹幕类型为什么被过滤（可一次查多个） |
| `/ppr test [次数]` | **自检**：连续造几条弹幕，验证「每次不同 + 伤害继承」 |

权限：`playerprojectile.admin`（会自动授予 `superadmin` 组）。

## 四、配置项

```jsonc
{
  "Settings": {
    "Enabled": true,                    // 总开关
    "RandomizeProjectileType": true,    // 是否随机换弹幕种类
    "KeepWeaponDamage": true,           // 伤害继承手持武器（核心，别关）
    "RandomizeProjectileDamage": false, // 在继承基础上再做倍率浮动
    "DamageMinMultiplier": 0.5,
    "DamageMaxMultiplier": 3.0,

    "AvoidRepeatingType": true,         // 保证「这次和上次不一样」
    "SameAttackWindowMs": 120,          // 「同一发」的时间判据（毫秒）

    "KeepOriginalSpeed": true,          // 保留原弹幕速度（高速子弹换了外形还是高速）
    "KeepOriginalKnockback": true,      // 保留原弹幕击退

    "IncludeHostile": true,             // 玩家打出的敌对标记弹幕也随机
    "ExcludeHostileTypes": false,       // 建池时排除标记为 hostile 的弹幕（会砍掉大部分花样）
    "IncludeMinions": false,            // 是否动召唤物弹幕（默认不动）
    "IncludeMeleeExtenders": false,     // 是否动鞭子弹幕（默认不动）

    "CollectFromItems": true,           // 用武器/弹药反向收集来确定「玩家弹幕」范围（推荐）
    "ForbidDestructiveProjectiles": true, // 禁掉破坏性弹幕（雷管/炸弹/手雷/火箭/集束…）【强烈建议别关】
    "MaxProjectileSize": 40,            // 池子体积上限（像素）
    "MinProjectileTimeLeft": 1,         // 池子存活时间下限（帧）
    "WhitelistProjectiles": [],         // 白名单：非空则只用这些 ID
    "BlacklistProjectiles": [],         // 黑名单：这些 ID 永不出现

    "HeartbeatIntervalMs": 100,         // 兜底扫描间隔
    "LogRandomizedProjectiles": false,  // 每换一条弹幕写一行日志
    "NotifyOnWorldLoad": true,          // 玩家进服提示
    "LogPoolOnStartup": true            // 启动时打印池子规模与抽样
  }
}
```

### 关于「同一发」判定

判定分两级，优先用武器使用动画（`player.itemAnimation`）：

- 同一次攻击里射出的多颗弹幕（霰弹枪一枪 5 颗、魔法导弹一次多发）动画值相同 → **共用同一种弹幕**，
  不会一枪散出 5 种东西；
- 下一次开火动画重新开始 → **换成另一种弹幕**。

连射类武器（迷你鲨、各种速射枪）每次开火都会换新花样，符合「每次弹幕都不同」的要求。

### 关于弹幕池

默认用 **武器反向收集**：遍历全部 6195 件物品的 `Item.shoot`，得到「玩家武器/弹药真的会射出的弹幕」
（1.4.5.8 上共 632 种），再过滤掉：

- 召唤物 / 哨兵 / 鞭子 / 钩爪（另有开关）
- 体积 > 40 像素的（巨石、月主砸地之类）
- `aiStyle == 0` 的（在玩家手里不会自己动）
- 穿透无限 + 超长存活的（持续性领域类）
- **保命名单**（143 项）：炸弹/手雷/雷管、水晶子弹、蜜蜂类、沙子/雪球类、
  以及月主/天顶剑/终极棱镜等纯光束特效 —— 这些换过去会连锁生成、行为不可控或让服务器凭空召唤东西

实测结果：**632 种 → 可用 176 种**。想要更多花样就把 `MaxProjectileSize` 放宽、
或把 `CollectFromItems` 关掉后自己用 `WhitelistProjectiles` 指定。

## 五、自检（在服务器控制台就能跑）

```
ppr test 8
```

输出示例（真实 1.4.5.8 服务端运行结果）：

```
[自检] 弹幕池 176 种（武器反向收集模式：扫描 6195 件物品 -> 玩家弹幕 632 种 -> 可用 176 种）
[自检] 第 1 发：子弹(14)(武器伤害30) -> 裂天剑(660)(实际伤害30)  换种类=是
[自检] 第 2 发：火球(258)(武器伤害45) -> 标枪(507)(实际伤害45)   换种类=是
[自检] 第 3 发：魔法飞弹(16)(武器伤害60) -> 空中祸害(710)(实际伤害60)  换种类=是
...
[自检] 【结果】成功造出 8 发；出现 8 种不同弹幕；连续两发相同：无
[自检] 【结果】伤害继承：每发都等于当时的武器伤害 ✔
[自检] 【结果】逐发伤害对照：期望 [30,45,60,75,90,105,120,135] 实际 [30,45,60,75,90,105,120,135]
```

## 六、实现要点（给后续维护者）

- **驱动方式**：拦 `NetSendData` 的 `ProjectileNew` 包（在数据发给客户端**之前**就把弹幕换好），
  外加 `Timer` 心跳 + `Main.QueueMainThreadAction` 兜底。
  TShock 6 环境里 `GamePostUpdate` 依赖的 OTAPI HookEvents 不触发，不能拿它当核心驱动。
- **换类型的正确姿势**：先快照中心点、速度、击退、owner、`Projectile.key`，`SetDefaults(新类型)`
  之后再全部写回，并按中心点重算位置（避免体积变化把弹幕卡进方块）。
  1.4.5.8 删掉了 `Projectile.identity`，改用 `key` 标识弹幕，必须原样带过去（用反射访问，兼容旧版本）。
- **伤害继承**：换类型**前**快照 `projectile.damage`（这就是服务端按手持武器算出的伤害，
  已含职业加成/套装加成），换完**写回**。`damage` 为 0 时兜底读 `player.HeldItem.damage`。
- 弹幕归属只看 `projectile.owner`（`Main.player` 索引）与 `npcProj`，**不看** `projectile.hostile`——
  1.4.5.8 里几乎所有攻击型弹幕默认都是 `hostile = true`，它表示「有攻击判定」，不表示「谁打出来的」。

## 七、源码与编译

```
player_projectile_randomizer/
├── src/PlayerProjectileRandomizer/
│   ├── PlayerProjectileRandomizer.csproj    net9.0，引用 libs 下的 TShockAPI/OTAPI/TerrariaServer
│   ├── Plugin.cs                            TerrariaPlugin 主体、命令、hook
│   ├── PlayerProjectileRandomizer.cs        随机化核心（选型/换型/伤害继承/自检）
│   ├── ProjectilePool.cs                    弹幕池构建与过滤
│   └── Config.cs                            配置模型
├── libs/                                    TShock 6.1 / Terraria 1.4.5.8 程序集
└── dist/Release/PlayerProjectileRandomizer.dll
```

编译：

```powershell
dotnet build player_projectile_randomizer\src\PlayerProjectileRandomizer\PlayerProjectileRandomizer.csproj -c Release
```

## 八、姊妹插件

| 插件 | 负责 |
|---|---|
| `MonsterHealthRandomizer` | 非 Boss 怪物**血量**随机 |
| `MonsterProjectileRandomizer` | 怪物**弹幕**种类随机 |
| **`PlayerProjectileRandomizer`（本插件）** | **玩家弹幕**种类随机 + 伤害继承手持武器 |

三个插件互不重叠：本插件通过 `npcProj` / `owner` 判断只处理玩家弹幕。

> ⚠️ **必须配套更新的坑**：1.4.5.8 里玩家弹幕默认也是 `hostile = true`，
> 所以旧版 `MonsterProjectileRandomizer`（按 `hostile && damage > 0` 筛弹幕）会把玩家弹幕
> 也当成怪物弹幕再随机一次 —— 两个插件互相踩，玩家弹幕会被换两次、伤害会被写成 0。
> 新版 `MonsterProjectileRandomizer` 已加 `IsPlayerProjectile()` 排除（按 `owner` 判断），
> 会同目录一起给出，**两个 dll 必须一起更新**。
