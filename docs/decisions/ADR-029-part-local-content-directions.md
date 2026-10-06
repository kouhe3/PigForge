# ADR-029: 内容方向一律走零件自己的参考系（推力轴、施力点、爆心）

## 状态

Accepted

## 日期

2026-10-06

## 背景

原版把「方向」类内容值全部交给零件**自己的 transform**：

- `FanPropeller.cs:154-155`：`transform.TransformDirection(GetDirectionVector(m_forceDirection))`（风扇 / 螺旋桨 / 旋翼）
- `Bellows.cs:84-87`：`transform.TransformDirection(m_direction)`（风箱）
- `Rocket.cs:298-300`：`transform.TransformDirection(m_direction)`（火箭 / 红火箭 / 汽水 / 可乐）
- `GrapplingHook.cs:467`：`transform.TransformDirection(m_direction)`（抓钩的钩头）

这里装着两件事：零件的**建造角**（`Contraption.SetRotation` 绕 z，`BasePart.Rotate`，`Rocket.cs:189`）与刚体的**实时姿态**。所以「把零件转过去」在原版就是「把推力转过去」。

PigForge 的规则层曾经把其中几条当成**世界轴**用：风扇一族 2026-10-04 修好（`ADR-022` 偏差 1，用户实机报告「风扇的推力方向反了」），**火箭与抓钩**是同一条缺陷剩下的两件（差距 `G97`）——症状完全一样：玩家转向后画面上朝向变了、推力方向不变。同样属于这条决策的还有**施力点**与**爆心**：原版用 `transform.position`（`FanPropeller.cs:151-152`、`Bellows.cs:87`、`Rocket.cs:298-300`、`Rocket.cs:627-634`），PigForge 有几条路径打在**刚体中心**上（TNT 已在 2026-10-03 改过，见 HANDOFF §4.A）。

## 决策

1. **一个取轴入口**：`GameplayRules.ResolvePartFrame(entity, body, bodyPosition)` → `out partPosition` / `out partRotation`：
   - `partPosition = body.Position + bodyRotation · localOffset`（成员在体坐标系里的位置偏移，房间装配时发布）
   - `partRotation = bodyRotation · localRotation`（成员在体坐标系里的旋转，`GameRoom.BindCluster` 传 `CompoundMember.LocalRotation`）
   缺失条目时退化为刚体位姿本身（单成员簇逐位一致）。
2. **任何读内容方向的规则都走它**：`RunFans`（含旋翼的 `m_rotorTargetDirection` 混合）、`RunBellows`、`RunRockets`、`RunGrapples`。
   反例（不许再出现）：把 `DirectionX/DirectionY` 直接当世界向量。
3. **施力点 = 零件自己的 `transform.position`**：风扇与风箱 `+ 方向 × 0.5`（`FanPropeller.cs:151-152`、`Bellows.cs:87`）；火箭与抓钩就是零件位置本身——`Rocket.cs:298-300` 的 `position = transform.position + zero * 0.5f` 里 `zero` 是反编译产生的未使用偏移；抓钩的冲量在原版打在**钩头刚体**上（`GrapplingHook.cs:467-473`），PigForge 的单冲量近似因此落在零件自己的 transform 上。
4. **爆心 = 施爆件自己的位姿**：`Rocket.cs:627-634` 的 `OverlapSphere(transform.position, …)` 与 `TNT.cs:236-252` 同源；`Explode` 与 `RunRockets` 共用 `ResolvePartFrame`（TNT 的旧实现是同一段代码的复制）。
5. **回归口径**：每条轴配两类用例——「转 90°/180° 后推力/拉力反向」与「刚体倾斜后跟着倾」；房间级用真内容 + 真 Bepu 的转向用例（`tests/PigForge.Server.Tests/RocketThrustTests.cs`、`FanThrustTests.cs`）。

## 取舍与后果

- **换来的**：玩家能用转向瞄准推进件；取轴只有一个入口，新增推进件不会再次分叉出世界轴写法；爆心与施力点不再有「刚体中心 vs 零件自己」的第二套语义。
- **已知偏差**：
  1. **多成员簇只到簇质心这一层**：原版每个零件一个刚体，我们按成员位姿在**簇**刚体上施力，是该刚体上的近似（与 `ADR-022` 偏差 1 同一条）。
  2. **抓钩仍是单冲量近似**：原版冲量打在钩头刚体上、再靠 `ConfigurableJoint` 拖整车（`GrapplingHook.cs:467`、`:624`）；内容里的 `impulse 22` 与 45° 方向是 PigForge 的标定/手写值，不是 prefab 真值（`m_direction` 是 `private Vector3.right`，prefab 未序列化）。
  3. **火箭族的内容值仍是手写的**（`thrustPerTick` 4/6/8、`durationTicks` 60/30、`explodeRadius` 4、`explodeImpulse` 20，来源 `49e9153`），而 prefab 真值是 `m_boostForce` **50**（Rocket / RedRocket）/ **35**（SodaBottle）、`m_ignitionTime 1` + `m_boostDuration 3`（汽水 1）+ `m_boostEndDuration 1`（汽水 0.5）、`m_maximumSpeed 18` / **10**、`m_explodes 1` 时 `m_explosionRadius 8` + `m_explosionImpulse 25`、`m_direction (1,0,0)`（13 个 prefab 全同）——**新差距 `G101`**：需要 `tools/bple-rockets/` 提取器 + 逐帧曲线（点火斜坡 / 平台 / 收尾）与 `LimitForceForSpeed` 限速。
  4. **动力轮的驱动仍是世界 X**（`RunMotors` 的 `(thrust, 0, 0)` 与 `velocity.X` 门控）：轮子铰接、建造角仍绕 z，转向后驱动方向该跟着零件的 +X 走——**新差距 `G102`**（未核实原版 `MotorWheel` 的确切轴，先记差距）。
  5. **`G96` 建造自动对齐**仍未做：轴修好之后，同一个摆法的**默认朝向**仍与原版不同（玩家仍要自己设角）。

## 参考

- 代码：`src/PigForge.Core/Runtime/GameplayRules.cs`（`ResolvePartFrame`、`RunFans`、`RunBellows`、`RunRockets`、`RunGrapples`、`Explode`）、`src/PigForge.Server/GameRoom.cs`（`BindCluster`）
- 原版：`FanPropeller.cs:151-155`、`Bellows.cs:84-87`、`Rocket.cs:298-300,627-634`、`GrapplingHook.cs:467-473`、`TNT.cs:236-252`
- 测试：`tests/PigForge.Server.Tests/RocketThrustTests.cs`、`tests/PigForge.Server.Tests/FanThrustTests.cs`、`tests/PigForge.Core.Tests/GameplayRulesTests.cs`
- 相关：`ADR-022`（偏差 1：同一条轴，风扇一族）、`ADR-019`（爆心 = 施爆件自身）、`docs/specs/fan-propeller.md` §7.2、差距 `G97`
