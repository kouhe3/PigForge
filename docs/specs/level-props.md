# 关卡道具件：原版的装饰四边面、图集与深度

> 状态：**分类、画法与深度已实测收口**（2026-10-09，全部数字由 `tools/bple-props/extract-props.mjs` 与
> `tools/bple-levels` 的工具读出，报告在 `tasks/` 下，不入库）。
> 范围决定：用户 2026-10-09 把 **A1 从「只做格式 + 地形」扩到含道具件**（`docs/specs/original-level-pack.md` §9 A1）。
> 关联差距：`G75`（关卡物件）、`G79`–`G84`（星级/计时/收集——道具件里的行为件是它们的载体）。

## 1. 一句话

原版关卡除地形之外还有 **368 个道具 prefab**（关卡调色板里除 8 族零件、21 个地形 prefab 之外的全部）。
它们不是一个整体：按「有没有渲染器 / 有没有碰撞体 / 有没有行为脚本」分成四类，**只有一类能在这一刀里
只靠画图复刻**——**单四边面的装饰件**（251 个 prefab、15132 个实例）。本规格就是它的真值。

## 2. 分类（实测，按 prefab 与实例）

| 类 | prefab | 实例 | 是什么 | 处置 |
|---|---|---|---|---|
| **装饰四边面** | **269** | **15256** | 单/多四边面 + `Sprite`/`UnmanagedSprite` 脚本，无碰撞体 | **本片（P4a）** |
| 系统标记 | 21 | 6674 | 只有脚本没有渲染器：`LevelStart`/`LevelManager`/`CameraSystem`/`TimeChallenge`/`DontUsePartChallenge`/`DessertPlace`/`Cloud*Set`/`Bridge`… | 各自单独（相机界已收口；挑战/甜品归 P5） |
| 地形 | 21 | 2146 | `e2dTerrain` 系列，**就是数据块的 2146 个地形对象** | 已交付（v2–v5） |
| 实心/交互 | 57 | 1976 | 有碰撞体：`TNT_Box` 322（`TNTBox`，爆）+ `BoxChallenge` 269 / `StarBox` 262（挑战与收集）+ `BreakableWall`/`WoodenPlank2m`/`Fan`（`LevelRigidbody`）+ 8 个 `background_*_set` 背景组 + `GoalArea*` | **P4b/P4c/P5** |

装饰类内部再分（实测）：

| 家族 | prefab | 实例 | 图集 | 说明 |
|---|---|---|---|---|
| `UnmanagedSprite` | 68 | 7769 | `Props_Generic_Sheet_01.png`（512²，16/32 网格） | 草地/蘑菇/水晶/岩石/砾石/贝壳/树枝… |
| `Sprite`（走精灵库） | 183 | 7363 | `IngameAtlas.png`(73) / `IngameAtlas2.png`(127)（2048²） | 南瓜叶/玛雅壁画/奶油线/碎屑/金堆/星光… |
| `Panel`+`SpriteReference` | 4 | 48 | — | 关卡里的"窗户"（UI 件）→ P4b |
| `PointLightSource`+`Sprite` | 6 | 40 | — | 发光蘑菇/水晶（多一个点光源，我们不模拟光）→ P4b |
| 多四边面特殊件 | 7 | 35 | — | `Slingshot`(20，带 `DynamicObject`)、`Bird_*`(9，`SpriteAnimation`)、`Stone5_2`/`Stone2_2`(6) → P4b |
| `WindowTiny` | 1 | 1 | — | 有 `Panel` 但没有渲染器 → 不画 |

**P4a = 前两行**（251 个 prefab、15132 个实例），判据是**一个 prefab 恰好一个四边面、没有碰撞体、
脚本集合 ⊆ {`Sprite`, `UnmanagedSprite`}**。这样「只画不碰」与原版一致——它本来就没有碰撞体。

## 3. 画法真值（两个类，各自一条公式）

两个类都生成一个**朝 +z 的四边面**（`MeshFilter.m_Mesh` 在 prefab 里是 `0`，运行时生成）。

### 3.1 `UnmanagedSprite`（`Assets/Scripts/Assembly-CSharp/UnmanagedSprite.cs`）

