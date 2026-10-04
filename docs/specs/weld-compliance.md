# 规格：焊点的弹性（weld compliance）——「不止木框」

状态：**设计（未实现）**，待 §6 的 A 类问题拍板。
来源（全部实测，非转述）：

- 原版：`Contraption.cs:1507-1555`（`AddFixedJoint`）、`:1540`（`enablePreprocessing`）、`FrameJointManager.cs:60-193`、`BasePart.cs:148,249`；`Assets/GameObject/Part_*.prefab` 全量字段统计。
- 报告：`tasks/bple-jointstrength-report.json` → `prefabScan.preprocessingHistogram`（316 / 27）、`frameElasticityDifferential`（木 vs 铁：**无 spring**，只差阈值 2.4× 与质量 2×）、`noSpringAnywhere`。
- 现状：`docs/decisions/ADR-011`（决策 2/3：焊成复合刚体）、`ADR-012`（Revolute 的 `springFrequency/springDampingRatio`）、`ADR-015` §决策 4（框的弹性不做）。

## 1. 原版真值

| # | 规则 | 出处 |
|---|---|---|
| 1 | **相邻每对零件 = 一个真实关节**：`ConfigurableJoint`，`x/y/z + angularX/Y/Z` 六轴全 `Locked`；任一端 `m_jointType == HingeJoint` 时改用 `HingeJoint`（`limits.min/max = ∓0.1°`、`axis = forward`） | `Contraption.cs:1507-1546` |
| 2 | **唯一的「软硬」开关**：`joint.enablePreprocessing = part.JointPreprocessing && other.JointPreprocessing`。PhysX 语义：预处理**开** → 关节在求解前置阶段就被满足（硬、不抖）；**关** → 只在迭代里满足，受力会拉伸错位（玩家感到「软/有弹性」） | `Contraption.cs:1540`；`BasePart.cs:148,249` |
| 3 | `m_jointPreprocessing: 1` 恰好 **27/343** 件（`grep -l` 实测）：`ColoredFrame`、`MetalFrame_01..12`、`MetalFrame_131/132`、`TimeBomb_01`、**`WoodenFrame_01..11`**。→ **框↔框（木或铁）＝硬；任何一端不是框＝软**。「弹性」是**默认**，「硬」才是例外 | 同上；报告 `preprocessingHistogram` |
| 4 | **全 343 prefab 没有任何 spring/damper/softness**：`noSpringAnywhere`（唯一的类弹簧字段是 `Part_MotorWheel_08_SET` 的 `m_springStiffness: 50`，属轮子悬挂） | 报告 `frameElasticityDifferential.noSpringAnywhere` |
| 5 | `FrameJointManager` 只为 3 类特例件（轻框 idx 10、支架框 idx 131、框内包裹 `SpringBoxingGlove` index 4）**补**刚性 `FixedJoint`（`enablePreprocessing = true`、`breakForce = 600 × ConnectionStrength`），且只补不删 → **框是被加固，不是被软化** | `FrameJointManager.cs:60-193`；报告 `b_FrameJointManagerFull` |
| 6 | 「木框更软」是**涌现**的：通用路径木↔木 1000 vs 铁↔铁 2400（2.4×），质量 0.5 vs 1，碰撞体/阻尼/插值完全相同 | 报告 `completedComparison_4`/`mass_5`/`d_negativeEvidence` |

**结论（对用户提问的直接回答）**：原版里「木框↔木框」是**最硬**的一类，没有弹性；有弹性的是**除框-框外的所有焊点**（因为另一端没有 `m_jointPreprocessing`）。所以「不止木框」正好等于「回到原版通用行为」；而「让木框-木框也软」是**有意偏差**（HANDOFF §6.5 的「两步走」第二步，第一步＝每件强度已落地）。

## 2. PigForge 现状

- `ADR-011` 决策 2/3：相邻可合并件**焊进同一个复合刚体**，关节数为 **0** → 焊点两侧没有任何相对自由度，弹性**无处存在**。这是本规格要动的核心。
- 契约已有 `PhysicsJointKind.{Distance, Revolute}`；`Revolute` 已带 `SpringFrequency`/`SpringDampingRatio`（ADR-012 轮子悬挂）→ 「弹簧关节」的管线在，缺的是「焊接」这一种关节。
- 契约已有 `PhysicsCapabilities.SupportedJointKinds`，两个后端各自申报（Jolt 对未实现的关节抛 `NotSupportedException`）。
- 后端可行性**已核实**：Bepu 2.4 有 `BepuPhysics.Constraints.Weld`（「All six degrees of freedom are solved simultaneously」）；Jolt 绑定（2.22.0）有 `SixDOFConstraint`（每轴可带 spring）与 `FixedConstraint`。
- 内容里**没有** `jointPreprocessing` 键（`grep -c` = 0）；提取器已在报告里统计它，但没写进内容。

## 3. 决策（候选，待实现）

