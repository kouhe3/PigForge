# 规格：IN 设置档（BPLE 的 mod 框架）与「基准档」问题

> 状态：**已拍板（基准 = 声明默认档）；§4.2 第 1–2 片已交付，§4.3 门控审查完成、四项待实现**（2026-10-06 第二十轮）。
> 差距：`G104`（火箭族的激活语义）、**`G105`（基线；新拆出 `G106`–`G109`）**。
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

**同一个道理的另一面（已拍板 2026-10-06，G109）**：IN 零件本身也是档位门控的——`BlasterTNT`、`OffRoadWheel`、
`HingePlate`、`SpecialEggs`、`MetalBox`、`NewAlienEgg` 等在**声明默认档里不存在**（`false`）。用户拍板：
**保留并标注为「IN 扩展件」**（不把它们从目录里删掉）。现状核对：目录 284 件里**只有 `52 tnt-blaster`
（`Part_TNT_07_SET`）属于这一类**（其余 283 件都有 vanilla 路径）；它的 curation 记录在
`tools/bple-variants/variant-overrides.json` 的 `extras` 条目（`note` 已写明「BlasterTNT is an IN extension
part (not in GameData.m_customParts)」），目录口径与理由记在 `docs/specs/part-variant-catalog.md`
§「IN 扩展件」。**不新增内容键**：今天没有消费者（建造栏与运行时都照旧用它），加一个没人读的位属死重量；
将来若要让客户端区分展示，再按「内容键必须五处同步」的流程补。

### 4.2 执行分期（每片一次验收）

1. ~~**工具基准切换**~~ —— **已交付**（2026-10-06 第十五轮，`172f330`）：新增共享的 `tools/in-settings/vanilla-settings.mjs`（只读声明默认档），
   `bple-fans` / `bple-lift` 改用它 → 风扇顶速 `18 → 3`、旋翼 `14 → 7`、螺旋桨 `∞ → 16`、
   气球 `23 N/帧 → 11.5 N/帧`（内容 `0.383333 → 0.191667`）；同步受影响的测试与规格数字。
2. ~~**代码常量**~~ —— **已交付**（2026-10-06 第二十轮）：
   - `EnginePowerLimit 4.0 → 1.0`（`GameplayRules.EnginePowerLimit = 1f`）：raw 比上限 `10 × 4 = 40 → 10`，
     上限因子 `40^0.585 = 8.6539 → 10^0.585 = 3.8459`（`Part_EngineSmall_05_SET` 的 5000 现在被截到 3.8459）。
   - `ConnectionStrength 2.0 → 1.0`（`CompoundAssembler.NormalJointStrength 250f → 125f`）：`GameData.asset:101-105`
     的 Normal 与 Weak 同为 125，vanilla 的木↔木 `breakForce` = **250**（B 档 1000）；缝阈值的比值
     `1.0 / 木↔铁 2.9 / 铁↔铁 4.8 / HighlyExtreme 9.6`（B 档口径是 1.0 / 1.7 / 2.4 / 4.8）。
   - `SandbagConnectionDistance = 1` / `BalloonConnectionDistance = 5`（原单个 `AttachmentSearchCells = 10`）：
     `ConstructionRules.{Sandbag,Balloon}AttachmentSearchCells` + `FindAttachmentTarget(..., maxCells)`，
     `GameRoom.BindAttachments` 按 `capabilities.HasBalloon` 选家族。
   - 测试同步：`PowerSystemTests.PowerFactorFollows…`、`CompoundAssemblerTests.{TheSeamThresholdScales…,TheCatalogGivesMetalWelds…}`、
     `JointAndEnclosureTests.{AttachmentSearchStops…,AttachmentSearchAccepts…,ASandbagOnlyReachesTheCellAboveIt,ABalloonReachesFiveCells}`。
