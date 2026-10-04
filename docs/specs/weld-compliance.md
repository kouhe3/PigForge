# 规格：焊点刚度与断裂（weld stiffness &amp; breakage）—— 原版是「每对一关节」，会弯

状态：**期 0（原版基准探针）已交付，结论按实测两轮修正**；下一步是「按实测曲线拟合 Bepu `Weld`」。
来源与证据：

- **实测**：`unity/PigForge.WeldProbe`（Unity **2021.3.45f2**，即 `BPLE 2022.1.9/ProjectSettings/ProjectVersion.txt` 钉的原版编辑器）headless 跑出的
  `tasks/weld-compliance-probe.json`（28 格）。复现：`unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 -- -executeMethod PigForge.WeldProbe.Probe.WeldComplianceProbe.Run -logFile -`（需 `unity license activate --personal --accept-eula`）。
- 原版源码：`Contraption.cs:1507-1555`（`AddFixedJoint`）、`:1540`、`:2238-2247,2599`、`FrameJointManager.cs:60-193`、`Frame.cs:44-52`、`BasePart.cs:148,249,1184-1196`；`Part_*.prefab`；`INSettingsBExp.json`。
- **原版基准（上游）**：`https://github.com/anstropleuton/BPLE`（`main`）——BPLE 的反编译工程，作者**只修错误、不加新功能**，故作原版基准；本地 `BPLE 2022.1.9`（原版编辑器 2021.3.45f2）与 `BPLE_Unity6`（迁到 Unity 6 + PigForge 侧改动）是它的拷贝。

## 0. 结论（2026-10-04，第三版；前两版都被实测推翻）

**原版不是「焊成一个刚体」——每对相邻零件是一条真实关节，而这条关节在链式受弯时明显让步。**
（用户给的游玩截图：一长串木框弯成连续弧线 —— 帧的轮廓是**连续倾斜**的，而原版摆放只有 90° 步进，所以那个弧度只能是关节**受力变形**出来的。）

### 0.1 单关节在小载荷下量不出软（第一轮）

| 载荷 | 相对漂移 |
|---|---|
| 20 N 常力，质量 0.5 / 1 / 4 / 20 kg；50 kg 动态锚；20 m/s 冲量（关节力峰 493 N）；`enablePreprocessing` 开/关；2.5D 约束 56/0；显式 projection | 全部 **0**（<1e-6 m），Z 角 0° |
| `enablePreprocessing` 的唯一可测差异 | 与质量无关的 0.196 mm（噪声级） |
| 断裂 | `breakForce=1000` → 累计力 **1020 N** 断；`2400` → **2420 N** 断（2.37×，与内容 2.4× 一致） |

→ 单关节 + 小载荷**量不到软**；这一轮证明了「断裂阈值」的量纲语义，但**不足以判断结构刚度**。

### 0.2 链式受弯下关节让步很大（第二轮，决定性）

8 个 0.5 kg 木框排成链、**一端固定**、自重下垂（`chain8_*`）：

| 格 | 尖端下沉 | 单关节最大相对角 | 整链曲率（各关节角之和） |
|---|---|---|---|
| `chain8_ppon_gap0`（框相邻、碰撞体互相接触，原版网格常态） | **1.84 m** | **10.18°** | **22.66°** |
| `chain8_ppon_gap1`（隔 1 格，只有关节） | **6.55 m** | 19.04° | 42.68° |
| `chain8_ppoff_gap0` | 1.92 m | 14.10° | 22.69° |
| `chain8_ppoff_gap1` | 6.80 m | 26.56° | 42.50° |

两个要点：

1. **关节确实软**：一条 8 框链每关节弯 10°+，整链弯 22°+ —— 与截图一致。
2. **软度不是 `enablePreprocessing` 造的**：开关只改「各关节之间怎么分配」（单关节最大 10.18° vs 14.10°），**整链曲率几乎不变**（22.66° vs 22.69°）。真正的机制是：**锁死的 D6 关节是迭代求解的**（原版 `m_DefaultSolverIterations = 6`、velocity iterations 1；`eDISABLE_PREPROCESSING` 的注释就是「worse solver accuracy for ill-conditioned constraints」）——链根那条关节要扛整条链的弯矩，6 次迭代收敛不了，于是留下可见的偏转。
3. **相邻碰撞体的接触也在出力**：`gap0` 比 `gap1` 下沉少 3.5 倍（1.84 vs 6.55 m），因为 1×1 的框面对面挡住彼此的转动。原版是「关节 + 接触」一起给的这种手感。

