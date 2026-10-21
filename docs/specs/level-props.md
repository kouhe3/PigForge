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
| **可绘制** | **321** | **17184** | 每个带渲染器的对象都画得出图：`Sprite`/`UnmanagedSprite` 四边面、`Panel` 自己生成的九宫格、或序列化的 `Plane`（§3.8），且带的其他脚本在装载那一刻不改画面 | **P4a（251 个单四边面）+ P4b（8 个背景组、2 个石头、6 个 `Lit*`）+ P4b′（5 个窗户、1 个弹弓、7 只鸟）+ P4c（30 个实心家具）+ P5-a（4 个挑战箱）+ P6-b（6 个终点区）+ P6-c（祖先猪、绳桥）已交付** |
| 桥（`Bridge`） | 1 | 24 | 只有 `Bridge` 脚本、没有渲染器：装载时按 `EndPoint` 的距离铺 `floor(距离/间距)` 个台阶（`Step.prefab`）并在每级与末端各放一个绳段（`StepRope.prefab`） | **P6-c 交付**（台阶/绳段烘进件帧，§3.9） |
| 云（5 个 `Cloud*Set` / 152 个实例） | 5 | 152 | 只有 `CloudGenerator`、没有渲染器：装载时按自己的盒子生成 5…10 朵云，每朵是世界坐标下的一个独立道具（§3.7），152 个实例共 **1020** 朵 | **P6 交付**（关卡里是 1020 个 `Background_Cloud_*` 道具实例） |
| 系统标记 | 21 | 6674 | 只有脚本没有渲染器：`LevelStart`/`LevelManager`/`CameraSystem`/`TimeChallenge`/`DontUsePartChallenge`/`DessertPlace`/`Cloud*Set`/`Bridge`… | 各自单独（相机界已收口；挑战归 P5；**甜品点见 §7 偏差 7**） |
| 地形 | 21 | 2146 | `e2dTerrain` 系列，**就是数据块的 2146 个地形对象** | 已交付（v2–v5） |
| 实心/交互 | 57 | 1976 | 有碰撞体：`TNT_Box` 322 + `BoxChallenge` 269 / `StarBox` 262（挑战与收集）+ `BreakableWall`/`WoodenPlank2m`/`WoodenBox`/`MetalBar*`/`MetalBox`（`LevelRigidbody` 家具）+ `Fan` + `ActivatedHingeDoor*` + `GoalArea*`/`Bridge`/`SecretPlace`/`PartBox`/`PressureButton*` + 三只会飞的鸟 | **P4c 交付了 30 个（836 个实例）的画面与盒体**；挑战件、`GoalArea*`、`Floating*`、`AncestorPig`、`Bridge` 留 P5/P6（§3.6 末逐条记原因） |
> 一行里的数字按「脚本集合」分类，**同一个 prefab 只出现在一行**：`8 个 background_*_set` 因带碰撞体算在实心/交互里，但它们的**画面**已经在 P4b 交付（碰撞体归 P4c）。

装饰类内部再分（实测）：

| 家族 | prefab | 实例 | 图集 | 说明 |
|---|---|---|---|---|
| `UnmanagedSprite` | 68 | 7769 | `Props_Generic_Sheet_01.png`（512²，16/32 网格） | 草地/蘑菇/水晶/岩石/砾石/贝壳/树枝… |
| `Sprite`（走精灵库） | 183 | 7363 | `IngameAtlas.png`(73) / `IngameAtlas2.png`(127)（2048²） | 南瓜叶/玛雅壁画/奶油线/碎屑/金堆/星光… |
| `Panel`+`SpriteReference` | 5 | 49 | `IngameAtlas2.png` | 关卡里的"窗户"：`Panel` 自己生成 `m_width x m_height` 的九宫格（§3.4），`SpriteReference` 只提供精灵 id/缩放、自己不画（`RenderingEnabled()` 为假） |
| `PointLightSource`+`Sprite` | 6 | 40 | `IngameAtlas.png` / `IngameAtlas2.png` | 发光蘑菇/水晶：已画，多出的点光源不建模（§7 偏差 4） |
| `Sprite`+`SpriteAnimation` | 4 | 9 | `IngameAtlas.png` | 栖在关卡里的鸟（`Bird_*_01`）：**画 `Sprite` 自己的那一帧**，动画不播（§7 偏差 5） |
| `Slingshot` | 1 | 20 | `IngameAtlas.png` | 弹弓：3 个精灵 + **2 条橡皮筋**（`Slingshot.cs` 的常量 + `RubberBand.mat` 的纯色，§3.5） |
| `Bird`+`DynamicObject`+… | 3 | 34 | `IngameAtlas.png` | 会飞的鸟（`Bird_Red`/`Bird_Blue`/`Bird_Yellow`）：**装载姿态**照画，飞行/表情行为不建模 |
| `Stone2_2`/`Stone5_2` | 2 | 6 | `IngameAtlas.png` | 两块石头叠一层 |
| `WindowTiny` | 1 | 1 | `IngameAtlas2.png` | 3x2 格的窗户（同 §3.4） |

**可绘制 = 前面的判据**（279 个 prefab、15542 个实例、**1017 个四边面**）：每个带渲染器的 GameObject
都给出图——`Sprite`/`UnmanagedSprite` 四边面，或 `Panel` 的九宫格——且脚本集合 ⊆
`SPRITE_CLASSES` ∪ `ALLOWED_EXTRA` ∪ `PANEL_CLASSES`。`ALLOWED_EXTRA` 里的每个脚本都有一条
"装载那一刻不改画面"的证据（`PointLightSource` 只加点光；`DynamicObject` 是**空类**；
`SpriteAnimation` 只在 `m_AutoPlay` 为真时播，工具断言接受的道具都没有；`AnimationHandler.Update` 是空的；
`Slingshot` 只建两条运行时 `LineRenderer`，精灵留给子对象；`Bird`/`FaceRotation`/`FixedRotation` 是鸟的
飞行行为，装载时不动）。多四边面的 22 个见下表。

