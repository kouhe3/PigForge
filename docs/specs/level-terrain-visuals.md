# 关卡地形的视觉面：fill 贴图与 `_curve` 边缘条带（原版 shader 的等价实现）

> 状态：**P2b-1（fill）与 P2b-2（`_curve` 条带）均已实现**（2026-10-06，第二十七 / 二十八轮）：
> fill 进内容 v3（`ADR-034`），条带进内容 v4（`ADR-035`）。地形视觉到此与原版同一套公式。
> 关联：`docs/specs/original-level-pack.md`（关卡包的二进制格式、搬运分解 §8）、`ADR-033`（关卡内容 v2 与
> `--play --level`）、`ADR-034`/`ADR-035`（v3 的 fill、v4 的条带）、`tasks/original-vs-implemented.md` 的 `G76`（地形视觉，本文件收口）。真值工具：
> `tools/bple-levels/{extract-levels,build-levels}.mjs`；原版渲染源码：`Assets/Resources/fill.shader`、
> `Assets/Resources/curve.shader`、`Assets/Scripts/Assembly-CSharp/{LevelLoader.cs,e2dTerrain*.cs}`。

## 1. 一句话

原版每块 `e2dTerrain` 的地面是一张**平铺贴图**（`e2d/Fill` shader：`tex2D(_MainTex, uv) * _Color`，uv 由**世界坐标**除以
tile 尺寸得到），贴图来自关卡文件的 reference 表、颜色与 tile 偏移来自关卡文件、tile 尺寸来自地形 **prefab**。
本片把这三样写进关卡内容（**schemaVersion 3**：每个地形条目一个 `fill` 块 + `collider` 位），把 17 张贴图交给
客户端（gitignored 的 `clients/web/public/assets/original/levels/`），客户端用 canvas pattern 还原同一条公式。

地形的**边缘**另有一条沿轮廓的 `_curve` 三角带（`e2d/Curve` shader：两层贴图由控制贴图的 G 通道逐节点选，
u = 弧长 × `1/size.x`、v 由节点行 1 到条带行 0）。**schemaVersion 4** 把这条带的**两行顶点**、两层贴图与它们的
wrap、u 尺度、以及「第二层覆盖哪些节点」写进每个地形条目，客户端逐三角做仿射贴图还原同一个 shader。

## 2. 原版真值

### 2.1 shader（唯一权威）

`Assets/Resources/fill.shader`（`Shader "e2d/Fill"`，全文 40 行）：

```hlsl
// vert:  o.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);   // uv 已在顶点里，_MainTex_ST 是 (1,1,0,0)
// frag:  return tex2D(_MainTex, i.texcoord) * _Color;
```

顶点 uv 由 `LevelLoader.ReadMesh(fillMesh: true)`（`LevelLoader.cs:279-290`）算出，**不是**文件里的数据：

```csharp
uv[j].x = (vertex[j].x - terrain.FillTextureTileOffsetX) / terrain.FillTextureTileWidth;
uv[j].y = (vertex[j].y - terrain.FillTextureTileOffsetY) / terrain.FillTextureTileHeight;
```

即 **uv = (世界坐标 − tile 偏移) / tile 尺寸**，地形实例只有平移（2146/2146，见 §3），所以「世界坐标」就是
`instance.position + 顶点`。**注意 uv 用的是网格自己的（本地）顶点**：贴图的相位锚在**地形自己的原点**上，
不是世界原点——两块地形的平铺相位相差各自的 `position`。

### 2.2 三样输入的来源

| 输入 | 来源 | 读法 |
|---|---|---|
| 贴图 | 关卡文件的 `fillTextureIndex` → loader 的 `m_references[index]` | `LevelLoader.cs:229-230`（`mainTexture = m_references[index]`） |
| 颜色 `_Color` | 关卡文件里的 `uint32`（RGBA，R 在高字节） | `LevelLoader.cs:172-181` `ReadColor`：`component = byte * 0.003921569f` |
| tile 偏移 | 关卡文件（两个 float，覆盖 prefab 的同名字段） | `LevelLoader.cs:214-216` |
| tile 尺寸 | **地形 prefab 的 `e2dTerrain`**（文件里没有） | `LevelLoader.cs:217-218` 只写偏移；`FillTextureTileWidth/Height` 保持 prefab 值（`e2dConstants.INIT_FILL_TEXTURE_*` = 1f 只是无 prefab 时的默认） |

