# ADR-035：地形边缘条带（`_curve`）进内容 v4，层选择在构建期折成运行段

- 状态：**已接受**（2026-10-06）
- 相关：`ADR-033`（关卡 v2 的 `terrain` + `GET /level` 侧通道）、`ADR-034`（fill 进 v3）、
  `ADR-032`（网格形状）、`docs/specs/level-terrain-visuals.md`（全部实测数字）、
  `docs/specs/original-level-pack.md`
- 差距：`G76`（地形视觉；fill 由 `ADR-034` 收口，本决策收掉最后的 `_curve`）

## 背景

`ADR-034` 之后地面还是缺一件东西：原版每块 `e2dTerrain` 都有一条沿轮廓的 **`_curve` 网格**——
`e2dTerrainCurveMesh.RebuildMesh` 把 `TerrainCurve` 节点和 `StripeVertices` 排成两行三角带，`e2d/Curve`
按 `u = 弧长 × _SplatParams0.x`、`v = 节点行 1 / 条带行 0` 贴上两层贴图（`_Splat0`/`_Splat1`），
层由**嵌在关卡文件里**的控制贴图 G 通道逐节点选。PigForge 到现在只在地形边缘画一道自造的深色描边。

实测（277 关全解，`tools/bple-levels/extract-levels.mjs`）：**2146/2146** 块地形都有这条网格，共
**481 430 个节点**（962 860 顶点）；索引恒为 `(节点 − 1) × 6`（0 例外）；条带宽 86% 是 **0.1 m**
（`e2dCurveTexture.size.y`），段长 ~0.2 m；u 尺度 **10/m**（`size.x = 0.1`，`_SplatParams0.x = 1/size.x`）；
控制贴图的通道只有 `r`（首个层）与 `g`（第二个层）两种取值，于是每地形的层选择就是一组运行段
（平均 4 段 / 地形，最多 157）；两个层贴图里 **8 张 Clamp / 8 张 Repeat**。

## 决策

### 1. 内容 v4：每条 terrain 一个 `curve` 块，按版本门控

```json
"curve": {
  "textures": [ { "texture": "Ground_Grass_Texture.png", "wrap": "clamp" },
                { "texture": "Ground_Rocks_Outline_Texture.png", "wrap": "repeat" } ],
  "uScale": 10,
  "splat1": [[0, 71], [76, 13], [113, 12]],
  "nodes":  [[x, y], ...],
  "stripe": [[x, y], ...]
}
```

v4 的每条 terrain **必须有** `curve`，v1–v3 出现即硬错误（服务端解析器与客户端校验器同一条规则，
与 `fill`/`collider` 的 v3 门控一致），所以旧文档不会被悄悄塞进半个条带。

### 2. 存**网格本身**，不存节点也不重建

关卡文件里存的就是那条带索引的三角带（`LevelLoader.ReadMesh(fillMesh: false, readColor: true)`），
而 `RebuildMesh` 的重建路径要 `TerrainCurve` 节点 + `e2dTerrainBoundary` 投影，**文件里没有节点数据**。
所以内容存两行顶点：`nodes[i]` = 偶顶点（`TerrainCurve` 节点），`stripe[i]` = 奇顶点（沿法线外推 `size.y`
后夹进地形包围盒）。三角索引**不写**：带顶点数恒定满足 `(节点 − 1) × 6`（工具硬断言），而
`RebuildMesh` 挑对角线的 `PointInTriangle` 判据作用在 z 全等的平面上，两种对角线覆盖同一个四边形。

偶顶点 100% 落在 fill 顶点集里，但只有 2134/2146 是「按文件序的子序列」（9 个乱序、3 个有点不在集内），
所以要**索引化引用**就必须顺带搬 `e2dTerrainBoundary` 的投影/夹取并处理这 12 个例外——不做，宁可多存。

### 3. 层选择在**构建期**折成 `splat1` 运行段

控制贴图是编辑器写出的 1 像素高 PNG，每节点一个 texel，`e2dTerrainCurveMesh.UpdateControlTextures` 把
节点的 `texture % 4` 写进通道（r=0/g=1/b=2/a=3），shader 只读 `floor(G)`。工具解码这张 PNG
（`lib/curve.mjs` 里的最小 PNG 读取器 + `node:zlib`），断言：

- 宽度 == `NextPowerOfTwo(节点数)`（`GetControlTextureSize`，2146/2146 相符）；
- 高度 1、8 位、非隔行；
- 每个节点的通道只可能是 `r` 或 `g`（第三个/第四个层要 `_Splat2/3`，而 shader 从不采它们）；
- 每地形的层表 ≥ 2（实测 2 层 2145 块 / 3 层 1 块，第三层是 `defaultcurvetexture.png`，shader 到不了）。

