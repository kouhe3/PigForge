# ADR-010: 弹性（restitution）由规则层合成，Bepu 后端无原生项

## 状态

Accepted。补全 ADR-002 决策 2「材质属性进内容层」的最后一环：材质进内容后，后端一直没有消费 `restitution`。

## 日期

2026-09-30

## 背景

用户报告「猪没有弹性」。实测（真实 `--play` 房间 + 真实 socket，猪从 y=5 落到 y=-3 的平地板）：

| 指标 | 实测 |
|---|---|
| 落地速度 | 11.94 m/s |
| 落地后回弹高度 | **0.011 m** |
| 内容里猪的 restitution | 0.8（当时的值） |

根因链：

1. 内容有 `material.restitution`，解析器读进 `PartDefinition`，Core 装进 `PhysicsMaterial`，Jolt 后端用 `BodyCreationSettings.Restitution` 真的消费它。
2. **但线上房间跑的是 Bepu，而 BepuPhysics 2.4.0 的材质记录里根本没有 restitution 项** —— `PairMaterialProperties` 只有 `FrictionCoefficient` / `MaximumRecoveryVelocity` / `SpringSettings`，整包 XML 文档里 "restitution" 出现 0 次（已核对包内文档，非凭记忆）。`BepuPhysicsWorld` 因此只用 friction，restitution 被静默丢弃。
3. `JoltPhysicsWorld` 虽有实现，但 Jolt 不在 `PigForge.slnx` 里，`PlayHost` 写死 Bepu —— 玩家永远拿不到弹性。
4. Bepu 2.4.0 也没有公开的 contact constraint factory（只有 `ContactConstraintAccessor`），所以不 fork 引擎就做不出真正的求解器级 restitution。

**内容数据同时是错的**：`tools/bple-shapes` 与 `tools/bple-variants` 根本不读材质，`content/parts.json` 的整张 material 表是早期手写的猜测。用原版 BPLE 工程核对（`Assets/PhysicMaterial/*.physicMaterial` + prefab collider 的 `m_Material` guid 反查）：

| 原版 prefab | 材质 | bounciness |
|---|---|---|
| `Part_Pig_01..20_SET`（20 件，全部皮肤） | `Pig_PhysMat` | **0.5** |
| `Part_KingPig_01..07_SET` | 无（Unity 默认） | 0 |
| `Part_Egg_01..06_SET` | `Contraption_PhysMat` | 0 |
| `Part_WoodenFrame` / `MetalFrame` / `Sandbag` / 全部车轮 / 地面 | `Contraption_PhysMat` 或无 | 0 |

即：**原版只有猪会弹**，猪王与鸟蛋完全不弹 —— 与用户的手感记忆、以及 ADR-002 原文「猪王与鸟蛋则完全无弹性」一致；内容表里猪 0.8 / 猪王 0.8 / 蛋 0.2 三处都是编的。

## 决策