`e2dTerrain.CurveClosed`/`PlasticEdges`/`NoCollider` 与 fill 无关（属 `_curve`，见 §7）。

### 2.3 贴图采样态

fill 贴图必须是 **Repeat + Bilinear**（`Ground_*.png.meta`：`wrapU: 0` `wrapV: 0` `filterMode: 1`）——uv 会远
超出 0..1（100 m 的关卡按 5 m 平铺 ⇒ 20 次重复），Clamp 会拖影，工具**硬断言**这两个值。

### 2.4 边缘条带（`curve.shader` 与 `e2dTerrainCurveMesh`）

每块地形还有一条沿轮廓的三角带，由 `e2dTerrainCurveMesh.RebuildMesh` 从 `e2dTerrain.TerrainCurve` 与
`StripeVertices` 排成两行：偶顶点是 `TerrainCurve` 节点（落在地形表面上），奇顶点是该节点沿法线外推
`e2dCurveTexture.size.y` 之后的点，再被 `e2dTerrainBoundary.EnsurePointIsInBoundary` **夹进地形的包围盒**
（不是夹进多边形）。每段两个三角形（`(节点 − 1) × 6` 个索引），对角线由 `e2dUtils.PointInTriangle` 选——
两行 z 相同、四边形是平面，两种对角线覆盖同一块面积，所以内容不存索引。

关卡文件里存的就是这条**网格本身**（`LevelLoader.ReadMesh(fillMesh: false, readColor: true)`：`float2` 顶点 +
`int16` 索引，`z` 一律 −0.01），节点数据不在文件里。着色由 `Assets/Resources/curve.shader` 决定：

```hlsl
// vert:   o.texcoord0 = float2(v.texcoord.y * _InvControlSize + _InvControlSizeHalf, 0);   // 节点下标 → 控制贴图
//         o.texcoord1 = float2(v.texcoord.x * _SplatParams0.x, v.color.x);                 // 弧长 → u，行 → v
// frag:   var2 = floor(tex2D(_Control, i.texcoord0).y);                                     // G 通道选层
//         var3 = tex2D(_Splat0, i.texcoord1); var3.xyz += (tex2D(_Splat1, i.texcoord1).xyz - var3.xyz) * var2;
```

| 输入 | 出处 |
|---|---|
| 两行顶点 | 关卡文件的 curve 网格（偶 = 节点，奇 = 条带顶点） |
| 层贴图 | 关卡文件的 `m_references`，按 `curveTextureCount` 条 `{textureIndex, size, fixedAngle, fadeThreshold}` |
| `uScale` | `_SplatParams0.x = 1f / CurveTextures[0].size.x`（`RebuildMaterial`，float32） |
| 逐节点层选择 | **嵌在关卡文件里的控制贴图 PNG**（1 像素高、每节点一个 texel，`r` = 第 1 层、`g` = 第 2 层） |

网格顶点上写死的东西：`v.color.x` = `(k + 1) % 2`，所以**偶顶点 v = 1、奇顶点 v = 0**；`uv.x` 是沿**偶顶点**
累积的弦长（`LevelLoader.ReadMesh` 的 curve 分支），`uv.y` 是节点下标；`uv[0] = uv[1] = (0, 0)`。层贴图的
wrap 是它自己的导入态（u 远超 1）：**Clamp 的层只会显示贴图最右一列**，Repeat 的层平铺。

## 3. 实测清单（pristine `BPLE 2022.1.9`，2026-10-06）

