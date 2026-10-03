# ADR-016: 轮胎摩擦取组成刚体的碰撞体材质，并按 Unity 的 combine 规则合成

## 状态

Accepted

## 日期

2026-10-03

## 背景

玩家报的原版差异之一：轮子在地面上「太粘」。原版每个碰撞体有自己的 `PhysicMaterial`：

| prefab | 轮毂/支撑盒 `SupportCollider` | 轮胎（球） |
|---|---|---|
| `Part_NormalWheel_*`、`SmallWheel`、`StickyWheel`、`MotorWheel` | `Contraption_PhysMat` 0.7 **Average** | `Contraption_WheelFriction_PhysMat` 0.025 **Multiply** |
| `Part_CartWheel_*`（木轮） | `Contraption_PhysMat` 0.7 Average | `Contraption_WoodenWheelFriction_PhysMat` 0.05 **Multiply** |

Unity 的 `PhysicMaterialCombine` 让**一对**表面按双方中**优先级最高**的模式合成（`Average < Minimum < Multiply < Maximum`），轮胎的 Multiply 因此压过地面的 Average：0.025 × 地面 0.7 ≈ **0.0175**。

PigForge 此前有两个偏差：内容里轮子是**单一**材质、取「collider 数最多的材质」→ 拿到轮毂的 0.7；`BepuPhysicsWorld.CombineFriction` 只做**平均**（0.7 与地面平均 ≈ 0.7），于是轮胎与地面差 ~40 倍。

per-shape 材质看似是唯一解，但实测否掉了这条路：

- **Bepu 升级无用**：NuGet 上 BepuPhysics 只有 `2.5.0-beta.0…29`（**没有 2.5 正式版**），且 `2.5.0-beta.29` 与本仓库所用 `2.4.0` 的 `INarrowPhaseCallbacks.cs` 与上游 `master` 逐字相同——`PairMaterialProperties` 仍只有 `FrictionCoefficient`/`MaximumRecoveryVelocity`/`SpringSettings`，child 回调仍无出参，per-child 材质的 TODO 原文仍在（也仍无 restitution）。
- **ADR-009 已经给出了 body 级表达**：轮子的物理刚体**只带轮胎球**，支撑盒挂在父刚体上。所以「轮子刚体的材质」就等于「轮胎的材质」，per-shape 材质在本项目里不再必要。

## 决策

1. **选材按「组成该刚体的碰撞体」**（`tools/bple-materials/apply-materials.mjs`）：`capabilities.wheel` 的零件取它的**球体**（轮胎）碰撞体的材质，其余零件保持报告的主材质。支撑盒在父刚体上，沿用父刚体材质——与原版「轮毂是 `Contraption_PhysMat`」一致。
2. **内容带 combine 模式**：`material.frictionCombine`（`average|minimum|multiply|maximum`，缺省 `average`）。数值与模式都来自 `tools/bple-materials` 报告（per-collider `m_Material` 是唯一可采信来源，ADR-010 决策 7）。schema 加枚举；`PartDefinition` 加 `FrictionCombine`；`PartContentParser` 严格解析并允许该可选键；客户端不消费材质，保持不校验。
3. **合成规则进契约层**：`PhysicsMaterial.FrictionWith`（`Physics.Abstractions`）实现 Unity 的配对规则——取优先级最高的模式并**只用它**。Bepu 的两张摩擦表升级为材质表，`CombineFriction` 直接调它；Jolt 后端本就用 `PairMaterialProperties`，行为不变。
4. **不升级 Bepu**：2.5 仍是 beta，且对 per-child 材质与 restitution 都没有帮助；升级属 HANDOFF §6 要求「先问」的物理版本变更，收益为零。
5. **一个刚体一份聚合材质**（补，2026-10-03）：原版每个 collider 各自一份 `PhysicMaterial`，而一个 PigForge 刚体只有一份材质，所以多成员簇把成员材质按 Unity 自己的配对规则折叠（`CompoundAssembler.CreateBodyMaterial`）：restitution 取成员最强值（与 ADR-010 决策 5 的 body 级口径一致）、combine 取优先级最高的成员模式（Average < Minimum < Multiply < Maximum）、系数用该模式作用在全部成员上（Average 取成员均值、Multiply 取乘积、Minimum/Maximum 取极值）。单成员簇退化为它自己的材质，所以铰接轮（刚体只带轮胎球）逐字不变；**`Attachments` 不参与聚合** —— 那是铰接轮的支撑盒，骑在父刚体上，原版父体的材质是轮毂的 `Contraption_PhysMat`（决策 1）。

