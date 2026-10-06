# 关卡地形的视觉面：fill 贴图（原版 shader 的等价实现）

> 状态：**P2b-1（fill）已实现**（2026-10-06，第二十七轮）。**P2b-2（`_curve` 条带）未做**，见 §7。
> 关联：`docs/specs/original-level-pack.md`（关卡包的二进制格式、搬运分解 §8）、`ADR-033`（关卡内容 v2 与
> `--play --level`）、`tasks/original-vs-implemented.md` 的 `G76`（地形视觉）。真值工具：
> `tools/bple-levels/{extract-levels,build-levels}.mjs`；原版渲染源码：`Assets/Resources/fill.shader`、
> `Assets/Scripts/Assembly-CSharp/{LevelLoader.cs,e2dTerrain*.cs}`。

## 1. 一句话

原版每块 `e2dTerrain` 的地面是一张**平铺贴图**（`e2d/Fill` shader：`tex2D(_MainTex, uv) * _Color`，uv 由**世界坐标**除以
tile 尺寸得到），贴图来自关卡文件的 reference 表、颜色与 tile 偏移来自关卡文件、tile 尺寸来自地形 **prefab**。
本片把这三样写进关卡内容（**schemaVersion 3**：每个地形条目一个 `fill` 块 + `collider` 位），把 17 张贴图交给
客户端（gitignored 的 `clients/web/public/assets/original/levels/`），客户端用 canvas pattern 还原同一条公式。

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

## 6. 贴图资产怎么落地

原版美术**不入库**（`.gitignore`：`clients/web/public/assets/original/`，"copyrighted, never committed"），零件图集
（`tools/bple-textures/extract.mjs`）就是这个先例。所以：

- 关卡 JSON 只写**文件名**（不知道 Web 路径）。
- `tools/bple-levels/build-levels.mjs` 把用到的贴图复制到 `clients/web/public/assets/original/levels/<name>`
  （默认目标可用 `--textures <dir>` 改；`--dry-run` 不写盘；逐字节相同则不动），并断言「用到的每一个 basename
  唯一」「源文件存在」「导入态 Repeat + Bilinear」。
- 客户端常量 `LEVEL_TEXTURE_BASE = "/assets/original/levels/"`（`clients/web/src/renderer/terrain.ts`）。

⇒ 新克隆的仓库在跑过 `build-levels.mjs`（且本机有 BPLE 工程）之前，地形是纯色填充——与零件图集同样的口径。

## 7. 已知偏差 / 未做

- **`_curve` 条带没做（P2b-2）**：原版每个地形还有一条沿轮廓的 `_curve` 网格（`e2d/Curve` shader：u = 弧长 /
  `size.x`、v = 顶点交替的 0/1、`_Control` 控制贴图的 G 通道 `floor()` 在 `_Splat0`/`_Splat1` 之间选一张），
  即草皮边/岩石边的美术。现在仍然画 v2 的那条深色描边当边缘。数据（`curveVertices`/`curveTriangles`、
  4293 条 curve texture、2146 张内嵌控制贴图、`e2dCurveTexture` 的 size/fixedAngle/fadeThreshold）**已经解码过**
  （`lib/reader.mjs`），只是没写进内容。
- **`mipmapEnabled`**：原版按 mip 采样，客户端在浏览器里交给 canvas 的 imageSmoothing（`quality`），不逐 mip 对齐。
- **`PlasticEdges`/`CurveClosed`**：属 `_curve`，同上。
- 其余未做（属 `original-level-pack.md` §8）：道具 prefab（368 个）、`PrefabOverrides`、目标/挑战/收集、进度。

## 8. 验收

```powershell
node tools/bple-levels/extract-levels.mjs                # 报告新增 fill 清单/直方图；0 失败
node tools/bple-levels/build-levels.mjs                  # 写 v3；第二次 0 changed（含贴图目录）
node tools/bple-levels/build-levels.mjs --dry-run        # 只打印计划
dotnet test PigForge.slnx                                # 关卡解析器 v1/v2/v3 用例
cd clients/web && pnpm test && pnpm build                # terrain 矩阵/着色/退化用例
```

实机：`node build-levels.mjs` → `dotnet run --project src/PigForge.Server -c Release --no-build -- --play --level original/episode_1_levels/Level_05.json`
→ 浏览器 `localhost:5173`：地面是 `Ground_Rocks_Texture` 的 5 m 平铺、第三块地形是 0x838383 的暗版、
`GET /level` 里的 `fill` 与客户端取到的 PNG 一致；把贴图目录改名后地形退化成纯色而不是报错。