| 多四边面家族 | prefab | 实例 | 四边面 | 说明 |
|---|---|---|---|---|
| `background_*_set`（背景组） | 8 | 258 | 53…250 | 关卡的天幕/远景/近景/前景层，各自带视差标签与整层色块（§4.2） |
| 窗户件（`WindowBgLarge`/`WindowBgSmall`/`WindowLarge`/`WindowSmall`/`WindowTiny`） | 5 | 49 | 6…9 | `Panel` 的九宫格（§3.4） |
| `Bird_*_01`（栖鸟） | 4 | 9 | 2 | Body + Face 两帧，动画不播（§7 偏差 5） |
| `Bird_Red`/`Bird_Blue`/`Bird_Yellow`（会飞的鸟） | 3 | 34 | 3 | Body + 头顶的标志 + Face |
| `Slingshot` | 1 | 20 | 5 | 3 个精灵 + 2 条橡皮筋（§3.5） |
| `GoalArea_*`（终点区） | 6 | 218 | 1…3 | 横幅是序列化的 `Plane`（§3.8）、再加 `UnmanagedSprite`/`Sprite` 的图与光晕；`GoalArea_MM*`（`RandomChildActivator`）仍不画 |
| `AncestorPig`（祖先猪） | 1 | 20 | 3 | `AncestorPig` 脚本 `Start` 只缓存子对象并抽一个眨眼计时器（`AncestorPig.cs:44-70`），眨眼/苏醒/睡去都在之后 —— 装载画面就是 prefab 自己的姿态 |
| `Stone2_2`/`Stone5_2` | 2 | 6 | 2 | 两块石头叠一层 |

## 3. 画法真值（四种画法，各有一条原版公式）

`UnmanagedSprite` 与 `Sprite` 各自生成一个**朝 +z 的四边面**（`MeshFilter.m_Mesh` 在 prefab 里是 `0`，
运行时生成）；`Panel` 生成一片九宫格（§3.4），`Slingshot` 另有两条运行时线（§3.5）。多四边面的件把
它们按层级拼在一起（§3.3）。

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

### 3.3 实例变换与多层 prefab

`LevelLoader.ReadPrefabInstance`：`position`（世界）、`rotation = Quaternion.Euler(euler)`（**euler 是度**）、
`localScale`；随后 `ReadData` 把 `position.x/y` 与 `localScale.x/y` 各乘 `IN TerrainScale × user TerrainScale`
（声明默认档 1.0 ⇒ 恒等）。四边面 `z` 恒 0 ⇒ **只有 `scale.x/y` 参与**；`scale.x = −1` 就是镜像
（实测大量 `-1,1,1`：`Grass_*`/`Rock_*`/`Branch_01`/`Star_01`…），`scale.z = 0` 无影响。
实测 `scale.y = −1` 也存在（`Grass_03` 一例 `1,-1,1`）。

**一个 prefab 可以不止一个四边面**（22/279 个可绘制件是）。它们的层级靠 `Transform.m_Father` /
`m_Children` 连起来，每个对象自己的 `m_LocalPosition`/`m_LocalRotation`/`m_LocalScale` 相对父级；
`tools/lib/props.mjs` 从**根**往下把每一级合成（根自己的局部变换被关卡实例覆盖，所以根按单位阵起步），
得到每个四边面**相对件原点**的 `{dx, dy, dz, rotation, scaleX, scaleY}`。合成后的 2D 矩阵必须能分解成
`R(旋转)·diag(sx, sy)`（没有错切），否则工具**报错**而不是猜（非均匀缩放的父级 + 带旋转的子级会引入错切）。
每个对象的 `m_IsActive` 沿着层级传下去：停用的对象（含其子树）不画。

**有些四边面贴图集，有些只填色**：材质 `_Custom/Unlit_Monochrome`（背景层的 `*_Fill` 层，
`Assets/Shader/_Custom_Unlit_Monochrome.shader` 的 `frag` 只 `return _Color`）没有 `_MainTex`，
只有 `m_Colors._Color`（RGBA）——这种四边面在清单里带 `color` 而没有图集矩形，客户端 `fillRect`。

**图集矩形是"自顶向下"的 y**（`x/y/w/h`，单位 px，`y` 从图集**上边**往下量）：Unity 自己的 uv `v` 从
**下边**往上量，工具在写出清单时换算（`atlasHeight − (v + h)·atlasHeight`），与 `tools/bple-textures`
写零件图集的做法一致（`extract.mjs` 的 `atlasSize.height − yBottom − h`），客户端拿到就直接当
`drawImage` 的源矩形用。**这条曾经错**：2026-10-10 之前工具写的是 Unity 方向、客户端按画布方向读，
于是每个四边面都取到了镜像行的那一格（窗户画成猪脸），修好后窗户/弹弓/鸟才归位（§8）。

### 3.4 窗户：`Panel` 的九宫格（`Panel.cs`）

窗户 prefab 上挂着三个脚本：`Sprite`（基类，`RenderingEnabled()` 为真，会往 `MeshFilter` 写一个四边面）、
`SpriteReference`（同基类但 `RenderingEnabled()` 覆写为假，自己不画，只当**取值源**）、`Panel`（自己
生成网格）。Unity 按 GameObject 组件表顺序 Awake（`m_Component` 里的顺序：Transform → MeshFilter →
MeshRenderer → `Sprite` → `SpriteReference` → `Panel`），所以**最后写进 `MeshFilter` 的是 `Panel` 的网格**
——画面是九宫格，不是那个单四边面；`tools/lib/props.mjs` 因此把"有 `Panel` 的对象走九宫格分支"，
并把孤立的 `SpriteReference`（没有 `Panel` 陪着）判为不画。