## 1. 原版真值（与实测一致）

| # | 规则 | 出处 |
|---|---|---|
| 1 | 相邻每对零件 = 一条真实关节：`ConfigurableJoint` 六轴 `Locked`；任一端 `m_jointType == HingeJoint` 时改用 `HingeJoint`（±0.1°） | `Contraption.cs:1507-1546` |
| 2 | **没有 spring/damper**：全 343 prefab 零弹簧字段 → 关节的「软」是**迭代求解的残差**（链式弯矩下 10°–26°/关节，实测 §0.2），不是参数 | 报告 `noSpringAnywhere`；§0.2 |
| 3 | `enablePreprocessing`（`a && b`，27/343 件 = 全部木/铁/彩框 + TimeBomb）：改的是**关节间的分配**，不改总曲率（实测 22.66° vs 22.69°） | `Contraption.cs:1540`；§0.2 |
| 4 | 相邻件的碰撞体**不屏蔽互相碰撞**（框面对面接触，实测把下沉从 6.55 m 压到 1.84 m） | `Part_*.prefab` 1×1×1；§0.2 |
| 5 | `breakForce = gs(a)+gs(b)`（再乘 `ConnectionStrength`）：实测 1000→1020 N、2400→2420 N 断；`breakTorque` 未设（默认 ∞） | `Contraption.cs:1541-1543,2268`；`Contraption.AddJointToMap`；§0.1 |
| 6 | 强力胶只改强度：`MakeUnbreakable()` 把每条关节 `breakForce = +Inf`；**无任何站点改 `enablePreprocessing`** | `:2238-2247,2599` 等 |
| 7 | 木/铁的全部物理输入只差质量（0.5 / 1 / 4；TimeBomb 0.2）与断裂阈值（1000 vs 2400）；`BoxFrame` 的 IN 质量 feature 在 BExp 无覆盖 → 均 1.0 | prefab；`BoxFrame.cs:9-18` |
| 8 | 原版没有关节软度的 IN 开关（只有 `ConnectionStrength` 54 与 `FrameJoint` 80） | `INFeature.cs`；`INSettingsBExp.json` |

## 2. PigForge 现状（要改的就是这里）

- `ADR-011` 决策 2/3：相邻可合并件**焊进同一复合刚体** → 关节数 0、成员之间**不产生接触**。
  ⇒ 原版的两种让步来源（关节的迭代残差 + 相邻接触）**都被消掉**：我们的框链是刚的，长结构不下垂也不弯；用户截图里的「会弯的框墙」在我们这里做不出来。
- `ADR-015` 已实现每对断裂阈值按强度比例缩放（木 1.0 / 铁 2.4 = 原版比例）✓，但阈值是**冲量幅值**而原版是**牛顿**（G15 单位偏差）。
- 契约已有 `PhysicsJointKind.{Distance, Revolute}`（Revolute 带 spring，ADR-012 轮子悬挂）；后端可用：Bepu 2.4 `Weld`（六自由度同解）、Jolt `SixDOFConstraint`。
- 内容里没有 `jointPreprocessing` 键；提取器已在报告里统计它。

## 3. 决策（按第二轮实测）

1. **`Weld` 弹簧关节是还原项，不是偏差**（第二轮实测推翻第一轮）：目标是把实测曲线拟合出来——
   单关节角位移对**弯矩**的响应（链根 ≈ 整链弯矩），而不是对拉力的响应（那本来就 ~0）。
2. **拆体策略**：只在**承载会出现可见弯矩**的缝上拆（框链/长结构），或按内容强度分档；`body` 数随刚度需求增长，需与基准/确定性一起验。
   先做最小可验：把「框↔框」缝拆成两体 + `Weld`，标定到 `chain8_ppon_gap0` 的 10.18°/关节、整链 22.66°（原版网格常态）。
3. **相邻接触必须保留**：两体之间照原版不屏蔽碰撞（`gap0` 与原版网格一致；`gap1` 只是诊断格）。若拆体后成员仍互相穿插，`CompoundAssembler` 的合并规则要同步调整。
4. **断裂**：继续用每对比例阈值；同时用实测的 1020/2420 N 把阈值从纯锚定的 `10f` 换成「事件 ↔ 冲量」的可复算换算（G15 收口）。
5. **`enablePreprocessing`**：确实起作用的只是分配细节（不是总量）→ 先不建模，只作事实记录。

