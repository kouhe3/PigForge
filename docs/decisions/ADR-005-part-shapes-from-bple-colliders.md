# ADR-005: 零件尺寸与形状以 BPLE 碰撞体为准，贴图按原作世界尺寸绘制

## 状态

Accepted

## 日期

2026-09-09

## 背景

`content/parts.json` 的 `shapes` 此前是手写近似值（车轮半径 0.4、发动机 `[0.4, 0.3, 0.4]`、TNT `[0.35, 0.35, 0.35]`……），与 BPLE 原作 prefab 里的碰撞体不一致，玩家看到的零件大小和物理手感都偏离原作。

原作的尺寸有两套，且本来就不相等：

- **碰撞体**：prefab 上的 `BoxCollider` / `SphereCollider` / `CapsuleCollider`（部分零件由脚本在运行时添加，如气球 `SphereCollider` r=0.5、沙袋 r=0.13）。这是物理与建造的权威。
- **贴图**：`Sprite` 网格的世界尺寸 = 像素 × `20/768`（`Sprite.cs` 里 `CreateMesh` 用 `10/768` 作半宽，相机高度 20 / 屏幕高 768）。猪的碰撞体是 r=0.42 的球，贴图合成包围盒约 0.97×0.92，两者不重合。

客户端此前把贴图合成包围盒等比适配到物理形状（ADR-003 决策 4）。于是物理形状一改，视觉尺寸跟着改，两套尺寸无法同时对齐；反过来为了贴图好看而虚增物理形状，又会让碰撞偏离原作。

原作是 2.5D：刚体冻结 Z 位移与 X/Y 旋转，所以 Z 方向的尺寸只是设计残留，只有 X/Y 与形状种类是可观察的。

## 决策

1. **碰撞体是零件尺寸的唯一权威。** `content/parts.json` 的 `shapes` 由 `tools/bple-shapes/extract-shapes.mjs` 从 BPLE prefab 提取：`box` 取 `m_Size / 2`，`sphere` 取 `m_Radius`，`capsule` 取 X/Y 包围盒（`box`），运行时添加的碰撞体按脚本常量补上。关节标记（`*Attachment`）与脚本辅助碰撞体（`MouthPos`）不算零件本体。提取报告写到 `artifacts/`（gitignore），零件映射复用 `tools/bple-textures/part-map.json`。
2. **只表达单形状、无偏移。** 建造平面的足迹投影（`PartFootprint.ForPart`）只读 `shapes[0]` 且不支持 shape offset，`PartContentLibrary.CreateBodyDefinition` 也显式拒绝 offset。因此多碰撞体零件取主体碰撞体（车轮取轮胎球，而不是 `SupportCollider` 支撑盒），碰撞体的局部偏移折中为居中。这是已知偏差，不是原作的完整碰撞几何。
3. **贴图按原作世界尺寸与偏移绘制。** `part-textures.json` 的 `sx/sy` 改为真实世界尺寸（旧版是真实尺寸的一半，渲染靠适配抵消），清单 `schemaVersion` 升到 2；`layoutSprites` 不再适配物理形状，按清单尺寸与偏移绘制，只乘建造期的 `scale`。旧版清单被解析器拒绝 → 回退形状渲染，不会静默画错大小。
4. **无原作对应件的零件保留手工形状**：`ground-slab`、`ball-weight`、`terrain-box`、`ramp-plank`、`firework-blue`、`rope`、`dynamite`、`spotlight`（`part-map.json` 里为 `null`，或 prefab 只有关节标记碰撞体）。
5. **客户端内联表必须与内容一致。** `clients/web/src/builder/slope.ts` 的 `PLAY_PARTS` 是离线坡道构建器的副本，由 `clients/web/src/builder/slope.test.ts` 对着 `content/parts.json` 逐字段校验，防止再次漂移。

## 影响

- 改尺寸或换映射后要重跑两个脚本：`node tools/bple-shapes/extract-shapes.mjs`（报告）与 `node tools/bple-textures/extract.mjs`（贴图清单 v2 + 图集）。
- 贴图不再被限制在物理形状内——猪的贴图会盖住旁边的方块，和原作一样。
- 已知偏差：车轮轮胎、弹簧顶板、翅膀、螺旋桨等碰撞体的局部偏移被折中为居中；猪王与蛋的 capsule 以 X/Y 包围盒近似。
- Z 半宽取原作 `m_Size.z / 2`（多数为 0.5），原作 Z 为 0 的 2D 碰撞体（TimeBomb）沿用原内容的 Z。

## 参考

- `tools/bple-shapes/extract-shapes.mjs`、`tools/bple-textures/extract.mjs`、`tools/bple-textures/part-map.json`
- `content/parts.json`、`clients/web/src/builder/slope.ts`、`clients/web/src/renderer/atlas.ts`、`clients/web/src/renderer/draw.ts`
- BPLE 侧依据：`Assets/GameObject/Part_*_SET.prefab`、`Assets/Scripts/Assembly-CSharp/Sprite.cs`、`Balloon.cs`、`Sandbag.cs`、`Umbrella.cs`
- `docs/decisions/ADR-003-original-texture-assets.md`
