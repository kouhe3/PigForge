# 关卡建造限制（内容 v9）：建造网格 + 每族配额

> 本片把原版关卡的**建造限制**搬进 PigForge：`LevelManager.m_constructionGridRows`（位掩码行构成的
> 建造网格）与 `LevelManager.m_partTypeCounts`（每个 PartType 的允许件数），二者都在关卡文件的
> `PrefabOverrides` 文本里。决策见 `ADR-037`；关卡格式与分解见 `docs/specs/original-level-pack.md`。

## 1. 一句话

275 个原版关卡现在带着**原版自己的**建造区域与零件配额：网格外的单元放不下，每族的件数用满就再也
放不下，建造栏只列该关发放的族并显示剩余数；手写的 `slope-v1`/`terrain-v1` 与所有 v1–v8 文档
（没有 `build`）保持不受限。

## 2. 原版真值

### 2.1 建造网格 = 位掩码行

```csharp
// LevelManager.cs:1280-1297，vanilla 分支（InfiniteGrid=false、GridSize=1，
// INDeclarationSettingsExp.json 里就是 false/1）
if (x < m_gridXmin || x > m_gridXmax || y < 0 || y >= m_gridHeight) return false;
int index = m_gridHeight - y - 1;                      // 行自下而上索引
int num = x - m_gridXmin;
return (CurrentConstructionGridRows[index] & (1 << num)) != 0;
```

矩形（`BaseGameMode.InitGameMode`，`BaseGameMode.cs:49-71`）：

```
columns = 所有行里 (GetNumberOfHighestBit(row) + 1) 的最大值
height  = 最后一个非零行的下标 + 1
xMin    = -(columns - 1) / 2      // C# 整除
xMax    =  columns / 2
```

锚点 = 关卡自己的 `LevelStart` 实例位置（`LevelManager.CreateGrid(..., levelStart.transform.position)`，
`LevelManager.cs:1261-1278`）；单元 → 世界 = 锚点 + (x, y)（`Contraption.SetPartPos`，
`Contraption.cs:1579-1590`）。所以**判定的是锚点单元**（件体可以探出网格边，原版也不禁止）。

### 2.2 每族配额 = 按下标对 prefab 默认表打补丁

表格来源 = `Assets/GameObject/LevelManager.prefab` 的 `m_partTypeCounts`：**35 项**，type 序列
`10,1,2,3,4,5,6,7,8,9,11,…,32,33,34,35`。关卡文本按 `ObjectDeserializer.ReadArray`
（`ObjectDeserializer.cs:720-800`）的规则**就地改写**：

- 先按 `ArraySize` **裁剪**列表（不会增长）；
- 每条 `Element <下标>`：整数列表替换/追加该槽，`List<PartCount>`（**类**）改写**槽里已有的对象**；
- 于是 **`Enum type` 行缺** = 保留 prefab 槽的 type，**`Integer count` 行缺** = 保留槽的 count
  （实测 194 关里都有这种条目，例如 `Level_Sandbox_06` 的 17 条只有 count 行）；
- **整条 Element 缺** = 连槽都不动（`Level_05` 缺 32…34 而 `ArraySize` 是 41；`ArraySize` 也可能比
  最高下标大，`34/35/38/…/47` 都出现过）；
- 下标越过列表尾时会 `Insert` 追加（只在按序追加时成立，越界即崩，所以不是 277 关都会出现的情况）。

消费方式（`BaseGameMode.GetPartCount`，`BaseGameMode.cs:558-578`）：取**第一个** `type` 匹配项的
`count`；**配额 0 的 PartType 在建造栏根本不出现**（`ConstructionUI.CheckUnlockedParts`，
`ConstructionUI.cs:534-612`），放置门控是 `partDesc.useCount < partDesc.maxCount`
（`ConstructionUI.cs:827/1205/1212/1292`），`useCount` 是当前**串**里该 PartType 的件数
（`Contraption.GetPartCount`，`Contraption.cs:515`，按 PartType 计数，所有皮肤共用）。

### 2.3 第二星字段（不搬）

`m_secondStarConstructionGridRows`（20 关）、`m_extraPartsForSecondStar`（21 关）、
`m_gridForSecondStar`（22 关）在反编译源码里**没有任何读取者**（grep 全 `Assets/Scripts` 只命中声明），
星级系统本身也未做 ⇒ 不搬，只在工具报告里计数（偏差 D3）。

### 2.4 PartType 枚举 → PigForge `partTypeId`

只能经 prefab 的 `m_partType` 字段（`tools/bple-textures/part-map.json` 的 `parts`/`variants` + prefab YAML；
`tools/lib/part-types.mjs` 的 `resolvePartTypes` 是唯一实现，`BasePart.cs:23-77` 的枚举名由
`readPartTypeNames` 抄出来）。已对上 **43** 个枚举；**enum 42 `Pumpkin` / enum 45 `GoldenPig` 没有基座**
（`G91`）——它们真的会被关卡发放（16 关 / 14 关），所以工具把它们放进显式 `unmapped` 允许表并在报告里
计数（偏差 D4）。皮肤与其基座共享同一个枚举，因此内容的 `partCounts` 用**基座 id** 作键。

