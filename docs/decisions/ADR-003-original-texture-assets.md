# ADR-003: 原版贴图资产——本地解包、不入库、形状回退

## 状态

Accepted

## 日期

2026-09-09

## 背景

客户端需要从"占位色块"升级为原版美术（`docs/specs/web-client-spec.md` M2「贴图资产清单与 sprite 渲染路径」）。原版《捣蛋猪》贴图是 Rovio 版权资产，不能进仓库；同时本机 `C:/tmp/BAD_PIGGIES/BPLE_Unity6` 是反编译重建的 Unity 工程，贴图是源 PNG，不需要 AssetBundle 反编译。解包过程中确认了三处易踩的坑：

- `Assets/Resources/guisystem/sprites.txt` 的 `selectionX/Y/W/H` 是 **1024 设计栅格**，与当前 2048 图集不对应，不能用来裁剪。
- `Assets/Resources/guisystem/spritemapping.txt` 的归一化 UV 才是权威裁剪矩形：`像素矩形 = round(uv × 纹理尺寸)`，Unity 原点在下（canvas 用 `y_top = H - y - h`）。已用木箱/TNT/气球三个精灵做像素级视觉核对。
- 图集 PNG 由精灵所在 GameObject 的 MeshRenderer 材质决定（材质 → `_MainTex` → PNG），`sprites.txt` 的 materialId 列在原工程里无对应资产、运行时不读。
- `UnmanagedSprite`（网格 UV 部件，如瓶子）的矩形直接写在 prefab 里，按 `m_UVx/UVy/width/height/subdivisions` 计算。
- `INSerializedSprite`（IN 扩展部件与部分皮肤，如 `BlasterTNT`）用**名字**查图集：`Assets/TextAsset/<Atlas>_TextAsset.txt` 首行是 `<图集名> <宽> <高>`，其后每行 `<名字> x y w h scaleX scaleY screenHeight`，矩形已是左上原点像素，世界尺寸 = `w*scaleX × h*scaleY` 像素 × `10/screenHeight`。

## 决策

1. **贴图不入库**：解包产物（图集 PNG + `part-textures.json`）写入 `clients/web/public/assets/original/` 并进 `.gitignore`；仓库只保存解包工具与人工映射表。干净检出没有贴图，客户端按形状渲染（现状行为）。
2. **解包直接读编辑器工程源 PNG**：`tools/bple-textures/extract.mjs`（零依赖 Node）读取 BPLE 工程，按上述规则切片，复制用到的图集并生成清单。仅在只有构建产物时才需要 UnityPy 之类工具，属于备选路径。
3. **运行时契约是可选的资产清单**：`part-textures.json`（`format: pigforge.part-textures`，`schemaVersion: 2`）按 `partTypeId` 给出图集矩形、部件局部偏移/世界尺寸/旋转。清单缺失、格式错误、图集加载失败都只回退到形状渲染，不报错、不阻塞。
   - 2026-09-30：清单升到 `schemaVersion: 3`，新增**可选**动画描述符（精灵级 `spin`/`clips`、部件级 `expression`，见 `docs/specs/part-texture-animation.md`）。v2 清单仍可解析（视为无动画），新字段全部可缺省，运行时契约与回退路径不变；生成物仍是本机文件，不入库。
4. **绘制语义**：同一零件的多个精灵按 prefab 的 z 排序绘制（猪=身体+耳朵+脸+眼，气球/沙袋多联=同图集矩形多份），按原作世界尺寸与局部偏移绘制（清单 v2 的 `sx/sy` 是真实世界尺寸，只乘建造期 `scale`；不再适配物理形状，见 ADR-005），保留局部旋转；名字为 `*Attachment` 的关节标记精灵不参与（运行时按连接方向条件显示）。
5. **映射置信度**：`GameData.m_parts` 的 46 个原版部件覆盖 43 个 PigForge 零件；`ground-slab`、`terrain-box`、`ramp-plank` 在原版没有对应部件，保持形状回退（PigForge 自制的 `ball-weight`/`firework-blue` 已删除）。映射表 `tools/bple-textures/part-map.json` 是唯一需要人工维护的部分。
6. **内容格式不动**：`content/parts.json`（`part-content-v1`）不含任何贴图字段；贴图清单是渲染层的可选叠加，物理/回放契约与 .NET 侧不受影响。

## 影响

- 启用贴图：本机需有 BPLE 工程，执行 `node tools/bple-textures/extract.mjs`（可加 `--bple <路径>` / `--out <目录>`）。
- 原版贴图更新或映射调整后需重跑脚本；清单与图集是生成物，不手工编辑。
- 干净检出、CI、无 BPLE 工程的机器：客户端自动走形状渲染，测试与构建不依赖贴图。

## 2026-10-04 修正：清单坐标的参照系

清单里每个精灵的 `cx`/`cy`（以及 `pivot`、动画帧的 `cx`/`cy`）**以「件原点」为参照**，与 `PGFS` 发的实体位置同系：一个单成员簇的 body pose 是它的形状质心，房间再按成员的 `LocalOffset` 把实体折回**件自己的原点**，所以「离线偏移 = 件原点系里的偏移」才落回原版节点链的位置。

**这曾经不是这样**：emitter 会把每个偏移**重新以「美术合成中心」为锚**（`s.cx - centreX`）。只要一件的美术大致居中于它的原点，两种参照看起来一样（目录里多数件差 ≤0.14），但**猪王的皇冠把它的美术中心抬到件原点上方 0.63**，于是美术被整体画低 0.63（用户报的「碰撞箱和贴图错位」）。修行：offset 一律保留在件原点系（`tools/bple-textures/extract.mjs`），调色板缩略图不受影响——`thumbnailPlacements` 按**合成自身的包围盒**居中，本来就不依赖这个参照系。

数值验收（美术中心 − 碰撞中心，Y）：猪王 **0.659 → 0.029**；木块 0.010、猪 0.007、气球 0.010、蛋 0.014、尾翼 0.014、车 0.005、马达轮 0.033（沙袋 −0.095 与轮子 −0.117 是原版自己的：沙袋的碰撞体是运行期加在 −0.1、轮子的旋转精灵用轴心 `pivot`）。

## 参考

- `tools/bple-textures/extract.mjs`、`tools/bple-textures/part-map.json`
- `clients/web/src/renderer/atlas.ts`、`clients/web/src/renderer/draw.ts`
- BPLE 侧依据：`Assets/Scripts/Assembly-CSharp/Sprite.cs`、`UnmanagedSprite.cs`、`RuntimeSpriteDatabase.cs`、`Assets/Resources/guisystem/{sprites,spritemapping}.txt`、`Assets/MonoBehaviour/GameData.asset`、`PartListData.asset`
- `docs/specs/web-client-spec.md` M2「差异视图 + 贴图映射」