- **精灵**：`Panel.Rebuild` 用 `GetComponent<SpriteReference>()` 的 `m_id`/`m_scaleX`/`m_scaleY`（**不是**
  旁边那个 `Sprite` 的字段），精灵 id 走 §3.2 的同一张库表。
- **网格**：`m_width x m_height` 个瓦片（实测 3x3、3x2），每片 `(int)(scaleX · row.width/3)` ×
  `(int)(scaleY · row.height/3)` 像素（**整数截断**），世界尺寸 = 像素数 × `20/768`（`CreateMesh` 的
  `2·tileWidth·10/768` 就是同一个因子）；瓦片按 `-0.5·m_width`…`+` 排开、以件原点为中心，`z = 0`。
- **uv**：精灵自己的 uv 矩形**切成三等份**（`GetUvs`）：第 0 列/行取左/下格、最后一列/行取右/上格、
  中间取中格 —— 于是 3x3 的"窗户框"精灵被拉成任意尺寸的九宫格（中间那格是玻璃）。

实测：`WindowBgLarge`（3x3，瓦片 0.8073×1.2760 m，共 2.422×3.828 m）、`WindowBgSmall`、`WindowLarge`、
`WindowSmall`、`WindowTiny`（3x2）共 5 个 prefab / 49 个实例 / 45 个四边面。

### 3.5 弹弓的两条橡皮筋（`Slingshot.cs`）

`Slingshot` 的画 = 三个子对象的 `Sprite`（`Front`/`Back`/`Pad`）+ **两条运行时 `LineRenderer`**（prefab 里
没有）：`Awake` 里 `SetWidth(0.25, 0.25)`、两个锚点写死在代码里（`(0.47, 2.23, −0.012)` 与
`(−0.57, 2.2, −0.38)`），第二端由 `SetDrawPosition(Vector3.zero)` 落在 `m_restPosition`——也就是
`Pad` 子对象的局部位置（prefab 里有：`(−0.0064, 2.2587, −0.013)`）。所以**装载那一刻**每条筋就是一个
`|p1 − p0| × 0.25` 的矩形；材质 `Material/RubberBand.mat` 没有 `_MainTex`，只有
`_Color (0.2509804, …, 1)`，于是它就是一块深灰的纯色（和背景层的 `*_Fill` 同一种画法）。
工具从 `Slingshot.cs` 里**解析**这四个常量与宽度（断言数量与数值），从 `.mat` 里取颜色，不信手抄。
实测每个弹弓 5 个四边面（3 精灵 + 2 筋），20 个实例。

### 3.6 实心件：原版自己的盒碰撞体 → 房间里的静态体（P4c）

关卡里那些"家具"——`TNT_Box` 的箱子、`BreakableWall` 的墙、`WoodenPlank2m`/`MetalBar*` 的板、`WoodenBox`/
`MetalBox`、`ActivatedHingeDoor*` 的门、`Fan`、四个 `PressureButton*`、`Secret_*`/`SecretStatue`、
`PartBox_0`/`DynamicPartBox`/`SecretPart*`——的**画面**按 §3 的同一套画法画（它们只是多了几个"装载那一刻
不改画面"的脚本，见 `SOLID_CLASSES` 的逐条证据）；**物理**则取 prefab 自己带的**非 trigger `BoxCollider`**：
`m_Size` 三个分量 + `m_Center` 偏移，按对象自己的变换链折进件坐标（与四边面同一条链路），写进关卡内容
**v7** 的 `props[i].boxes`，由房间建**静态盒体**（`GameRoom.EnsurePropBodies`）。

- **只有盒体**：`Fan` 的两片叶片是 `CapsuleCollider`（`Colliders` 子对象，半径 0.7）、鸟是 capsule —— 本片
  不建模（§7 偏差 11）。
- **trigger 不是墙**：`BoxChallenge`（1.33² 的 trigger）、`GoalArea*` 的终点区、`Secret_*`、
  `PressureButton*` 的按下区都是 trigger —— 原版里它们只是"有人经过这里"的标记，本片不建体（终点区另有
  `goalZone` ✓）。
- **背景组自己的 `GroundCollider` 跳过**：`background_*_set` 里那个 188 m 宽的盒体在背景自己的 z 平面上
  （关卡平面之后），原版用它兜住飞出去的猪；本片的地板是地形（§4 表），所以按对象名
  （`GroundCollider`/`CloudCollider`）跳过，不当成关卡里的墙。
- **装的是"静态"**：原版给木板/箱子/墙/门挂了 `Rigidbody`（`WoodenPlank2m` 质量 4、`BreakableWall` 质量 4
  但 `m_UseGravity: 0`、门 4），`RigidbodyFreezer.Start` 只是记录原约束、**不冻结**（`RigidbodyFreezer.cs:19-30`）
  —— 也就是原版里它们会掉、会被推、会碎。本片按规格 §6 P4c 的约定把它们建成**静态盒**：位置就是关卡放的
  那一帧，行为差异见 §7 偏差 10。
- **实测**：30 个 prefab / 836 个实例的画面 + **658 个盒体分布在 123 个关卡**（`TNT_Box` 322、`BreakableWall`
  116、`WoodenPlank2m` 67、`Fan` 0（全 capsule）、门 74、`WoodenBox`/`Metal*` 65、其余按钮/秘密/件箱只有
  trigger）。房间侧：`PropSolidRoomTests` 让猪落在盒体上（对照：去掉盒体就掉出世界）✓。

