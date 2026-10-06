# 规格：焊点刚度与断裂（weld stiffness &amp; breakage）—— 原版是「每对一关节」，会弯

状态：**期 0–4 全部交付（2026-10-04）**：原版基准探针 → 验收表 → 契约 `Weld` → 框↔框拆体与房间接线 → 用同一条 8 框链对照验收（见 §4.4）。
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
| 5 | `breakForce = (gs(a)+gs(b)) × ConnectionStrength`：声明默认档（vanilla）的倍率是 1，木↔木 **250**、木↔铁 725、铁↔铁 **1200**（B 档 2.0 会把这四档整体翻倍，`docs/specs/in-settings-profiles.md`）；探针用 1000 → **1020 N**、2400 → **2420 N** 验证断点≈声明值；`breakTorque` 未设（默认 ∞） | `Contraption.cs:1541-1543,2268`；`Contraption.AddJointToMap`；§0.1 |
| 6 | 强力胶只改强度：`MakeUnbreakable()` 把每条关节 `breakForce = +Inf`；**无任何站点改 `enablePreprocessing`** | `:2238-2247,2599` 等 |
| 7 | 木/铁的全部物理输入只差质量（0.5 / 1 / 4；TimeBomb 0.2）与断裂阈值（vanilla 250 vs 1200，B 档 1000 vs 2400）；`BoxFrame` 的 IN 质量 feature 在声明默认档无覆盖 → 均 1.0 | prefab；`BoxFrame.cs:9-18` |
| 8 | 原版没有关节软度的 IN 开关（只有 `ConnectionStrength` 54 与 `FrameJoint` 80） | `INFeature.cs`；`INSettingsBExp.json` |

## 2. PigForge 现状（要改的就是这里）

- ~~`ADR-011` 决策 2/3：相邻可合并件**焊进同一复合刚体** → 关节数 0、成员之间**不产生接触**。~~
  **已改（2026-10-04，期 3）**：**框↔框例外** —— 两端都是 `canEnclose` 的缝不再 union，而是两个 body + 一条软 `Weld`（且**保留相邻碰撞**）。其余件仍按 `ADR-011` 焊进同一刚体。见 §4.4。
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
| 2 | 契约 `PhysicsJointKind.Weld`（六自由度 + spring）→ Bepu `Weld` / Jolt `SixDOFConstraint` | **已交付（2026-10-04）**：见 §4.3 |
| 3 | 装配：按缝拆体 + 保留相邻接触 + 房间接线 + 断裂路径 | **已交付**：见 §4.4 —— `CompoundAssemblerTests`（框链拆成 N 体 + N-1 Weld）、`FrameWeldRoomTests`（房间级 6 条用例）、双跑哈希一致 |
| 4 | 对照验收：Bepu 侧跑同一 8 框链，比 `chain8_ppon_gap0` 的下沉/角 | **已交付**：见 §4.4 —— `WeldComplianceTests` 三条落在 §4.1 容差内；基准与实机探针写回 |

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

### 4.2 期 3 接入点（侦察结果，2026-10-04）

「框」在内容里就是 **`capabilities.canEnclose == true`**（22 件：`wooden-block` 1、`metal-box` 18、各变体…；`PartContentParser` 的 `canEnclose` 分支 → `PartCapabilities.CanEnclose`；模型侧孪生＝`ConstructionRules.IsChassis`）。要改的点，按依赖序：

1. **合并判据**：`CompoundAssembler.CanMergePair`（`:730-744`，逐字照 `Contraption.cs:690`）保持不动，**另加**一条「两端 `CanEnclose` 则不 union」的判据，接在邻居 union 循环 `Assemble`（`:392-402`）之前——这样框↔框永远分成两个 cluster。
2. **每对 weld 的登记**：在 `CompoundAssembly`（`:44-46`）增加一条按 `(Left, Right)` 排序的 weld 列表（照 `CollectHinges` 的确定性做法 `:778-836`、seams 的 `Sort` `:1013`），位姿取两 member 的 transform（同 `CollectHinges` 模式）。
3. **房间接线**：`GameRoom` 新增 `BindWeldJoints(assembly.Welds)`，镜像 `BindWheelHinges`（`:1406-1478`），在三处 materialize 点紧跟其调用（`:564`、`:896`、`:934`）；**每条 weld 必须调 `LinkPowerCluster(frameA, frameB)`**（先例 `:1472`）——否则 `PowerClusterKey`（`GameplayRules.cs:917-941`）会让另一个框上的引擎不再给这个框的耗能件供能。
4. **关节生命周期**：`_weldJoints` 新表，并扩 `ForgetJointsForBody`（`:1657-1674`），否则拆簇/销毁会泄漏仍约束着已销毁 body 的关节。
5. **断裂**：拆体后该对**没有 seam**（`NearestSeam`/`SplitAlongSeam` 都按 seam 工作）→ weld 条目自己要带中点与 `BreakImpulse`（沿用现有强度数学 `:1000-1009`），否则框对变成不可断。
6. **随之变化的量**：`BodyCount`/`_entitiesByBody.Count`（缓冲尺寸 `:1957`）、`GameplayRules` 的每 body 代表实体/材质聚合（`:253-303`）——都是「一个 body 一个代表」的假设，拆体后每个框各自代表自己。
7. **最大风险（要在期 3 实测）**：**Bepu `Weld` 没有 anchor 参数**（它约束 B 在 A 坐标系里的相对位姿）→ 契约要带这个相对位姿；并且 §3.3 要求**保留相邻接触**，焊接的两面会互相推（原版如此），其在本仓库求解器下的稳定性**未经验证**。