- **世界尺寸** = `2 · spriteWidth · 10/768 · localScale.x`（`CreatePlane(…, spriteWidth, spriteHeight)` 的顶点
  `±width·10/768`，即 `spriteWidth/38.4`；`Size` 属性写的 `localScale.x · spriteWidth/768 · 20` 同值）。
  **必须用 prefab 里序列化的 `m_spriteWidth`/`m_spriteHeight`**（`Awake` 直接拿它们建面），
  **不要**用 `m_scale × 图集边长/网格` 重算——本项目里两者不等（见 §7 偏差 1）。
- **uv**：`CalculateUVs(m_UVx, m_UVy, m_width, m_height)`：`grid = m_atlasGridSubdivisions`（实测 16 或 32），
  `u0 = m_UVx/grid + 1/2048 + m_border/1024`，`u1 = u0 + (1/grid)·m_width − 2/2048 − 2·m_border/1024`（v 同理）。
- **图集** = 该件 `MeshRenderer.m_Materials[0]` 的 `_MainTex`。
- 四边面**以件原点为中心**（没有枢轴）。

### 3.2 `Sprite`（“管理”精灵：几何来自运行时精灵库）

- **世界尺寸** = `2 · (int)(m_scaleX · db.width) · 10/768`（`SelectSprite` 的 `num = (int)(m_scaleX*data.width)`
  → `CreateMesh`），即 `(int)(m_scaleX·db.width)/38.4`；`db` 是下面那张表的一行。
- **uv** = `Assets/Resources/GUISystem/spritemapping.txt` 的 `id x y w h`（**归一化**，实测 1629 行全覆盖）。
- **枢轴**（四边面中心相对件原点的偏移，世界单位）：
  `pivotX = (int)(m_scaleX · (db.selectionX + db.selectionWidth/2 − (db.UVx + db.width/2) + db.pivotX + m_pivotX))`，
  `pivotY` 同式（`selectionY`/`selectionHeight`/`UVy`/`height`/`db.pivotY`/`m_pivotY`）；
  `CreateMesh` 把中心放在 `(−2·pivotX·10/768, −2·pivotY·10/768)`。**实测 200 个管理装饰件的 `m_pivotX/Y` 全是 0**，
  但公式照抄，不特判。
- **`db`** = `Assets/Resources/GUISystem/sprites.txt`（`RuntimeSpriteDatabase.LoadFast`）：tab 分隔 14 或 15 列 =
  `id`、`"名字"`、`materialId`、`selectionX`、`selectionY`、`selectionWidth`、`selectionHeight`、`pivotX`、`pivotY`、
  `UVx`、`UVy`、`width`、`height`、`subdivisions`[、`opaqueBorderPixels`]（第 1 列是 `m_id`）。
- **图集** = 该件 `MeshRenderer.m_Materials[0]` 的 `_MainTex`（**不是** `db.materialId`——那个 guid 在本工程里
  解析不到任何资产，且没有任何代码把 `SpriteData.material` 赋给渲染器；实测 232/232 个管理件的 prefab 材质
  与 `db.materialId` 都不同）。

### 3.3 实例变换

`LevelLoader.ReadPrefabInstance`：`position`（世界）、`rotation = Quaternion.Euler(euler)`（**euler 是度**）、
`localScale`；随后 `ReadData` 把 `position.x/y` 与 `localScale.x/y` 各乘 `IN TerrainScale × user TerrainScale`
（声明默认档 1.0 ⇒ 恒等）。四边面 `z` 恒 0 ⇒ **只有 `scale.x/y` 参与**；`scale.x = −1` 就是镜像
（实测大量 `-1,1,1`：`Grass_*`/`Rock_*`/`Branch_01`/`Star_01`…），`scale.z = 0` 无影响。
实测 `scale.y = −1` 也存在（`Grass_03` 一例 `1,-1,1`）。

## 4. 深度（z）与绘制顺序

原版是 3D 渲染 + 深度测试，2D 客户端必须自己排序。**近 = z 小**（相机 `IngameCamera.cs:423`
把 `currentPos.z = −15f`，`CameraSystem.prefab` 的 `GameCamera` 局部 z=0、旋转单位 ⇒ 朝 +z、正交、
近裁剪 0.3/远 1000 ⇒ 可见 z ∈ [−14.7, 985]）。佐证：

