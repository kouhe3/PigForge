# ADR-007: 零件多碰撞体与局部偏移（还原原作轮子）

## 状态

Accepted

## 日期

2026-09-09

## 背景

ADR-005 决策 2 为适配当时的物理契约（`BodyDefinition` 只表达无偏移的单形状、`PartFootprint` 只读 `shapes[0]`），把多碰撞体零件折中成「主体碰撞体 + 居中」：车轮只剩轮胎球，丢掉 `SupportCollider` 支撑盒。

实测后果：木轮（`part 7`，轮胎球 r=0.33）放在木框正下方一格时，轮胎顶到木框底还差 0.17，超过连接距离 `ConstructionRules.ConnectionProximity = 0.15`，轮子接不到木框上——车装不起来。原作的轮子正是靠顶部支撑盒（木轮 0.85×0.25 @ y=+0.34、马达轮 0.6×0.2 @ y=+0.3345）与上层零件连接的。

## 决策

1. **`part-content-v1` 的每个 shape 保留 `offset`（零件局部偏移，+y 上）**。schema 与 `PartContentParser` 早已支持，`PartContentLibrary.CreateBodyDefinition` 不再拒绝：单形状且零偏移 → primitive body；否则（多形状或带偏移）→ 一个 `CompoundShapeDefinition`。静态件带偏移仍不支持（Bepu 无静态 compound），显式抛 `NotSupportedException`。
2. **`PartFootprint` 是形状并集**：每个 shape 投影成圆或旋转矩形（偏移随建造角度旋转、随 scale 缩放），`Bounds` 取并集 AABB，`Overlaps` 任一对相交即命中。放置占用、连接距离、空间哈希都用真实几何。
3. **焊接（`CompoundAssembler`）展开成员的每个 leaf shape**：shape 偏移先按成员局部旋转旋转，再叠加到成员偏移上；Bepu compound child 支持 box 与 sphere。
4. **提取器输出原作的每一个非 trigger 碰撞体及其偏移**（`tools/bple-shapes/apply-shapes.mjs`；胶囊仍折中成 X/Y 包围盒），`content/parts.json` 与客户端 `playParts.generated.ts` 重新生成。

## 影响

- 267 个零件中 149 个形状变化：62 个多碰撞体零件（车轮、伞、旋翼、脱钩器等）恢复第二碰撞体；带偏移的单碰撞体零件（猪、螺旋桨、火箭……）恢复原作局部偏移。物理手感与连接距离随之贴近原作。
- 车轮通过支撑盒与上层零件连接，木轮 + 木框可直接叠放（回归测试：`CompoundAssemblerTests.WoodenWheelSupportColliderConnectsToFrameDirectlyAbove`、`SandboxRoomTests.SandboxWheelUnderFrameHingesToFrame` —— 后者随 ADR-008 改为断言两个刚体经关节相连，而非同一个刚体；轮体只带轮胎、支撑盒挂父体的细节见 ADR-009）。
- 客户端无贴图时的占位渲染改为绘制零件的全部形状（含偏移）；`validateContent` 校验 `offset` 为三个有限数。
- 生成链：`node tools/bple-shapes/extract-shapes.mjs --out artifacts/bple-part-shapes.json` → `node tools/bple-shapes/apply-shapes.mjs` → `node tools/web-parts/generate-play-parts.mjs`。
- 已知残留偏差：胶囊仍折中成包围盒；静态件的偏移/多形状仍不支持（原作里这类零件不存在）。
- 单成员簇的体姿按成员自身坐标系求（`CompoundAssembler.BuildCluster`）：体积加权的世界和会把坐标乘除一个来回，浮点残差会让「单个居中形状」的静态件误入 compound 路径并抛 `NotSupportedException`（斜坡/地板的坐标就是分数）。回归测试：`CompoundAssemblerTests.StaticPartAtFractionalPositionKeepsPrimitiveBody`。

## 参考

- `docs/decisions/ADR-005-part-shapes-from-bple-colliders.md`（决策 2 由本 ADR 取代）
- `src/PigForge.Core/Construction/PartFootprint.cs`、`src/PigForge.Core/Content/PartContentLibrary.cs`、`src/PigForge.Core/Construction/CompoundAssembler.cs`、`tools/bple-shapes/apply-shapes.mjs`
- BPLE 依据：`Assets/GameObject/Part_{CartWheel,MotorWheel,NormalWheel,SmallWheel,StickyWheel}_*_SET.prefab`（`WheelPivot` 的 `SphereCollider` + `SupportCollider` 的 `BoxCollider`）