## 影响

- 内容：37 件获得 `"frictionCombine": "multiply"`（5 个轮子家族与变体），26 件轮胎 0.025、11 件木轮 0.05；`average` 保持隐式不写，diff 只落在真正改变行为的行上。另有 1 件 Minimum（`Contraption_PhysMat_Alien`）。
- 行为：木车在 14.3° 长坡上**打滑**（轮胎低摩擦），不再自由滚动——这是原版轮胎的真实行为；`SandboxWoodenCartRollsDownTheLongSlope` 的断言随之从「ω = v/r」改为「ω > 0 且不超过自由滚动」，回归目标（轮子必须能自转）不变。
- 合成：两个 Average 表面仍是历史平均；Multiply 轮胎 × Average 地面变成相乘。`PhysicsMaterial.Default` 仍是 (0, 0.8, Average)。
- 回归：`PhysicsMaterialTests`（5 组 combine 真值 + 默认值）、`PartContentTests`（解析往返、未知值拒绝、真实内容守卫：轮子 = 0.025/0.05 + Multiply、木块 = Average 0.7）、`SandboxWoodenCartRollsDownTheLongSlope`（真内容 + 真 Bepu 的端到端打滑）。
- 聚合材质：多成员簇的摩擦/弹性不再取 `Members[0]`；`CompoundAssemblerTests.AMergedClusterCarriesTheAggregatedMemberMaterial` 钉住 Multiply 胜出（0.7 × 0.5 = 0.35、restitution 取 0.5）与两个 Average 成员取均值（0.75）、最强 restitution（0.2）三种情形。

## 三项残留的核实结论（2026-10-03 第二轮）

上一版列的三项残留逐条核实；第一项本轮修掉，另两项是「已经等价」与「没有可追溯来源」，都不动内容。

### 1. 多成员簇的材质 —— 已改为按刚体聚合成员材质（改动）

核实：ADR-010 偏差 2 那句「按 body 聚合成员材质（max restitution + 质量求和）」写的是**弹性**的聚合；摩擦当时并没有聚合 —— `CompoundCluster.CreateBodyDefinition` 的三个 `PhysicsMaterial` 构造点一律用 `Members[0]`（代表成员）。所以 ADR-016 残留的描述才是改动前的实情，两处说法以代码为准，不冲突。

改动见决策 5：`CompoundAssembler.CreateBodyMaterial` 把成员材质按 Unity 的配对规则折叠，单成员簇退化为自身材质（铰接轮不变），`Attachments` 不参与。回归：`CompoundAssemblerTests.AMergedClusterCarriesTheAggregatedMemberMaterial`。

### 2. `bounceCombine` —— 已等价，不需要新内容键（仅记录）

穷举 `BPLE_Unity6/Assets/PhysicMaterial/` 的 8 个材质资产（`bounciness` / `bounceCombine` 是 `*.physicMaterial` 第 12/14 行）：

| 材质 | bounciness | bounceCombine | 谁在用 |
|---|---|---|---|
| `Pig_PhysMat` | 0.5 | **3 = Maximum** | 20 个猪 prefab（`Pig_PhysMat.physicMaterial:12,14`） |
| `JunkPrize_PhysMat` | 0.75 | **3 = Maximum** | 无（关卡杂物，不在 `Part_*.prefab` 里，报告 `usedBy: []`） |
| `Contraption_WheelFriction_PhysMat` / `Contraption_WoodenWheelFriction_PhysMat 1` | 0 | 2 = Multiply | 轮子球体（`Contraption_WheelFriction_PhysMat.physicMaterial:12,14`） |
| 其余 4 个（`Contraption_PhysMat`、`Contraption_PhysMat_Alien`、`Contraption_LowFriction_PhysMat`、`Ground_PhysMat`） | 0 | 0 = Average | 结构件/关卡 |