| 项 | 值 |
|---|---|
| `e2dTerrain` 对象 | **2146**（带碰撞体 **1648**，纯视觉 **498**） |
| fill 贴图（去重） | **17**：16 张 `Assets/Texture2D/Ground_*.png` + `Assets/Resources/labelbackground.png`（1 个地形） |
| 贴图解析失败 | **0**（2146/2146 都解析到一张存在的 PNG） |
| tile 尺寸 | 21 个地形 prefab 全是 **5 × 5**（`FillTextureTileWidth/Height`；唯一取值） |
| 颜色 | 8 种：`0xffffffff` 1768、`0xbebeffff` 167、`0x838383ff` 76、`0x828282ff` 52、`0x969696ff` 45、`0x829cb9ff` 34、`0x879cb9ff` 3、`0x889cb9ff` 1 |
| tile 偏移 | 2080 个是 `(0, 6.2)`（= `e2dTerrainBase.prefab` 自己的值），其余 66 个是 `(0, 4…8.4)` 里的几个值 |
| 贴图尺寸 | 16 张 512×512、`Ground_Temple_cave`/`Ground_Temple_cave_dark` 1024×1024、`labelbackground` 32×32（RGBA8） |
| 轮廓 | 2146/2146 都能走成闭环（1643+494 顶点表即边界；其余 9 个是捏合顶点/界外顶点，`lib/outline.mjs` 处理） |
| 内容体积 | 8.7 MB（v2，只写 collider 地形）→ **11.35 MB**（v3：+498 块纯视觉地形 + 每块的 `fill` 块），贴图 17 张共 1 360 460 字节 |

边缘条带（同一轮实测，`tasks/bple-levels-report.json` 的 `curve` 段）：

| 项 | 值 |
|---|---|
| 有 curve 网格的地形 | **2146 / 2146**（每块都有），节点 **481 430**，索引恒 `(节点 − 1) × 6`（0 例外） |
| 每地形节点数 | 628 个取值（最常见 48） |
| 条带宽 `abs(stripe − node)` | **0.1 m** 415 835、**0.5 m** 37 544、**0.05 m** 14 088、**0** 12 171（夹取后退化），其余零星（最大 9.55） |
| 段长（相邻节点） | ~0.16–0.28 m |
| 层表条数 | **2** 层 2145 块 / **3** 层 1 块（第三层是 `Resources/defaultcurvetexture.png`，shader 不采） |
| `e2dCurveTexture.size`（x×y，米） | `0.1×0.1` 2005、`0.1×0.5` 1847、`0.1×0.2` 189、`0.1×0` 142、`0.1×0.05` 108，其余 2 条 |
| 第二层运行段 | 合计 **5260** 段，命中 **428 752 / 481 430** 个节点（89.1%）；每地形 0 段 105、1 段 992、最多 157 |
| 层贴图 | **16** 张（8 Clamp + 8 Repeat，全 Bilinear）；`Ground_*` 地面贴图多是 Clamp、`Border*`/`*_Outline*` 多是 Repeat |
| 内容体积 | 11.35 MB → **33.15 MB**（v4：+962 860 个点；`splat1` 运行段与三个标量可忽略），贴图 33 张共 1 363 672 字节 |

## 4. 内容契约：`schemaVersion 3`

v3 在 v2 之上加两件事，**每个** terrain 条目都带：

```jsonc
{
  "position": [-20.253517, -0.10463762, 0],
  "depth": 10,
  "collider": true,                    // v3 必填：false = 原版有网格但无 MeshCollider（纯视觉）
  "fill": {
    "texture": "Ground_Rocks_Texture.png",  // 关卡贴图目录下的文件名（不含路径）
    "color": [255, 255, 255, 255],          // RGBA **字节**，即文件里的 uint32
    "tileOffset": [0, 6.2],                 // 世界米
    "tileSize": [5, 5]                      // 世界米，来自地形 prefab
  },
  "loops": [[[x, y], ...]]
}
```

规则（服务端解析器与客户端校验器都实现）：

