# 原版关卡包：二进制关卡格式、调色板与播放顺序

> 状态：**格式已收口**（2026-10-06，全部数字由 `tools/bple-levels/extract-levels.mjs` 实测）。
> 搬运范围（搬多少关、地形物理怎么做、进度存哪）仍是**开放问题**，见 §9。
> 关联差距：`tasks/original-vs-implemented.md` 的 `G73`（格式）、`G74`（关卡数量/进度）、
> `G75`（关卡物件）、`G76`（地形）、`G77`（过关判定）、`G79`/`G81`/`G82`/`G83`/`G84`（星级/计时/收集/进度/结算）。

## 1. 一句话

原版不是 Unity 场景：每关是一个**自定义二进制对象树**（`<scene>_data.bytes`）
+ 一个把它翻译成 prefab 的**调色板**（同目录的 `Resources/levels/**/<scene>_loader.prefab` 的 `LevelLoader.m_prefabs`），
播放顺序与星关门限在 `GameObject/Episode*Levels.prefab` 的 `m_levelInfos` / `m_starLimits` 里。

工具：`node tools/bple-levels/extract-levels.mjs`（默认读 `../BPLE 2022.1.9`，报告写 `tasks/bple-levels-report.{json,md}`）。
它**硬失败**于：文件数不是 277、任一文件解码后剩字节、关卡没有对应 loader、`PrefabIndex` 越出调色板、
调色板 guid 解析不到资产、某关没有任何地形对象。漂移不会静默通过。

## 2. 文件清单

| 项 | 数量 | 出处 |
|---|---|---|
| 关卡数据文件 | **277** | `Assets/assetbundles/*levels*/*.bytes` |
| 每集 bundle | `episode_1..6` = 45/45/45/45/30/45、`episode_race` = 8、`episode_sandbox` = 10、`episode_sandbox_levels_2` = 4 | 同上 |
| 每关 loader（调色板） | **277** | `Assets/Resources/levels/<episode*>[_cave]/<scene>_loader.prefab` |
| 播放顺序清单 | **8**（6 集 + Race + Sandbox） | `Assets/GameObject/Episode*Levels.prefab` |

**两个本地副本的关卡资产有真实差异**（2026-10-06 实测）：277 个 `_data.bytes` **逐字节相同**，
但 Unity 6 迁移把三个 loader 的调色板改短了 —— `episode_6_level_12` 152→**151**、`episode_6_level_ii` 191→**190**、
`mmsandbox` 804→**799**，而关卡数据仍然引用那些下标（`episode_6_level_12` 要 `151`、`MMSandbox` 要 `802/803`）。
短调色板会把越界之后的所有实例**静默错认**，所以本工具默认读 **pristine `BPLE 2022.1.9`**（原版自己的
Unity 2021.3.45f2 工程），报告里的 `source.bpleRoot` 记录实际读的是哪一份。

## 3. 二进制格式

小端；字符串按 .NET `BinaryReader` 语义（7 位长度前缀 + UTF-8）。
运行期读法 `LevelLoader.cs:88-208`，去 Unity 依赖的纯解析版 `LevelFormatReader.cs:9-124`（两者逐字段一致）。

```
int32 rootCount，然后 rootCount 个对象
对象 := int16 childCount
        childCount == 0  → prefab 实例：string name; int16 prefabIndex;
                           Vector3 position, Vector3 euler, Vector3 localScale; 数据块
        childCount  > 0  → group：string name; Vector3 position; childCount 个对象
数据块 := uint8 type：0 无 | 1 地形 | 2 PrefabOverrides
地形   := float fillTextureTileOffsetX, float fillTextureTileOffsetY
          int32 n, n × Vector2   fillVertices      （z 恒为 0，真 2D 多边形）
          int32 m, m × int16     fillTriangles     ← 索引是 **int16**
          uint32 fillColor       （RGBA 字节，LevelLoader.ReadColor）
          int32 fillTextureIndex （索引进 loader 的 m_references）
          int32 n, n × Vector2   curveVertices
          int32 m, m × int16     curveTriangles
          int32 curveTextureCount，逐条：int32 textureIndex, Vector2 size, bool fixedAngle, float fadeThreshold
          int32 controlFlag；> 0 时 int32 byteLength + byteLength 字节（**一张 PNG**）
          bool hasCollider
overrides := int32 byteLength + byteLength 字节（UTF-8，ObjectDeserializer 文本）
```

读完之后原版还做两件事，搬运必须一起复刻：

- **`TerrainScale` 乘子**：每个实例的 `position` 与 `localScale` 各乘
  `INSettings.TerrainScale × INUserSettings.LevelSceneSettings.TerrainScale`，z 不变（`LevelLoader.cs:186-193`）。
  声明默认档 `INDeclarationSettingsExp.json` 里 `TerrainScale` 的声明值是 **1.0** ⇒ vanilla 下是恒等变换
  （见 `docs/specs/in-settings-profiles.md` 的基准口径）。