只有猪与 JunkPrize 的 bounciness 非零，而两者都是 Maximum（乘/取小/取平均都轮不到）；轮子的 Multiply 只会赢过 Average/Minimum，那些材质的 bounciness 全是 0，乘积恒为 0。内容侧也一样：`content/parts.json` 里 restitution > 0 的只有猪家族 20 条（0.5）。因此 ADR-010 决策 6 的 `max(e_A, e_B)` 与原版**每一对材质**都逐值相等 —— 结论是**不加 `bounceCombine` 字段**，`materials[].bounceCombine`（报告里有、内容里没有）继续不消费。

### 3. 关卡地形材质 —— 无原版可追溯来源，保留手写（仅记录）

三个需要核实的件是 PigForge 自造的静态关卡几何：`content/parts.json` 2 `ground-slab`（无 `material` → `PhysicsMaterial.Default` = 0 / 0.8 / Average）、5 `terrain-box`（0 / 0.9 / Average）、6 `ramp-plank`（0 / 0.9 / Average）。按 `tools/bple-materials` 的口径穷举后，**没有可以照抄的原版关卡材质**：

- 映射表里三者都是 `null`（`tools/bple-textures/part-map.json`；ADR-003 决策 5、ADR-005 决策 4），`tasks/bple-materials-report.md:702-704` 与 `tasks/bple-jointstrength-report.json` 的 `unmappedNote` 也把三者记为「不在提取器范围内」。提取器只扫 `Assets/GameObject/Part_*.prefab`，扫不到关卡地形。
- `Ground_PhysMat`（dynamic 1 / static 1 / bounciness 0 / Average）**唯一的引用者是 `BoxingGlove*.prefab`** 的拳头球体（5 个 prefab，各 1 处 `m_Material`，`BoxingGlove.prefab:83`）；没有任何地形 prefab 引用它，所以它不是「地面材质」的可采信来源。
- 官方关卡的地形是关卡包里的烘焙网格（`LevelFormatReader.cs:86-124`、`e2dTerrain.cs:7-27`），collider 不是 prefab 字段；唯一有据可依的关卡地形材质是冰面关运行时打的 `ground_physmat_ice`（0.01/0.01 Multiply，`LevelRigidbody.cs:98,314-350`），那不是普通地面。
- 名字最接近的 `Part_Ground_01_SET.prefab` 是 1×1×1 的 box + `Contraption_PhysMat`（0.7 Average，`Part_Ground_01_SET.prefab:47,50`），在报告里 `partTypeIds: []`（无 PigForge 映射），形状也与 10×0.5×10 的 `ground-slab` 不同。

因此**保留现状**：不手改数值、也不从别处借一个数；等关卡地形按原版网格重做（差距清单 §「地形」行）时，材质才随关卡数据一起进来。

## 参考

- 代码：`tools/bple-materials/apply-materials.mjs`、`src/PigForge.Physics.Abstractions/PhysicsContracts.cs`、`src/PigForge.Physics.Bepu/BepuPhysicsWorld.cs`、`src/PigForge.Core/Content/PartContent{Document,Parser}.cs`、`src/PigForge.Core/Construction/CompoundAssembler.cs`（`CreateBodyMaterial`，决策 5）
- 报告：`tasks/bple-materials-report.json`（per-collider 材质与 combine）
- 上游证据：`bepuphysics2` `2.5.0-beta.29` / `master` 的 `BepuPhysics/CollisionDetection/INarrowPhaseCallbacks.cs`（per-child 材质 TODO）、NuGet `bepuphysics` 版本索引（无 2.5 正式版）
- 相关：ADR-009（轮子独立刚体只带轮胎）、ADR-010（材质提取与弹性归属）、ADR-015（同批的接缝强度）
