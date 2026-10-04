# 规格：焊点刚度与断裂（weld stiffness &amp; breakage）—— 实测结论

状态：**期 0（原版基准探针）已交付**；结论改写了本规格的方向。
来源与证据：

- **实测**：`unity/PigForge.WeldProbe`（Unity **2021.3.45f2**，即 `BPLE 2022.1.9/ProjectSettings/ProjectVersion.txt` 钉的原版编辑器）用 Unity CLI headless 跑出的
  `tasks/weld-compliance-probe.json`（24 格，脚本头部逐条带源码引用）。
- 原版源码：`Contraption.cs:1507-1555`、`:1540`、`:2238-2247,2599`、`FrameJointManager.cs:60-193`、`Frame.cs:44-52`、`BasePart.cs:148,249,1184-1196`；`Part_*.prefab` 全量字段；`INSettingsBExp.json` / `INDeclarationSettingsExp.json`。
- 文档语义：Unity 6.6 `Joint.enablePreprocessing`、PhysX 3.4 `PxConstraint::setMinResponseThreshold` / `eDISABLE_PREPROCESSING`。
- 上游核对：公开反编译仓 `github.com/anstropleuton/BPLE`（"Decompilation of BPLE"，C#，2026-09 仍更新）与本地 `BPLE 2022.1.9` / `BPLE_Unity6` 同源。

## 0. 结论（2026-10-04，先看这一节）

**原版的相邻零件焊接在玩家量级下是「刚」的，没有弹簧式弹性。**

探针把原版的关节建法逐条照搬（六轴 Locked、anchor 取相对位置一半、`enablePreprocessing = a && b`、`breakForce = gs(a)+gs(b)`），
在**原版编辑器 + 原版物理设置**下量「相对位的漂移 × 时间」，24 格里：

| 载荷 / 变量 | 结果 |
|---|---|
| 20 N 常力，质量 0.5 / 1 / 4 / 20 kg，`enablePreprocessing` 开与关，2.5D 约束 56 与 0 | **漂移 0**（< 1e-6 m） |
| 50 kg 动态锚（真实整车质量比 100:1） | **漂移 0**，关节力 19.7 N |
| 20 m/s 冲量瞬态 | **漂移 0**，关节力峰值 493 N |
| 显式 `projectionMode = PositionAndRotation` 对照 | 同样 0（刚度不来自 projection） |
| `enablePreprocessing` 的唯一可测差异 | 与质量无关的 **0.196 mm** Y 向差（噪声级）；开关对玩家可感的软硬**没有贡献** |
| **断裂阈值** | `breakForce = 1000` 在累计 **1020 N** 时断，`2400` 在 **2420 N** 时断 → 比例 **2.37×**，与内容里 `(gs(a)+gs(b))×ConnectionStrength` 的 2.4× 一致 |

因此：

1. **玩家的「木框比铁框软」＝更早断，不是更会形变**：木↔木 1000 N、铁↔铁 2400 N。这条我们**已经实现**（`ADR-015`：每对 seam 阈值按两端强度比例缩放，木 1.0 / 铁 2.4）。
2. **`Weld` 弹簧关节不属于「还原」**：做了就是**有意偏差**（手感偏好），要不要做由用户定（见 §6）。
3. **`enablePreprocessing` 的内容键不再是物理必需**（它在玩家量级没有可测效果）；但它是原版真值、且是文档里那条「丢弃硬行」机制的载体，故仍可作为**只读事实**记录，不驱动行为。
4. 真正值得投入的还原项转为**断裂阈值的单位标定**（`G15`）：现在有了实测的 1020 / 2420 N 断点，可以把内容里的 seam 冲量阈值锚定到「什么样的事件应当断开」——我们的 `BreakImpulse` 是冲量幅值，与 PhysX 的牛顿阈值不同量纲（`ADR-015` §5 记录的偏差）。

## 1. 原版真值（与实测一致的静态事实）

| # | 规则 | 出处 |
|---|---|---|
| 1 | 相邻每对零件 = 一个真实关节：`ConfigurableJoint` 六轴 `Locked`；任一端 `m_jointType == HingeJoint` 时改用 `HingeJoint`（±0.1°） | `Contraption.cs:1507-1546` |
| 2 | 唯一的软硬开关 `joint.enablePreprocessing = a && b`；文档说打开时 PhysX 丢弃「冲量巨大而速度变化极小」的约束行（Unity 6.6 手册 / PhysX 行响应阈值）——**实测在玩家量级没有可测效果（≤0.2 mm，§0）** | `Contraption.cs:1540`；`Joint.enablePreprocessing` |
| 3 | `m_jointPreprocessing: 1` 恰好 **27/343** 件（全部木/铁/彩框 + TimeBomb）；其余 316 件的对为 false → 若该机制起作用，软硬分界应是「框↔框 / 其余」 | prefab 全量统计；`tasks/bple-jointstrength-report.json` |
| 4 | 全 343 prefab **无 spring/damper** | 报告 `noSpringAnywhere` |
| 5 | `FrameJointManager` 只对 3 类特例件**补** `FixedJoint`（`enablePreprocessing = true`、`breakForce = 600×ConnectionStrength`），只补不删 | `FrameJointManager.cs:60-193` |
| 6 | 强力胶**只改强度**：`MakeUnbreakable()`（`:2238-2247,2599`）把 `m_jointMap` 每条 `breakForce = +Inf`；各族 `HasSuperGlue` 分支同样只给 `+∞`/放宽触发；**无任何站点改 `enablePreprocessing`** | 上列文件行 |
| 7 | 木/铁的全部物理输入只差质量（0.5 / 1 / 4 / TimeBomb 0.2）与断裂阈值；`BoxFrame` 的 IN 质量两个 feature 在 BExp 无覆盖 → 均 1.0 | prefab；`BoxFrame.cs:9-18` |
| 8 | 原版没有关节软度的 IN 开关（只有 `ConnectionStrength` 54 与 `FrameJoint` 80） | `INFeature.cs`；`INSettingsBExp.json` |