3. **门控分支** —— **已审查（结论见 §4.3）**：~~`DynamicPowerSystem`~~（第二十轮之三，`G106`）、~~`Stable/StrongSpringConnection`~~（第二十轮之二，`G107`）、~~`EnclosableParts` 的「可包裹件族」~~（第二十轮之四，`G108`）均已落地；**只剩** IN 档专属零件的目录范围（`G109`，待拍板）。
4. ~~**火箭 / TNT 的倍率**~~ —— **已核对**：火箭族由 `tools/bple-rockets` 按声明默认档写出（G101/G104）；
   TNT 的 `GameplayConfig.TntBlastImpulse = 25f` 与 prefab `m_explosionImpulse: 25` 一致、`TNTExplosionForce` vanilla = 1.0，
   **倍率无待办**。（TNT 的半径 4 vs prefab 8 是另一条差距 `G46`。）
5. **记录** —— 本轮已更新：`ADR-015`、`ADR-012`/`ADR-022`/`ADR-025`/`ADR-027`/`ADR-028` 之外的
   `docs/specs/{weld-compliance,nesting-and-pig-cargo,power-system,spring-joint,part-variant-catalog}.md`、根 `AGENTS.md`、
   `tasks/original-vs-implemented.md`（G105 + 新差距行）；`fan-propeller`/`body-defaults`/`boxing-glove` 里仍有 B 档读数的段落见 §4.3 的处置列。

### 4.3 门控审查（2026-10-06 第二十轮，`INDeclarationSettingsExp.json` = vanilla）

原版读取点由两个只读子代理逐条核对（`INFeature.*` 全树 grep + 逐处 verbatim）。结论与处置：

