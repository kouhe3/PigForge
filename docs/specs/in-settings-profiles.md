# 规格：IN 设置档（BPLE 的 mod 框架）与「基准档」问题

> 状态：**待拍板**（2026-10-06 立，用户问「是 BPLE 的 MOD 吗」时查清）。
> 差距：`G104`（火箭族的激活语义）、**`G105`（基线：现有 IN 倍率来自 B 档）**。
> 影响面：所有读 `INSettingsBExp.json` 的提取器与由它们写出的内容值。

## 1. 这是什么

`INSettings` / `INFeature` 与 `INSettings{A,AExp,B,BExp,O,OExp}.json`、`INDeclarationSettings{,Exp}.json`
属于 **IN（Innovation）扩展框架**（`INSettings.cs` 里 `using Innovation;`，同族的还有 `INAliasSettings`、
`INAddon*`、`INAppInterface`、`INVersionSelector`）——即 BPLE 里内建的 **mod 框架**，不是原版游戏的平衡数据。

启动流程（证据）：

1. 游戏进一个**版本选择菜单**：`INVersionSelector.cs:44-67`（按数字键 1–4 → `SelectVersion(i)`，回车 → `EnterVersion()`）。
2. `EnterVersion()` → `INSettings.Initialize(m_version)`（`:65-67`）。
3. `INSettings.Load(int version)`（`INSettings.cs:304-320`）按版本挑档：

   | version | 载入文件 |
   |---|---|
   | 0 | **不载入任何档**：全部取 `INDeclarationSettingsExp.json` 的**声明默认值** |
   | 1 | `INSettingsOExp.json` |
   | 2 | `INSettingsAExp.json` |
   | 其他（3） | `INSettingsBExp.json` |

## 2. 三个档的差别（键数）

| 来源 | 键数 | 性质 |
|---|---|---|
| `INDeclarationSettingsExp.json`（声明默认） | **208** | 每个 IN 功能的初始值，**= vanilla 语义** |
| A 档（`INSettingsAExp.json`） | 39 个覆写 | 只开 UI / 编辑器工具（`CommandSystem`、`InfiniteTools`、`UnlockLevels`、`PartPlacementOperation`、`NewCamera`、`RuntimeGameData`…），**不覆写任何物理/战斗倍率** |
| O 档（`INSettingsOExp.json`） | 27 个覆写 | 同上，更少 |
| **B 档**（`INSettingsBExp.json`） | **130 个覆写** | 把 IN 的**全部系统与零件打开**，并**重定物理量**（下面逐条） |

### 2.1 B 档与声明默认的物理/玩法差异（节选，`G105` 的证据）

| 键 | 声明默认（vanilla） | B 档 |
|---|---|---|
| `SwitchableCokeSodaRocket` | **false** | true |
| `RocketForce` / `RocketSpeed` | **1.0** / **1.0** | 2.0 / **∞** |
| `CokeSodaForce` / `CokeSodaSpeed` | **1.0** / **1.0** | 2.0 / 2.0 |
| `AlienCokeForceValue` / `AlienCokeSpeedValue` | **35** / **25** | 150 / **∞** |
| `AlienSodaForceValue` / `AlienSodaSpeedValue` | **150** / **150** | 200 / 200 |
| `RocketExplosionForce` / `RocketExplosionCoolingTime` | **1.0** / 0.0 | 2.0 / 0.1 |
| `TNTExplosionForce` | **1.0** | 2.0 |
| `BalloonForce` | **1.0** | 2.0 |
| `FanSpeed` | **1.0** | 6.0 |
| `RotorSpeed` | **1.0** | 2.0 |
| `PropellerSpeed` | **1.0** | **∞** |
| `ConnectionStrength` | **1.0** | 2.0 |
| `EnginePowerLimit` | **1.0** | 4.0 |
| `TerrainScale` | 1.0 | 8.0 |
| `RotatableTNT` / `EnclosableParts` / `BlasterTNT` | **false** | true |
| `StableSpringConnection` / `StrongSpringConnection` / `SwitchableBoxingGlove` | **false** | true |
| `OffRoadWheel` / `HingePlate` / `NewAlienEgg` / `MetalBox` / `SpecialEggs` / … | **false** | true |
| `TNTExplosionRadius` / `RocketExplosionRadius` | 1.0 / 1.0 | 1.0 / 1.0（相同） |
| `FanForce` / `RotorForce` / `PropellerForce` | 1.0 | 1.0（相同） |

> A 档还会单独开 `OffRoadWheel`、`HingePlate`、`StableHook`、`StableRope`、`UnlockCustomParts`
> ——即**部分 IN 零件在 A 档就存在**，但战斗/物理倍率仍是默认值。

## 3. 对 PigForge 的后果

**部分 IN 零件本身是档位门控的**：`BlasterTNT`、`OffRoadWheel`、`HingePlate`、`SpecialEggs`、`MetalBox`、
`NewAlienEgg` 等在声明默认里是 `false`（`INPartFactoryManager` 据此决定造不造这些件）——即我们目录里
已收录的部分零件只在 A/B 档存在。**未穷举核实**，需要时按同一张表逐件查。

**现有内容值里取自 B 档的那些**（工具读的都是 `INSettingsBExp.json`）：

