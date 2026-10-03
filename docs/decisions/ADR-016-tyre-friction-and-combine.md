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

## 影响

- 内容：37 件获得 `"frictionCombine": "multiply"`（5 个轮子家族与变体），26 件轮胎 0.025、11 件木轮 0.05；`average` 保持隐式不写，diff 只落在真正改变行为的行上。另有 1 件 Minimum（`Contraption_PhysMat_Alien`）。
- 行为：木车在 14.3° 长坡上**打滑**（轮胎低摩擦），不再自由滚动——这是原版轮胎的真实行为；`SandboxWoodenCartRollsDownTheLongSlope` 的断言随之从「ω = v/r」改为「ω > 0 且不超过自由滚动」，回归目标（轮子必须能自转）不变。
- 合成：两个 Average 表面仍是历史平均；Multiply 轮胎 × Average 地面变成相乘。`PhysicsMaterial.Default` 仍是 (0, 0.8, Average)。
- 回归：`PhysicsMaterialTests`（5 组 combine 真值 + 默认值）、`PartContentTests`（解析往返、未知值拒绝、真实内容守卫：轮子 = 0.025/0.05 + Multiply、木块 = Average 0.7）、`SandboxWoodenCartRollsDownTheLongSlope`（真内容 + 真 Bepu 的端到端打滑）。

## 已知残留

- compound **簇**仍取 `Members[0]` 的材质，而不是逐形状；对轮子（单成员刚体）已正确，对多成员簇是既有近似，本轮不动。
- `materials[].bounceCombine` 未消费：弹性由规则层的 `max(restitution)` 合成（ADR-010），与 Unity 的 `bounceCombine` 语义尚未对齐。
- 关卡地形材质（如原版 `ground_physmat_ice`）仍由内容手写，不走提取器。

## 参考

- 代码：`tools/bple-materials/apply-materials.mjs`、`src/PigForge.Physics.Abstractions/PhysicsContracts.cs`、`src/PigForge.Physics.Bepu/BepuPhysicsWorld.cs`、`src/PigForge.Core/Content/PartContent{Document,Parser}.cs`
- 报告：`tasks/bple-materials-report.json`（per-collider 材质与 combine）
- 上游证据：`bepuphysics2` `2.5.0-beta.29` / `master` 的 `BepuPhysics/CollisionDetection/INarrowPhaseCallbacks.cs`（per-child 材质 TODO）、NuGet `bepuphysics` 版本索引（无 2.5 正式版）
- 相关：ADR-009（轮子独立刚体只带轮胎）、ADR-010（材质提取与弹性归属）、ADR-015（同批的接缝强度）
