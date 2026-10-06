# ADR-025: 每件刚体阻尼与全场 7 rad/s 角速度上限按原版落地

## 状态

Accepted

## 日期

2026-10-04

## 背景

原版的「手感上限」不是某个零件自带的数值，而是**两条它自己从不覆盖的 Unity/PhysX 默认值**：

1. `Rigidbody.drag = 0.2` / `angularDrag = 0.05`（`BasePart.EnsureRigidbody`，`BasePart.cs:1192-1205`），机翼/尾翼
   `1/0.2`（`Wings.cs:99-100`、`Tail.cs:52-53`）、气球 `2/0.5`（`Balloon.cs:130-131`）、沙袋 `1/10`
   （`Sandbag.cs:133-134`）、猪王 `0.5/1`（`KingPig.cs:79-81`）。
2. `Physics.defaultMaxAngularSpeed = 7` rad/s（原版 `ProjectSettings/DynamicsManager.asset` 的
   `m_DefaultMaxAngularSpeed`），PhysX 按**模长**截断；没有脚本写 `maxAngularVelocity`，343 个
   `Part_*.prefab` 也没有一个序列化 `m_MaxAngularVelocity`。

PigForge 的 `IPhysicsWorld` 契约里连这两个字段都没有，于是动力轮、螺旋桨、火箭和翻滚的框全都没有终端速度：
实测木框双马达轮的车在沙盒里到 **17.775 m/s 且仍在加速**，用户指出「原版手感有上限」。上一轮（`b4ca6c6`）只补了
动力轮 `15 × powerFactor` 的硬上限，那只是三条机制里的一条（`docs/specs/power-system.md` §7、差距 G86）。

规格与全部实测数字见 `docs/specs/body-defaults.md`。

## 决策

1. **两条一起做，本机量到底**（用户 2026-10-04 拍板）。新增探针
   `unity/PigForge.WeldProbe/Assets/PigForge/Probe/Editor/BodyDefaultsProbe.cs`（入口
   `PigForge.WeldProbe.Probe.BodyDefaultsProbe.Run`），钉在原版编辑器 **2021.3.45f2** + 原版物理设置，
   把四条量出来：角速度上限（含 1000 / 0 两个对照）、线性阻尼每步系数、角阻尼每步系数、自由落体终端速度。
   结果：上限 **7 rad/s**（种子 100 → 第一步 7；上限 1000 时同种子 100、同扭矩 119.9999，证明非空）；
   阻尼 **`v *= max(0, 1 - c·dt)`**（50 步每步误差 **0**，把 `1/(1+c·dt)` 与指数型都否掉）；**重力在阻尼之前**；
   终端速度 5 s 30.9174 / 50 s 48.85183（离散预测 48.8538）。两次运行逐字节一致。
2. **真值来自提取器，不是手写**：新工具 `tools/bple-damping/`（`extract-damping.mjs` 读反编译源的**继承链**
   （`EnsureRigidbody` → `Initialize`），`apply-damping.mjs` 报告驱动、幂等、`--dry-run`）。硬断言：类→阻尼表
   逐值、50 件以上的直方图、任何在别的**生成期**方法里写阻尼的类都报错（运行期覆盖显式列进报告的
   `runtimeOverrides`）。工具同时从 `ProjectSettings/DynamicsManager.asset` 读 `m_DefaultMaxAngularSpeed`。
   Unity 6 迁移把 API 改名了（`drag` → `linearDamping`、`angularDrag` → `angularDamping`）——提取器**两种拼写都认**，
   因为 `BPLE_Unity6` 与 pristine `BPLE 2022.1.9` 各有其一。
3. **契约加三个字段**：`BodyDefinition.LinearDamping` / `AngularDamping` / `MaximumAngularSpeed`（有限、非负；
   `0` = 无阻尼 / 不限）。**注意约定差异**：Unity 与 Jolt 自己的 `maxAngularVelocity = 0` 是「角速度清零」，
   PigForge 的 `0` 是「不限」——内容永远写正值，Jolt 后端把 `0` 翻译成 `1e18f` 哨兵。
4. **内容只声明一次默认 + 例外**：顶层 `physics = { maximumAngularSpeed, damping }` 必填（原版的工程默认 +
   `BasePart` 的那对值）；件级 `damping` 只在原版类覆盖时写（264 个动态件里 62 个）。静态件不许写：原版的静态
   关卡件不是刚体（解析器硬拒；客户端校验器同规则）。