| IN 键（vanilla 值） | vanilla 行为（出处） | PigForge 现状 | 处置 |
|---|---|---|---|
| `EnginePowerLimit = 1.0` | raw 比上限 `10 × 1 = 10`（`Contraption.cs:545`，两分支同一表达式） | 已改成 1f | **本片交付** |
| `ConnectionStrength = 1.0` | Normal 不翻倍（`Contraption.cs:1494-1503`），木↔木 250 / 木↔铁 725 / 铁↔铁 1200 | 已改（NormalJointStrength 125） | **本片交付** |
| `SandbagConnectionDistance = 1` / `BalloonConnectionDistance = 5` | 逐家族循环上界（`Sandbag.cs:63`、`Balloon.cs:87`） | 已改（逐家族常量） | **本片交付** |
| `DynamicPowerSystem = false` | **走遗留分支**（`Contraption.cs:556-582`）：分母 = 装配期消耗（开关不影响）− `0.9 ×` 每个无接地马达轮消耗，且消耗**不逐帧重算**（`:2626-2649`） | **已落地**（`GameplayRules.RecomputePowerClusters` 每 tick 刷新 + `IsMotorWheelGrounded` 取原版 `m_hasContact = true` 的初值） | **G106 已收口**：两动力轮的车峰值从 12.089 上限抬到实测 **13.6895 m/s**（一轮离地时上限 17.984），见 `docs/specs/power-system.md` §6/§7 |
| `StableSpringConnection = false` / `StrongSpringConnection = false` | 8 个皮肤**全部**走 y 软限位路径（`Spring.cs:99-138`）、质量取 prefab（弹簧 0.3，不强制 1，`:70-76`）、`breakForce = 250`（`:15,26-41`）、`> 3 m` 拉断生效（`:80-92` 不短路） | 内容是 B 档口径：逐皮肤 `joint: bungee/limit`、`breakForce 1200`、`mass 1` | **新差距 `G107`**，需要 `tools/bple-springs` 按 vanilla 重跑 + `SpringProbe` 复核标定（两条 `*EffectiveStiffnessScale` 都是在 1 kg 弹力绳探针格上拟合的） |
| `SwitchableBoxingGlove = false` | 一次性按钮：`Update` 的 else 分支在 `!m_enabled` 时出拳、`m_ShootTime`（prefab 覆写 0.4）后回卷再复位（`SpringBoxingGlove.cs:345-395`） | 内容 `activation: "trigger"`（按下出拳、可重复） | **已一致**（G99 当时按用户实机报告选对了分支，理由从「B 档 toggle」改写为「vanilla 按钮」） |
| `SwitchableWing` / `SwitchableTail` = false | 机翼/尾翼常开（`Wings.cs:106`、`Tail.cs:59`） | `Aerodynamics.cs` 已按此实现 | 无 |
| `SwitchableCokeSodaRocket = false` | 火箭/瓶族一生一次（`Rocket.cs:570-581`） | 已实现（G104） | 无 |
| `EnclosableParts = false` | **只关掉基类** `CanBeEnclosed()`（`BasePart.cs:1148-1165`）；13 个类覆写为 `true`（猪族/蛋/引擎/齿轮箱/TNT/点光/南瓜/拳套/定时炸弹/铰链板/CustomPart），`Frame.CanEncloseParts()` 恒 true，`Frame.Initialize` 的焊接**无门控**。B 档专属的只有 `CanConnectTo(JCD)` 的 `enclosedInto` 放宽（`Contraption.cs:735-740`）与 `Rocket.cs:141`/`SpotLight.cs:72` 的附件隐藏 | **已落地**（`tools/bple-joints` 解析 `m_Script` guid → 类名 + 继承链，写出 81 条 `canBeEnclosed`；`ConstructionRules.TryResolveEnclosure` 要求该位；`!canEnclose` 的推导式已删） | **G108 已收口**：真服务器实测「框 + 猪」`status 0`（并进同一 body）／「框 + 扇」`status 5 error 6`（`CellsOccupied`）／「框 + 蛋」`status 0` |
| `Rotatable*` = false（TNT/Wing/Tail/Sandbag/Balloon/Gearbox/Pumpkin） | 关掉 (a) `EffectDirection()` 随 yaw 旋转、(b) `m_autoAlign = Rotate` 自动对齐、(c) UI 四向按钮；附带事实：`Tail.cs:78-84` 读的是 `RotatableWing`，`RotatableTail` 全树未被读取 | 自由 yaw（`Place` 接受任意角）；开关条按 **(零件类型, 有效方向)** 分组，方向随 yaw 旋转（2026-10-06 第十九轮，**用户点名**） | 开关条分组**保留为有意偏差**（纯 UI，用户要的）；自动对齐仍是差距 `G96` |
| `AvoidanceRocket` / `TrackingRocket` = false | 无追踪/规避弹道（`Rocket.cs:292-311`） | 未实现 | 无 |
| `BlasterTNT`、`OffRoadWheel`、`HingePlate`、`MetalBox`、`WoodenBox`、`ColoredFrame`、`BracketFrame`、`AutoGun`、`MultipartGenerator`、`DecelerationLight`、`AutoControlLight`、`FuelSystem`/`ElectricalSystem`/`MechanicalSystem` = false | 14 个 `RegisterPart` 键为 false → `RemoveCustomPart` 掉对应条目（`INPartFactoryManager.cs:57-110`），即这些零件在 vanilla **不存在** | 目录 284 件里**只有 52 `tnt-blaster`（`Part_TNT_07_SET`）是 IN 档专属** | **新差距 `G109`**：目录范围（保留并标注 / 移除）待用户拍板 |
| `NoDrag = false` | 不清零全场阻尼（`INContraption.cs:305`） | 运行期阻尼覆盖（G90）本就未做 | 无变化 |

## 5. 参考

- 原版：`Assets/Scripts/Assembly-CSharp/{INSettings,INVersionSelector,INFeature}.cs`；
  `Assets/TextAsset/{INDeclarationSettings,INDeclarationSettingsExp,INSettingsA,INSettingsAExp,INSettingsB,INSettingsBExp,INSettingsO,INSettingsOExp}.json`
- 代码：`tools/bple-fans/extract-fans.mjs`、`tools/bple-lift/`、`tools/bple-springs/`、`tools/bple-power/`
  （全部读 `INSettingsBExp.json`）
- 相关：`tasks/original-vs-implemented.md` 的 `G104`/`G105`；`docs/specs/play-part-switches.md`（火箭族激活语义）；
  `docs/specs/fan-propeller.md` §7；`docs/specs/body-defaults.md`；`ADR-027`/`ADR-028`