1. **弹性归会话层拥有者决定**：`PhysicsCapabilities.AppliesRestitutionNatively` 声明后端是否自带 restitution 项。Bepu = false（规则层合成），Jolt = true（求解器自己应用），两者**永不叠加**。
2. **接触事件携带求解前信息**：`PhysicsEvent` 的接触事件新增 `ContactNormal`（单位向量，方向定义为「把 `BodyA` 沿 `+Normal` 推、`BodyB` 沿 `-Normal` 推即可分离」）与 `ApproachSpeed`（求解前的相对法向接近速度，m/s）。Bepu 在 `ConfigureContactManifold`（求解之前、单线程）读取 manifold 法线与两侧速度，并按「最强接近」做**确定性归约**（并列时按法线分量字典序），因此结果不依赖 manifold 遍历顺序。replay 层把事件投影成 `ReplayEvent(kind, bodyA, bodyB)`，所以 **replay schema 与差分比较面不变**。
3. **法线朝向不信任引擎约定**：Bepu 侧用求解前相对速度定符号，`+Normal` 恒为「分离 `ContactPair.A`」的方向。这样与引擎的 manifold 符号约定无关。
4. **规则层「瞄准出射速度」，而不是「加冲量」**：`GameplayRules.ApplyBounce` 在 `ContactStarted` 时先算出这次撞击应得的分离速度 `e · v_approach`，再与本 tick 求解后的相对法向速度（`RelativeNormalSpeed`，正 = 正在分离）求差，把差值转成一对等大反向冲量（`+Normal` 给 A、`-Normal` 给 B，静态侧视作无限质量），作用点在刚体中心（不引入转矩）。**加冲量做不出 restitution**（见偏差 1），所以同时发一条 `PhysicsCommandKind.SuppressContact`，让这一对只在承载冲量的那一步不做接触生成（Bepu 复用 `AllowContactGeneration` 已有的过滤，一 tick 后自动恢复）—— 没有接触约束抢法向速度，注入的分离速度就能活到下一步，而下一步双方已经分开、接触照常。`_bouncedBodies` 重臂守卫沿用 spring 弹跳同款语义（离开接触才重新武装），避免持续接触时每 tick 都弹。
5. **body 级材质 = 成员最强 restitution + 成员质量之和**。接触事件只给 body，而猪通常焊在复合车身上（与木框共体），所以 body 聚合成员材质；成员表随绑定/解绑/断裂重算（`RecomputeBodyMaterial` / `UnlinkBodyMember`），不会因为一次接缝拆分就把弹性丢掉。
6. **合成规则取「较大者」**：`max(e_A, e_B)`，不是平均。理由：地面/结构件 restitution 均为 0，取平均会把猪的 0.5 折成 0.25。这与原版一致：`Pig_PhysMat.physicMaterial` 的 `bounceCombine: 3` 就是 Unity 的 **Maximum**（`frictionCombine: 0` 是 Average），所以原版猪对地面同样是 `max(0.5, 0) = 0.5` —— 猪**会**从地面弹，这条不是偏差。
7. **内容数据按原版真值修正**：猪家族（4 与 110–128，20 条）0.8 → **0.5**；猪王家族（24 与 221–226，7 条）0.8 → **0**；鸟蛋家族（27 与 237–241，6 条）0.2 → **0**。唯一证据来源是新增的 `tools/bple-materials/extract-materials.mjs`（扫描全部 `Part_*.prefab` 的 collider 材质引用）。

## 修复过程中被实测打回的三次

三条都记录在此以免后人重走：

1. **在活跃接触上「加冲量」做不出 restitution**。冲量命令排在 `Step` 之前，撞击又不是一个 tick 就能解完的（穿透恢复弹簧很软），所以注入的分离速度会被接触约束拉回它自己的速度目标：9.32 m/s 落地、注入应得的 7.22 m/s，实测只留下 1.12 m/s。把 `MaximumRecoveryVelocity` 从 2 提到 30 **完全无效**（说明限制来自弹簧本身，不是恢复钳制），而 Bepu 2.4.0 的 `ContactConstraintAccessor` 只是只读提取器，没有公开的自定义接触约束入口 —— 所以真 restitution 无法在不解包引擎的前提下做进接触里。
2. **`GameRoom` 的命令过滤会静默丢掉配对命令**。`DropCommandsForDestroyedBodies` 原本用 `command.Body` 过滤；反弹的抑制命令主体是**静态地面**（配对按 body id 排序，地面在前）→ 整条命令被丢弃，抑制从未生效。现已按「配对里任一端是存活动态体」放行。
3. **任何「此刻读到的速度」都低估撞击，事件本身晚两拍**。求解器的穿透恢复弹簧在形状看起来还没接触时就开始吸能，所以接触事件到达时能读到的速度都已经是残值：实测 9.81 m/s 的落地，后端事件报 2.18 m/s，本层「上一 tick 速度」也是 2.18 m/s（事件比最后一个自由 tick 晚了两拍），而 6.05 m/s 的落地事件报 5.97（几乎没被吸）。因此规则层维护**每刚体的「最近峰值速度」**来还原真实撞击速度：`StorePreviousVelocities` 每 tick 记下「本 tick 速度 vs 已记峰值」中较大者**连同它发生的 tick**，`PeakApproach` 只在「距今 ≤ 2 tick」时才采信 —— 超过就作废（那属于另一次、无关的接触），所以**不需要任何衰减常数**，自由落体每 tick 都在刷新峰值，撞击速度因此是精确值。

