# TShock 弹幕随机化插件（怪物 / 玩家）

给 **Terraria 1.4.5.8 + TShock 6.2.1** 用的一对弹幕随机化插件，故意拆成两个各干一件事 ——
"怪物弹幕"和"玩家弹幕"互不干扰，出问题也好单独开关。

| 插件 | 干什么 | 命令 | 权限 |
|---|---|---|---|
| `MonsterProjectileRandomizer` | 怪物打出的弹幕**随机换种类**，伤害严格保持原版（陷阱打 100，射出来还是 100） | `/mp`（`/monsterprojectile`） | `monsterprojectile.admin` |
| `PlayerProjectileRandomizer` | 玩家打出的弹幕**随机换种类**，伤害**继承手持武器**（1.4.5.8 里弹幕自身无伤害，伤害由武器传入） | `/ppr`（`/playerprojectile`） | `playerprojectile.admin` |

编译好的 dll 在各自的 `dist/Release/`，丢进 TShock 的 `ServerPlugins/` 重启即可。

## 环境要求

- Terraria 服务端 **1.4.5.8**（OTAPI 1.4.5.8 / TerrariaServer 6.1.0）
- **TShock 6.1.0+**（线上实测 6.2.1 正常）
- .NET 9 运行时；编译需要 .NET SDK 9 或更高

---

## ⚠️ 手机端（PE）崩溃：根因与黑名单

**这是本仓库最值得看的一段。**

怪物弹幕随机化**只换弹幕类型、不会动 `ai[]` 数据**。而 Terraria 手机版有一批弹幕会把
`ai[0]` / `ai[1]` 当作**实体索引**（玩家 / NPC / 弹幕的下标）来用：

> 官方论坛 Mobile Bug Reports（已标记 REPORTED）
> *Projectiles That Home in or Target Entities May Crash The Game*
>
> 如果 `ai[]` 里的值非法（**大于 200、小于 0、或 0.2 这种非整数**），**手机客户端会直接崩溃**。
> **PC 端因为有 try-catch（`ignoreErrors = true`）只会抛 IndexOutOfArray、不会崩。**
>
> <https://forums.terraria.org/index.php?threads/projectiles-that-home-in-or-target-entities-may-crash-the-game.142204/>

原弹幕的 `ai[]` 里存的通常是计数器、角度、计时值（比如 0.2，或者 >200 的计数值）。
一旦被随机成"把 ai 当实体索引"的弹幕 → 索引非法 → **PC 只报错、手机直接闪退**。
这就是"PC 端测不出来、PE 端一崩一片"的原因。

**本仓库默认黑名单已经排除了全部 24 个这类弹幕。** 做法是反编译 `Terraria.Projectile`，
找出所有 `Main.player[(int)ai[...]]` / `Main.npc[(int)ai[...]]` / `Main.projectile[(int)ai[...]]`
的写法，映射到 **14 个危险 aiStyle**（18 / 65 / 70 / 79 / 80 / 82 / 83 / 84 / 85 / 89 / 109 / 110 / 112 / 129），
再用 `Terraria.Projectile.mfwh_SetDefaults` 建出 `type → aiStyle` 表交叉得到：

```
44, 45, 263, 274, 385, 405, 447, 448, 452, 454, 455, 456, 461, 490,
537, 582, 584, 590, 632, 642, 644, 659, 836, 1092
```

其中**确实落在怪物池里的有 10 个**：

| ID | 名称 | aiStyle |
|---|---|---|
| 385 | 鲨鱼旋风矢（官方帖点名的 Sharknado Bolt） | 65 |
| 447 | 火星死亡射线 | 79 |
| 452 | 幻影眼 | 82 |
| 454 | 幻影球 | 83 |
| 455 | 幻影死亡射线 | 84 |
| 537 | 星尘激光 | 84 |
| 456 | 月蛭 | 85 |
| 490 | 拜月教仪式 | 89 |
| 836 | 蒲公英种子 | 112 |
| 1092 | 图书管理员骷髅书 | 18 |

> **注意**：这是"配置层规避"，不是根治。
> 更彻底的做法是在随机化换型时把 `ai[0]` / `ai[1]` 清零（0 是合法索引），
> 这样池子里有没有这类弹幕都安全。本仓库暂未实现该改动。

