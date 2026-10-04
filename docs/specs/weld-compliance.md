# 规格：焊点弹性（weld compliance）—— 框↔框是软的

状态：**设计（未实现）**。真值已核实（含一处我此前读反的地方，见 §0）。待 §6 的标定拍板。
来源（全部实测，非转述）：

- 原版：`Contraption.cs:1507-1555`（`AddFixedJoint`）、`:1540`（`enablePreprocessing`）、`FrameJointManager.cs:60-193`、`Frame.cs:44-52`、`HingePlate.cs:464,476`、`Hook.cs:66`、`GrapplingHook.cs:357`、`Balloon.cs:159`、`BalloonBalancer.cs:37`、`BasePart.cs:148,249`；`Assets/GameObject/Part_*.prefab` 全量字段统计。
- 语义（外部权威）：Unity 6.6 手册 `Joint.enablePreprocessing`（原文摘录见 §1 真值 2）；PhysX 3.4 `PxConstraint::setMinResponseThreshold`、`PxConstraintFlag::eDISABLE_PREPROCESSING`。
- 报告：`tasks/bple-jointstrength-report.json` → `prefabScan.preprocessingHistogram`（316 / 27）、`frameElasticityDifferential`（无 spring；木 vs 铁只差阈值与质量）。
- 现状：`ADR-011`（决策 2/3 焊成复合刚体）、`ADR-012`（Revolute 的 spring）、`ADR-015` §决策 4。

## 0. 更正记录（2026-10-04）

我先前把 `enablePreprocessing = true` 当成「更硬」，因此把「木框↔木框的弹性」记成**超出原版的有意偏差**。**这是错的**（用户指出，已核对官方文档）：preprocessing 打开时 PhysX 会**丢弃**那些「冲量巨大但只造成极小速度变化」的约束行（即最硬的那些行，阈值机制＝`PxConstraint::setMinResponseThreshold`），关节因此**不会全力抵抗 → 表现为软**；关掉才是「所有行都生效 → 硬」。所以**框↔框在原版本来就是软的**，用户要的是**还原**，不是偏差。

## 1. 原版真值