| 事实 | 出处/实测 |
|---|---|
| 地面 fill 网格顶点 z = 0 | `LevelLoader.ReadMesh`（`fillMesh` 分支不写 z） |
| 边缘条带 z = −0.01 ⇒ 比地面**更近** | `LevelLoader.cs:299`、`e2dTerrainCurveMesh.cs:57` |
| 游玩平面 = **z = −5** | 20 个关卡预置零件实例实测 z ≈ −5.00（`Part_WoodenFrame_01_SET`、`Part_CartWheel_01_SET`、`Part_Balloon_01_SET`…） |
| 装饰件以 −5 为中心，z ∈ [−7.48, +7.00] | 15256 个装饰实例实测；−5.00 一个值就有 7207 个实例 |
| 背景/云在 z ≥ 0（`background_*_set` z=0、`CloudJungleSet` z=15） | 实测 |
| 被"藏起来"的实例在 z ≤ −100 | `Daisy_02` −214.38、`TimeChallenge` −123.69、背景组 −225.14（都在相机后面 ⇒ 不可见） |

**客户端的绘制顺序 = z 降序（远的先画）**，同 z 用一个固定次序打破平局（原版的深度缓冲对同深度是未定义的：
实测大量装饰件恰好落在 −5.00，与游玩平面同深）。本片采用：

```
背景网格 → [道具 z>0，z 降序] → 地面 fill（z=0） → goalZone（z=0，画在地面之上）
        → [道具 z=0] → 边缘条带（z=-0.01） → [道具 -5<z<0] → 实体层（z=-5）
        → [道具 z=-5] → [道具 z<-5] → 放置预览/选中框
```

- 实体层的 z 记作 **−5**（原版的游玩平面），所以 `z < −5` 的装饰（草、蘑菇、水晶的主体）画在车**之上**，
  `z = −5` 的画在车**之下**——这是本片的一个平局选择（§7 偏差 2）。
- `z ≤ −100` 的实例照旧参与排序 ⇒ 画在所有东西之上（**与原版不同**，§7 偏差 3）——它们在原版里是
  「藏起来的」实例，P4b 之前先如实上报数量。

## 5. 内容契约

### 5.1 关卡内容 **v6**（`schemas/level-content-v6.schema.json`）

在 v5 之上加一个顶层 `props` 数组（**必填**，可以是空数组；v1–v5 出现 `props` 是硬错误）：

```json
"props": [
  { "id": "Grass_02", "x": 40.97, "y": -0.07, "z": -5, "rotation": 0, "scaleX": 1, "scaleY": 1 }
]
```

| 字段 | 含义 |
|---|---|
| `id` | 道具清单里的键（关卡调色板 prefab 的名字，`basename(path, ".prefab")`） |
| `x` `y` `z` | 实例的世界位置（`TerrainScale` 声明默认档 1.0；float32 精确） |
| `rotation` | 关卡文件 `euler.z`（**度**）换算的**弧度**（`Quaternion.Euler` 只有 z 分量非 0） |
| `scaleX` `scaleY` | 实例的 `localScale.x/y`（`−1` = 镜像） |

只有 §2 表里 P4a 那 251 个 id 会出现在这里；其余实例（系统/实心/背景/多面特殊件）由工具**统计上报**，
不静默丢弃。

### 5.2 客户端道具清单 `clients/web/public/assets/original/level-props.json`

视觉面（图集 + 源矩形 + 世界尺寸 + 枢轴偏移）是**客户端资产**，与零件贴图清单同一条路线
（`clients/web/public/assets/original/` 下的图片不入库）；键名与 `part-textures.json` 一致：

```json
{
  "format": "pigforge.level-props",
  "schemaVersion": 1,
  "unitsPerPixel": 0.026041666666666668,
  "atlases": { "Props_Generic_Sheet_01.png": { "width": 512, "height": 512 }, "IngameAtlas.png": { "width": 2048, "height": 2048 }, "IngameAtlas2.png": { "width": 2048, "height": 2048 } },
  "props": { "Grass_02": { "atlas": "Props_Generic_Sheet_01.png", "x": 96, "y": 96, "w": 64, "h": 64, "cx": 0, "cy": 0, "sx": 0.99, "sy": 0.99 } }
}
```

`x/y/w/h` 是图集里的像素源矩形，`cx/cy` 是四边面中心相对件原点的世界偏移（§3.2 的枢轴），`sx/sy` 是世界尺寸。
`Props_Generic_Sheet_01.png`（512²，212 KB）与零件图集一起复制到该目录（不入库）。

## 6. 分片

