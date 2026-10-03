# ADR-019: TNT 爆炸驱动施爆件所在的复合体

## 状态

Accepted（修复 ADR-002 决策 4 的落地缺陷）

## 日期

2026-10-03

## 背景

玩家实机报「TNT 现在对世界里的木框没有冲击」。诊断见 `tasks/tnt-blast-diagnosis.md`：PigForge 把一次建造焊成**一个 compound body**（ADR-011），TNT 与紧邻木框同 body，而爆炸循环用 `bodyId == link.Body.Value → continue` 把施爆件自己的 body 整个排除，于是整簇收不到任何冲量（实测紧邻木框 `vx = 0`，3.0 外的独立木块 `vx = 9.58`）。同一路径上还有两处记账缺陷：

- `GameplayRules.DestroyEntity` / `CleanupEntityStores` 销毁**单个**成员就把整 body 从 `_entitiesByBody`/`_dynamicBodies` 摘掉，随后 `DropCommandsForDestroyedBodies` 丢弃指向该 body 的冲量命令，且撞击点火/蛋/脱钩的实体查找一并失效（`GameRoom.UnbindEntity` 早已按「成员空了才销毁」处理，规则层没对齐）。
- 拆分路径：`GameRoom.UnbindEntity` 只改房间自己的表，不回告规则层，**旧 body 残留在 `_dynamicBodies`**；下一次爆炸会把冲量打给已销毁的 body（Bepu `ApplyCommands` 直接抛 `Impulse target body ... does not exist`，房间停止出帧，玩家看到 TNT 与木块一起消失）；`SplitFromAppliedCommands` 还会拿仍列着已销毁成员的 cluster 去重建 body（`_bodies.Set` 对陈旧实体抛 `KeyNotFoundException`）。

原版每个零件是独立 Rigidbody，爆炸按**施爆件自身的世界位姿**给半径内每个刚体施力：`TNT.cs:130` 的 `Explode` 用 `Physics.OverlapSphere(transform.position, radius)` 取 collider（`:138`），对每个 collider 找其父刚体（`:141`）调 `AddExplosionForce`（`:145`），而 `AddExplosionForce` 用力方向 `target.position - TNT.transform.position`、大小 ∝ `impulse / max(|vector|,1)^1.5`（`:210-230`）。施爆件自己的刚体 `vector = 0` → `normalized = 0` → 不受力，但**焊接构造的其它成员是独立刚体、照常受力并被炸断**。

## 决策

1. **爆炸把施爆件所在的 body 也纳入冲量目标**。`Explode` 去掉自身的 `continue`，半径内的每个动态 body 都收一条冲量命令（含自身）。ADR-002 决策 4 的「半径内动态体」本就没有排除源体，施爆件所在的复合体正是其中之一。
2. **爆心 = 施爆件自身的世界位姿，不是 body 质心**。原版量的是 `target.position - TNT.transform.position`；焊接后 body 质心在簇内、与施爆件不重合，继续用质心会（a）把非焊接件的距离量错，（b）让「自身 body」距离恒为 0、方向退化成竖直上抛。为此 `GameplayRules.LinkBody` 增加成员在 body 内的局部偏移（`GameRoom.BindCluster` 传 `member.LocalOffset`），每 tick 快照额外记 body 旋转，`Explode` 用 `bodyPos + rot·localOffset` 还原施爆件的世界位置。几何仍沿用 PigForge 既有口径：线性衰减、按目标刚体中心施力（ADR-006 决策 2）。
3. **一个 body 失去成员时只有成员清空才清记账**：`UnlinkBodyMember` 在 `_membersByBody[body]` 仍有成员时把代表实体顺延到 `members[0]` 并保留 `_dynamicBodies`，只有孤立 body 才移除；语义与 `GameRoom.UnbindEntity` 对齐。
4. **拆分契约双向**：`GameRoom.UnbindEntity` 反向调用 `GameplayRules.UnbindBody(entity)`，规则层的旧 body 随之清空；`SplitFromAppliedCommands` / `DetachFromCompound` 在拆缝前用 `CompoundAssembler.WithoutMembers` 把**已销毁成员**从 live cluster 里剔除（保留其余成员的局部位姿，只丢两端已死的缝），避免为死实体重建 body。

## 影响

真 Bepu、真实内容、`--play` + 裸 WS 观察者（TNT 9 在 x=-16.5，木块 1 在 -15.5（边缘间隙 0.025 → 焊接），另一木块在 -13.5）：

| 时刻 | 紧邻木块 vx | 远处木块 vx |
|---|---|---|
| 修复前 | 0.000 | 9.583 |
| 修复后（tick 505） | **10.787** | **6.218** |

远处木块从 9.58 降到 6.22 是决策 2 的直接结果：爆心从簇质心（-16.0）移到施爆件（-16.5），距离 2.5 → 3.0。紧邻木块从 0 变为与远处同量级（比值 1.73）；爆炸帧数 12 → 11，只有 TNT 消失，两块木块都在快照里。

拆分路径的崩溃也复现并修掉：紧邻件被炸毁后旧 body 失效，第二发 TNT 引爆时修复前抛 `Impulse target body 10 does not exist or is not dynamic.`，修复后 40 tick 全绿。

回归：`GameplayRulesTests.DestroyingOneMemberKeepsTheSharedBodyDynamicForTheSurvivor`、`…KeepsTheSharedBodysEntityLookupForImpactIgnition`（决策 3 的两个可观测面：冲量命令存活、撞击点火仍解析到幸存件）、`GameRoomTests.SeamSplitRebindsEveryMemberToItsNewBody`（拆簇后每个实体 `physicsBodyId` 非 0 且互不相同）、`TntBlastTests.{BlastDrivesTheWeldedBlockNextToTheChargeAndKeepsEveryLiveBlock, BlastAfterASeamSplitDoesNotTargetTheVacatedBody, BlastSceneIsDeterministicAcrossRuns}`（真 Bepu；场景内容镜像出厂件 1/9，避免被并发的内容编辑波及）。全部测试项目单跑绿。

## 未做

- **成员被摧毁后 body 的碰撞形状仍含该成员**：既有已记录的限制（`docs/specs/play-part-switches.md` Open Questions：「气球放气后…该 body 的碰撞形状仍含气球…要彻底移除需要『成员被摧毁时重建复合体』的独立切片」）。本 ADR 只保证拆分/记账不再复活死实体，不重建存活 body；火箭/气球自毁同样如此。
- `RadialBlast`（火箭/礼花）仍排除源 body，未随本 ADR 改动。

## 参考

- BPLE 依据：`TNT.cs:130-182`（`Explode`：`OverlapSphere`、逐 collider 找父刚体、`FindPartJoints` 断键）、`TNT.cs:210-230`（`AddExplosionForce`：`target.position - TNT.transform.position`、`max(|vector|,1)^1.5`、`AddForce(..., Impulse)`）
- 代码：`src/PigForge.Core/Runtime/GameplayRules.cs`（`Explode`、`LinkBody`/`UnbindBody`/`UnlinkBodyMember`）、`src/PigForge.Core/Construction/CompoundAssembler.cs`（`WithoutMembers`）、`src/PigForge.Server/GameRoom.cs`（`BindCluster`、`UnbindEntity`、`PruneDeadMembers`、`SplitFromAppliedCommands`/`DetachFromCompound`）
- 相关：ADR-002（无伤害、TNT = 纯动量源）、ADR-006（变体效果与爆炸几何）、ADR-011/015（焊接判据与缝隙强度）、`docs/specs/play-part-switches.md`（形状重建的既有限制）