**P4c 之外（逐条理由）**：`GoalArea_*`（7 个 prefab / ~203 实例）的旗子是**序列化的网格 + 材质**（`GoalBox.cs:39`
直接拿 `Renderer.material`），不是运行时生成的四边面 —— 要网格支持；`FloatingStarBox`/`FloatingPartBox` 的
绳与气球是运行时生成的（`RopeVisualization`/`LevelBalloon`）；`AncestorPig` 的眼睛会自动播动画（工具断言
拒绝）；`RandomChildActivator` 的终点件每次随机挑一个子对象显示；`Bridge`/`BridgeStep`、挑战件
（`BoxChallenge`/`StarBox`/`Collectable`/`WaypointChallenge`）归 P5。

### 3.7 云：`CloudGenerator` 的运行时生成 → 每朵云一个道具（P6）

5 个 `Cloud*Set`（`CloudPlateauSet`/`CloudJungleSet`/`CloudNightSet`/`CloudHalloweenSet`/`CloudLPASet`，152 个实例）
prefab 自己**没有渲染器**：`CloudGenerator.Start` 在自己的盒子里生成 `m_maxClouds` 朵云，每朵是 `m_cloudSet`
里随机一个云 prefab（`GameObject/Background_Cloud_*.prefab`，13 个），位置 = `set.position + (x∈±m_cloudLimits,
y∈±m_height, z∈[0, m_farPlane])`；`useCustomParallax` 时整组注册成 `speedX = 1` 的视差层（贴住相机）。

**关键**：`SpawnCloud` 先写**世界坐标**再 `transform.parent = set.transform`，而 Unity 的 `SetParent(…, true)`
保住世界缩放 —— 所以**实例自己的缩放不会传到云上**。实测 `Level_05` 的 `CloudJungleSet` 实例缩放是 **80×60**：
照普通道具那样把生成的点当"件内坐标"再乘实例缩放，云会飞到 1000 米外（第一版就是这么错的）。

因此工具只读**生成器规格**（数量/盒子/视差/云 prefab 名单），由 `build-levels.mjs` 把每个实例展开成
`maxClouds` 个**独立道具实例**（id = 云 prefab 名、世界坐标、rotation 0、scale 1×1）。云 prefab 不在关卡调色板里，
所以它们进客户端清单的 `cloudProps` 段（与 `props` 同形状），客户端解析时两段合一（`levelProps.ts`）。

实测：13 个云 prefab（各 1 个四边面）✓、152 个实例共 **1020** 朵云 ✓；种子的取法与偏差见 §7。

### 3.8 终点区：序列化的 `Plane` 横幅（`GoalArea_*`）

终点区（`GoalArea_01`/`02`/`Night`/`Morning`/`StarLevel`/`Halloween`，218 个实例）的**横幅**不是运行时生成的四边面：
那件对象的 `MeshFilter.m_Mesh` 指向工程自己的 `Assets/Mesh/Plane.asset`，材质是 `Material/GoalFlag.mat`
（`_MainTex` = `Texture2D/Props_Goal_Area_01.png`，128² 方格旗，`_Color` 白、无 tiling）。它**没有** `Sprite`/
`UnmanagedSprite` 组件，所以：

- 工具按 **guid** 认这个网格（不认名字），断言它的顶点数 33 与自己的 `m_LocalAABB`（平面、z 厚为 0），
  然後把**它自己的包围盒**当作四边面：大小 = `2 × extent`、中心 = AABB 的 `m_Center`，纹理整张铺在上面
  （`x/y/w/h` = `0,0,128,128`；网格的 uv 在 `.asset` 里是打包的，读不出来，所以这条"整张铺一次"是**约定**
  ——§7 偏差 14）。换任何别的网格都会**报错**，不会猜。
- 该对象自己的旋转是 `(0.7071, -0.7071, 0, 0)` —— 绕 xy 平面内一根轴的 180° 转身（`z = w = 0`）：法线翻到
  −z、面内是一个**镜像**。`localFrame` 现在能分解这一族（`[[x²−y², 2xy], [2xy, y²−x²]]`），所以横幅照原版
  **竖着**立在终点上（实测 −90° + `scaleY` 取负）。
- 同 prefab 的 `Map`/`Glow` 仍是普通 `UnmanagedSprite` 四边面 ✓；`GoalArea_MM*`（26+13+4+2 个实例）因为
  `RandomChildActivator` 每次随机挑一个子对象显示，**仍不画**。

实测：6 个 prefab / **218** 个实例（其中 5 个多四边面）、新图集 `Props_Goal_Area_01.png`（第 22 张）、
关卡里 218 个终点区道具实例 ✓。

### 3.9 绳桥：`Bridge` 按 `EndPoint` 铺台阶与绳段（P6-c）

`Bridge` prefab 自己也没有渲染器：`Bridge.OnDataLoaded` 把 `StepParent` 清空后，按
`floor(|EndPoint − 根| / (stepLength + stepGap))` 铺 `stepPrefab`（`Step.prefab`，`BridgeStep : LevelRigidbody`，
`Awake` 只缓存铰链、`OnDataLoaded` 是空的）—— 每级的**局部坐标** `x = (k + 0.5) × (stepLength + stepGap)`、
`localRotation` 单位阵 —— 再在每级位置与 `EndPoint` 各放一个 `stepRopePrefab`（`StepRope.prefab`，一个
`Sprite` 四边面）。台阶是**实例的子对象**（这一条与云相反：`SpawnCloud` 写的是世界坐标），所以工具把两者都烘进
**件自己的帧**：台阶偏移 `(k+0.5)·间距`、绳段偏移取台阶（末段取 `EndPoint`）的局部位置，旋转/缩放照抄。
`stepLength`/`stepGap` 从 prefab 读（实测 1 / 0.2），数量由 `EndPoint` 的局部位置算出，没有随机。

实测：24 个实例（全部 rotation 0、scale 1×1）、每个 5…N 个四边面（`Level_24` 的短桥 = 2 台阶 + 3 绳段 = 5）；
新图集不变（走 `IngameAtlas2.png`）。