## 3. 内容契约 v9

在 v8 之上加一个**可选**的顶层对象：

```json
"build": {
  "origin": [2.4379761, 5.45759],
  "rows": [15, 15, 0],
  "partCounts": [ { "partTypeId": 1, "count": 3 }, { "partTypeId": 4, "count": 1 } ]
}
```

| 字段 | 含义 |
|---|---|
| `origin` | 该关 `LevelStart` 实例的 (x, y)，float32 精确（`Math.fround`） |
| `rows` | 关卡自己的 `m_constructionGridRows` **原样**；下标 0 = 原版 index 0，§2.1 的公式是唯一解释 |
| `partCounts` | **只写 count > 0** 的条目；键是**基座 partTypeId**；按 id 升序 |

规则：

- **v9 可选、v1–v8 出现即硬错误**（`LevelContentParser` 与 `clients/web/src/schema/levelContent.ts` 同一条
  规则，与 v5 的 `cameraLimits` 相反——那个是 v5 必带）。
- v9 缺 `build` = 不限制（275 关带、2 个沙盒关不带，偏差 D6）。
- `rows` 至少一个元素；`origin` 两个有限数；`partCounts` 元素的 `partTypeId`/`count` 都必须是正整数，
  未知键/重复键一律拒。
- 解析后的文档结构：`LevelContentDocument.Build`（nullable `LevelBuildLimits`），元素类型
  `LevelPartAllowance(uint PartTypeId, uint Count)`。

## 4. 规则层

`GameRoom.SetupFromLevel`（`src/PigForge.Server/GameRoom.cs`）把 `level.Build` 装进
`ConstructionRules.UseLevelBuildLimits(LevelBuildLimits?)`（同一份文档不重装）；`null` = 不受限。

| 判据 | 落点 | 语义 | 错误码 |
|---|---|---|---|
| 网格 | `Place` 与 `Retransform`（Move/Rotate/Scale） | 锚点单元 `(floor(x), floor(y))` 必须 `AllowsCell`；`Retransform` 看**变换后**的锚点 | `OutsideConstructionGrid` = **18** |
| 配额 | 只 `Place` | `CountOfFamily(owner, family) >= allowance(family)` 即拒；族 = `VariantOf ?? PartTypeId`；表里没有或 0 一律不允许 | `PartAllowanceExceeded` = **17** |

- 网格公式逐字照抄 §2.1（`AllowsCell`），`rows` 为空或没有 `build` 时不受限。
- 移动/旋转/缩放不改变件数，因此不查配额（与原版 `useCount` 只在放置/移除时变一致）。
- **一个关卡房间 = 一个共享串**：`GameRoom.ExecuteCommand` 把所有玩家的放置都记在 owner `0`
  （沙盒路径用 `place.PlayerId`），所以配额是**房间级**计数，与原版单机一个串的口径一致（偏差 D2）。
- 关卡自己预置的 20 个零件实例（`spawns`）**不计入**配额（偏差 D7）。

## 5. 客户端

- 校验器：`clients/web/src/schema/levelContent.ts`（v9 的 `build`，字段与 §3 逐条对应）；
  网格的算法与规则层同一份：`constructionGridShape` / `constructionCellExists`。
- 建造栏（`App.vue` 的 `.palette` + `builder/palette.ts`）：有 `build` 时只列 `partCounts` 里
  `count > 0` 的**基座族**（皮肤挂在基座行下面），每个按钮显示剩余数（配额 − 房间里的该族件数，按
  实体算，§4 的共享串口径），用尽即 `disabled`；没有 `build` 时列全部 `PALETTE`、不显示计数。
- 绘制：`renderer/draw.ts` 的 `drawConstructionGrid` 把存在的单元画成半透明格（fill 12% + 边框），
  锚点 = `floor(origin)`，在地面之后、零件之前画。
- 本地预判（提示性，服务端仍是权威）：越格点击 → 提示「该位置不在建造网格内，无法放置。」、
  **不发命令**；该族已用尽 → 提示「该零件数量已达关卡上限。」、**不发命令**。

### 5.1 顺带修正：天幕视差层的归属由因子决定

建造网格要看得见才有意义，而实测发现 79 关里 **1574 个视差层四边面**被 `propDepthBuckets` 放到了
游玩平面之前（只看 z）：`Level_05` 的 `background_jungle_01_set` 三片（z = −15.31/−14.28/−13.25，
因子 0.6 = `ParallaxLayerFar`）把整块建造区盖成龙叶（像素实测：8 格单元里外同色，去掉 `props` 后
单元内 `41,57,57` vs 外面 `34,41,34`）。原版的规则是因子（`ParallaxManager.cs:82-99`）：

