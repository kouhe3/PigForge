# ADR-027: 运行时子实体（一个放置件可以带 N 个刚体/实体）

## 状态

Accepted

## 日期

2026-10-04

## 背景

PigForge 的模型一直是「**一个放置件 = 一个实体**」：`ConstructionRules.Place` 产出一个 `EntityId`，装配时按簇合并成刚体，
`PGFC` 只认这个 id，`PGFS` 每实体 73 B 定长，客户端按 `partTypeId` 画图，归属（owner）挂在建造实体上。

原版不是这样。它有几个零件在 `Initialize`/运行期**额外实例化刚体**：

| 零件 | 额外刚体 | 出处 |
|---|---|---|
| 拳套 `SpringBoxingGlove` | 「手套」本体（`BoxingGlove*.prefab`，mass 0.5，yDrive 弹簧驱动） | `SpringBoxingGlove.cs:160-215` |
| 弹簧 `Spring`（拉断后） | `SpringEndpoint.prefab` 端点刚体（续接断掉的关节） | `Spring.cs:78-92,131-176` |
| 气球 / 沙袋 | 每件克隆 N 个刚体（`m_numberOfBalloons` / `m_numberOfBalloons` 袋数） | G52/G53 |
| 绳 | 8 节刚体 | G63 |

这些能力今天**一个都做不了**——不是因为物理契约缺关节，而是「多出来的那个刚体在实体模型里没有户口」：
没有户口就不能被快照发布、不能被 RESET/断连清场、不能确定性地创建与销毁。

## 决策

1. **引入「运行时子实体」**：一个放置件（host）在物化时可以注册 **N 个子实体**（`EntityId` + 自己的刚体/关节），
   归属**跟随 host 的 owner**。子实体由内容声明（`capabilities.glove`、将来的 `balloon.count`…），**不按零件 id 硬编码**。
2. **不建建造学籍**：子实体不进 `ConstructionRules`（无占格、无 footprint、无 owner 计数、不吃 `Place/Remove/Rotate/Move/Scale` 命令）。
   它只是一个「有 `PartLink` + 刚体 + 快照记录」的实体。
3. **生命周期由 host 决定**：物化（`StartPlayer`/`Start`/`MaterializeLevelActors`）时创建；
   host 被 RESET / 离开 / 拆簇摧毁 / 被规则销毁时，子实体**在同一处一并销毁**（先 `ForgetJoints` 再 `DestroyBody`，沿用 ADR-023 的顺序纪律）。
   也允许**运行期创建/销毁**（弹簧拉断生成端点刚体；端点刚体被规则销毁后 host 仍活着）。
4. **确定性**：创建顺序固定（host 实体值升序，然后子序号升序）；子实体 id 由 `EntityStore.Create` 分配（slot 复用 + generation）。
5. **线上零改动**：子实体就是普通实体——`PGFS` 照 73 B 发（`partTypeId` 暂用 **host 的** `partTypeId`，客户端按 host 的美术/形状画；
   拳套拳头自己的美术是 P1 客户端项），`PGFC` 不加 kind、不加字段。归属不上线这一条不变（客户端看不到子实体是谁的）。
6. **规则层**：子实体可以带角色（将来的气球/沙袋要各自的气力/掉落），但**初始不参与**装配的**并簇**（不与任何件 weld）；
   它与 host 的关系由**关节**表达（拳套的 yDrive 关节、弹簧拉断后的端点关节）。

## 影响

- `src/PigForge.Server/GameRoom.cs`：物化路径要按内容创建/销毁子实体（与 `BindCluster`/`UnbindEntity`/`ResetPlayer`/`LeavePlayer` 同处收口）。
- `src/PigForge.Core`：内容侧新增 capability 描述（`PartCapabilities` + 解析器 + schema + 客户端校验五处同步）。
- `CompoundAssembler`：子实体不进 union-find（不合并、不占地、不参与接缝/焊接登记）。
- 测试：确定性双跑（子实体 id 与哈希）、RESET/断连清场（子实体跟着走）、`MaxSnapshotEntityCount` 要把子实体算进去（否则大帧被丢弃）。
- 首个消费者：拳套（`docs/specs/boxing-glove.md`）；第二个消费者：弹簧拉断的端点刚体（`docs/specs/spring-joint.md` §4）。
- 直接受益的后续切片：多刚体沙袋/气球（G52/G53）、绳（G63）。