### 3.10 会炸的木箱：`TNTBox` 的撞击爆炸（关卡内容 **v11**）

关卡里那个印着 TNT 的木箱（`TNT_Box.prefab`，全包 **322** 个实例、99 关；另有 4 个
`DynamicTNTBox` 是带 `LevelRigidbody` 的动态件，本片不做）不是装饰：它的 **静态** `BoxCollider`
（0.95³，无 `Rigidbody`）+ `TNTBox : Goal` 组件就是原版的机制 ——

```
TNTBox.cs:15-19   OnCollisionEnter(Collision c) { if (c.relativeVelocity.magnitude > m_triggerSpeed) Explode(); }
TNTBox.cs:44-100  Explode(): OverlapSphere(m_explosionRadius) →
                    FindParentWithRigidBody + AddExplosionForce(target, 1f / CountChildColliders(target))
                    AddExplosionForce: impulse = factor * m_explosionImpulse / max(|目标 − 箱子|, 1)^1.5，
                       沿 (目标 − 箱子) 方向；目标质量 < 0.1 → 再乘 mass，< 0.4 → 再乘 mass/0.4
                   半径内每个 `TNT` 组件另外调用它自己的 Explode()
                   最后 ShineLight()：关掉渲染器、`SetActive(false)` ⇒ 箱子从场景里消失
```

prefab 自己的三个数（`TNT_Box.prefab:19-23`）：`m_triggerSpeed: 2`、`m_explosionRadius: 8`、
`m_explosionImpulse: 25`。工具把它们按 prefab 读出，关卡内容 **v11** 的道具多一个可选
`explosive: { triggerSpeed, explosionRadius, explosionImpulse }`（只有 `TNTBox` 组件的那 322 个实例有），
房间把该道具自己的静态盒体建成 body 时把它声明给规则层：

| 环节 | 落点 |
|---|---|
| 读数 | `tools/lib/props.mjs` 的 `explosiveOf`（`field()` 直接读组件字段）→ `extract-props` 的清单记录 → `build-levels.mjs` 写进每个实例 |
| 契约 | `schemas/level-content-v11.schema.json` 的 `$defs.propExplosive`；`LevelPropExplosiveDefinition`；客户端 `levelContent.ts` 校验（v11 才允许） |
| 判定 | `GameplayRules.ExplodePropOnImpact`：撞击量用与点火同一个估计（`PairRelativeSpeed`：事件自带的**相对速度模** / 上一 tick 速度 / 最近 2 tick 峰值，法向接近速度只作下界），超过 `triggerSpeed` 即炸 —— 原版木箱读的是 `c.relativeVelocity.magnitude`（`TNTBox.cs:22-24`），擦碰也算；`ContactStarted` 与 `ContactPersisted` 都读（同一对持续接触也算撞击） |
| 效果 | 半径内每个动态 body 一条 `ApplyImpulse`（`impulse / max(d,1)^1.5`，`_massByBody` 有正值时按 0.1/0.4 档折减）；半径内的 TNT 零件被点亮引信（原版是直接 `Explode`，我们走既有连锁语义）；道具索引进 `CollectedProps` |
| 消失 | 房间收到 `CollectedProps` 就 `DestroyPropBodies(propIndex)`（客户端本来就会隐藏 `collected` 的道具画面）；`Retry` 重建全部道具静态体并重新声明 |

与 3.6 同一套「道具自己带盒体」的路径，所以**箱子看得见、挡得住车、撞上去会炸**。
已知偏差：冲量打在目标 body 的**质心**且没有原版的「每碰撞体 1/N」系数（PigForge 的复合体是一个 body，
原版是每个 collider 一个 rigidbody，与 §7 / `ADR-029` 同类）；`DynamicTNTBox`（4 个带刚体的）仍是静态
视觉件 + 静态盒体。

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
| 背景组**实例**在 z = 0，但组内每层各有自己的 z | `background_jungle_01_set` 的 102 个四边面 `dz ∈ [−15.31, +20]`（天幕 +20、近景 −15.3…） |
| 云在 z ≥ 0（`CloudJungleSet` z=15） | 实测 |
| 被"藏起来"的实例在 z ≤ −100 | `Daisy_02` −214.38、`TimeChallenge` −123.69（都在相机后面 ⇒ 不可见） |

**客户端的绘制顺序 = z 降序（远的先画）**，同 z 用一个固定次序打破平局（原版的深度缓冲对同深度是未定义的：
实测大量装饰件恰好落在 −5.00，与游玩平面同深）。**排序按四边面**（一个道具自己的四边面各在自己的深度上：
背景组的天幕在 +20、近景在 −15）取 `实例 z + 四边面 dz`，落在下面这些分段里：

```
背景网格 → [四边面 z>0，z 降序] → 地面 fill（z=0） → [四边面 z=0]
        → 边缘条带（z=-0.01） → goalZone → [四边面 -5<z<0] → 实体层（z=-5）
        → [四边面 z=-5] → [四边面 z<-5] → 世界边界框 → 放置预览/选中框/框选
```

- 实体层的 z 记作 **−5**（原版的游玩平面），所以 `z < −5` 的装饰（草、蘑菇、水晶的主体）画在车**之上**，
  `z = −5` 的画在车**之下**——这是本片的一个平局选择（§7 偏差 2）。
- `z ≤ −100` 的实例照旧参与排序 ⇒ 画在所有东西之上（**与原版不同**，§7 偏差 3）——它们在原版里是
  「藏起来的」实例（装饰件里只剩 12 个），工具依旧如实上报数量。

### 4.2 视差（背景层）——`ParallaxManager`