### 4.3 期 2 交付记录（2026-10-04）

- **契约**：`JointDefinition.Weld(...)`（`PhysicsJointKind.Weld`），参数 = 两侧局部 anchor + `breakForce`/`breakTorque` + 可选 `springFrequency`/`springDampingRatio`（0 = 刚性）+ **`RestRotation`**（B 的局部坐标系在 A 里的静息朝向，缺省 Identity）。`RestRotation` 是我复查时补的：weld 必须锁在**摆放时的相对位姿**，而框按 90° 步进摆放是常态——原版 `ConfigurableJoint` 锁的就是创建时的相对位姿。
- **Bepu**（`BepuPhysics 2.4 Constraints.Weld`：`LocalOffset`/`LocalOrientation`/`SpringSettings`）：`LocalOffset = anchorA − RestRotation·anchorB`（**不能**直接 `anchorA − anchorB`，那个只在相对朝向为单位四元数时成立，我复查时抓到这个 bug），`LocalOrientation = RestRotation`；spring 覆盖位置与朝向（Bepu 的软 weld 是完整六自由度弹簧）。
- **Jolt**（`SixDOFConstraintSettings` + 六轴 `MakeFixedAxis`）：A 的约束轴取单位基，**B 的轴取 `RestRotation⁻¹` 旋过**（这样两系只在 B 处于摆放朝向时对齐）。**已知分歧**：JoltPhysicsSharp 的 `SixDOFConstraintSettings` 只有 3 个 translation 的 `limitsSpringSettings`，旋转限位没有 spring → Jolt 的软 weld 是「平移软、旋转仍硬」，与 Bepu 不同；已在 `CreateJoint` 注释里写明是**有意分歧**而非静默降级。Jolt 目前只实现 weld 一种关节（其余抛 `NotSupportedException`）。
- **能力申报**：Bepu `{Revolute, Distance, Weld}`；Jolt `{Weld}`。
- **测试**：非 Jolt **41**（+9）、Jolt **11**（+5）；含契约（缺省 Identity、非有限值拒绝）、两后端各一条「quarter-turn 摆放保持 90° 且间距 1」——**非空验证**：把 `LocalOrientation`/`LocalOffset`/Jolt 轴改回朴素写法即红。
- 验收命令：`dotnet test … --filter "FullyQualifiedName!~Jolt"` 与 `--filter "FullyQualifiedName~Jolt"` 分开跑；`dotnet build PigForge.slnx -c Release` 与（Jolt 不在 slnx 里）单独 `-c Release` 编译 Jolt 工程。

### 4.4 期 3/4 交付记录（2026-10-04）

**装配（期 3）**

- `CompoundAssembler` 的邻居 union 循环里多一条判据：**两端都是框（`ConstructionRules.IsChassis`，即内容 `canEnclose`）就不 union**，改成登记一条 `CompoundWeld`。`CanMergePair`（`Contraption.cs:690` 逐字）不动，所以「两端都 target 就不连」等原版判据照旧。被包裹件与框的合并路径不变（框不可能被包裹，`ConstructionRules.TryResolveEnclosure` 直接拒绝）。
- `CompoundWeld`（Left/Right + 两侧 anchor + `BreakImpulse`）自带断裂信息：拆体后这一对**没有 seam**（`NearestSeam`/`SplitAlongSeam` 只认 seam），阈值沿用接缝那套强度数学（木↔木 = `SeamBreakImpulse`，铁↔铁 2.4×）。
- **anchor 照原版**：`AnchorInLeft/Right` = 中点在各自零件局部系里的坐标（`Contraption.AddFixedJoint` 的 `InverseTransformPoint(other.position) * 0.5f`），存零件系而不是 body 系——框可能与它包裹的件共用一个 body，绑定时才折算到 body 系（`GameRoom.ToBodyLocal`）。
- 排序与去重按 `(Left, Right)` 升序，`CompoundAssembly.Welds` 是新增的第三条输出。