峰值寿命取 **2 tick** 是实测结论而非估计：1 m ~ 11 m 落差的六次测量里，事件一律比峰值晚 2 tick（最高 14.55 m/s 也是 2），所以 2 是刚好够用的最小值；调大可让更陈旧的峰值参与，没有收益。

## 实测

真实 `--play` 房间 + 真实 socket，猪（`partTypeId` 4）落到 y=-3 的平地板，每档落差都重启服务器取干净世界（同一世界连测会被上一只猪干扰）。`撞击速度` 由轨迹独立测出，`峰值` 是规则层记住的值 —— 两者每档都相等，即**机制拿到的就是真实撞击速度**：

| 落差 | 撞击速度 | 规则层峰值 | 交付 e（发射速度） | 回弹 | 交付 e（由回弹反推） | 解析标称 e²h |
|---|---|---|---|---|---|---|
| 11 m | 14.55 m/s | 14.55 ✓ | 0.489 | 2.638 m | 0.494 | 2.75 m |
| 8 m | 12.43 m/s | 12.43 ✓ | 0.487 | 1.918 m | 0.494 | 2.00 m |
| 5 m | 9.81 m/s | 9.81 ✓ | 0.483 | 1.188 m | 0.492 | 1.23 m |
| 3 m | 7.52 m/s | 7.52 ✓ | 0.478 | 0.689 m | 0.489 | 0.72 m |
| 2 m | 6.05 m/s | 6.05 ✓ | 0.473 | 0.441 m | 0.486 | 0.47 m |
| 1 m | 4.25 m/s | 4.25 ✓ | 0.462 | 0.213 m | 0.480 | 0.23 m |

修复前同场景回弹 0.011 m（几乎不弹），且跨高度不一致（等效 e 在 0.12~0.44 之间乱跳）。逐 tick 轨迹是干净的抛物线（`+5.39 → +5.22 → +5.06 …`），无能量注入。回归测试 `tests/PigForge.Server.Tests/PigBounceTests.cs`（真实 `PlayHost.CreateSandboxRoom()`，7.5 m 落差实测回弹 1.766 m）钉住回弹区间与该轨迹的跨运行确定性。

## 已记录的偏差

1. **交付系数 = 标称值减去「承载冲量那一 tick 的重力」**：实测 0.473~0.489（由回弹反推 0.480~0.494），差的那点是 `g·dt = 0.163 m/s`：冲量排在 `Step` 之前，重力随后在同一 tick 里把速度拉低 `g·dt`（5 m 档：应得 4.90 m/s，实测 4.74 = 4.90 − 0.163 ✓）。落差越大、撞击越快，这一项的占比越小（标称 0.5 → 交付 0.480→0.494 随落差上升）。这是**物理**，不是损失：任何求解器都在同一步里同时受重力（原版 Unity 在求解前先积分重力，出射速度同样被这一步的重力影响）。若要连这 1~4% 也补掉，需要在目标里加回 `g·dt`，代价是规则层要引入重力常数与方向约定 —— 收益不可感知，故不做。
2. **按形状解析退化为按 body 解析**：一个复合车体上任意成员带弹性，整个 body 就有弹性（猪在车内 → 车会弹）。真正的按形状/接触点解析需要事件携带「哪个形状」的信息。
3. **接触抑制是一 tick 的引擎级让步**：跳过的这一 tick 里该对不做接触检测，靠「双方正在分离」保证不穿透。若将来弹跳需要跨越多个 tick（例如极软材质），这个机制要重新设计。
4. **friction 表仍是手写值且已漂移**（本次未动，属独立决策）：原版结构件 `Contraption_PhysMat` 是 0.7/0.5，而内容里 34 个零件（木块/电机/TNT/风扇/火箭…）**完全没有 material 字段**（退化为 0/0.8）；气球 0.7 vs 原版 0；弹簧 0.9 vs 原版 0；粘性轮 2.5 vs 原版 0.7。全量对照见 `tasks/bple-materials-report.md`（gitignored 的临时报告）与 `tools/bple-materials/extract-materials.mjs`。
5. **文档站待同步**：`docs/parts/cargo.md`（在产品文档 stash 内）目前仍写猪王「恢复系数 0.8」，弹出 stash 后需按本 ADR 修正。