## 4. 分期

| 期 | 内容 | 验收 |
|---|---|---|
| 0 | 原版基准探针（2021.3.45f2 + 原版关节/物理；28 格，含 8 框链） | **已交付**：`unity/PigForge.WeldProbe`、`tasks/weld-compliance-probe.json` |
| 1 | 拟合目标固化：把 `chain8_ppon_gap0/gap1` 的「尖端下沉 + 每关节角」写成本规格的验收表（含容差） | 表格 + 容差进本规格；探针可复跑 |
| 2 | 契约 `PhysicsJointKind.Weld`（六自由度 + spring）→ Bepu `Weld` / Jolt `SixDOFConstraint` | 契约测试（非法参数拒绝）+ 两后端「两刚体保持相对位姿，且角位移随 spring 变化」 |
| 3 | 装配：按缝拆体 + 保留相邻接触 + 房间接线 + 断裂路径 | `CompoundAssemblerTests`（框链拆成 N 体 + N-1 Weld）、`GameRoom` 房间级用例、双跑哈希一致 |
| 4 | 对照验收：Bepu 侧跑同一 8 框链，比 `chain8_ppon_gap0` 的下沉/角 | 数字落在期 1 的容差内；基准（body 数 vs 帧预算）与实机探针写回 |

### 4.1 拟合目标与容差（期 1 固化）

基准 = `tasks/weld-compliance-probe.json` 的 `chain8_ppon_gap0` / `chain8_ppon_gap1`
（Unity 2021.3.45f2 + 原版关节/物理设置，50 Hz，6 s 稳态）：

| 指标 | 目标 gap0（框相邻、碰撞体接触） | 目标 gap1（隔 1 格、只有关节） | 容差 |
|---|---|---|---|
| 尖端下沉 | **1.84 m** | **6.55 m** | ±25% |
| 单关节最大相对角 | **10.18°** | **19.04°** | ±25% |
| 整链曲率（Σ 各关节角） | **22.66°** | **42.68°** | ±25% |

- 口径：8 个 0.5 kg 木框、1×1×1 碰撞体、`drag 0.2` / `angularDrag 0.05`、2.5D 自由度锁定（原版 `(RigidbodyConstraints)56`）、一端固定、自重下垂、6 s 取稳态。
- 两处**必然**的偏差要一起报：①引擎不同（Bepu vs PhysX 4.1）；②**tick 不同**（PigForge 60 Hz vs 原版 50 Hz）。容差为它们留白；超出容差时先核对口径（自由度锁定、质量、步长）再调参数，不许直接改目标。
- 期 4 的物理验收放在 `PigForge.Physics.Tests`（Bepu 直连同一条链）；房间级用例只负责装配正确性与确定性，不背这两个数字。

## 5. 开放问题（需甲方拍板）

1. ~~**拆体范围**~~ —— **已定（2026-10-04，用户）**：**只拆框↔框的缝**（body 数只在框链上增长），用期 4 的实测对照兜底；不够再扩大。
2. **软度用 Bepu 的什么机制拟合**：`Weld` 的 spring 频率/阻尼比（连续弹簧）还是「有限迭代 + 每 tick 一次的约束修正」（更接近原版的迭代残差）？
   - 推荐：先试 `Weld` + spring（契约里已有 spring 字段），拟合不上再考虑迭代方案。置信度：中。
3. **断裂阈值换算**：用 1020/2420 N 与「规则层事件产生的冲量」定一个可复算常数（替换 `10f`）？
   - 推荐：做（G15 真还原）。置信度：中高。

## 6. 复现

```bash
unity license activate --personal --accept-eula        # 一次性
unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 --timeout 1200 \
  -- -executeMethod PigForge.WeldProbe.Probe.WeldComplianceProbe.Run -logFile -
```

格子：A `load_pp{on,off}_c{56,0}_m{0.5,1,4,20}`、B `rig_pp{on,off}`、C `break_{1000,2400}`、D `impulse_pp{on,off}`、E `proj_pp{on,off}`、**F `chain8_pp{on,off}_gap{0,1}`**。
