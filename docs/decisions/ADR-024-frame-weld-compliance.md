# ADR-024: 框↔框不并成一个刚体，而是两个 body 加一条软焊点

## 状态

Accepted

## 日期

2026-10-04

## 背景

`ADR-011` 决策 2/3 让相邻可合并件**焊进同一复合刚体**：关节数 0、成员之间不产生接触。原版不是这样——它给**每一对相邻零件**建一条真实关节（`ConfigurableJoint` 六轴 Locked，`Contraption.cs:1507-1546`），而这条关节在**链式受弯**时会明显让步：`unity/PigForge.WeldProbe`（钉在原版自己的编辑器 2021.3.45f2 + 原版物理设置）实测 8 个 0.5 kg 木框一端固定的自重下垂是**尖端下沉 1.84 m / 单关节 10.18° / 整链 22.66°**（`tasks/weld-compliance-probe.json` 的 `chain8_ppon_gap0`）。机制是**锁死 D6 关节的迭代残差**（`m_DefaultSolverIterations = 6`，链根要扛整链弯矩）**加上相邻碰撞体的接触**（接触把下沉从 6.55 m 压到 1.84 m），不是任何弹簧参数——全部 343 个 prefab 零 spring 字段，`enablePreprocessing` 只改各关节之间的分配、不改总曲率。

于是 PigForge 的长结构不但不下垂、不弯，连用户截图里那种「原生草坪级」刚度和手感都复现不了。规格与全部实测数字见 `docs/specs/weld-compliance.md`。

## 决策

1. **只拆框↔框的缝**（用户 2026-10-04 拍板）。「框」＝内容 `capabilities.canEnclose`（`ConstructionRules.IsChassis`；22 件：`wooden-block` 1、`metal-box` 18 及变体）。`CompoundAssembler` 的邻居 union 循环里，两端都是框就不 union，改成登记一条 `CompoundWeld`（`Left/Right` + 两侧 anchor + `BreakImpulse`，按 `(Left, Right)` 升序、去重）。其余件的合并规则一字不动（`CanMergePair` 仍是 `Contraption.cs:690` 逐字）；被包裹件与框的合并路径也不动（框不可能被包裹）。
2. **anchor 照原版**：中点在各自**零件**局部系里的坐标（`Contraption.AddFixedJoint` 的 `InverseTransformPoint(other.position) * 0.5f`）。框可能与它包裹的件共用一个 body，所以绑定时才折到 body 系。
3. **相邻碰撞保留**：weld 的两端照原版继续互相碰撞（Bepu 只对 revolute 关节做 `IgnoreCollision` 那一对），这正是下沉被压掉 3.5 倍的那一半机制。
4. **软度用连续弹簧拟合**：`CompoundAssembler.FrameWeldSpringFrequency = 20 Hz`、`FrameWeldSpringDampingRatio = 1.0`（临界阻尼）。这是 **PigForge 标定值**——原版没有可抄的数字。同一条 8 框链在 Bepu 上量到下沉 2.118 m / 单关节 8.617° / 整链 25.861°，三条都在规格 §4.1 的 ±25% 内（最差偏差 ≈15.3%）。
   - **2026-10-06 修正（G90 的顺带项）**：频率从 **20.5 → 20 Hz**。第一版是在**无阻尼夹具**上扫频的（当时 `BodyDefinition` 连阻尼字段都没有），而原版那条链是**真零件**、每个刚体都吃 `drag 0.2 / angularDrag 0.05`——ADR-025 落地后房间里的框链也拿到同一对，于是夹具补上这对值重扫：阻尼让整段偏移变小，最佳点左移。无阻尼夹具 20.5 Hz 的最差偏差 15.6%，带阻尼夹具 20 Hz 的是 15.3%，同一水平；只有频率这一个数字变了，比值仍是 1.0（`docs/specs/weld-compliance.md` §4.4 有表）。