- v1/v2 文档：`terrain` 可省；terrain 条目里 **不许**出现 `fill`/`collider`（出现即硬错误，防旧文档漂移）。
- v3 文档：每个 terrain 条目 **必须**有 `collider` 与 `fill`；`fill.texture` 非空且无空白、`color` 是 4 个
  `0..255` 整数、`tileOffset` 是 2 个有限数、`tileSize` 是 2 个**正**有限数。
- `color` 存**字节**而不是 `_Color` 的浮点：文件里就是 `uint32`，字节是精确值，浮点化会引入一次
  `byte * 0.003921569f` 的舍入；客户端按原版同一个常量还原。

**为什么 terrain 从 1648 涨到 2146**：v2 把 terrain 当作「碰撞体来源」，只写带 `hasCollider` 的；v3 的 terrain 是
「原版的一块地形」，视觉与碰撞各由 `collider` 决定——498 块纯视觉地形（占 fill 顶点的 21%）在 v2 里被整块丢掉，
地上会缺图。房间现在只为 `collider: true` 的条目建静态网格体。

## 4b. 内容契约：`schemaVersion 4`（边缘条带）

v4 在 v3 之上给**每个** terrain 加一个 `curve` 块（`ADR-035`）：

```jsonc
"curve": {
  "textures": [                                    // 恰好 2 条：shader 的 _Splat0 / _Splat1
    { "texture": "Ground_Grass_Texture.png", "wrap": "clamp" },
    { "texture": "Ground_Rocks_Outline_Texture.png", "wrap": "repeat" }
  ],
  "uScale": 10,                                    // 1f / CurveTextures[0].size.x（float32）
  "splat1": [[0, 71], [76, 13], [113, 12]],        // 用 textures[1] 的节点运行段；其余用 textures[0]
  "nodes":  [[x, y], ...],                         // 偶顶点（TerrainCurve 节点），≥ 2 个
  "stripe": [[x, y], ...]                          // 奇顶点（外推并夹取后的条带点），与 nodes 等长
}
```

规则（服务端解析器与客户端校验器都实现）：

- v1–v3 文档：terrain 条目里 **不许**出现 `curve`（硬错误）；v4 文档：每条 terrain **必须**有 `curve`。
- `nodes`/`stripe` 等长且各 ≥ 2 点，点都是 `[x, y]` 有限数；`nodes.length` 就是节点数。
- `textures` 恰好 2 条 `{ texture, wrap }`，`texture` 是 1–128 个非空白字符的文件名，`wrap ∈ {"repeat","clamp"}`。
- `uScale` 是**正**有限数；`splat1` 每项 `[start, count]`，`start ≥ 0`、`count ≥ 1`、按 start **严格递增**
  （即互不重叠）、`start + count ≤ 节点数`。

**为什么存两行顶点**：关卡文件里就是这条网格，节点数据（`TerrainCurve`）不在文件里；`RebuildMesh` 的重建路径
还要 `e2dTerrainBoundary` 的投影/夹取。索引不存（恒 `(节点−1)×6`，两种对角线覆盖同一平面面积）。
**为什么 layer 折成运行段**：控制贴图是每节点一个 texel 的 1 像素高 PNG，G 通道即层选择；工具在构建期解码
PNG 并断言宽度 == `NextPowerOfTwo(节点数)`、通道只有 `r`/`g`，之后就不再需要这张图。
**为什么 wrap 进内容**：u = 弧长 × 10 远超 1，Clamp 的层只会显示贴图最右一列——这是画面的一部分，不是实现细节。

## 5. 客户端画法

1. **贴图**：`/assets/original/levels/<name>`（与零件图集同源，见 §6）。取不到就把该地形退化成 v2 行为
   （纯色填充 + 描边），不报错。