原版 `CameraSystem` 上挂着 `ParallaxManager`，它在 `Start` 用 **tag** 找层
（`GameObject.FindGameObjectsWithTag`：`ParallaxLayerFurther/Far/Near/Sky/Foreground/FixedFollowCamera`），
之后每帧 `m_offset.x += 相机的 Δx`，再把每层设成 `x = 它自己的 x + m_offset.x × 系数`
（`ParallaxManager.cs:82-99`）：

| tag | 系数 x | 系数 y |
|---|---|---|
| `ParallaxLayerForeground` | **−0.4** | **0.2**（只往下、不超过自己的起始高度） |
| `ParallaxLayerFurther` | 0.7 | 0 |
| `ParallaxLayerFar` | 0.6 | 0 |
| `ParallaxLayerNear` | 0.4 | 0 |
| `ParallaxLayerSky` | 0.8 | 0 |
| `ParallaxLayerFixedFollowCamera` | **1**（贴住屏幕） | 0 |

系数 1 = 完全跟着相机（屏幕上不动）、0 = 钉在世界里（最远不了）；所以天幕 0.8、近景 0.4、
前景 −0.4（比世界跑得还快）。客户端的参照点 = **关卡装载时的相机位置**（`App.vue` 按关卡相机界
取景之后记下它），「相机相对装载时动了多少」就是 `m_offset`。

**实测佐证**（浏览器里直接调 `drawProps`）：相机 x 平移 20 米时，`ParallaxLayerSky` 层的世界位移
读数为 **16 米** = `0.8 × 20`，屏幕位移 `−88 px` = `(20 − 16) × 22 px/m`（§8）。

**层的归属由因子决定，不由 z 决定**（2026-10-11 修，`draw.ts` 的 `propDepthBuckets`）：视差层的 z 是
**天幕自己那趟绘制里的深度**，不在关卡的 −15…+20 区间里 —— 实测 **79 关 / 1574 个四边面**的因子是正的
（`ParallaxLayerFar` 0.6），z 却是 −15.31…−13.25（比游玩平面还近），纯按 z 分段会把整片景物画到关卡
之上（`Level_05` 的 `background_jungle_01_set` 三片就这样盖住了它的建造区与地面）。所以按因子的符号分
两边：**因子 > 0**（Sky 0.8 / Further 0.7 / Far 0.6 / Near 0.4）是**世界之后**的景物，进新的 `backdrop`
桶、按**因子降序**在地形之前画；**因子 < 0**（`ParallaxLayerForeground` −0.4）留在 `near`（世界之前）；
因子 0（无 `ParallaxManager` 标签的普通装饰）仍按 z 分段。`ParallaxLayerFixedFollowCamera`（1）按
「世界之后」处理。

## 5. 内容契约

### 5.1 关卡内容 **v7**（`schemas/level-content-v7.schema.json`）

在 v5 之上加一个顶层 `props` 数组（**必填**，可以是空数组；v1–v5 出现 `props` 是硬错误）：

```json
"props": [
  { "id": "Grass_02", "x": 40.97, "y": -0.07, "z": -5, "rotation": 0, "scaleX": 1, "scaleY": 1 }
]
```

| 字段 | 含义 |
|---|---|
| `id` | 道具清单里的键 = 关卡调色板 prefab 的名字，**空白折成下划线**（两个背景组原名带空格：`background_cave_01_set 1`；内容标识符不许有空白，`tools/lib/props.mjs` 的 `propId` 是唯一的转换点，提取器断言 id 唯一） |
| `x` `y` `z` | 实例的世界位置（`TerrainScale` 声明默认档 1.0；float32 精确） |
| `rotation` | 关卡文件 `euler.z`（**度**）换算的**弧度**（`Quaternion.Euler` 只有 z 分量非 0） |
| `scaleX` `scaleY` | 实例的 `localScale.x/y`（`−1` = 镜像） |

只有 §2 里那 279 个**可绘制** id 会出现在这里（P4a 的 251 + P4b 的 15 + P4b′ 的 13）；其余实例
（系统标记、实心件、挑战件、`DessertPlace`）由工具**统计上报**，不静默丢弃。

### 5.2 客户端道具清单 `clients/web/public/assets/original/level-props.json`

视觉面（图集 + 源矩形 + 世界尺寸 + 枢轴偏移 + 四边面自己的层次帧）是**客户端资产**，与零件贴图清单
同一条路线（`clients/web/public/assets/original/` 下的图片不入库）；键名与 `part-textures.json` 一致：
**v2 起一个道具 = 一个 `quads` 数组**（P4b 的多层件需要），每个四边面带 `dx/dy/dz/rotation/scaleX/scaleY`
（相对件原点，§3.3）、`parallax{x,y[,limitY]}`（§4.2），以及 `atlas`+`x/y/w/h` **或** `color`（§3.3 的纯色层）。
`x/y/w/h` 是图集里的像素源矩形，**`y` 自顶向下**（与 `part-textures.json` 同一约定，见 §3.3 末）：

```json
{
  "format": "pigforge.level-props",
  "schemaVersion": 2,
  "unitsPerPixel": 0.026041666666666668,
  "atlases": { "Props_Generic_Sheet_01.png": { "width": 512, "height": 512 }, "Background_Jungle_Sheet_01.png": { "width": 1024, "height": 1024 } },
  "props": {
    "Grass_02": { "quads": [ { "atlas": "Props_Generic_Sheet_01.png", "x": 64.25, "y": 128.25, "w": 63.5, "h": 63.5, "cx": 0, "cy": 0, "sx": 0.9896, "sy": 0.9896, "dx": 0, "dy": 0, "dz": 0, "rotation": 0, "scaleX": 1, "scaleY": 1, "parallax": { "x": 0, "y": 0 } } ] }
  }
}
```

清单还有一个 **`cloudProps`** 段（§3.7）：13 个云 prefab 的 `{ quads: [...] }`，形状与 `props` 相同，
客户端解析时与 `props` 合并；关卡里那 1020 个云实例就按这些 id 解析。