5. **Bepu 自己实现，Jolt 用原生**：Bepu 2.4.0 的 `BodyDescription` 没有这两个概念（元数据反射验证），所以按已有
   `_constraintsByHandle` 的先例加 `BodyMotionSettings[] _motionByHandle`，在 `PoseIntegratorCallbacks.IntegrateVelocity`
   的逐 lane 循环里按 PhysX 的顺序施加（重力 → 阻尼 → 模长截断 → 冻结自由度）；越界句柄返回全 0 → 不写。
   Jolt 直填 `LinearDamping`/`AngularDamping`/`MaxAngularVelocity`（它自己的默认值是 0.05 阻尼与
   `0.25π·60 ≈ 47.12` rad/s 上限，与 PigForge 无关，所以三个都显式写）。
6. **一个 body 一个阻尼 → 按质量折叠**：`CompoundCluster.Damping = sum(m·d)/sum(m)`，与 body 的质量同一个权重；
   单成员逐位保留；宿主挂件（铰链轮的支撑盒）不贡献——与它不贡献质量一致。
7. **Jolt 的测试类串行**：两个 Jolt 测试类并行跑会把测试宿主崩在原生求解器里（仓库已知 flaky；单类跑一直正常）。
   两个类放进同一个 `[Collection("Jolt")]`，实测 15/15 稳定通过。

## 影响

- `src/PigForge.Physics.Abstractions/PhysicsContracts.cs`（三个字段 + 校验）、`BepuPhysicsWorld.cs`
  （`BodyMotionSettings` 表 + `IntegrateVelocity` 里的阻尼/截断）、`JoltPhysicsWorld.cs`（三个原生字段 + 哨兵）、
  `src/PigForge.Core/Content/{PartContentDocument,PartContentParser,PartContentLibrary}.cs`、
  `src/PigForge.Core/Construction/CompoundAssembler.cs`（`CompoundMember.Damping`、`CompoundCluster.Damping`、
  `FoldDamping`）、`tools/bple-damping/`、`schemas/part-content-v1.schema.json`、
  `clients/web/src/schema/{types,validateContent}.ts`。
- **可见行为变化**：**全场轨迹都变了**（这是本轮的目的）。已实测的后果：动力轮车顶速从 17.775 m/s 降下来
  （见 §5.1 实机读数）；风扇转角速度 4.63 → **3.84 rad/s**；落猪回弹 1.766 → **1.415 m**；落地安定需要
  120 → **240 tick**。气球那条测试被改写成**阻尼上升的递推**（每 tick 与实测速度差 < 0.05 m/s），因为
  「恒定加速度」的前提在有了 drag 2.0 之后不再成立（终速 `(F/m - g)/c ≈ 110 m/s`）。
- **内容**：`content/parts.json` +63 行（1 行顶层 `physics` + 62 行例外），`apply-damping.mjs` 第二次跑 0 改动。
  客户端 `playParts.generated.json` 整份照抄，故跟着更新。
- **测试**：新增 Core `BodyDampingTests`（6 条：解析/默认与覆盖/静态件、出厂内容逐值、质量折叠、单成员、
  挂件不泄漏、解析器三条拒绝）、Physics `BodyDampingTests`（6 条 Bepu）、`JoltBodyDampingTests`（4 条）；
  改动 3 个 Server 行为测试（气球/风扇/猪）。计数：Core **241** / Server **110** / Protocol 39 / Replay 11 /
  Physics **65**（50 非 Jolt + 15 Jolt，**分开跑**）/ web 224。Release 构建 0 警告 0 错误。
- **未做**（规格 §6 有全表）：`Pig.FixedUpdate` 的慢速增阻、旋翼的 `angularDrag 1000/1`、绳的逐节阻尼、
  `NoDrag` 开关、IN 铰链板体这些**运行期**覆盖；~~`m_BounceThreshold: 2`~~ —— **已照搬**（2026-10-06，
  差距 G89：`GameplayRules.MinimumBounceApproachSpeed = 2f`，门控装在**估算**出来的接近速度上，因为后端的
  接触事件读数会低估已被求解器吸掉的落地速度）；`m_DefaultSolverIterations` 等其余 PhysicsManager 项逐条核对。
- **顺带确认的文档**：Unity 自己 2021.3 文档说 `Physics.defaultMaxAngularSpeed` 默认 50，**与工程资产和实测
  都矛盾**（都是 7）；以资产为准。
