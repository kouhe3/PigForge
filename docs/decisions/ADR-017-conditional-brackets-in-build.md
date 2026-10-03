# ADR-017: 支架碰撞体参与建造贴合与连接，但不参与物理与格子占用

## 状态

Accepted

## 日期

2026-10-03

## 背景

玩家报：「汽水、火箭和滑翔翼的建造模式拖拽对齐是错的，预期是对齐到支架」。

原版的关节支架（`Top/Bottom/Left/RightAttachment`）是**实心 collider**：显示时参与碰撞，隐藏时被 `Rocket.cs:157-160` 改成 trigger。ADR-005 决策 1 当初以「关节标记不算零件本体」为由，让两个提取器把它们**整个丢弃**。后果有三层，本轮之前只解决了第一层（渲染，ADR-014）：

1. 支架不显示 → 已由 ADR-014 修（条件精灵）。
2. **拖拽贴合**用零件自己的几何 → 汽水/火箭贴到**主体**边缘，而原版玩家是把零件对齐到**支架**（支架在主体外 0.37，宽 0.5）。
3. **连接距离**同理只算主体：位置对齐方式一改，若连接仍只看主体，贴合到支架的零件就会连不上。

## 决策

1. **提取器保留支架 collider 并打 `condition`**：`tools/bple-shapes/extract-shapes.mjs` 不再丢弃 `/attachment/i`，而是给形状加上与精灵同源的 `{ "kind": "attachment", "side": "..." }`；`apply-shapes.mjs` 报告驱动写出。`content/parts.json` 因此有 18 个零件（rocket 家族 8 + 汽水家族 10）各多 4 个条件形状。
2. **条件形状是建造语义，不是物理几何**：
   - 物理：`PartContentLibrary.EnumerateShapePlacements` 跳过条件形状（`CreateBodyDefinition`、`DescribeWheel` 都经它），所以刚体永远不会含支架——隐藏的支架在原件里就是 trigger。
   - 格子占用：`PartFootprint.Overlaps` 只用**非条件**形状，支架不会把邻格封死。
3. **贴合与连接包含条件形状**：客户端 `snapBoxOf`（拖拽贴合的 AABB）读全部形状；服务端新增 `PartFootprint.Touches`（含条件形状）并让 `ConstructionRules.CollectOverlapping(..., connect)` 在**连接**语义下用它、在**占用**语义下仍用 `Overlaps`。`Bounds` 始终取全集，候选桶不会漏。
4. **全条件形状的零件不覆盖内容**：spotlight 的全部碰撞体都是支架（没有主体 collider），若照抄会把它的物理体清空。`apply-shapes.mjs` 对这种 prefab 退回「保持已写形状」，`PartContentLibrary` 则对「一个非条件形状都没有」的零件**显式抛错**（宁可失败也不造出空刚体）。
5. **不做**（留在 ADR-014/§5.1 的后续）：支架随连接状态切换实心/trigger 的双向语义、滑翔翼 body collider 的两态（`Wings.cs:62-71`，`center.y/size.y` 在 0.05/0.3 与 −0.15/0.6 之间切换）。
   - 2026-10-03：**两条都已实现**，见 ADR-021（内容键 `connectionVisual` + 服务端 `ConnectionShapes`）；本决策的「物理永远不含支架」被它取代，占格仍按 ADR-020 的声明格盒。

## 影响

- 拖拽：rocket/汽水的贴合框实测从 `halfX 0.35` 变成 **`halfX 0.62`**（= 支架外缘 0.37 + 半宽 0.25），左右上下同理——零件现在停在支架贴住邻居的位置。
- 占用与连接分离：把火箭放在距方块 0.9 处（支架重叠、主体不重叠）仍然**放置成功**，且**连接成立**（两个断言都在 `ConstructionRulesTests.AConditionalBracketConnectsWithoutOccupying`）。
- 滑翔翼**未变**：它的 prefab 只有一个 body collider（已含固定架区域的 1.9×0.612 box），`halfX 0.95 / halfY 0.3061`。若要求按固定架上下边缘（视觉上比 collider 更外）贴合，需要实现第 5 条的 wing 两态。
- 回归：`ConstructionRulesTests`（占用忽略/连接包含条件形状）、`tools.test.ts`（snap box 覆盖条件支架）、`PartContentParser` 的 `condition` 白名单与严格校验（schema + 客户端 `validateContent` 同步）。

## 参考

- 代码：`tools/bple-shapes/{extract-shapes,apply-shapes}.mjs`、`src/PigForge.Core/Construction/PartFootprint.cs`、`src/PigForge.Core/Construction/ConstructionRules.cs`、`src/PigForge.Core/Content/PartContent{Library,Parser,Document}.cs`、`clients/web/src/editor/tools.ts`
- BPLE 依据：`Rocket.cs:139-166`（四侧 + `isTrigger = !activeInHierarchy`）、`Wings.cs:41-75`（固定架与 body collider 两态）
- 相关：ADR-005（被本 ADR 修正的过滤）、ADR-007（多碰撞体与偏移）、ADR-014（条件精灵）、ADR-016（同批的材质）