**房间（期 3）**

- `GameRoom.BindWeldJoints(assembly.Welds)` 镜像 `BindWheelHinges`，在三处 materialize 点调用；每条 weld 都调 `_rules.LinkPowerCluster(weld.Right, weld.Left)`——原版的功率分量就是关节图（`Contraption.cs:1293` unions 每个 `m_jointMap` 条目），少了这条边，「引擎在 A 框、耗能件焊在 B 框」就会断电。`RestRotation` 取**绑定时刻**的相对朝向（摆放时＝摆放相对姿态；拆簇重建后＝当时的实际姿态，不会把框拧回旧姿态）。
- **断裂**：`GameRoom.BreakWeldsFromAppliedCommands`（跟 `SplitFromAppliedCommands` 同一 tick 相位）用「本 tick 已施加的冲量 > 该 weld 的 `BreakImpulse` 且落点最近」判定，命中就 `DestroyJoint` + 丢掉该 weld 定义 + `UnlinkPowerCluster`，再按升序重连剩余 weld 的功率边（下游成员不能留着断路给的旧 key）。两端本来就是各自独立的 body，所以断裂只是关节消失。
- **生命周期**：`_weldJoints`/`_welds`/`_weldKeys` 三张表，`ForgetJointsForBody` 一并清理；`SplitFromAppliedCommands`/`DetachFromCompound` 现在**先 `ForgetJointsForBody` 再 `DestroyBody`**（原版这步只清了规则层），拆完调 `RebindWeldJoints()` 把仍存活的对按新 body 重建（`RestRotation` 用当时的相对姿态）。少了这两步，任何一次拆簇都会把框从邻居上撕下来。
- **顺带修掉的功率记账 bug**：`GameplayRules.LinkPowerCluster` 以前只写一条 `member → hostKey`，而 `LinkBody` 记录的代表实体是**最后一个**绑定的成员——于是「引擎与框共 body」时，引擎自己解析到的 key 是它自己，weld 写的边被代表实体遮蔽，边「连上了但不起作用」。现在同一条边写到 member **所在 body 的全部成员**上；`UnlinkPowerCluster` 同样按成员撤销。诊断口径 `GameRoom.WeldJointCount`（活体 weld 数）。

**拟合与对照验收（期 4）**

选定的机制是 spec 推荐的**连续弹簧**：`CompoundAssembler.FrameWeldSpringFrequency = 20.5 Hz`、`FrameWeldSpringDampingRatio = 1.0`（临界阻尼）。这是 **PigForge 标定值**（原版没有可抄的弹簧：343 个 prefab 零 spring 字段），用 §4.1 那条链扫频扫出来的：

| 指标 | 原版 `chain8_ppon_gap0` | Bepu @20.5 Hz | 偏差 | 容差 |
|---|---|---|---|---|
| 尖端下沉 | 1.8415 m | **2.1285 m** | +15.6% | ±25% |
| 单关节最大相对角 | 10.175° | **8.647°** | −15.0% | ±25% |
| 整链曲率 | 22.661° | **26.026°** | +14.9% | ±25% |

（`tests/PigForge.Physics.Tests/WeldComplianceTests.cs`，360 tick = 6 s，取整个区间最大值；扫频 14–26 Hz 显示指标随频率单调，20.5 Hz 是三条指标里最差偏差最小的点 ≈16%。）

**已记录的偏差**（都用注释写进了测试/实现）：

1. **引擎不同**：Bepu 2.4 vs PhysX 4.1；**tick 不同**：60 Hz vs 原版 50 Hz（容差为这两条留白）。
2. **夹具的「固定端」**：原版挂在 kinematic 刚体上；物理契约没有 static↔dynamic 关节（Bepu 直接拒），所以夹具改用「六轴全锁的 dynamic body（1000 kg）」——同样不可动。
3. **没有逐件阻尼（G86）**：原版每个零件刚体 `linearDamping 0.2 / angularDamping 0.05`，PigForge 没有。静态下垂与它无关，但长时振荡会不同。
4. **弹簧 vs 迭代残差**：弹簧刚度与载荷无关，原版的残差随弯矩增长。8 框链拟合得住；更长的链（16/32 框实测）Bepu 会折下去 13.8/31 m，原版在这种工况下会怎样**未测**（§5.2 的备选机制「有限迭代 + 每 tick 修正」仍未实现，属于已知的模型差异）。
5. **功率边的撤销是「断开 + 重连剩余」**：轮子的边（`BindWheelHinges`）只在绑定时解析一次，若它经由被断开的 weld 解析过，断裂后不会自动回落到自己的框 —— 既有行为，本轮未动。