| 内容 | 现值（= B 档） | 声明默认档（vanilla） |
|---|---|---|
| 风扇 / 螺旋桨 / 旋翼 `maxSpeed`（`tools/bple-fans`） | 18 / ∞ / 14 | **3 / 16 / 7** |
| 气球升力（`tools/bple-lift`） | `0.383333` = 23 N/帧（11.5 × 2.0） | **`0.191667` = 11.5 N/帧** |
| 火箭族 `thrustPerTick` 与速度上限（G101/G104） | 见 G104 | **火箭 `0.8333`/tick、上限 18 m/s；瓶 `0.5833`、上限 10 m/s** |
| TNT / 火箭爆炸倍率 | 25→50（×2.0） | **×1.0** |
| 焊缝强度（`ConnectionStrength`） | ×2.0 | **×1.0（整表减半）** |
| 弹簧 / 拳套的 `Stable/Strong/Switchable*` 分支（ADR-027/028） | 以 B 档为真实现 | 默认档为 false，行为不同 |
| 自动对齐 / 包裹 / 可旋转件（`Rotatable*`、`EnclosableParts`、G96 相关） | 以 B 档为真 | 默认档为 false |

## 4. 已拍板：基准 = 声明默认档（vanilla）

2026-10-06 用户拍板：**PigForge 的 IN 倍率一律取声明默认值（`INDeclarationSettingsExp.json`）**，
即 A/O/版本 0 的语义、也是用户实机所见的零售版行为（火箭一次用品、TNT 不参与自动对齐等）。
B 档（`INSettingsB*.json`）是 mod 的「全功能」档，**不作为任何原版零件的数值来源**。

### 4.1 架构约束（用户 2026-10-06 重申，与既有「变体 = 独立零件」一致）

**不许用 mod 值改写原版零件**：B 档改了哪个原版零件，那个零件就不是原版零件了——要保留 mod 的手感就得
**新增一个零件**，而不是把原版零件的数值改掉。这与本仓库既有的目录架构同源：

- 每个皮肤/效果变体都是**自己的内容条目**（独立 `partTypeId` + `variantOf`/`variantName` 分组，
  `ADR-004`/`ADR-006`、`docs/specs/part-variant-catalog.md`），不是基准件的属性；
- 行为差异走**该条目自己的能力值**（如异形风箱的 `bellows`、拳套的 `glove.distanceY`），
  绝不去改基准件的值；
- 所以本次「回到 vanilla」是**把原版零件的数字恢复成原版的**，B 档的数字不保留在原版零件上；
  若将来要 B 档体验，做法是（a）新增零件，或（b）像原作那样做成可选的档位，而不是覆盖原版值。

**同一个道理的另一面（待办）**：IN 零件本身也是档位门控的——`BlasterTNT`、`OffRoadWheel`、`HingePlate`、
`SpecialEggs`、`MetalBox`、`NewAlienEgg` 等在**声明默认档里不存在**（`false`）。我们目录里已经把它们收作
**独立 part id**（与「不同东西 = 新零件」一致），但要注意它们不是零售版零件；要不要继续收、要不要另开
「IN 扩展件」分类标注，见 §5 待办。

### 4.2 执行分期（每片一次验收）

1. **工具基准切换**：新增共享的 `tools/in-settings/vanilla-settings.mjs`（只读声明默认档），
   `bple-fans` / `bple-lift` 改用它 → 风扇顶速 `18 → 3`、旋翼 `14 → 7`、螺旋桨 `∞ → 16`、
   气球 `23 N/帧 → 11.5 N/帧`（内容 `0.383333 → 0.191667`）；同步受影响的测试与规格数字。
2. **代码常量**：`EnginePowerLimit 4.0 → 1.0`（`GameplayRules.cs:150` 的 40 → 10）、
   `ConnectionStrength 2.0 → 1.0`（`CompoundAssembler.cs:457` 的焊缝 ×2 → ×1）、
   `BalloonConnectionDistance 10 → 5` / `SandbagConnectionDistance 10 → 1`（`ConstructionRules`）。
3. **门控分支**：`EnclosableParts`、`Rotatable*`（G96 的范围）、`Avoidance/TrackingRocket`、
   `StableSpringConnection`/`StrongSpringConnection`/`SwitchableBoxingGlove`（ADR-027/028 的默认分支）
   按 `false` 重审。
4. **火箭 / TNT 的倍率**：与 G101（`tools/bple-rockets`）同批，按声明默认倍率（×1.0）写内容。
5. **记录**：更新受影响规格（`fan-propeller`、`body-defaults`、`spring-joint`、`boxing-glove`、
   `part-variant-catalog`）与 ADR（022/025/027/028、013 的气球升力），差距表 `G105` 收口。

## 5. 参考

- 原版：`Assets/Scripts/Assembly-CSharp/{INSettings,INVersionSelector,INFeature}.cs`；
  `Assets/TextAsset/{INDeclarationSettings,INDeclarationSettingsExp,INSettingsA,INSettingsAExp,INSettingsB,INSettingsBExp,INSettingsO,INSettingsOExp}.json`
- 代码：`tools/bple-fans/extract-fans.mjs`、`tools/bple-lift/`、`tools/bple-springs/`、`tools/bple-power/`
  （全部读 `INSettingsBExp.json`）
- 相关：`tasks/original-vs-implemented.md` 的 `G104`/`G105`；`docs/specs/play-part-switches.md`（火箭族激活语义）；
  `docs/specs/fan-propeller.md` §7；`docs/specs/body-defaults.md`；`ADR-027`/`ADR-028`