## 2. PigForge 现状

- `ADR-011` 决策 2/3：相邻可合并件焊进**同一复合刚体**（关节数 0）→ **刚度上与实测的原版一致**（都是刚的）；差异在**形状**：原版是「每件一刚体 + 每对一个关节 + 牛顿阈值」，我们是「簇 + 缝 + 冲量阈值」。
- `ADR-015` 已实现每对断裂阈值按两端强度比例缩放（木 1.0 / 铁 2.4 = 原版比例）→ §0 第 1 条已覆盖。
- 契约已有 `PhysicsJointKind.{Distance, Revolute}`（Revolute 带 spring，ADR-012 轮子悬挂）；`SupportedJointKinds` 由后端申报。后端可行性（若要做偏差版）：Bepu 2.4 `Weld`、Jolt 绑定 `SixDOFConstraint` 均存在。
- 内容里**没有** `jointPreprocessing` 键（`grep -c` = 0）；提取器已在报告里统计它。
- 探针宿主：`unity/PigForge.WeldProbe`（2021.3.45f2，最小工程，只有探针脚本 + 原版物理设置），
  `unity/PigForge.UnityReference`（6000.5.6f1）保持原样（它是 `ADR-001` 的参考运行时，PhysX 代不同，不用于本次基线）。

## 3. 决策（按实测重排）

1. **不做 `Weld` 弹簧关节作为还原项**：实测没有可复现的弹性可言；原版「软」= 断裂阈值。若用户仍想要「结构会晃」的手感，那是**有意偏差**，按 §6 的参数标定流程单独立项。
2. **`jointPreprocessing` 只作为事实记录**（可选）：写进内容不驱动行为，或干脆只留在报告里。**默认：不写进内容**（避免给人「它有用」的错觉）。
3. **转向断裂阈值的单位标定**：用实测的 1020 / 2420 N 与我们的冲量阈值对齐——先量「同样的事件在我们的规则层产生多大冲量」（TNT 25、拆簇冲量、落地冲击），再定一个可复算的换算常数，替换 `ADR-015` §5 里那个纯锚定的 10f。
4. 协议不动；`FrameJointManager` 的补充网不做（只作用于 3 类特例件，且是叠加）。

## 4. 有意偏差（如果要做「会晃的结构」）

- 原版没有弹簧式弹性（实测）；加 `Weld` 弹簧 = 纯手感偏差。
- 参数标定流程（若立项）：`unity/PigForge.WeldProbe` 已具备量曲线的能力，但**没有原版数值可拟合** → 只能按手感试玩定（单档 + 频率/阻尼；`docs/specs/fan-propeller.md` §7 的经验：先单档、后分档）。
- 影响面：只在「软-软对」拆体（框链），body 数增加有限；断裂路径、客户端（PGFS 逐实体位姿已足够）与确定性都要重验。

## 5. 交付记录

| 期 | 内容 | 状态 |
|---|---|---|
| 0 | 原版基准探针（原版编辑器 2021.3.45f2 + 原版关节/物理设置，24 格曲线） | **已交付**：`unity/PigForge.WeldProbe`、`tasks/weld-compliance-probe.json` |
| — | 规格按实测改写（本节） | 已交付 |
| A | *（可选）*「会晃的结构」偏差版：内容键 + `Weld` 关节 + 只在软-软对拆体 | 待用户决定 |
| B | **断裂阈值单位标定**：用实测断点把 seam `BreakImpulse` 锚定到物理事件（替换 10f 的纯锚定） | 待立项（推荐） |

## 6. 开放问题（需甲方拍板）

1. **「结构会晃的弹性」到底要不要做？** 实测证明原版没有（§0）；做了就是**有意偏差**。
   - 推荐：**先不做**，先把 §5 的 B（断裂阈值标定）做掉——它是真还原，且用量到的 1020/2420 N 就能推进。置信度：中（手感类诉求，只有试玩能拍板）。
   - 若要做：按 §4 的流程（单档参数 → 试玩 → 再决定分档），并在 ADR 里写明这是偏差。
2. **`jointPreprocessing` 要不要写进内容**（即使不驱动行为）？
   - 推荐：**不写**（避免误导后续读者以为它有物理作用）。置信度：中。

## 7. 复现方式

```bash
# 需要有效 license：unity license activate --personal --accept-eula
unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 --timeout 1200 \
  -- -executeMethod PigForge.WeldProbe.Probe.WeldComplianceProbe.Run -logFile -
# 输出：unity/PigForge.WeldProbe/replays/weld-compliance-probe.json（抄进 tasks/ 作证据）
```

格子矩阵：A `load_pp{on,off}_c{56,0}_m{0.5,1,4,20}`、B `rig_pp{on,off}`、C `break_{1000,2400}`、D `impulse_pp{on,off}`、E `proj_pp{on,off}`。