2. **颜色乘**：贴图先画进一张同尺寸的离屏 canvas，再用 `globalCompositeOperation = "multiply"` 叠一层
   `rgba(byte*0.003921569 …)`，最后用这张**已着色**的 canvas 建 pattern。逐像素等价于 `tex * _Color`（含
   alpha：shader 的 `Blend SrcAlpha OneMinusSrcAlpha` 与 canvas 的 source-over 一致）。按 `(texture, color)`
   缓存。
3. **pattern 矩阵**：pattern 的坐标空间是贴图像素（x 向右、y 向下），要映射到 canvas 像素（`scale` = 相机每米
   像素数，`origin = 地形 position + tile 偏移`）：

   ```
   a =  tileSize.x * scale / texW          e = (origin.x - camera.x) * scale + width / 2
   d =  tileSize.y * scale / texH          f =  height / 2 - (origin.y + tileSize.y - camera.y) * scale
   b = c = 0
   ```

   `f` 多出的 `tileSize.y` 是因为 Unity 的 uv 原点在**左下**而 canvas 的行从**上**开始（v = 1 − row/texH）：
   贴图第 0 行是 tile 的**上边**，即 `origin.y + tileSize.y`。
4. **覆盖范围**：`loops` 按 nonzero 填充（孔洞是反向环，`lib/outline.mjs` 已经分开走），与 v2 一致。

**实测校核**（真 Chromium + 真 vite，三块地形的官方关卡 `original/episode_1_levels/Level_05`）：

- 把 `CanvasPattern.prototype.setTransform` 打桩记录，应用**实际发出**的矩阵与公式对三块地形**逐位相等**
  （`a = d = 5 × 9.2256 / 512`，`e`/`f` 各不相同）。
- 512×512、每格 64 texel 的棋盘贴图按 `tileSize 5`、相机 9.2256 px/m、dpr 1.25 绘制：格边界与公式预测的
  间距（7.2075 px）与位置一致（**平均偏差 0.25 px、最大 0.49 px**）。这条同时钉住锚点：若漏掉
  `position`（用世界原点锚定），边界会整体错开 `(position + tileOffset) mod 0.625 m`，本例约 **2.8 px**。
- 同一测试里把 `color` 换成 `[128,128,128,255]`：渲染值 = texel × 128/255（**逐位**）。
- 端到端：真服务器 + 真 vite + 真页面，地面贴图可见、右侧那块 `0x838383` 的地形明显更暗；把贴图目录改名后
  地面退回纯色 `#5c6b52`，**控制台 0 错误**。

> 反面对照：拿「渲染像素 vs 贴图盒式平均」的相关性当判据会读到很低（≈0.02–0.18），因为渲染是 8.9 倍缩小、
> 走浏览器自己的 mip/双线性链，与盒式平均不是同一个滤波器；判据要用上面前三条（逐位矩阵、棋盘边界、
> 逐位着色）。

### 5.2 边缘条带

1. **几何**：对每个节点 `i`，四边形 `(nodes[i], stripe[i], stripe[i+1], nodes[i+1])` 拆两个三角形；
   宽为 0 的段（12171 个节点）没有面积，直接跳过。
2. **uv**：`u[i]` = 沿 `nodes` 累积的弦长（`u[0] = 0`）乘 `uScale`；`v = 1` 在 `nodes` 行、`v = 0` 在 `stripe` 行。
   纹理像素空间是 `(u × texW, (1 − v) × texH)`（Unity 的 v 轴向上，折进矩阵里）。
3. **逐三角仿射**：由三角形三个角的 `(纹理像素, 画布像素)` 对应解出仿射矩阵，`clip()` 到三角形后用 pattern 填充
   ——这等价于硬件在平面四边形上的插值（正交相机下没有透视项）。
4. **层**：节点落在 `splat1` 运行段里就用 `textures[1]`，否则 `textures[0]`。层在同一段的两个节点之间切换时，
   客户端按**左节点**选层（硬边）；原版的控制贴图是双线性采样并跨三角形插值 `floor(G)`，所以它在**一段之内**
   会两层淡入淡出（见 §7 的偏差）。