`x/y/w/h` 是图集里的像素源矩形，`cx/cy` 是四边面中心相对**它自己那一层**原点的世界偏移（§3.2 的枢轴），
`sx/sy` 是世界尺寸；`color` 是 `[r,g,b,a]`（0…1，只对纯色层）。清单引用到的图集（P4c 之后 **21 张**：
零件图集 + 3 张装饰图集 + 10 张 `Background_*_Sheet_01.png` + 6 张 `*_Sky_Texture.png`）全部复制到该目录
（不入库）。

## 6. 分片

| 片 | 内容 | 状态 |
|---|---|---|
| P4a ✅ | 251 个单四边面装饰件：清单工具 + 关卡 v6 的 `props` + 客户端按 z 分段绘制 | **2026-10-09 交付** |
| P4b ✅ | 15 个多四边面/多点光源件：8 个 `background_*_set` 背景组（53…250 个四边面、视差标签、纯色层）+ `Stone2_2`/`Stone5_2` + 6 个 `Lit*` —— prefab 层级遍历（`m_Father`/`m_Children`/`m_LocalPosition`）、四边面各自深度、`ParallaxManager` 的视差 | **2026-10-09 交付** |
| **P4b′ ✅** | 13 个剩余可绘制件：5 个窗户（`Panel` 九宫格，§3.4）+ 1 个 `Slingshot`（3 精灵 + 2 条橡皮筋，§3.5）+ 4 只栖鸟（`SpriteAnimation` 不播那一帧）+ 3 只会飞的鸟（装载姿态）—— 加上**图集源矩形 y 方向的修正**（§3.3 末） | **2026-10-10 交付** |
| **P4c ✅** | 实心件：30 个 prefab / 836 个实例的画面（`TNTBox`/`LevelRigidbody`/`RigidbodyFreezer`/`Fan`/`ActivatedHingeDoor`/`GoalBox`/`SecretPlace`/`SecretStatue`/`PartBox`/`PartSecret`/`PressureButton` 各自的装载期证据）+ 关卡内容 **v7** 的 `boxes`（658 个盒体 / 123 关）+ 房间的**静态盒体**（`EnsurePropBodies`） | **2026-10-10 交付** |
| **P5-a ✅** | 4 个挑战箱的画面（`BoxChallenge` 269 / `StarBox` 262 / `DynamicBoxChallenge` 12 / `DynamicStarBox` 1）：`Collectable`/`WaypointChallenge`/`StarBox` 三个脚本没有 `Awake`/`Start`/`OnEnable`，装载画面就是 prefab 自己的（星箱多一层 `Particles_Sheet_01.png` 的光） | **2026-10-10 交付** |
| P5-b | 挑战的**行为**：收集（猪/载具经过即收集、星箱开箱）、星级/计时（`PrefabOverrides` 的挑战块）、实心件的**动态/破坏**（原版木板/箱/墙/门有 `Rigidbody`，本片建的是静态体） | 待做（`docs/specs/original-level-pack.md` §8 P5） |
| P5 的甜品 | `DessertPlace` 的 5339 个生成点**不画**：原版在 `BaseGameMode.PlaceDesserts` 里用 `Random` 抽点心与地点、只在 `ChiefPigExploded`（通关后）或 IN 的 `EnableDesserts` 打开时才放、还要读存档里的摆放串 —— 静态复刻不出这一帧（§7 偏差 7） | 归 P5 |
| **P6-a ✅** | 云：5 个 `Cloud*Set` / 152 个实例 → **1020** 朵云道具（§3.7）+ 清单的 `cloudProps` 段 + 客户端合并 | **2026-10-10 交付** |
| **P6-b ✅** | 终点区的横幅：序列化的 `Plane` 按自己的包围盒画成四边面（§3.8）+ `localFrame` 支持"面内镜像"那一族旋转 —— 6 个 prefab / **218** 个实例 | **2026-10-10 交付** |
| **P6-c ◐** | 祖先猪（`AncestorPig` 20 实例，§3.8 前一行）与绳桥（`Bridge` 24 实例，§3.9）**已交付**；`Floating*`（运行时绳/气球）、`GoalArea_MM*`（`RandomChildActivator` 随机子对象）、`DessertPlace` 的甜品点、`backgroundobject`/`LitArea`/`WindArea`（无渲染器的标记/区域） | 部分（其余按 §7 偏差 10/12 的理由画不出来） |
| P5-b | 挑战的**行为**（收集、星级/计时）与实心件的**动态/破坏** | 待做 |

## 7. 有意偏差

1. **`UnmanagedSprite` 用序列化的 `m_spriteWidth/Height`，不重算**。prefab 里那对值与本工程当前图集不匹配
   （`Mushroom_02`：`m_textureWidth 128` / `m_spriteWidth 38` 是按 **1024** 图集算的，而
   `Props_Generic_Sheet_01.png` 是 512²；按 512 重算会得到 19.2）。原版 `Awake` 直接用序列化值建面，
   编辑器那条重算路径（`OnDrawGizmos`→`ResetSize`）只在 `m_spriteWidth == 0` 时写它，所以**序列化值就是线上值**。
2. **同 z 的平局次序是我们的选择**：原版深度缓冲对同深度是未定义的（装饰实例里有 7207 个落在 −5.00，
   与游玩平面同深）。本片让同 z 的四边面画在实体**之下**、画在地面**之上**，同 z 内部按关卡文件顺序。
3. **z ≤ −100 的"藏起来"的实例照旧画出**（原版它们在相机后面，不可见）。工具如实报告数量（装饰件里 12 个），
   相机裁剪另说。