---

## 巨石类伤害（aiStyle == 25）

1.4.5.8 里滚动巨石的 `SetDefaults` **根本不设置 `damage`**（模板值就是 0），原版巨石的伤害是
**生成时按难度传进去的** —— 官方 Wiki 的 [Boulder](https://terraria.wiki.gg/wiki/Boulder) 页面写着对玩家
**140 / 280 / 420** 分别对应经典 / 专家 / 大师（基础值 140，对 NPC 是 70）。

所以"巨石用自身伤害"这条路走不通：换型后取到的模板值就是 **0**（实测日志里
`种类 670->99，伤害 10->0` 这类记录有 1277 条）。

但反过来**直接套 140 也不对** —— 那会把小怪的 10 点伤害弹幕放大成 140（实测 `10->140`
出现过 630 次、`30->140` 出现过 2034 次，最高放大 14 倍）。

**最终做法**：巨石默认**与其它弹幕一致，继承「原版那条弹幕的伤害」**，不做特殊处理：

| 配置 | 默认 | 含义 |
|---|---|---|
| `BoulderDamage` | **`0`** | **推荐**。巨石继承「原版那条弹幕的伤害」，不会放大低伤害弹幕 |
| | `140` | 巨石固定用这个基础值（游戏按难度自动乘倍率）。**注意**：此时低伤害弹幕变巨石会被拉高（`10->140`），属于旧行为 |

> 代码上巨石**不再**走"自身伤害"分支（`ProjectileRandomizer.cs`），只保留 `BoulderDamage > 0` 时的固定值覆盖。

---

## 编译

```bash
# 方式一：仓库里没有 libs/ —— 自动回退 NuGet 包 TShock 6.1.0，clone 下来直接能编
dotnet build monster_projectile_randomizer/src/MonsterProjectileRandomizer/MonsterProjectileRandomizer.csproj -c Release
dotnet build player_projectile_randomizer/src/PlayerProjectileRandomizer/PlayerProjectileRandomizer.csproj -c Release
```

```bash
# 方式二：把服务器上的三个 dll 放进 <插件目录>/libs/ 再编译
#   libs/TShockAPI.dll   libs/OTAPI.dll   libs/TerrariaServer.dll
# 存在 libs 时 csproj 用本地引用，产物与线上运行环境完全同版本
```

产物统一输出到 `<插件目录>/dist/Release/<AssemblyName>.dll`（`DebugType=none`，不产 pdb）。

## 常用命令

两个插件的子命令基本对称（控制台执行时不加斜杠）：

| 命令 | 作用 |
|---|---|
| `status` | 查看开关、池子种类数、已随机化条数、最近错误 |
| `pool` | 重建池子，列出可用种类数与被黑名单拦下的完整清单 |
| `find <关键词>` | 在**所有**弹幕名里搜，输出「名字(ID) 结论 aiStyle 尺寸 timeLeft hostile」 |
| `reload` | 重载 `tshock/<插件名>.json` —— **改黑名单不用重启** |
| `on` / `off` | 临时启用 / 禁用 |

`find` 是排查最快的入口，结论会直接标 `★在池内` / `ID 黑名单` / `名字关键词` / `其它规则`。
例如玩家反馈"某个弹幕卡住了"，先 `find 关键词` 定位 ID 与拦截状态，再加进
`BlacklistProjectiles` 然后 `reload` 即可，**不需要重新编译**。

配置项里还有一个 `LogRandomizedProjectiles`，打开后每次随机化都会写一行
`[MonsterProjectile] 弹幕 #8：种类 686->203，伤害 500->500`，
排查"到底随机出了什么导致崩"时非常有用。

## 目录结构

```
monster_projectile_randomizer/
  src/MonsterProjectileRandomizer/     源码 + csproj
  dist/Release/                        编译好的 dll
player_projectile_randomizer/
  src/PlayerProjectileRandomizer/      源码 + csproj
  dist/Release/                        编译好的 dll
  PlayerProjectileRandomizer-说明.md    使用说明（含历史变更记录）
```

## 许可

自用插件，随意取用修改。