- **地形碰撞体来自 fill 多边形**，不是 curve 网格：`hasCollider` 时把 fill 顶点按
  `e2dConstants.COLLISION_MESH_Z_DEPTH` 挤出成三角带，铺在名为 collider 的子节点上（`LevelLoader.cs:339-380`）。
  `hasCollider` 的关卡占比见 §6。

`PrefabOverrides` 是**文本**（`ObjectDeserializer`），不是二进制；解析时用 loader 的 `m_references`
作为引用表（`LevelLoader.cs:198-208`）。277 关**全部**带 overrides（1795 条 / 2.08 MB）——它是关卡级组件数据
（挑战目标、相机限制、星级门限之类）的载体，所以「搬关」绕不开它。

## 4. 调色板与 `m_references`

- `m_prefabs` 是有序 `GameObject` 引用表（YAML 里是 guid 列表，经 `*.meta` 解析到 prefab 资产路径）；
  关卡里的 `int16 prefabIndex` 直接索引它。实测每关调色板 **24…799** 条（中位 70），跨全部关卡去重后 **376** 个 prefab。
- **实例名不是 prefab 名**：`LevelLoader.cs:113` 用数据里的字符串覆盖 `gameObject.name`，那是关卡作者起的标签。
  实测 26072 个实例里 **6415** 个标签与调色板 prefab 名不同 —— 典型两例：调色板里同一个 `DessertPlace`
  prefab 被 **5325** 个实例复用、靠标签区分 `DessertPlace01…10`；`background_*_set` 系族标签大写而资产名小写。
  ⇒ 搬关要的是「下标 → prefab」，标签只用于辨认。
- `m_references` 是**通用对象表**（地形贴图 + `CameraPreview` 之类的脚本 + …），不全是贴图：
  实测去重后 **715** 个资源，主要是 `Assets/Texture2D/Ground_*.png` / `Border*.png`；`fillTextureIndex`
  与每个 curve texture 的 `textureIndex` 都索引它。
- 每个地形另有一张**嵌在关卡文件里**的控制贴图（PNG 字节，合计 **2146** 张 = 每张地形一张）。

## 5. 播放顺序、星关门限与进度

`Episode*Levels.prefab` 的 `m_levelInfos` 是**有序**列表（`sceneName` + loader guid），`totalLevelCount` 与长度一致
（45/45/45/45/30/45）；Race/Sandbox 用的是另一个类，键名是 `m_levels`，只有 `m_levelLoaderPath`（没有 `sceneName`），
场景名从路径 `<scene>_loader.prefab` 取。

`m_starLimits` 是 **9 个小端 int32** 的打包 blob，六集**完全相同**：`8, 8, 8, 10, 10, 10, 12, 12, 12`。
45 关的集里每 5 关一个星关 ⇒ 9 个门限对应 9 个星关，语义是「该行前几关的星数之和 ≥ 门限则解锁」
（`LevelComplete.cs:348-349`；`tasks/original-vs-implemented.md` 的 `G83` 把该校验标为 [INFERENCE]，本文档只记录读到的整数）。

`GameProgress.cs` 持久化每关 `_stars` / `_time` / `_challenge_N`；星关/竞速关另有按星数解锁零件（`m_starLimit → m_part`）。

## 6. 实测总量（pristine 副本，2026-10-06）

| 项 | 值 |
|---|---|
| 实例（prefab instances） | **26072**（另有 900 个 group、4815 个根） |
| 地形对象 | **2146**（**每关都有**，276 关多于 1 个，单关最多 123 个） |
| 带碰撞体的地形 | **1648**（占 2146 的 77%） |
| fill 网格合计 | 484 056 顶点 / 1 439 265 索引 |
| curve 网格合计 | 962 860 顶点 / 2 875 704 索引 |
| curve texture 条目 | 4293 |
| 控制贴图（内嵌 PNG） | 2146 |
| PrefabOverrides | 1795 条 / 2 077 285 字节（**277/277 关都有**） |
| 调色板去重 prefab | 376 = 零件 **8** + 非零件道具 **368** |
| 出现过的零件 prefab | 仅 8 族共 20 个实例（气球 6、手推车轮 5、木框 3、铁框 2、普通轮/木尾翼/伞/螺旋桨各 1） |
| 高频道具 | `DessertPlace` 5325、`Glow_01` 657、`Mushroom_02` 386、`Gravel_02/03` 370/361、`e2dTerrainBase` 358、`Crystal_02/03` 325/329、`TNT_Box` 321、`LevelStart`/`LevelManager`/`CameraSystem` 各 277/277/276、`BoxChallenge` 269、`StarBox` 262 |

两条对范围判断有用的结论：

