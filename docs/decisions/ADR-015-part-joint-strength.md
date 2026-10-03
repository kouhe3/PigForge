# ADR-015: 每件关节强度决定焊缝断裂阈值

## 状态

Accepted

## 日期

2026-10-03

## 背景

玩家最关心的原版差异之一是「木架比铁架容易散」。原版的每一条焊缝都有断裂强度，而且**判定在关节上、两端相加**：

- 每件声明一个 `m_jointConnectionStrength` 枚举（`BasePart.cs:130-137`；343 个 `Part_*.prefab` 全部声明，分布 Weak 45 / Normal 120 / High 98 / Extreme 60 / HighlyExtreme 20）；
- 枚举经 `GameData.asset:101-105` 的浮点解析（125 / 125 / 600 / 900 / 1200），且 `Contraption.GetJointConnectionStrength`（`Contraption.cs:1494-1506`）在 `INFeature.ConnectionStrength > 1` 时**只把 Normal 翻倍**——出厂 `INSettingsBExp.json:208-210` 是 2.0，于是 Normal 实际是 250，Weak 仍是 125；
- 通用相邻焊缝取 `(gs(a) + gs(b)) × ConnectionStrength`（`Contraption.cs:1541-1543`，再乘一次于 `:2268`）。木↔木 = (250+250)×2 = **1000**，铁↔铁 = (600+600)×2 = **2400**，木↔铁 = 1700。铁架强度的 2.4 倍就是玩家感觉到的差异。

PigForge 此前只有一个全局 `CompoundAssembler.DefaultSeamBreakImpulse = 10f`，每条 seam 写同一个值（`CompoundAssembler.cs:848`），内容里没有任何强度键。

## 决策

1. **提取器是唯一来源。** `tools/bple-joints/extract-joints.mjs` 除 joint 类型外再输出每 prefab 的 `m_jointConnectionStrength`（raw + 名称）与 `m_jointPreprocessing`、`m_jointType`，并统计全项目直方图；同时**硬断言** 343 prefab / 强度 45-120-98-60-20 / preprocessing 316-27 / joint kind 302-41，漂移即失败退出，`content/parts.json` 不会被写出错误世界。报告多出 `prefabScan` 段（含 per-prefab 紧凑表）供审计。
2. **内容是 5 值枚举、可缺省。** `schemas/part-content-v1.schema.json` 增加 `capabilities.jointConnectionStrength`：`weak|normal|high|extreme|highlyExtreme`；`PartContentDocument.cs` 增加 `JointConnectionStrength` 枚举与 `PartCapabilities` 字段（`None` = 未声明）；`PartContentParser` 严格解析（未知值报错）并进白名单；客户端 `types.ts`/`validateContent.ts` 同步。`tools/bple-joints/apply-joints.mjs` 报告驱动、幂等写入，覆盖全部 264 个有 prefab 的件；没有 prefab 的 3 个手写静态件（`ground-slab`/`terrain-box`/`ramp-plank`）不写，运行时回退 Normal。
3. **每对 seam 阈值 = 调用方 fallback × 强度比。**
   `CompoundAssembler.BuildCluster` 把常量换成
   `seamBreakImpulse * (s(left) + s(right)) / (2 * 250)`，
   其中 `s()` 是上面的枚举解析（Normal 已是 250），未声明回退 250。Normal-Normal 因此**恰好保持** `DefaultSeamBreakImpulse`，既有测试与手感不变；木↔木 1.0、木↔铁 1.7、铁↔铁 2.4、HighlyExtreme↔HighlyExtreme 4.8 与原版比例一致。签名 `Assemble(..., float seamBreakImpulse = DefaultSeamBreakImpulse)` 不变。
   绝对刻度是 PigForge 的选择：原版的 `breakForce` 是牛顿阈值，我们的 `BreakImpulse` 是施加的冲量幅值，两者不同量纲，因此只保序与比例并锚定在现状的 10f（计划 §5 的 tradeoff）。
4. **本轮不做的路径，明确记录**（都留在 `tasks/bple-jointstrength-plan.md` 与 `tasks/bple-jointstrength-report.json`）：
   - `Hook.cs:68-80` 有一份**没有 ×2、也不乘 ConnectionStrength** 的重复表（挂钩零件）；
   - `HingePlate` ×3（`HingePlate.cs:453`）、`Spring` 250/1200（`Spring.cs:15,38`）、`Rope` +∞（`Rope.cs:238-239`）、`GrapplingHook` 的 claw joint 从未赋 `breakForce`；
   - `FrameJointManager` 对 3 类特例件补充的额外关节（`FrameJointManager.cs:172`）不实现——它在通用焊缝之上**叠加**，而 compound body 已经表达「焊在一起」；
   - 框的弹性不做：原版框关节是最硬的一类且无 spring，木框「软」是**质量差 + 阈值差**涌现的（ADR 记录于计划 §2）。
   `+∞` 的两种情形（SuperGlue/Rope）里，前者已由既有 `capabilities.glue` + `IsGluedCompound` 覆盖。

## 影响

- 264/267 件带上提取强度；`content/parts.json` 与 `clients/web/src/builder/playParts.generated.json` 重新生成（生成器 `--check` 267 通过）。
- 断裂行为：木↔木与单件未声明的情形与改动前**逐位一致**（10f）；金属↔金属升到 24；TNT（Weak）降到 5；HighlyExtreme 件升到 24（与 High 同因 600 vs 600… 实际按对计算）。
- 确定性不变：`CompoundCluster.ComputeHash` 已把 `seam.BreakImpulse` 计入，哈希随内容变化但双跑稳定（既有 double-run 测试全绿）。
- 回归：`CompoundAssemblerTests.TheSeamThresholdScalesWithBothEndsDeclaredStrength`（0.5/1.0/1.7/2.4/4.8 比例）、`TheCatalogGivesMetalWeldsTheOriginalStrengthRatio`（真实目录 1.0 vs 2.4）、`GameRoomTests.AStrongerWeldedPairSurvivesTheImpulseThatSplitsTheNormalPair`（同布局同冲量、只有强度不同，一个裂一个不裂）、`PartContentTests` 的解析往返/拒绝/真实内容守卫（264 件有值、2/5/6 回退、木 1 = Normal、金属 18 = High）。
- 工具：`extract-joints.mjs` 现在会因源树漂移而失败退出；`apply-joints.mjs` 的 capabilities 括号匹配改为配对扫描（原先 `indexOf("}")` 在 capabilities 内已有嵌套 `attachment` 时会提前截断，写坏文件并留下重复键——本轮实测到并修掉）。

## 参考

- 计划与证据：`tasks/bple-jointstrength-plan.md`、`tasks/bple-jointstrength-report.json`
- 代码：`tools/bple-joints/extract-joints.mjs`、`tools/bple-joints/apply-joints.mjs`、`src/PigForge.Core/Construction/CompoundAssembler.cs`、`src/PigForge.Core/Content/PartContent{Document,Parser}.cs`、`schemas/part-content-v1.schema.json`
- BPLE 依据：`Contraption.cs:1494-1506`、`:1541-1543`、`:2268`、`BasePart.cs:130-137`、`GameData.asset:101-105`、`INSettingsBExp.json:208-210`