| # | 规则 | 出处 |
|---|---|---|
| 1 | **相邻每对零件 = 一个真实关节**：`ConfigurableJoint`，`x/y/z + angularX/Y/Z` 六轴全 `Locked`；任一端 `m_jointType == HingeJoint` 时改用 `HingeJoint`（`limits.min/max = ∓0.1°`、`axis = forward`） | `Contraption.cs:1507-1546` |
| 2 | **软硬只看一个开关**：`joint.enablePreprocessing = part.JointPreprocessing && other.JointPreprocessing`。语义（Unity 6.6 原文）：「This flag has a connection with rigidbodies that have some of their rotational degrees of freedom frozen… **When the flag is set, PhysX would ignore constraints that produce huge impulses generating only a small change in velocity.** Whilst it **may reduce the overall accuracy of the joint simulation**, it's been proven to help with overconstrained configurations like in the 2D case.」→ **flag = true ⇒ 关节软（硬行被丢弃）**；flag = false ⇒ 硬（`eDISABLE_PREPROCESSING`: suppress constraint preprocessing；配 `PxConstraint::setMinResponseThreshold`，同一条行响应阈值机制） | `Contraption.cs:1540`；Unity 6.6 `Joint.enablePreprocessing`；PhysX 3.4 `PxConstraint` |
| 3 | `m_jointPreprocessing: 1` 恰好 **27/343** 件（`grep -l` 实测）：`ColoredFrame`、`MetalFrame_01..12`、`MetalFrame_131/132`、`TimeBomb_01`、**`WoodenFrame_01..11`** → **框↔框（木或铁）＝软**；**其余 316 件的对（框↔非框、非框↔非框）＝硬** | prefab；报告 `preprocessingHistogram` |
| 4 | 反向印证（工程自己的两极用法）：需要**弹簧/驱动生效**的关节都显式关掉预处理（`Balloon.cs:159`、`BalloonBalancer.cs:37`、`Hook.cs:66`、`GrapplingHook.cs:357`、`HingePlate.cs:464,476`、`Frame.cs:50` 的包裹关节），因为预处理会把产生大冲量的驱动行也丢掉；而唯一显式打开的是 `FrameJointManager.cs:182` 的**框补充网**（也软） | 上列文件行 |
| 5 | **全 343 prefab 没有任何 spring/damper/softness**：`noSpringAnywhere`（唯一的类弹簧字段是 `Part_MotorWheel_08_SET` 的 `m_springStiffness: 50`，属轮子悬挂） | 报告 `frameElasticityDifferential.noSpringAnywhere` |
| 6 | `FrameJointManager` 为 3 类特例件（轻框 idx 10、支架框 idx 131、框内包裹 `SpringBoxingGlove` index 4）在同一分量内**补**最多 16/32 条最近的 `FixedJoint`（`enablePreprocessing = true`、`breakForce = 600 × ConnectionStrength`），只补不删 | `FrameJointManager.cs:60-193`；报告 `b_FrameJointManagerFull` |
| 7 | 「木框比铁框更容易散」是**涌现**：木↔木 1000 vs 铁↔铁 2400（2.4×），质量 0.5 vs 1，碰撞体/阻尼/插值相同；软硬对两个框族**相同**（都是 preprocessing 1） | 报告 `completedComparison_4`/`mass_5`/`d_negativeEvidence` |
| 8 | **强力胶只改强度、不改软硬**：`Contraption.cs:2599` → `MakeUnbreakable()`（`:2238-2247`）把 `m_jointMap` 里每条关节 `breakForce = +Inf`；各族的 `HasSuperGlue` 分支同样只是 `+∞`（`FrameJointManager.cs:172`、`HingePlate.cs:453`）或放宽触发条件（`Kicker.cs:266`、`Spring.cs:86`、`GrapplingHook.cs:281`、`SpringBoxingGlove.cs:190`）。**源码里没有任何 `HasSuperGlue` 站点改 `enablePreprocessing`** → 上胶后"更硬"是「永不脱开」的手感，不是约束本身变刚。（用户记忆为「胶也改软度」：本 build 的源码不支持；要么是别的版本，要么就是这条观感。） | `Contraption.cs:2238-2247,2599`；上列各族文件行 |
| 9 | **木/铁之差的可用「迹」**：标准框 `m_mass` = 木 0.5 / 铁 1 / `MetalFrame_131` 4 / `TimeBomb_01` 0.2；`BoxFrame` 另走 IN `WoodenBoxMass`/`MetalBoxMass`，而这两个 feature 在 `INSettingsBExp.json` **没有覆盖项** → 落回声明默认 **1.0 / 1.0**（箱式框木铁同重）。其余输入（碰撞体 1×1×1、阻尼 0.2/0.05、插值、关节 flag、软硬）木铁**完全相同**，只有断裂阈值不同 | prefab；`BoxFrame.cs:9-18`；`INDeclarationSettingsExp.json`；`INSettingsBExp.json` |
| 10 | 原版**没有**任何「关节软度」的 IN 开关：`INFeature` 里与关节相关的只有 `ConnectionStrength`(54) 与 `FrameJoint`(80)，且 `FrameJoint = true` | `INFeature.cs:54,80`；`INSettingsBExp.json` |

**一句话**：原版里**只有框与框之间是弹性的**（木框、铁框、彩框、TimeBomb 全在内 → 这正是「不止木框」的含义），其余焊点是硬的。

## 2. PigForge 现状