5. **weld 是功率边**：原版的功率分量就是关节图（`Contraption.cs:1293` unions 每个 `m_jointMap` 条目），所以 `GameRoom.BindWeldJoints` 每条都调 `LinkPowerCluster`，否则「引擎焊在 A 框、耗能件焊在 B 框」会断电。
6. **断裂**：`CompoundWeld` 自带 `BreakImpulse`（沿用接缝那套强度数学：木↔木 = `SeamBreakImpulse`，铁↔铁 2.4×），因为拆体后这一对不再有 seam。`GameRoom.BreakWeldsFromAppliedCommands` 在同一个 tick 相位用「本 tick 已施加的冲量 > 阈值且落点最近」判定；命中只销毁关节（两端本来就是独立 body），并按升序重连剩余 weld 的功率边。
7. **生命周期**：`_weldJoints`/`_welds`/`_weldKeys` 三张表；`ForgetJointsForBody` 一并清理；两条拆簇路径（`SplitFromAppliedCommands`、`DetachFromCompound`）现在**先 `ForgetJointsForBody` 再 `DestroyBody`**，拆完 `RebindWeldJoints()` 按新 body 重建仍存活的对（`RestRotation` 取当时的相对姿态，不会把框拧回旧姿态）。

## 影响

- `src/PigForge.Core/Construction/CompoundAssembler.cs`（`CompoundWeld`、`CompoundAssembly.Welds`、`IsFramePair`/`RegisterWeld`、两个标定常量）、`src/PigForge.Server/GameRoom.cs`（绑定时三处 + 断裂 + 重建 + `WeldJointCount`）、`src/PigForge.Core/Runtime/GameplayRules.cs`（`LinkPowerCluster` 按 body 全部成员写边、新增 `UnlinkPowerCluster`）。
- **顺带修掉一个真 bug**：`LinkPowerCluster` 以前只写一条 `member → hostKey`，而 `LinkBody` 记录的代表实体是**最后一个**绑定的成员——「引擎与框共 body」时引擎自己解析到的 key 是它自己，weld 写的边被代表实体遮蔽，于是「边连上了但不起作用」（实测：引擎在远框时马达轮 0.004 m/s → 修完 17.9 m/s）。现在同一条边写到 member 所在 body 的**全部成员**上。
- **可见行为变化**：框链/框墙现在**会弯**，而且 body 数按框数增长；框之间的刚度远低于以前（这是目的）。轮轴、引擎、气球、火箭全不受影响（不拆）。
- **已知偏差**（详见规格 §4.4）：Bepu vs PhysX 4.1、60 Hz vs 原版 50 Hz；夹具用「六轴全锁的 dynamic body」代替原版的 kinematic 固定端（物理契约没有 static↔dynamic 关节）；~~缺 G86 逐件阻尼~~（2026-10-06 起夹具与房间都带原版的 `0.2 / 0.05`，拟合已重做）；连续弹簧的刚度与载荷无关，而原版的残差随弯矩增长——更长的链（16/32 框实测折 13.8/31 m）**未与原版对照**。
- **测试**：Core `CompoundAssemblerTests`（框对两 body 一 weld、8 框链 N body + N-1 weld、框仍与包裹件合并、标定常量、双跑哈希）、`PowerSystemTests`（weld 功率边 + 断开）；Server `FrameWeldRoomTests` 6 条（4 帧 4 body 3 weld、双房哈希一致、爆炸断焊、拆簇重建焊点、重置后重建仍拿回焊点、焊上的引擎驱动对侧马达轮）；Physics `WeldComplianceTests`（8 框链三条指标落在容差内 + 双跑一致 + 刚性 weld 不达标）。**非空验证过**：把 union 判据、`BreakWeldsFromAppliedCommands`、`RebindWeldJoints`、`LinkPowerCluster` 任一处去掉，对应测试即红。
- 实机探针（真服务器 + 真内容 + 浏览器走 WS）：四件 ack 全 `status 0 / error 0`，两框两个 body、引擎与远框同 body，拨开关后车架峰值 |vx| = 17.775 m/s。
