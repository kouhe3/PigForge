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
4. **规则层合成冲量**：`GameplayRules.ApplyBounce` 在 `ContactStarted` 时，对 `e · v_approach · m_reduced` 施加一对等大反向冲量（`+Normal` 给 A、`-Normal` 给 B，静态侧视作无限质量），作用点在刚体中心（不引入转矩）。命令在下一 tick 的 `ApplyCommands` 生效，此时求解器已消掉接近速度，所以补上的就是分离速度。`_bouncedBodies` 重臂守卫沿用 spring 弹跳同款语义（离开接触才重新武装），避免持续接触时每 tick 都弹。
5. **body 级材质 = 成员最强 restitution + 成员质量之和**。接触事件只给 body，而猪通常焊在复合车身上（与木框共体），所以 body 聚合成员材质；成员表随绑定/解绑/断裂重算（`RecomputeBodyMaterial` / `UnlinkBodyMember`），不会因为一次接缝拆分就把弹性丢掉。
6. **合成规则取「较大者」**：`max(e_A, e_B)`，不是平均。理由：ADR-002 要求「猪…以高弹性弹开」，而地面/结构件 restitution 均为 0，取平均会把猪的 0.5 折成 0.25。这是一个**记录在案的偏差** —— 原版 Unity 是按材质 `bounceCombine`（`Pig_PhysMat` = Multiply）优先级合成，猪对地面严格算是 `0.5 × 0 = 0`（即原版猪其实不怎么从地面弹），PigForge 有意选择让猪弹。
7. **内容数据按原版真值修正**：猪家族（4 与 110–128，20 条）0.8 → **0.5**；猪王家族（24 与 221–226，7 条）0.8 → **0**；鸟蛋家族（27 与 237–241，6 条）0.2 → **0**。唯一证据来源是新增的 `tools/bple-materials/extract-materials.mjs`（扫描全部 `Part_*.prefab` 的 collider 材质引用）。

## 实测与已记录的偏差

修复过程有两次实测反打，都记录在此以免后人重走：

1. **在活跃接触上「加冲量」做不出 restitution**。冲量命令排在 `Step` 之前，撞击又不是一个 tick 就能解完的（穿透恢复弹簧很软），所以注入的分离速度会被接触约束拉回它自己的速度目标：9.32 m/s 落地、注入应得的 7.22 m/s，实测只留下 1.12 m/s。把 `MaximumRecoveryVelocity` 从 2 提到 30 **完全无效**（说明限制来自弹簧本身，不是恢复钳制），而 Bepu 2.4.0 的 `ContactConstraintAccessor` 只是只读提取器，没有公开的自定义接触约束入口 —— 所以真 restitution 无法在不解包引擎的前提下做进接触里。
2. **`GameRoom` 的命令过滤会静默丢掉配对命令**。`DropCommandsForDestroyedBodies` 原本用 `command.Body` 过滤；反弹的抑制命令主体是**静态地面**（配对按 body id 排序，地面在前）→ 整条命令被丢弃，抑制从未生效。现已按「配对里任一端是存活动态体」放行。
3. **接触事件可能比撞击晚一拍**。实测 9.32 m/s 的落地，事件报上来只有 2.56 m/s（求解器一个 tick 就消掉了大部分接近速度），而 11.94 m/s 的落地事件报的是 10.86（求解器要两拍才消完，恰好赶上）。因此规则层不能只看事件，改为维护**衰减的峰值接近速度**（每 tick `×0.93`，并被更大的即时速度覆盖）来还原真实撞击速度。

最终机制：`ContactStarted` 时按 `e × 峰值接近速度` 算出应得分离速度，与「本 tick 求解后的相对法向速度」求差得到需要补的速度增量，转成一对冲量，**并同时把这一对在该 tick 的接触生成关掉**（`PhysicsCommandKind.SuppressContact`，Bepu 复用 `AllowContactGeneration` 已有的过滤，一 tick 后自动恢复）。这样注入的分离速度不会被接触约束吃掉，而下一 tick 双方已经分开、接触照常。

## 实测

四档落差，每档都重启服务器取干净世界（同一世界连测会被上一只猪干扰）：

| 落差 | 撞击速度 | 交付 e | 回弹 | 解析标称（e²h） |
|---|---|---|---|---|
| 8 m | 11.94 m/s | 0.451 | 1.514 m | 2.00 m |
| 4 m | 9.32 m/s | 0.447 | 0.923 m | 1.25 m |
| 3 m | 8.17 m/s | 0.445 | 0.703 m | 1.00 m |

修复前同场景为 0.011 m（几乎不弹），且跨高度不一致（0.12~0.44）。现在**稳定 0.445~0.451 且随撞击速度正确缩放**；逐 tick 轨迹是干净的抛物线。回归测试 `tests/PigForge.Server.Tests/PigBounceTests.cs`（真实 `PlayHost.CreateSandboxRoom()`）钉住回弹区间与该轨迹的跨运行确定性。

## 已记录的偏差

1. **交付系数约为标称值的 90%**：e=0.5 实测交付 0.445~0.451（回弹约为解析值的 75%）。原因是合成冲量落在接触发生后的下一 tick：那一 tick 的重力、以及已经产生的分离量都会吃掉一点。内容里的 restitution 应理解为**相对弹性**而非精确系数；要完全对齐需要把弹跳放进同一 tick 的求解前（需要引擎支持，见偏差 1）。
2. **按形状解析退化为按 body 解析**：一个复合车体上任意成员带弹性，整个 body 就有弹性（猪在车内 → 车会弹）。真正的按形状/接触点解析需要事件携带「哪个形状」的信息。
3. **接触抑制是一 tick 的引擎级让步**：跳过的这一 tick 里该对不做接触检测，靠「双方正在分离」保证不穿透。若将来弹跳需要跨越多个 tick（例如极软材质），这个机制要重新设计。
4. **friction 表仍是手写值且已漂移**（本次未动，属独立决策）：原版结构件 `Contraption_PhysMat` 是 0.7/0.5，而内容里 34 个零件（木块/电机/TNT/风扇/火箭…）**完全没有 material 字段**（退化为 0/0.8）；气球 0.7 vs 原版 0；弹簧 0.9 vs 原版 0；粘性轮 2.5 vs 原版 0.7。全量对照见 `tasks/bple-materials-report.md`（gitignored 的临时报告）与 `tools/bple-materials/extract-materials.mjs`。
5. **文档站待同步**：`docs/parts/cargo.md`（在产品文档 stash 内）目前仍写猪王「恢复系数 0.8」，弹出 stash 后需按本 ADR 修正。