- `ADR-011` 决策 2/3：相邻可合并件**焊进同一个复合刚体**，关节数为 **0** → 焊点两侧没有相对自由度，弹性无处存在。框结构因此比原版**硬**（原版框-框软、且还有补充网）。
- 契约已有 `PhysicsJointKind.{Distance, Revolute}`，`Revolute` 带 `SpringFrequency`/`SpringDampingRatio`（ADR-012 轮子悬挂）→ 弹簧关节管线在，缺「焊接」这一种。
- 契约 `PhysicsCapabilities.SupportedJointKinds` 已存在，两个后端各自申报；后端可行性已核实：**Bepu 2.4 有 `BepuPhysics.Constraints.Weld`**（「All six degrees of freedom are solved simultaneously」），**Jolt 绑定 2.22 有 `SixDOFConstraint`**（每轴可带 spring）与 `FixedConstraint`。
- 内容里**没有** `jointPreprocessing` 键（`grep -c` = 0）；`tools/bple-joints` 已在报告里统计它，但没写进内容。
- Unity 侧现状（`ADR-001`）：本机装了 **Unity 6000.5.6f1**；`unity/PigForge.UnityReference` 只有 `Replay/ReferenceReplayExporter.cs` + smoke test，且导出的是 replay **v1**（.NET 侧是 v2，属已知陈旧项）→ 探针若要复用它的导出管线，需先把 schema 对齐；原版工程 `BPLE_Unity6` 则自带全部真 prefab 与 PhysX 设置。

## 3. 决策（候选）

1. **内容键 `capabilities.jointPreprocessing`（布尔，缺省 false）**：唯一来源 `tools/bple-joints`（照 `jointConnectionStrength` 的做法：报告驱动、幂等、硬断言 316/27）。必要性：这 27 件不只是框（含 `TimeBomb_01`）且变体可能不同，不能靠「是不是框」硬编码。
2. **只有「两端 preprocessing=true」的缝才拆成柔性关节**：一对里两端都是软件 → 拆成两个刚体 + 一个 `PhysicsJointKind.Weld`（六自由度 + `springFrequency/springDampingRatio`）；其余对（至少一端非软件）**保持现在的复合刚体**。
   - 好处：body 数只在**框链**上增长（一段 10 块的框墙 = 10 body + 9 Weld），绝大多数车体仍是少量 body；这也正是原版的分界。
   - 车轮仍是 Revolute（ADR-008/009），不改成 Weld；`CollectHinges`/`GameRoom._wheelJoints` 的管线可复用。
3. **断裂**：Weld 的阈值沿用每对 `seamBreakImpulse`（`ADR-015` 比例公式不变；单位偏差继续按 G15 记录）。柔性焊点被拉开/被炸时仍走「最近的缝」拆簇路径。
4. **协议不动**：每件成为独立 body 后 PGFS 本来就逐实体发 `position/rotation`（73 B/实体）→ 客户端无需改就能看到框结构的柔性形变。
5. **弹性参数是等效建模**：原版的「软」来自 PhysX **按行响应丢行**（负载相关的非线性行为），我们用线性 spring 近似 → 必须记 ADR 并给出标定锚与断言。

## 4. 有意偏差（相对原版只剩模型层）

- **模型层**：PhysX 的「丢弃硬行」是**取决于负载/冲量大小**的非线性行为；线性 `Weld` spring 无法逐字复现。可能的表现差异：原版框连接在**大冲击**下更容易让步（因为大冲量行恰好就是被丢弃的那些），我们的 spring 是等刚度的。
- **不做**（若用户不要求）：`FrameJointManager` 的框补充网（真值 6，也是软关节，一个框对可有 1 + 最多 16 条）。它是叠加，不是替代 → 见 §6 问题 2。
- **不引入**「木框比铁框软」的任何额外常数：两个框族在原版的关节软硬完全相同，差异只有断裂阈值与质量（真值 7）。

## 5. 分期（每期独立验收）

| 期 | 内容 | 验收 |
|---|---|---|
| **0** | **Unity 合规探针**（§6 A 类 1 的矩阵）：真 prefab + 真关节旗标 + 真 PhysX 量「相对位姿 × 时间」，导出 `tasks/weld-compliance-probe.json`，拟合出 `Weld` 的频率/阻尼比（必要时按质量分档） | 曲线可复现；拟合残差落在声明容差内；探针脚本与原始数据入库（`tasks/` 为工作区） |
| A | 内容 `jointPreprocessing`（提取器 + 五处同步 + 重生成 `playParts`） | **行为中性**：测试计数不变、`parts: 267`；提取器硬断言 316/27；解析器往返/拒绝用例 |
| B | 契约 `PhysicsJointKind.Weld`（六自由度 + spring 设置 + `SupportedJointKinds`）：Bepu `Weld`、Jolt `SixDOFConstraint`（绑定不足则明确 `NotSupportedException` 并记录） | 契约测试（非法参数拒绝）+ 两后端「两刚体被焊住：相对位姿保持；超阈值时拉伸量随 spring 改变」 |
| C | 装配策略 + 房间接线：只在软-软对上拆体并登记 Weld；断裂/脱钩/拆簇路径；`attachYaw` 与客户端回归 | `CompoundAssemblerTests`：软-软对 → 两个 body + 一个 Weld；其它对 → 仍是一个 body。`GameRoom` 房间级用例；双跑哈希一致 |
| D | 验收：基准（框链 body 数 vs 帧预算）、跨后端差分、实机探针（木框墙/框车过坎、重载、TNT 拆簇） | 数字写回本规格 |

