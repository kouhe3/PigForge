# ADR-034：地形的地面（fill）进内容 v3，贴图按原版美术的规矩不入库

- 状态：**已接受**（2026-10-06）
- 相关：`ADR-033`（关卡 v2 的 `terrain` + `GET /level` 侧通道）、`ADR-032`（网格形状本身）、
  `docs/specs/level-terrain-visuals.md`（全部实测数字）、`docs/specs/original-level-pack.md`
- 差距：`G76`（地形视觉；本决策收掉 fill，`_curve` 仍缺）

## 背景

`ADR-033` 让关卡把地形作为**碰撞体**进内容：一条 `e2dTerrain` 一个 `position`/`depth`/边界环，于是官方关卡
能玩、能撞。但地面在屏幕上只是「一堆灰多边形」：原版每块地形其实是一张**带贴图的多边形**，
`Assets/Resources/fill.shader` 全文 40 行——`return tex2D(_MainTex, i.texcoord) * _Color;`，uv 由
`LevelLoader.ReadMesh`（`LevelLoader.cs:279-290`）逐顶点算成 `(顶点 − tile 偏移) / tile 尺寸`。

而且 v2 的 terrain 只写**带碰撞体**的那种（1648 条），原版另外 **498 块纯视觉地形**（占 fill 顶点的 21%）
连同它们的地面一起被丢掉了——那些地方在原版是画出来的。

## 决策

### 1. 内容 v3：每个 `e2dTerrain` 一条，带 `collider` 位与 `fill` 块

```json
{
  "position": [-20.253517, -0.10463762, 0], "depth": 10,
  "collider": true,
  "fill": { "texture": "Ground_Rocks_Texture.png", "color": [255,255,255,255],
            "tileOffset": [0, 6.2], "tileSize": [5, 5] },
  "loops": [[[x, y], ...]]
}
```

- terrain 从「碰撞体来源」变成「原版的一块地形」：视觉与碰撞各由 `fill` 与 `collider` 决定，
  2146 条**一一对应**原版对象（1648 撞 / 498 纯视觉）。房间（`GameRoom.EnsureTerrainBodies`）只为
  `collider: true` 的建静态网格体，`collider` 为假的仍然只画。
- 两个新字段**按版本门控**：v3 必填，v1/v2 出现即硬错误（服务端解析器与客户端校验器同一条规则）。
  旧文档因此不会悄悄带上半个 fill。

### 2. 三个输入各有出处，一个都不许手写

| 输入 | 出处 |
|---|---|
| 贴图 | 关卡文件的 `fillTextureIndex` → loader 的 `m_references`（2146/2146 都解析到存在的 PNG） |
| `_Color` | 关卡文件里的 `uint32`（`LevelLoader.ReadColor`：`byte * 0.003921569f`），内容**存字节**——文件里就是字节，浮点化只会引入一次舍入 |
| tile 偏移 | 关卡文件（覆盖 prefab 的同名字段） |
| **tile 尺寸** | **地形 prefab 的 `e2dTerrain`**（关卡文件里没有这个数） |

`tools/bple-levels/lib/fill.mjs` 按 `m_Script` guid 找到组件、**硬断言** tile 尺寸直方图恰好 `{5×5}`
（21 个地形 prefab 全一样），并断言每张 fill 贴图的导入态是 **Repeat + Bilinear**——uv 会跑出 0..1（100 m
的关卡按 5 m 平铺 20 次），Clamp 会拖影而不是平铺。直方图变了就报错，不静默通过。

### 3. 贴图**不入库**，只把文件名写进关卡文档

原版美术一入 git 就违反仓库自己的规矩（`.gitignore` 那条 `clients/web/public/assets/original/`，
"copyrighted, never committed"，零件图集就是这么做的）。所以：

- 关卡文档只写**文件名**（不知道任何 Web 路径）；
- `build-levels.mjs` 把用到的 17 张 PNG 复制到 `clients/web/public/assets/original/levels/`
  （`--textures <dir>` 可换目标；幂等、`--dry-run`、不再引用的会被删）。

### 4. 客户端按 shader 那条公式画，锚点是**地形自己的原点**

`clients/web/src/renderer/terrain.ts` 用一张 canvas pattern：一个 repeat = `tileSize` 世界米，
矩阵 `a = tileSize.x·scale/texW`、`d = tileSize.y·scale/texH`、原点 `e/f` 取
**地形 position + tile 偏移**（uv 用的是网格自己的**本地**顶点，不是世界坐标），Unity 的 v 轴向上折进 `f`。
颜色用一次 `multiply` 离屏着色（逐像素 = `tex * _Color`，含 alpha），按 `(texture, color)` 缓存。
取不到贴图（没跑工具的克隆）**退回纯色 `#5c6b52`，不报错**。

选 canvas pattern 而不是自己写光栅器：一个 repeat = 一次 `fill()`，缩放/相机变化只改矩阵；原版 shader 的
`transparent` + 纹理采样语义与 canvas 的 source-over 一致。

### 5. 客户端不信 `collider`：两边都画

`collider` 是服务器的事（建不建刚体）。客户端画**每一块**地形，与原版一致；把 `collider` 也搬进渲染只会
让两边有机会不一致。

## 影响与偏差

- 内容 **8.7 MB → 11.35 MB**，另有 **17 张贴图 / 1 360 460 字节**（不入库 ⇒ 新克隆在跑
  `build-levels.mjs` 之前是纯色地面，与零件图集同一条口径）。
- **mip/滤波不逐像素对齐**：原版按 Unity 的 mip 链采样，浏览器用自己的缩小滤波。视觉等价，但不是逐像素
  相等——所以验收判据用「应用实际发出的矩阵」「棋盘格边界的位置」「灰度着色逐位相等」三条，不用截图相关性。
- **`_curve` 边缘条带未做**（`e2d/Curve`：u = 弧长、`_Control` 的 G 通道 `floor()` 在 `_Splat0/1` 之间选、
  2146 张嵌在关卡文件里的控制贴图）。现在地形边缘仍是那道深色描边。
- `Assets/Resources/labelbackground.png` 被 1 块地形当作 fill 贴图用；照搬，不特例。
- `PlasticEdges`/`CurveClosed` 属 `_curve`，随它一起留。
