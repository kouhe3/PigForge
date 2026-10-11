# ADR-037：关卡内容 v9 —— 建造网格（`m_constructionGridRows`）与每族配额（`m_partTypeCounts`）

- 状态：**已接受**（2026-10-11）
- 相关：`ADR-033`（关卡 v2 的 `terrain` + `GET /level` 侧通道）、`ADR-036`（v5 的 `cameraLimits`）、
  `ADR-030`（PGFC v3）、`docs/specs/level-build-limits.md`（本片的规格）、
  `docs/specs/original-level-pack.md`（格式与分解）、`docs/specs/level-props.md`（道具与天幕视差）
- 差距：`G112`（原版关卡的建造限制从未搬进来）、`G91`（`Pumpkin`/`GoldenPig` 两族没有基座）

## 背景

`PrefabOverrides`（关卡文件里每实例一段的 `ObjectDeserializer` 文本，277/277 关都带）一直只被读了
`LevelManager.m_cameraLimits` 一个字段（`ADR-036`）。原版真正的**建造限制**有两个来源，都在同一个
`LevelManager` 块里：

**① 建造网格**是**位掩码行**，不是矩形：

```csharp
// LevelManager.cs:1280-1297（vanilla 分支：InfiniteGrid=false / GridSize=1，两个开关在
// INDeclarationSettingsExp.json 里就是 false/1）
public bool CanPlacePartAtGridCell(int x, int y) {
    if (x < m_gridXmin || x > m_gridXmax || y < 0 || y >= m_gridHeight) return false;
    int index = m_gridHeight - y - 1;          // 行是**自下而上**索引的
    int num = x - m_gridXmin;
    return (CurrentConstructionGridRows[index] & (1 << num)) != 0;
}
```

矩形由 `BaseGameMode.InitGameMode`（`BaseGameMode.cs:49-71`）算出来：`columns` = 所有行里
`GetNumberOfHighestBit(row) + 1` 的最大值，`height` = 最后一个非零行的下标 + 1，`xMin = -(columns-1)/2`、
`xMax = columns/2`（C# 整除），锚点是**关卡自己的 `LevelStart` 实例位置**（`LevelManager.CreateGrid`，
`LevelManager.cs:1261-1278`）；单元 → 世界的映射就是「锚点 + (x, y)」（`Contraption.SetPartPos`，
`Contraption.cs:1579-1590` 把 `transform.localPosition` 设成 `(x, y, …)`，父级是建在锚点上的
ConstructionUI）。实测 277/277 关都有这个数组，**但没有一关的行带洞**（每行都是 `2^n - 1`），76 种
形状（columns 0…11、height 0…7），锚点**几乎全是小数**（277 关里只有 1 关是整数）。

**② 每族配额**是**按下标对 prefab 默认表打补丁**的列表，不是「关卡写了一串数字」：

```csharp
// LevelManager.cs:71-77
public List<int> m_constructionGridRows = new List<int>();          // 上面那个
public List<PartCount> m_partTypeCounts = new List<PartCount>();    // 每族的允许件数
```

`ObjectDeserializer.ReadArray`（`ObjectDeserializer.cs:720-800`）先按 `ArraySize` **裁剪**已有列表，
再逐条 `Element <下标>` **就地改写**：所以

- `Enum type` / `Integer count` 行**缺**= 保留 prefab 槽里的值（`LevelManager.prefab` 的默认表是
  **35 项**，type 序列 `10,1,2,…,9,11,…,32,33,34,35`）；
- 整条 `Element` 缺 = 连槽都不动（`Level_05` 少了 32…34，`ArraySize` 仍是 41）;
- 下标越过列表尾 = 追加（`Activator.CreateInstance` + `Insert`，只在按序追加时成立）。

原版消费它的方式是 `BaseGameMode.GetPartCount`（`BaseGameMode.cs:558-578`）：取**第一个** `type` 匹配
项的 `count`；**配额 0 的 PartType 在建造栏里根本不出现**（`ConstructionUI.CheckUnlockedParts`，
`ConstructionUI.cs:534-612` 对 `GetPartTypeCount == 0` 的件 `Destroy` 按钮），放置的门控是
`partDesc.useCount < partDesc.maxCount`（`ConstructionUI.cs:827/1205/1212/1292`），`useCount` 是当前
**串**里该 PartType 的件数（`Contraption.GetPartCount`，`Contraption.cs:515`，按 PartType 而不是按皮肤）。