| 片 | 内容 | 状态 |
|---|---|---|
| **P4a** | 251 个装饰四边面：清单工具 + 关卡 v6 的 `props` + 客户端按 z 排序绘制 | **本片** |
| P4b | 多四边面与特殊件：8 个 `background_*_set` 背景组（各自 ~110 个 GameObject 的视差层）、`Slingshot`/`Bird_*`/`Stone*_2`/`Lit*`/`Window*`、`DessertPlace` 甜品生成 | 待做（清单已经是「一个 prefab = 若干带局部变换的四边面」，扩展的是 prefab 层级遍历） |
| P4c | 实心件：`TNT_Box`/`BreakableWall`/`WoodenPlank2m`/`Fan`/`GoalArea*` 的碰撞体进房间（静态盒）+ 画图 | 待做 |
| P5 | 行为件：`BoxChallenge`/`StarBox`/`Collectable`/`WaypointChallenge`/`TNTBox`/`LevelRigidbody`/`TimeChallenge`/`DontUsePartChallenge` 与星级/计时/收集（`PrefabOverrides` 的挑战块） | 待做（`docs/specs/original-level-pack.md` §8 P5） |

## 7. 有意偏差

1. **`UnmanagedSprite` 用序列化的 `m_spriteWidth/Height`，不重算**。prefab 里那对值与本工程当前图集不匹配
   （`Mushroom_02`：`m_textureWidth 128` / `m_spriteWidth 38` 是按 **1024** 图集算的，而
   `Props_Generic_Sheet_01.png` 是 512²；按 512 重算会得到 19.2）。原版 `Awake` 直接用序列化值建面，
   编辑器那条重算路径（`OnDrawGizmos`→`ResetSize`）只在 `m_spriteWidth == 0` 时写它，所以**序列化值就是线上值**。
2. **同 z 的平局次序是我们的选择**：原版深度缓冲对同深度是未定义的（15132 个装饰实例里有 7207 个落在 −5.00，
   与游玩平面同深）。本片让同 z 的道具画在实体**之下**、画在地面**之上**。
3. **z ≤ −100 的"藏起来"的实例照旧画出**（原版它们在相机后面，不可见）。工具如实报告数量（实测 12 个实例），
   等 P4b 处理背景与相机裁剪时一起收。
4. **不模拟 `Lit*` 的点光源**、`SpriteAnimation`（鸟）、`DynamicObject`（弹弓）——它们不在 P4a 的家族里。
5. 图集不入库（与零件图集同一条 `.gitignore`）；缺图时客户端按现有地形/零件的老规矩回退（不画、不报错）。

## 8. 验收

```powershell
node tools/bple-props/extract-props.mjs          # 写 level-props.json + 复制图集 + 报告；第二次 0 改动
node tools/bple-levels/build-levels.mjs          # 关卡内容 v6；第二次 0 改动
dotnet test PigForge.slnx                        # 解析器 v6（v1–v5 出现 props 即硬错误）
cd clients/web; pnpm test                        # 客户端校验器 + 绘制顺序
```

必须复核的数字（报告里断言；2026-10-09 实跑）：

- `palette prefabs 368` / `instances 26072`；`kinds: decor=251/15132 system=21/6674 solid=57/1976 special=18/124 terrain=21/2146`
  （**26052** = 26072 − 20 个预置零件，`spawns` 里数）；`atlases = {Props_Generic_Sheet_01.png: 68, IngameAtlas.png: 73, IngameAtlas2.png: 127}`。
- 每个装饰件都能解析出图集 + uv + 尺寸（0 个未解析：`tools/bple-props` 一份失败都不报）。
- 关卡侧：277 个文件都写 v6，`props` 合计 **15132** 条（每关 10…478，无空关）；`bple-levels` 的同一份口径断言
  `decor instances: 15132` 与 `classified instances: 26072`。
- 客户端实机（真服务器 `--play --level original/episode_1_levels/Level_05.json` + 真 vite + 真 Chromium）：
  真实关卡文档 + 真实清单走 `drawFrame` 一帧：**16 个装饰实例 → 16 次 `drawImage`**（关掉装饰时 0 次），
  与「无装饰」帧相比 **8467 个像素**不同；应用本体（`建造` 页连上 `/play`）同屏画出贴图地面 + 边缘条带 +
  水晶/蘑菇/贝壳/金堆/草/星等装饰，控制台 0 错误。把 `level-props.json` 挪走后重载只剩地形与条带（回退路径），
  同样 0 错误。