4. **不模拟 `Lit*` 的点光源**（光晕）、鸟身上那颗**粒子系统**（`Particles_Bird_Sleep`/`Particle System` 是
   一整套睡眠尘，装载即不发射、且整棵子树不在画面里）、以及 `Bird`/`FaceRotation` 的**飞行与表情**行为。
5. **鸟的帧动画不播**：`SpriteAnimation.OnEnable` 只在 `m_AutoPlay` 为真且 `m_AutoPlayName` 非空时播
   （`AnimationHandler.Update` 是空的）；实测 4 只栖鸟与 3 只飞鸟的 `m_AutoPlay` 都是 `0`，所以
   `Sprite` 自己那一帧就是装载帧。工具**断言**这条：一旦有道具的动画会自动播，道具件直接报错而不是画错帧。
6. **橡皮筋画的是装载姿态**：原版每帧按 `SetDrawPosition` 重新拉（拖鸟时贴住鸟），本片只画
   `SetDrawPosition(Vector3.zero)` 的那一刻（§3.5）。
7. **视差是"参照装载时相机"的无状态近似**：原版逐帧累加并夹紧（前景层的 y 永不高于起始高度），
   客户端按 `min(0, 0.2 × Δy)` 直接算，来回平移相机时与原版会有细微差别（`ParallaxManager.Update`）。
8. **`tools/lib/props.mjs` 的 `propId`** 把 prefab 名里的空白折成下划线（内容/协议标识符不许有空白）；
   两个背景组受影响（`background_*_set 1` → `background_*_set_1`），提取器断言 id 唯一。
9. 图集不入库（与零件图集同一条 `.gitignore`）；缺图时客户端按现有地形/零件的老规矩回退（不画、不报错）。
10. **甜品点不画**（见 §6 的 P5 行）：原版 `PlaceDesserts` 是 `Random.Range` 挑点心与地点、通关后才放、
   摆放结果存在存档串里的，静态关卡内容无法复刻；`DessertPlace` 自身也只是个编辑器 gizmo（`OnDrawGizmos`）。

## 8. 验收

```powershell
node tools/bple-props/extract-props.mjs          # 写 level-props.json + 复制图集 + 报告；第二次 0 改动
node tools/bple-levels/build-levels.mjs          # 关卡内容 v6；第二次 0 改动
dotnet test PigForge.slnx                        # 解析器 v6（v1–v5 出现 props 即硬错误）
cd clients/web; pnpm test                        # 客户端校验器 + 绘制顺序
```

必须复核的数字（报告里断言；2026-10-10 实跑，P4a…P4c + P5-a + P6-a 同一轮）：

- `palette prefabs 368` / `instances 26072`；`kinds: decor=261/15220 system=21/6674 solid=57/1976 special=8/36 terrain=21/2146`
  （按「地形 / 有没有渲染器 / 有没有碰撞体 / 脚本集合」分类，同一 prefab 只算一行；`special` 的 8 个
  ——`Slingshot`(1)/栖鸟(4)/`Stone*_2`(2)/`WindowTiny`(1)，**都会画**）。
- **可绘制 321 个 prefab / 17184 个实例 / 1105 个四边面 / 22 张图集**（新增 `IngameAtlas3.png`、
  `Particles_Sheet_01.png` 与 `Props_Goal_Area_01.png`）；其中单四边面 **273 个 / 15881 个实例**；多四边面恰好是
  那 48 个（工具硬断言这份名单：`Bird_*` 7、`Slingshot`、`Stone*_2`、5 个窗户、9 个背景组、8 个门、`Fan`、
  4 个按钮、`SecretPartWilfred`、`HingeDoorToLeft4m`、4 个挑战箱、5 个终点区、`AncestorPig`、`Bridge`）。
- **云**：13 个云 prefab（各 1 个四边面 ✓ 工具硬断言）、152 个 CloudSet 实例 → 关卡里 **1020** 个
  `Background_Cloud_*` 道具实例；`Level_05` 的 8 朵云落在世界坐标（`y` 8…28、`z` ≈ 15.5…16）✓、
  一帧里 8 次 `drawImage` ✓、**7441 个像素**与空帧不同 ✓。
- **关卡侧 v7**：126 个文档声明 v7 并带 `boxes`，**671 个盒体**，每个尺寸为正、偏移有限（Core 与 Server 各断言一遍）。
- 每个四边面都能解析出图集 + uv + 尺寸（或纯色 + 尺寸）：`tools/bple-props` 0 失败；id 无空白且唯一。
- 关卡侧：277 个文件都写 v6/v7，`props` 合计 **18204** 条（17184 个可绘制件 + 1020 朵云）；
  `bple-levels` 的同一份口径断言 `placed prop instances: 18204`、`cloud instances: 1020` 与
  `classified instances: 26072`。
- 客户端实机（真 vite + 真 Chromium，`/level` 取自真服务器）：
  `scenario_56`（69 个道具：1 弹弓 + 4 只飞鸟 + 64 个装饰）一帧里 **131 次 `drawImage`**，
  与"每个实例 × 它的图集四边面"逐项相等（131/131）；`Episode_6_Tower-Sandbox`（485 个道具、7 个窗户）
  里同一位置画出的是一扇**拱形窗户**（鸟脸那种镜像行已修正，§3.3 末）。
- 单独渲染确认：弹弓 = 木叉 + 深灰橡皮筋，栖鸟/飞鸟 = 各色鸟（飞鸟头顶带 `!`），
  `WindowBgLarge`/`WindowTiny` = 深/浅灰九宫格窗框。
- **视差**：浏览器里直接调 `drawProps`，相机 x 平移 20 米 → `ParallaxLayerSky` 层的世界位移 **16 米** = `0.8 × 20`，
  屏幕位移 `−88 px` = `(20 − 16) × 22 px/m`；装饰件（`parallax = 0`）不移动 ✓（2026-10-09 实测）。