5. **wrap**：`repeat` 的层用平铺 pattern；`clamp` 的层在 `u = 1` 处切开三角形（u 沿三角形线性 ⇒ 切线是直线），
   `u ≤ 1` 的部分照常贴图、`u ≥ 1` 的部分用贴图**最右一列**做的竖向线性渐变填——等价于 Clamp 采样器。
6. **画序**：所有 fill 先画，再把所有条带画在上面（原版条带 z = −0.01 比 fill 的 0 更近相机）。
7. **退化**：任一层贴图没加载出来就跳过该地形的条带（不报错、不画描边）。

## 6. 贴图资产怎么落地

原版美术**不入库**（`.gitignore`：`clients/web/public/assets/original/`，"copyrighted, never committed"），零件图集
（`tools/bple-textures/extract.mjs`）就是这个先例。所以：

- 关卡 JSON 只写**文件名**（不知道 Web 路径）。
- `tools/bple-levels/build-levels.mjs` 把用到的贴图（fill 17 张 + 条带 16 张，去重后 33 张）复制到
  `clients/web/public/assets/original/levels/<name>`（默认目标可用 `--textures <dir>` 改；`--dry-run` 不写盘；
  逐字节相同则不动），并断言「用到的每一个 basename 唯一」「源文件存在」「Bilinear」；fill 的那一半额外要求
  Repeat（条带层允许 Clamp）。
- 客户端常量 `LEVEL_TEXTURE_BASE = "/assets/original/levels/"`（`clients/web/src/renderer/terrain.ts`）。

⇒ 新克隆的仓库在跑过 `build-levels.mjs`（且本机有 BPLE 工程）之前，地形是纯色填充——与零件图集同样的口径。

## 7. 已知偏差 / 未做

- **条带很细**：86% 的节点只宽 0.1 m、段长 ~0.2 m ⇒ 按「整关入画」的默认缩放大致 **1 px**（放大才看得到原版那道边）；
  宽 0 的段退化无面积，客户端跳过。
- **splat 边界是硬边**（§5.2 第 4 条）：原版两层在一段内互相淡入淡出，客户端按节点选层；差一个 ~0.2 m 段的过渡。
- **`mipmapEnabled`**：原版按 mip 采样，客户端在浏览器里交给 canvas 的 imageSmoothing（`quality`），不逐 mip 对齐。
- **`PlasticEdges`/`CurveClosed`**：`_curve` 的编辑器侧开关，文件里没有，未做。
- **`e2dCurveTexture.fixedAngle`/`fadeThreshold`**：shader 从不读，未写进内容（`size.x` 折成 `uScale`，`size.y` 已烘进顶点）。
- **控制贴图本体不入库/不进内容**：只把折出的 `splat1` 运行段写进内容；将来若要逐 texel 相等再说。
- 其余未做（属 `original-level-pack.md` §8）：道具 prefab（368 个）、`PrefabOverrides`、目标/挑战/收集、进度。

## 8. 验收

```powershell
node tools/bple-levels/extract-levels.mjs                # fill + curve 清单/直方图 + 硬断言；0 失败
node tools/bple-levels/build-levels.mjs                  # 写 v4；第二次 0 changed（含贴图目录）
node tools/bple-levels/build-levels.mjs --dry-run        # 只打印计划
dotnet test PigForge.slnx                                # 关卡解析器 v1/v2/v3/v4 用例
cd clients/web && pnpm test && pnpm build                # terrain 矩阵/着色/退化 + 条带用例
```

实机：`node build-levels.mjs` → `dotnet run --project src/PigForge.Server -c Release --no-build -- --play --level original/episode_1_levels/Level_05.json`
→ 浏览器 `localhost:5173`：地面是 `Ground_Rocks_Texture` 的 5 m 平铺、第三块地形是 0x838383 的暗版、
`GET /level` 里的 `fill` 与客户端取到的 PNG 一致；关卡的边缘能看出条带（原版那道描边艺术），
把贴图目录改名后已加载的两套都退化成纯地面而不是报错。