同一块里还有第二星的三件套（`m_secondStarConstructionGridRows` 20 关、`m_extraPartsForSecondStar` 21 关、
`m_gridForSecondStar` 22 关）——反编译源码里**没有任何读取者**（grep 全 `Assets/Scripts` 只命中声明），
而星级系统本身还没做。

**原版 PartType 枚举 ↔ PigForge `partTypeId`** 只能经 prefab 的 `m_partType` 字段搭桥
（`tools/bple-textures/part-map.json` 的 `parts`/`variants` + prefab YAML）：43 个枚举能对上 PigForge 的
基座，**enum 42 `Pumpkin` 与 enum 45 `GoldenPig` 没有基座**（`G91` 未交付）——而它们真的会被关卡发放
（Pumpkin 16 关、GoldenPig 14 关）。

PigForge 当时的状况：**一点限制都没有**。建造面是无界的背景格（`renderer/draw.ts` 的 `drawGrid`），
任何官方关卡里都能摆全部 284 件，`ConstructionRules` 只有「每 owner 最多 256 件」这种与关卡无关的上限。
`docs/specs/original-level-pack.md` §8 的 P2「其余 override 字段（建造网格行、挑战、道具行为）」就是指这里。

## 决策

### 1. 内容升 v9：`build` = 锚点 + 网格行 + 每族配额

```json
"build": {
  "origin": [2.4379761, 5.45759],
  "rows": [15, 15, 0],
  "partCounts": [ { "partTypeId": 1, "count": 3 }, { "partTypeId": 4, "count": 1 } ]
}
```

- `origin` = 该关 `LevelStart` 实例的 (x, y)，**float32 精确**（`Math.fround`，与 `bounds`/`goalZone` 同规矩）。
- `rows` = 关卡自己的 `m_constructionGridRows` **原样**（下标 0 = 原版 index 0），解释权只有上面那条公式。
- `partCounts` = **只写 count > 0** 的条目（配额 0 = 不出现在建造栏，与「不写」等价），键是
  **基座 partTypeId**（皮肤 `variantOf` 计入基座的族），按 id 升序。
- **v9 的 `build` 是可选**：v1–v8 出现即硬错误（解析器与客户端校验器同一条规则），v9 缺它是合法的
  ＝不限制。275 关写出，缺的两关是 `Level_Sandbox_02`/`Level_Sandbox_06`（见偏差 D6）。

### 2. 锚点取 `floor`，网格是整数世界单元

PigForge 的单元是整数世界坐标（`ConstructionRules.CellSize = 1f`，`cellX = floor(x)`），原版格点是小数，
所以锚点取 `floor(origin)`：`Level_05` 的 (2.4379761, 5.45759) → 单元 (2, 5)…(4, 6)（4×2 = 8 格）。
偏差 < 1 单元（D1）。

### 3. 规则层：`ConstructionRules.UseLevelBuildLimits(LevelBuildLimits?)`

`GameRoom.SetupFromLevel` 装一次（同一份文档反复调用不会重算）；`null` = 今天的行为（`slope-v1`/`terrain-v1`
与所有 v1–v8 文档）。两条判据逐字照抄原版：

- **网格**（`Place` 与 `Retransform`）：锚点单元必须 `AllowsCell` —— 和原版一样只看**锚点单元**
  （件体可以探出网格边），`Retransform` 看**变换后**的锚点。
- **配额**（只 `Place`）：`CountOfFamily(owner, family) >= allowance(family)` 即拒，族 = `VariantOf ?? PartTypeId`，
  表里没有或 0 一律不允许。移动/旋转/缩放不改变件数，因此不查配额（与原版 `useCount` 只在放置/移除时变一致）。

新错误码：`ConstructionError.PartAllowanceExceeded = 17`、`OutsideConstructionGrid = 18`。

### 4. 一个关卡房间 = 一个共享串，配额是房间级的