- **官方关卡几乎不预置零件**（只有 8 个实例级零件，20 个实例）——「关卡里的零件」是**建造栏**的事，
  不是 `m_prefabs` 的事。所以搬关 ≠ 搬调色板，而是搬**地形 + 道具 + 关卡目标**。
- 道具是 368 个 prefab 的**新内容面**（`DessertPlace`、`StarBox`、`BoxChallenge`、`Glow_01`、`Gravel_*`、
  `Crystal_*`、`Mushroom_*`、`Pumpkin_Leaf`、`Grass_*`、`Rock_*`、`FGStones*`、`Colorful_Sparkles_*`、
  各 `e2dTerrainBase_*` 变体…），PigForge 目前**一个都没有**。

## 7. PigForge 现状

- 关卡载体：`content/levels/{slope-v1,terrain-v1}.json`，schema `schemas/level-content-v1.schema.json`，
  解析器 `LevelContentParser`（启动期严格校验）。地形是若干静态 primitive（`ground-slab` box、
  `terrain-box` box、`ramp-plank` 斜 box）按 `spawns` 摆放；**没有网格地形、没有曲线地形**（`G76`）。
- 目标：`goalZone` 矩形 = 过关（`G77` 缺「同载具零件代猪到达 2.5 单位连通」那条）；失败 = 出界或 1200 tick 超时（`G78`，原版两者皆无）。
- 没有星级/挑战/计时/收集/存档/关卡选择/结算页（`G79`–`G84` 全部未实现）。
- 物理契约已有 `PhysicsShapeKind.ConvexMesh` / `TriangleMesh` 两个枚举值、`PartContentParser` 也认得
  `convexMesh`/`triangleMesh` 的 `vertices`/`triangles` 键，但**两个后端都不实现**：
  `BepuPhysicsWorld.cs:153` 明说「exactly one box, sphere, or compound shape per body」，
  compound 子件也只允许 box/sphere（`:1242`）。Bepu 2.4.0 自带 `BepuPhysics.Collidables.Mesh`（三角网格）形状。

## 8. 搬运分解（建议顺序）

| 片 | 内容 | 依赖 |
|---|---|---|
| P1 ✅ | 解码工具 + 报告（本文档的证据面） | — |
| P2 | 关卡内容格式 v2：地形（fill 多边形 + 曲线 + 控制贴图）、道具实例（prefab → PigForge 内容）、`PrefabOverrides` 的最小语义集 | 格式已定（§3） |
| P3 | 地形进物理：新增网格形状的契约 + 两个后端（或按 §9 A2 的近似路线） | P2 |
| P4 | 道具件：先做每关都需要的（`LevelStart`、`DessertPlace`、`StarBox`、`BoxChallenge`、`e2dTerrainBase`） | P2 |
| P5 | 目标/挑战/收集：星级 3 条（过关 + 两个 Challenge）、计时、收集计数 | P4 |
| P6 | 进度持久化 + 关卡选择 + 结算页 | P5 |

## 9. 开放问题（A = 需要拍板，B = 按默认落文档）

- **A1 搬运范围**：① 只做格式与地形（能加载任意官方关卡、但不做进度/星级）；② 先搬一个 episode（45 关）
  打通「选择 → 玩 → 结算 → 存进度」；③ 全 277 关（含 368 个道具 prefab 与全部挑战类型）。工作量差一个数量级。
- **A2 地形物理**：① 真三角网格形状（两个后端都要新增；最接近原版）；② 离线把 fill 多边形转成静态凸体/盒子拼接
  （用现有契约，零后端改动，但与原版地形表面不一致）。③ 只做视觉网格、碰撞仍用包围体（最省，手感最差）。
- **A3 进度存哪**：服务器侧文件（多人一致、可做排名）还是客户端 localStorage（单机、零后端）。
- **A4 是否保留原版失败条件**：搬关后 `MaxTicks` 超时与出界判负是 PigForge 自定（`G78`），原版两者皆无。
- **B1 关卡 JSON**：新增 `schemaVersion 2`（地形 + 道具 + 目标扩展），现有两张关卡保持可解析。
- **B2 道具件的最小集合**：先按「每关都出现」的清单做，其余按需。
- **B3 `TerrainScale`**：按声明默认档 1.0 走恒等；不做用户档位。

## 10. 验收

```powershell
node tools/bple-levels/extract-levels.mjs                     # 默认 pristine；期望 0 失败
node tools/bple-levels/extract-levels.mjs --bple "C:/tmp/BAD_PIGGIES/BPLE_Unity6"
#   → 期望 4 条失败：episode_6_level_12 / episode_6_level_ii / MMSandbox 的越界下标（§2 的迁移漂移）
```

报告里必须复核的数字：`counts.files = 277`、每 bundle 45/45/45/45/30/45/8/10/4、`totals.terrain = 2146`、
`totals.terrainWithCollider = 1648`、`totals.prefabOverrides = 1795`、`palette.parts` 恰 8 族、
`failures = []`。
