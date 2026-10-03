# ADR-014: 按连接状态显示的条件精灵

## 状态

Accepted

## 日期

2026-10-03

## 背景

玩家试玩报了三个 bug：汽水/火箭的**固定架（`*Attachment`）从不出现**、滑翔翼的**上下固定架同时显示**、轮胎摩擦不贴原版。前两条都源自同一个有意为之的过滤：

- `tools/bple-textures/extract.mjs` 按 `/attachment/i` 丢弃全部 `*Attachment` 精灵；
- `tools/bple-shapes/extract-shapes.mjs` 也丢掉配对的碰撞体。

当时的判断（ADR-005 决策 1）是「关节标记不算零件本体」。实测推翻了它：原版**确实**会显示这些标记，只是**条件性**地显示——每个 `ChangeVisualConnections` 覆写按 `Contraption.CanConnectTo(part, direction)` 的邻居结果开关对应节点（`Rocket.cs:139-161`、`TNT.cs:86-108`、`SpotLight.cs:70-105`、`GrapplingHook.cs:218-255`、`Wings.cs:41-72`、`JetEngine.cs:202-214`）。滑翔翼的「上下同显」不是 bug 的成因：原版在**侧面**连接时两架确实同时显示（`Wings.cs:50-57`，`Left`/`Right` 同时落在两个 OR 集合里），我们的错在于**无条件**画了全部。

判定输入全部是**部件自身帧**里的方向：原版先 `Rotate(Direction.Up, m_gridRotation)` 再问邻居，所以标签是局部的，客户端用实体 yaw 转进网格即可。

## 决策

1. **提取器保留并标记条件精灵，manifest 升 v4。** `tools/bple-textures/extract.mjs`
   - `*Attachment` 与 `TopFrameSprite`/`BottomFrameSprite` 不再被过滤，而是打上 `condition`（`{kind:"attachment", side}` 或 `{kind:"frame", mount}`，side/mount 均为**部件局部**方向）；
   - 零件条目带 `connectionVisual`，取自 prefab 挂载的宿主脚本（`Rocket`→`attachmentFallback`、`TNT`/`BlasterTNT`→`attachmentPlain`、`SpotLight`/`GrapplingHook`/`ExplodingGrapplingHook`→`attachmentEight`、`Wings`→`frame`）；未登记的宿主只 warning，其条件精灵保持隐藏（与本改动前一致）。
2. **客户端按连接状态门控。** 新增 `clients/web/src/renderer/connectionVisuals.ts`：
   - `connectableSides` 对每个「manifest 带规则」的实体求 8 个局部侧是否有**可焊**邻居。可焊判据＝原版 `Contraption.cs:690`（两端非 `none` 且至少一端 `source`），与 `CompoundAssembler.CanMergePair` 同源；邻居＝位置落在该侧一格中心 0.35 格内（构建期是格心，运行期同一刚体相对位置不变）。
   - `conditionalSpriteVisible` 按族还原原版公式：`attachmentFallback`（bottom 兼作「无其它侧」幽灵）、`attachmentPlain`（各侧独立，无 fallback）、`attachmentEight`（对角仅在 45° 朝向时显示，正交反之，另有兜底 bottom）、`frame`（局部 Right/Up/Left 与 Left/Down/Right 两个集合）。
   - `draw.ts` 只画通过判定的精灵；无 `connectionVisual` 的零件不画任何条件精灵。
3. **不建模的部分，明确记录：**
   - **条件碰撞体不做。** `*Attachment` 的 BoxCollider（显示时实心、隐藏时 `isTrigger`，`Rocket.cs:157-160`）与滑翔翼 body collider 的两态（`center.y/size.y`，`Wings.cs:62-71`）都还没有进内容或物理——形状集合目前仍是每零件静态的。这是下一步工作，需要新的内容键与服务端 shape 条件。**（2026-10-03 完成：ADR-021，内容键 `connectionVisual` + 服务端 `ConnectionShapes`。）**
   - **per-shape material 不做。** 轮胎（`Contraption_*WheelFriction_PhysMat` 0.025/0.05，`frictionCombine=Multiply`）与轮毂（0.7）的差异无法在 Bepu 2.4 的 body 级材质表里表达：`INarrowPhaseCallbacks.cs` 的 child 回调**没有** `PairMaterialProperties` 出参，上游以 TODO 明确写着「finer grained material tuning, both per child and per contact」尚未实现。修轮胎摩擦需要三方之一，均需先拍板：升级 Bepu、在规则层自研摩擦（Bepu 摩擦置零）、或接受「混合材质取单一值 + 实现 Unity 的 frictionCombine 优先级」的近似。
   - **声明方向（`m_jointConnectionDirection`）与包裹短路（`enclosedInto`）未建模。** 当前 43 个条件件的该字段全是 `Any`（`Any ⇒ 恒真`），包裹短路只影响被塞进框里的少数情形，暂不实现。
4. **原版的一处编译器痕迹按语义实现。** `SpotLight.cs:101-104` 的兜底行重复检查 `flag6` 且漏掉 `flag2`；本实现按「八个方向都检查」的语义写，并在代码注释标注（ADR 记录此偏离）。

## 影响

- manifest `schemaVersion` 3 → 4；`atlas.ts` 接受 2/3/4，多出的字段全部可选，旧清单即「少这一层」的新清单。
- 43 个零件（rocket/red-rocket/coke/soda 18、spotlight/grapple 10、wooden/metal wing 9、TNT 变体 6）现在按邻居状态显示标记；其余零件渲染不变。
- 无协议改动：连接方向由已发布的布局推导，PGFS v4 不动。
- 客户端新增 `PartCapabilities.jointConnectionType` 声明与校验（内容里本来就有该键，此前类型缺失）。
- 回归：`connectionVisuals.test.ts`（11 例，规则真值表）、`atlas.test.ts` v4 解析与拒绝、`draw.test.ts` 三个门控场景；实机确认 rocket 无邻居/下方→bottom 支架、上方→top 支架，wing 无邻居→bottom、上方→top、侧面→两架同显。

## 参考

- 代码：`tools/bple-textures/extract.mjs`、`clients/web/src/renderer/connectionVisuals.ts`、`clients/web/src/renderer/draw.ts`、`clients/web/src/renderer/atlas.ts`
- BPLE 依据：`Wings.cs:41-72`、`Rocket.cs:139-161`、`TNT.cs:86-108`、`SpotLight.cs:70-105`、`GrapplingHook.cs:218-255`、`JetEngine.cs:202-214`、`Contraption.cs:677-790`、`BasePart.cs:735-776`
- 上游限制：`BepuPhysics 2.4.0` `INarrowPhaseCallbacks.cs` 的 per-child material TODO
- 相关：ADR-005（决策 1 的「关节标记不算本体」由本 ADR 修正）、ADR-011（焊接判据）

## 修订记录

- 2026-10-03：邻居判定原先是「另一件的原点落在本件某个本地边的**一格**格心上」，但按支架对齐的零件（滑翔翼，ADR-018）原点离它焊住的木框有 0.74 格——滑翔翼在木框**左侧**时认不到邻居，`frame` 规则的 `top` 判为 false，两个支架只画出一个（用户实机截图）。改为**对齐盒接触**判定（与建造吸附、服务端焊接同一几何），方向取两盒贴合的那一轴、再反旋转回零件自身坐标系。`connectableSides` 现在依赖零件有可投影形状（`shapes: []` 的测试替身不再算数）。