`GameRoom.ExecuteCommand` 把**每个**玩家的放置都记在 owner `0` 上（沙盒路径才用 `place.PlayerId`，
`GameRoom.cs:470` vs `:965`）——关卡房间本来就是「一个串一起搭」（原版是单机），所以配额是**房间级**的
计数，与 `Contraption.GetPartCount` 的口径一致。客户端的「剩余数」必须按**房间里的实体**算，不能只算
自己 acked 的件，否则会出现「面板显示还剩 3 件、服务端说已达上限」的错位（实测过，见 §验收）。
建造栏只对**有 `build` 的关卡**过滤/计数，沙盒房间不受影响。

### 5. 顺带修正：天幕视差层的归属由**因子**决定，不是 z

建造网格要看得见才有了意义，而实测发现一个既有的绘制缺陷：79 关里 **1574 个视差层四边面**被放到了
游玩平面之前（`renderer/draw.ts` 的 `propDepthBuckets` 只看 `z`），其中 `Level_05` 的
`background_jungle_01_set` 三片（z = −15.31/−14.28/−13.25，因子 0.6 = `ParallaxLayerFar`）把整块
建造区盖成龙叶。原版的规则在 `ParallaxManager.cs:82-99` 的因子表里：正因子（Sky 0.8 / Further 0.7 /
Far 0.6 / Near 0.4）是**世界之后**的景物，负因子（Foreground −0.4）才是**世界之前**。所以
`propDepthBuckets` 改成：因子 > 0 的进新的 `backdrop` 桶（按因子降序），在地形之前画；因子 < 0 的留在
`near`；其余仍按 z 分段。这也是本片能在浏览器里看到地面与网格的前提。

## 后果

- 275 关现在带着**原版自己的**建造区域与零件配额；`--play --level` 下越格放置被
  `OutsideConstructionGrid` 拒、超配额被 `PartAllowanceExceeded` 拒，客户端建造栏只列该关发放的族并
  显示剩余数，用尽即禁用（点击本地拒绝、不发注定被拒的命令）。
- 已知偏差（规格 §7 逐条）：**D1** 锚点取整；**D2** 配额是房间级计数（原版是单机一个串）；
  **D3** 第二星字段不搬（反编译无读取者、星级未做）；**D4** enum 42/45 没有基座 ⇒ 那 30 条发放
  在报告里计数、在关卡里不出现（`G91` 收口前）；**D5** 只写 count>0 的条目；
  **D6** 两个沙盒关卡不写 `build`（它们的网格行全是 0，原版沙盒的零件来源是 `GameProgress` 解锁表，
  我们没建模；搬进来只会让这两关一件都放不下）；**D7** 关卡自己预置的 20 个零件实例不计入配额
  （原版 `Contraption.GetPartCount` 会算上布局里的件，但那 20 个实例是否进串未考证）。
- 客户端本地预判（网格与配额各一条）是**提示性**的：服务端仍是权威，本地只是不发注定被拒的命令。

## 验证（2026-10-11，全部实跑）

- 工具：`node tools/bple-levels/build-levels.mjs` 第二次 `0 changed`；普查 `275/277` 关带 build、
  1926 族 / 3394 件、42 种网格形状。
- 独立复核（另一个探针，走「逐行文本扫描 + part-map + 关卡 reader」，不复用工具的 `overrides.mjs`）：
  277 关的 `rows`/`origin`/`partCounts` 与反算值**全部一致，0 分歧**。
- 测试：Core **395**、Server **161**、Protocol 43、Replay 11、Physics 96（75 非 Jolt + 21 Jolt，分开跑；
  合并跑仍会撞既有的 Jolt 原生崩溃）、web **397**；Release 构建 0 警告 0 错误。
- 真机（真服务器 + 真内容 + 真 Bepu，客户端自己的编解码器直连 WS）：Level_05 上
  `(2,5) → 0/0`、`(5,5) → 5/18`、三块木块 `0/0` 后第四块 `5/17`、第二只猪 `5/17`、未发放的 tnt `5/17`。
- 浏览器（vite dev + 真实 Chromium）：建造栏只列 `猪:1 木块:3 大轮:2 风箱:1`；连点三次木块后
  变 `0` 且按钮**禁用**；再点越格 → 本地提示、不发命令；重复连接后剩余数按**房间**计（木块 0）。