于是「层」在内容里就是 `splat1` 运行段（`[[start, count], ...]`，按 start 严格递增、互不重叠、`count ≥ 1`、
终点不越界——解析器逐条校验）。**控制贴图本体不进内容**：它唯一的用途就是这组运行段。

### 4. `wrap` 必须进内容，因为它就是画面的一部分

u = 弧长 × 10 远超 1，所以采样结果取决于贴图自己的 Unity 导入态：`repeat` 平铺艺术，`clamp` 把贴图
**最右一列**沿整条带拉伸。16 张层贴图里 8 张是 Clamp（含多数 `Ground_*` 地面贴图），8 张是 Repeat
（`Border*`/`*_Outline*`）。`lib/curve.mjs` 从 `.meta` 读 `wrapU/wrapV`（并断言两者一致、filter 是
Bilinear、贴图存在），内容写 `"repeat" | "clamp"`。

### 5. `uScale` = `1f / CurveTextures[0].size.x`，在 float32 里算

shader 的 `_SplatParams0.x` 是 `RebuildMaterial` 用 float32 算的 `1f / size.x`；工具写
`Math.fround(1 / Math.fround(size.x))`（两个操作数本来就是 float32，双精度除一次再舍入等于 float32 除），
所以内容里是**恰好那个 float32**（实测 4291 条 10、1 条 1.3159、1 条 1）。

### 6. 贴图仍然**不入库**，与 fill 合流到一个目录

16 张层贴图（与 fill 的 17 张有重叠）交由 `build-levels.mjs` 的同一段逻辑复制到
`clients/web/public/assets/original/levels/`：`.gitignore` 那条「copyrighted, never committed」照旧，
新增/不再引用会被复制/删除。关卡文档只写文件名。

### 7. 客户端逐三角仿射贴图，按 shader 的 uv 与 wrap 画

每个四边形拆两个三角形，对每个三角形解出「纹理像素空间 `(u·texW, (1−v)·texH)` → 画布像素」的仿射矩阵，
`clip()` 到三角形后用 pattern 填充（v 的 1/0 与 Unity 的 v 轴向上在矩阵里折掉）。`repeat` 直接用平铺
pattern；`clamp` 在 `u = 1` 处**把三角形切开**（u 沿三角形线性，切线是直线），`u ≤ 1` 的部分照常贴图，
`u ≥ 1` 的部分用贴图最右一列做的竖向线性渐变填——这正是 Clamp 采样器给出的结果。
取不到贴图就跳过该地形的条带，不报错（与 fill 同一条退化规则）。

### 8. 画序：先所有 fill，再所有条带

原版条带在 z = −0.01、fill 在 z = 0，而相机在 z = −15 看向 +z ⇒ **z 小的更近**，所以条带压在 fill 之上。
客户端因此分两趟画（fill 全部 → 条带全部），与既有的「跨实体按 z 排序」口径一致。

## 影响与偏差

- **内容 11.35 MB → 33.15 MB**（曲线顶点 +21.2 MiB；`splat1` 运行段与三个标量可忽略），
  贴图 33 张 / 1 363 672 字节（不入库）。构建仍幂等（第二次 0 改动）。
- **条带很细**：86% 的节点只宽 **0.1 m**（段长 ~0.2 m），按默认「整关入画」的缩放大致是 **1 px**；
  12176 个节点宽为 0（夹取后退化），12 个节点宽 0.05/0.5 之外的值。它是地形的**描边艺术**，
  不是草皮——放大看才是原版那道边。
- **splat 边界是硬边**：原版控制贴图按 Bilinear 采样并跨三角形插值 `floor(G)`，所以两层在一段之内会
  **互相淡入淡出**；客户端按节点选层（段内不混合）。差一个 ~0.2 m 段的过渡，默认缩放下不足 1 px。
- **mip/滤波不逐像素对齐**（同 `ADR-034`）：验收仍用「逐三角矩阵」「棋盘/渐变边界位置」「选中层的像素」
  这类确定性判据，而不用截图相关性。浏览器 canvas 的 bilinear 与 Unity 的 mip 链不是同一条。
- **未做**：`PlasticEdges`/`CurveClosed`（`_curve` 的编辑器侧开关，文件里没有）、逐 texel 控制贴图
  入内容（只存折出的运行段）、`e2dCurveTexture.fixedAngle`/`fadeThreshold`（shader 从不读）。