| tag | 因子 x | 因子 y | 归属 |
|---|---|---|---|
| `ParallaxLayerSky` | 0.8 | 0 | 世界之后 |
| `ParallaxLayerFurther` | 0.7 | 0 | 世界之后 |
| `ParallaxLayerFar` | 0.6 | 0 | 世界之后 |
| `ParallaxLayerNear` | 0.4 | 0 | 世界之后 |
| `ParallaxLayerFixedFollowCamera` | 1 | 0 | 屏幕固定（按「世界之后」处理） |
| `ParallaxLayerForeground` | **−0.4** | 0.2 | **世界之前** |

所以 `propDepthBuckets` 多一个 `backdrop` 桶：**因子 > 0** 的按**因子降序**（越远越先画）在地形之前画；
**因子 < 0** 的留在 `near`；其余仍按 z 分段。

## 6. 普查（2026-10-11，pristine `BPLE 2022.1.9`）

| 项 | 值 |
|---|---|
| 带 `build` 的关卡 | **275 / 277**（缺的 2 个：`Level_Sandbox_02`、`Level_Sandbox_06`，`m_sandbox = True`） |
| 网格形状 | **42 种**（columns 0…11、height 0…7；`4x2` 24 关、`4x3` 44 关、`5x3` 44 关最多） |
| 发放的族 | 每关 **2…42** 个（`1926` 条族记录 / `3394` 件配额总和） |
| 第二星三件套 | `m_secondStarConstructionGridRows` 20 关、`m_extraPartsForSecondStar` 21 关、`m_gridForSecondStar` 22 关（无读取者，不搬） |
| 没有 PigForge 基座的发放 | `Pumpkin`(42) 16 关、`GoldenPig`(45) 14 关（`G91`，报告里计数） |
| 受天幕错层影响的关卡 | **79** 关 / **1574** 个四边面（§5.1） |

## 7. 偏差（有意，不做就是不做）

| # | 偏差 | 理由 |
|---|---|---|
| D1 | 网格锚点取 `floor(origin)` | PigForge 的单元是整数世界坐标（`ConstructionRules.CellSize = 1f`），原版格点是小数；差 < 1 单元 |
| D2 | 配额是**房间级**计数 | 关卡房间所有放置都记在 owner 0（一个共享串），原版也是单机一个串 |
| D3 | 第二星字段不搬 | 反编译源码无读取者，星级系统未做 |
| D4 | enum 42/45 的发放不出现 | `Pumpkin`/`GoldenPig` 无基座（`G91`），报告计数、关卡里不列出 |
| D5 | `partCounts` 只写 count>0 | 与「配额 0 = 建造栏不出现」等价 |
| D6 | 2 个沙盒关卡不写 `build` | 它们的网格行全是 0（按公式 = 没有任何可放单元），原版沙盒的零件来源是 `GameProgress` 解锁表（未建模）；搬进来只会让这两关一件都放不下 |
| D7 | 关卡预置的 20 个零件实例不计入配额 | 原版 `Contraption.GetPartCount` 会算上布局里的件，但那 20 个实例是否进串未考证 |
| D8 | 网格只判**锚点单元** | 逐字照抄 `CanPlacePartAtGridCell(x, y)`；件体可以探出网格边（原版如此） |

## 8. 验收

```powershell
# 内容（幂等；第二次 0 changed）
node tools/bple-levels/build-levels.mjs
#   -> 275/277 levels, 42 grid shapes, 1926 families / 3394 parts
#      build no-base PartTypes: Pumpkin(42) x16, GoldenPig(45) x14

# .NET（Jolt 项目分开跑：合并跑会撞既有的原生崩溃）
dotnet build PigForge.slnx -c Release          # 0 警告 0 错误
dotnet test tests/PigForge.Core.Tests/PigForge.Core.Tests.csproj     # 395
dotnet test tests/PigForge.Server.Tests/PigForge.Server.Tests.csproj # 161
dotnet test tests/PigForge.Physics.Tests/PigForge.Physics.Tests.csproj --filter "FullyQualifiedName!~Jolt"  # 75
dotnet test tests/PigForge.Physics.Tests/PigForge.Physics.Tests.csproj --filter "FullyQualifiedName~Jolt"   # 21

# 客户端
cd clients/web && pnpm test && pnpm build       # 397 / vue-tsc 干净

# 真机（构建 -> 验收 -> 重启）
dotnet run --project src/PigForge.Server -c Release -- --play --level original/episode_1_levels/Level_05.json
curl http://127.0.0.1:5088/level    # v9 带 build：origin [2.4379761,5.45759]、rows [15,15,0]、4 条配额
```

真机读数（2026-10-11，真 Bepu，客户端自己的编解码器直连 WS）：`(2,5) → 0/0`、`(5,5) → 5/18`、
三块木块 `0/0` 后第四块 `5/17`、第二只猪 `5/17`、未发放的 tnt `5/17`；浏览器里建造栏只列
`猪:1 木块:3 大轮:2 风箱:1`，三次木块后 `木块:0(off)`，再点不发命令（本地提示）。