## 6. 开放问题

**A 类（需甲方拍板）**

1. **柔性档位不猜，用 Unity 基准量（`ADR-001` 的离线参考运行时）**。原版没有可抄的 spring 数值（它的软来自 PhysX 丢行，随负载非线性），但**有可测的输入**：真 prefab（真质量/碰撞体）+ 真关节（六轴 Locked + `enablePreprocessing` 真值）+ 真 PhysX。
   - 探针矩阵（每格一条「相对位姿 × 时间」曲线）：
     | 组合 | 目的 |
     |---|---|
     | 木框↔木框（0.5 kg） | 用户所说的「最软」基准 |
     | 铁框↔铁框（1 kg） | 「要不软」的对照 |
     | `MetalFrame_131`↔铁框（4 kg） | 质量单调性检查（软硬到底随质量往哪边走） |
     | 木框↔铁框 | 混合对 |
     | 框↔非框（轮子/引擎） | 硬档基准（`enablePreprocessing = false`） |
     | 同上但 `m_currentGlue != None` | 胶只改 `breakForce`（真值 8）在曲线上是否可分辨 |
   - 施加方式固定：a) 悬挂末端挂已知质量、b) 一次已知冲量、c) 恒定横向负载；采样 60 Hz，导出到 `replays/`（replay schema 需从 v1 升到 v2，见真值/已知陈旧项）。
   - 交付物：`tasks/weld-compliance-probe.json`（原始曲线）+ 拟合出的 `Weld` 参数（频率/阻尼比，必要时按质量分档 + 每档的直方图断言）。
   - 只有拟合结果显示「单一档与实测差在容差外」时才引入分档；置信度：中（方法可靠，工作量在 Unity 侧）。
   - **待拍板点**：探针跑在 **`BPLE_Unity6`（原版工程，真 prefab/真设置，最权威）** 还是 PigForge 仓库里的 `unity/PigForge.UnityReference`（干净但只有图元，需复刻木/铁框的质量与碰撞体）？推荐前者，产出只落 `tasks/`/`docs/`。
2. **要不要同时做 `FrameJointManager` 的框补充网**（真值 6）？
   - 推荐：**先不做**（它只作用于轻框/支架框/包裹拳套的框，且是叠加的冗余连接；先看单条 Weld 的手感再定）。置信度：中。

**B 类（按默认落文档，不阻塞）**

- 断裂阈值沿用现有每对比例（含胶 `+∞`）；协议不动；Jolt 同步实现；车轮仍是 Revolute；框-框**不加**任何比铁框更软的额外常数（木/铁差别只允许来自实测拟合出的质量相关项，且必须能溯源到探针曲线）。
- 胶的建模照真值 8：**只改强度**（`+∞`），不额外改 `Weld` 的软度。

## 7. 验收计划（收工前必须报数字）

- 单测：A/B/C 三期用例 + 既有 `CompoundAssemblerTests`/`GameRoomTests`/`PropulsionGateTests` 全绿（五套分开跑）。
- 确定性：双跑同一冲量脚本比 `long` 哈希；跨后端（Bepu/Jolt）差分不隐藏分歧。
- 性能：`PigForge.Benchmarks`（框链 body 数上升后的帧预算，目标值待 A 类问题 1 一并定）。
- 实机：真服务器 + 真内容 + 真 Bepu 探针（木框墙受冲击的形变量、框车过坎、TNT 拆簇），数字写回本规格。