1. **内容键 `capabilities.jointPreprocessing`（布尔，缺省 false）**，唯一来源 `tools/bple-joints`（照 `jointConnectionStrength` 的做法：报告驱动、幂等、硬断言 316/27）。
   必要性：这 27 件**不只是框**（还含 `TimeBomb_01`），且变体可能不同 → 不能靠「是不是框」硬编码。
2. **装配策略改成「两端都 preprocessing 才合并」**：一对里只要有一端 `jointPreprocessing = false`，就拆成两个刚体 + 一个 **`PhysicsJointKind.Weld`**（六自由度 + `springFrequency/springDampingRatio`）。
   - 结果：一个载具 = 若干「硬核」（框族彼此焊死）+ 其余件各自一个 body，用可拉伸的 Weld 挂在核上——这正是原版「每件一刚体 + 每对一个关节」的可负担近似（原版是**每件**一刚体，我们只在**软件对**上拆）。
   - 车轮已是「独立 body + Revolute」（ADR-008/009），这条管线可复用（`CompoundAssembler.CollectHinges`、`GameRoom._wheelJoints`）。
3. **断裂**：`Weld` 的断裂阈值沿用每对 `seamBreakImpulse`（`ADR-015` 的比例公式不变）；单位偏差（原版 `breakForce` 是牛顿，我们是冲量幅值）继续按 `ADR-015` §5/G15 记录。
4. **协议不动**：每个零件成为独立 body 后，PGFS 本来就**逐实体**发 `position/rotation`（73 B/实体），客户端无需改就能看到柔性形变。
5. **弹性参数是 PigForge 的等效建模**（原版没有 spring 数值可抄，它的「软」来自 PhysX 的预处理关闭）→ 必须记 ADR，并给出标定锚与直方图断言。

## 4. 有意偏差（要写进 ADR）

- **框↔框也做成弹性的**（若用户选这一档）：原版是最硬的一类；做了就是超出原版的手感偏差（用户已同意「两步走」，本规格是第二步的收口）。
- **弹性强度自定**：原版无 spring 数值；我们用频率/阻尼等效，需声明这是建模选择而不是移植。

## 5. 分期（每期都能独立验收）

| 期 | 内容 | 验收 |
|---|---|---|
| A | 内容 `jointPreprocessing`（提取器 + 五处同步 + 重生成 `playParts`） | **行为中性**：全量测试计数不变、`parts: 267`；提取器硬断言 316/27；解析器往返/拒绝用例 |
| B | 契约：`PhysicsJointKind.Weld`（六自由度 + spring 设置 + `SupportedJointKinds`），Bepu 实现（`Weld`），Jolt 实现（`SixDOFConstraint`）或明确 `NotSupportedException` | 契约测试（非法参数拒绝）+ 两后端「两刚体被焊住 → 相对位姿保持、超阈值拉伸量随 spring 变化」 |
| C | 装配策略 + 房间接线：按内容拆体/合并、`Weld` 关节登记、断裂路径、快照与 `attachYaw` 回归 | `CompoundAssemblerTests` 新用例（同布局，一端 preprocessing=false → 两个 body + 一个 Weld；两端 true → 仍是一个 body）；`GameRoom` 房间级用例；双跑哈希一致 |
| D | 验收：基准（body 数/帧预算）、跨后端差分、实机探针（木架车过坎/重载/断裂）、文档与 ADR | 基准报告 + 实机数字进规格 |

## 6. 开放问题

**A 类（需甲方拍板）**

1. **框↔框要不要也软？** 原版是硬的（两端 `m_jointPreprocessing = 1`）。
   - 推荐：**先只做原版（非框-框软），框-框保持硬**，试玩后再按手感开"框也软"的开关。置信度：中（用户这次的原话偏向"框也要"，但那会同时软化所有木架结构，断裂与堆叠手感都会变）。
2. **弹性强度给到什么程度**（Weld 的 spring 频率/阻尼）：原版无数值可抄。
   - 推荐：**单一档 + 两个标定常数**（频率取轮子悬挂同数量级、阻尼比接近临界 1.0，避免长时间抖动），先用实机（木架车过坎/被炸）定，再决定是否按 `m_jointConnectionStrength`/质量分档。置信度：低（纯手感，必须试玩）。

**B 类（按默认落文档，不阻塞）**

- 断裂阈值沿用现有每对比例（含胶的 `+∞`）；协议不动；Jolt 后端同步实现（若绑定不足则明确 `NotSupportedException` 并记录）；车轮仍是 Revolute（不改成 Weld）。

## 7. 验收计划（收工前必须报数字）

- 单测：A/B/C 三期各自的用例 + 既有 `CompoundAssemblerTests`/`GameRoomTests`/`PropulsionGateTests` 全绿（`dotnet test` 五套分开跑）。
- 确定性：双跑同一冲量脚本比 `long` 哈希；跨后端（Bepu/Jolt）差分不隐藏分歧。
- 性能：`PigForge.Benchmarks`（body 数上升后的帧预算）——需在 A 类问题 3 里给目标。
- 实机：真服务器 + 真内容 + 真 Bepu 探针（木架车下坡/过坎、重载、TNT 拆簇），数字写回本规格。