**基准与实机**

- `dotnet run --project src/PigForge.Benchmarks -c Release`：typical/stress/concurrent-8 三景数字与改动前一致（stress p95 1.010 → 1.031 ms，属运行间噪声）。注意基准夹具用的是**合成内容（无 `canEnclose`）**，所以它根本不产生 weld —— 框链的成本不在这个基准里。
- 实机探针（真服务器 + 真内容 + 浏览器里用客户端自己的编解码器走 WS）：两框 + 包裹引擎 + 马达轮的焊上车架，四个 `PlacePart`、`StartSimulation`、`SetPartTypeActive` 全部 ack `status 0 / error 0`；快照里两框是**两个不同 body**、引擎与远框同 body；拨动开关后车架峰值 **|vx| = 17.775 m/s**（与 `FrameWeldRoomTests` 的进程内读数 17.87 m/s 一致）。

## 5. 开放问题（需甲方拍板）

1. ~~**拆体范围**~~ —— **已定（2026-10-04，用户）**：**先只拆框↔框的缝**（body 数只在框链上增长），用期 4 的实测对照兜底。
   - **其余零件（非框缝）本轮不做**（用户同日明确：「一口气做所有零件步子跨太大了」）。评估何时扩大，看期 4 的三组数字：①Bepu 侧 8 框链是否落在 §4.1 容差内；②`PigForge.Benchmarks` 的 body 数 / 帧预算；③实机试玩手感（框链下沉是否像截图那样）。扩大前必须先写出**非框零件上的对应验收口径**——框链的弯矩目标不能直接套到轮轴/引擎/气球这些本来就另有模型的件上。
   - **期 4 的数字（2026-10-04 收口）**：①8 框链三条指标都在 ±25% 内（最差偏差 ≈16%，§4.4 表）；②基准夹具是合成内容、不含 weld，框链成本不在 `PigForge.Benchmarks` 里（要量得新加一个夹具）；③实机探针证明链路通（两框两 body、焊上的引擎能驱动对侧马达轮）。**仍未扩大**：非框缝（轮轴/引擎/气球）不拆，扩大前必须先写它们自己的验收口径。
2. ~~**软度用 Bepu 的什么机制拟合**~~ —— **已定（2026-10-04）**：采纳 spec 推荐的连续弹簧，拟合结果 `20.5 Hz / ζ=1.0`（§4.4）。备选「有限迭代 + 每 tick 一次修正」**未实现**，它更接近原版的机制（残差随弯矩增长，而不是线性弹簧）——长链工况（16/32 框）是当前模型与它差异最大的地方。
3. **断裂阈值换算**：用 1020/2420 N 与「规则层事件产生的冲量」定一个可复算常数（替换 `10f`）？
   - **仍未做**（G15）。本轮 weld 沿用接缝的单位（`SeamBreakImpulse` 与强度比例），所以「框对能断、木比铁脆」是对的，但阈值仍是锚定冲量而不是从 1020/2420 N 推出来的。
4. **非框零件的软度**要不要同样处理（用户 2026-10-04：步子太大，先不做）。

## 6. 复现

```bash
unity license activate --personal --accept-eula        # 一次性
unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 --timeout 1200 \
  -- -executeMethod PigForge.WeldProbe.Probe.WeldComplianceProbe.Run -logFile -
```

格子：A `load_pp{on,off}_c{56,0}_m{0.5,1,4,20}`、B `rig_pp{on,off}`、C `break_{1000,2400}`、D `impulse_pp{on,off}`、E `proj_pp{on,off}`、**F `chain8_pp{on,off}_gap{0,1}`**。

期 3/4 的验收（不需要 Unity，全是 .NET 测试）：

```bash
dotnet test tests/PigForge.Core.Tests/PigForge.Core.Tests.csproj --filter "FullyQualifiedName~CompoundAssembler"
dotnet test tests/PigForge.Server.Tests/PigForge.Server.Tests.csproj --filter "FullyQualifiedName~FrameWeld"
dotnet test tests/PigForge.Physics.Tests/PigForge.Physics.Tests.csproj --filter "FullyQualifiedName~WeldCompliance"
```
