# ADR-020: 建造占格取原版 prefab 声明的格盒

## 状态

Accepted（修订 ADR-018 决策 6 的服务端半边；ADR-017 的 `_all` 语义不变）

## 日期

2026-10-03

## 背景

用户报「旋翼不用连任何动力源就会飞」。上一轮补了两道门控（推进件必须有底盘邻居、功率因子要乘）后复测，暴露更深的总根因：**旋翼放不到木框旁边**——服务端直接回 `CellsOccupied`（error 6）。

原因是 PigForge 一直用「零件 body 碰撞体的并集」当占格（`PartFootprint._body`，ADR-005/ADR-017，ADR-018 决策 6 只给它补了 `frame` 支架的例外），而旋翼的碰撞体是 **2.55 格宽**的桨叶，数学上必然压到邻格。这不是旋翼一家的毛病：滑翔翼（1.9 格）、风扇、螺旋桨、聚光灯、抓钩、弹簧，凡是外伸件都无法贴合。

原版从不这样做。占格是 prefab 里序列化的格盒：`BasePart.cs:197-200` 的 `m_gridXmin/m_gridXmax/m_gridYmin/m_gridYmax`，`ConstructionUI.cs:1279` 按它判「某格是否属于这个零件」、`:1344` 按它列出的格子清占位，而 `GridPositionToWorldPosition` 把网格坐标 `(x, y)` 映到 `contraption.position + right*x + up*y`——**格心在整数坐标，格宽 1**。（`Contraption.FindPartAt` 只登记零件自己那一格，多格盒是放置时的清占位/选件范围。）

原版的账本（`tools/bple-grid/extract-grid.mjs` 实测，343 件 `Part_*.prefab`）：

| 格盒 `minX,maxX,minY,maxY` | 件数 |
|---|---|
| `0,0,0,0` | **332** |
| `-1,1,0,1` | **11** |

332 件只占原点一格——螺旋桨、风扇、机翼、聚光灯、抓钩、弹簧、猪全在内；11 件是 3×2，**恰好**是 `Part_KingPig_01..07` 与 `Part_GoldenPig_01..04`；0 件缺字段。三个直方图都被提取器做成硬断言（漂移即 `exit 1`）。

## 决策

1. **提取器 `tools/bple-grid/`**：`extract-grid.mjs` 读全部 `Part_*.prefab` 的四个字段，报告每件的格盒 + 全量直方图，并把上面的 332/11 与「11 件必须全在 KingPig/GoldenPig 族内」做成硬断言；`apply-grid.mjs` 按报告幂等写入内容（`--dry-run` 可验），缺字段/取值非法即抛错。与 `tools/bple-joints`、`tools/bple-materials` 同一模式：**原版真值只有一个来源**。
2. **内容只在非默认时写 `gridBox`**：`content/parts.json` 的 7 个金猪系（24/221–226，映射 `Part_KingPig_01..07`）带 `"gridBox": { "minX": -1, "maxX": 1, "minY": 0, "maxY": 1 }`，其余 257 件不写字段。**缺失 = 原版默认（原点单格）**，这个默认本身是原版 332/343 的众数，写进 schema 描述、`PartContentParser`（`GridCellBox.Single`）与客户端 `validateContent`，只有这一处语义、没有第二个源。形状照抄 `capabilities.canEnclose`：只在非默认时出现。
3. **`PartFootprint._body` 改用格盒**：占格/`Overlaps`/`CellsOccupied`/`TransformBlocked` 只看声明格盒；**`_all` 一字不改**（碰撞体并集 + 条件支架，ADR-017），连接接近 `Touches` 仍按几何贴合判定。所以 ADR-018 的 `frame` 条件形状**继续有用**（客户端对齐 + `_all`），只是不再参与占格。
4. **格盒落到网格坐标，再按 scale 与四分之一圈取向**：原版的占格是**格图**不是几何测试，所以任意平面位姿先归到它所在的网格坐标（最近格心，`floor(position + 0.5)`），再以该格心为锚画声明格盒：宽度/高度随 `entity.scale` 缩放，方向取 `yaw` 解出的**四分之一圈**。取整不用 `MathF.Round`（半数进偶，会让恰好落在格线上的零件依赖格号奇偶）。
   - 归格是必须的，不是选配：客户端「贴合」把零件的可连边落在**格线**上（滑翔翼吸附到 1.7404、木框在 1.0——支架左边缘正好压在 1.5），也就是**邻格里**；不归格的话每个贴合件都会被自己邻居的格盒判成重叠，正是 ADR-018 修掉的那个 bug。
   - 只取四分之一圈也是必须的：格图只有四个朝向，而客户端建造本就按四分之一圈解 `yaw`（`clients/web/src/editor/tools.ts` 的 `connectionEdges`：`Math.round(yaw / (Math.PI / 2)) % 4`）。按原始 yaw 转格盒的话，任何 15°/30°/45° 摆放的 1×1 零件（格盒转过几度就有 1.13 格宽）都会压住邻格，原版网格从不如此；木车/斜坡关（0.9 rad）会立刻摆不下。
5. **`Bounds()` 取两个几何的并集**：空间哈希的桶覆盖与 `MaxFootprintCells` 上限必须同时罩住占格格盒（金猪 3×2 比它的碰撞体宽）与碰撞体并集（旋翼的碰撞体比它那一格宽）。
6. **碰撞体直伸邻格**（不占格）是原版行为，有意保留：桨叶、翅膀、弹簧可以压在邻格上，只要原点那一格（或声明的格盒）没被占。

## 影响

`PartFootprint` 现在携带两个互不包含的几何：

| 语义 | 几何 | 用途 |
|---|---|---|
| 占格 | 声明格盒 → 归格 → `scale`/四分之一圈 | `Overlaps`、`CellsOccupied`、`TransformBlocked`、`FootprintTooLarge` |
| 连接 | 全部碰撞体（含 `condition` 支架） | `Touches`、连接上限、`PartFootprint.Bounds` |

实测（真服务器 Release + 真内容 `terrain-v1`，`--play` + 裸 WS 客户端，用客户端自己的 `encodeCommand`/`decodeAck`/`decodeSnapshotFrame`；每轮重启服务保证房间干净）：

**A. 旋翼紧邻木框（本 ADR 的核心）** — 木框(1) 在 `(-11,10)` + 猪(4) 同格（`EnclosedBy` 成帧）+ 旋翼(37) 在 `(-10,10)`：

| 命令 | status | error |
|---|---|---|
| PlacePart frame(1) @ (-11,10) | 0 | 0 |
| PlacePart pig(4) @ (-11,10)（同格，包裹） | 0 | 0 |
| **PlacePart rotor(37) @ (-10,10)（紧邻，2.55 格桨叶）** | **0** | **0** |
| PlacePart rotor(37) @ (-20,10)（孤立） | 0 | 0 |
| PlacePart rotor(37) @ (-10.4,10.4)（与紧邻旋翼同格） | 5 | **6** |
| StartSimulation | 0 | 0 |

`StartSimulation` 后（无开关，因为旋翼当前是气球式持续升力）：簇内（木框+猪+旋翼同一个 body 10）y `9.997 → 327.36`（tick 333，vy 0.16 → 169.8），x 恒为 −10；**孤立旋翼**（body 11，无框无猪）y `9.997 → −2.05`（自由落体落地），x 从 −20 漂到 −22.4。**紧邻木框且簇内有猪的旋翼上升，孤立旋翼毫无升力。**

**B. `SetPartActive` 那一半** — 同一场景加发 `SetPartActive(rotor @ -10,10, true)`（沙盒 Editing 阶段这条命令是 `WrongMode`，只能在 Start 之后发）：ack `status 0 / error 0 / entityId 1048588`，随后该实体**下一帧就从世界里消失**（+15 tick 起快照里不再有这个 entity）。这是 `GameplayRules.RunBalloons` 的开关语义（气球放气摧毁）落到了旋翼身上，属 `tasks/original-vs-implemented.md` §6 已记录的**既有偏差**（"旋翼被建模成气球式升力 + 放气摧毁"），与本 ADR 无关，也未修（见「未做」）。

**C. ADR-018 滑翔翼回归** — 木框(1) 在 `(1,0)`，木滑翔翼(31) 在 `1.7404`（ADR-018 实测吸附位，支架左边缘压在 1.5）：

| 命令 | status | error |
|---|---|---|
| PlacePart frame(1) @ (1,0) | 0 | 0 |
| **PlacePart glider(31) @ (1.7404,0)** | **0** | **0** |
| PlacePart glider(31) @ (2.2,0)（同格） | 5 | 6 |
| PlacePart frame(1) @ (2.0,0)（同格，框架不能当被包裹件） | 5 | 6 |
| MovePart glider → (1.7404, 1) | 0 | 0 |
| MovePart frame → (1, 0.4)（滑翔翼让出的那一格） | 0 | 0 |

客户端吸附数字一个字没改（`clients/web/src/editor/tools.ts` 与 `renderer/connectionVisuals.ts` 只读内容，不动）。

回归与新增测试：`CellOccupancyTests`（真内容：旋翼紧邻木框 + 同格被拒 + 金猪 3×2 六格全占 + 四分之一圈转向 + 滑翔翼 ADR-018 位姿 + 格盒随 scale）。改写的旧断言（旧测试假设「碰撞体并集占格」，按新语义重写而不是放宽）：

- `FreePlacementTests.OverlapIsRejectedRegardlessOfAlignmentWhileTouchingIsLegal` → `SameCellIsRejectedRegardlessOfAlignmentWhileNeighbouringCellsAreLegal`：原断言用「45° 的两个 1×1 块中心距 0.9，碰撞体相交」证明重叠被拒；格盒语义下 0.9 与 0.4 都落在**同一格**，改成同格在任意角度都被拒 + 邻格贴边合法 + 贴边即连。
- `ConstructionRulesTests.RotateIntoOccupiedFootprintIsBlocked`：1×1 格盒旋转**不换格**（这本身就是原版行为），改用带 3×2 声明格盒的测试件，转 90° 时格盒从 `x[-1.5,1.5] y[-0.5,1.5]` 摆到 `x[-1.5,0.5] y[-1.5,1.5]`，撞下格被拒、位姿不变。
- `JointAndEnclosureTests` 的猪/轮用例、`SandboxRoomTests` 的木车与轮挂载用例**未改**：归格后 0.9 格距的邻居仍然是相邻格，全部照旧绿。

各项目单独跑：Core 209、Server 99、Protocol 39、Replay 11、Physics 32（非 Jolt）+ 6（Jolt）、客户端 224，全绿。确定性双跑哈希用例（`ConstructionRulesTests` / `FreePlacementTests` / `TntBlastTests.BlastSceneIsDeterministicAcrossRuns` / Replay）保持绿。

## 未做

- **客户端不做占格预测**：落点合法仍只由服务端判定，客户端贴合盒（碰撞体/支架 AABB）与占格格盒是两套几何——"对齐 ≠ 占用"（ADR-018 决策 2/3 的结论）。因此放手后被拒仍靠服务器拒绝 + 下一帧回跳（既有行为）。
- **原版放置时会清掉被占格上的非底盘件**（`ConstructionUI.ClearCollidingParts`，金猪还会放过绳子/弹簧）。PigForge 仍是直接拒绝（`CellsOccupied`），没有清占位路径。保真缺口，未开切片。
- **格盒不随连接状态变化**（原版也不变）；`grip`/`frame` 支架随连接状态实心/trigger 的双向语义仍未做（ADR-017 决策 5 遗留）。
- **旋翼开关语义**：本轮实测确认旋翼被开关打开即被摧毁（气球式放气），因此「`SetPartActive(rotor,true)` 后旋翼上升」这条验收在现有语义下无法成立；旋翼当前也不要求开关就有持续升力。这是 §6 已记录的偏差，修它属于旋翼/推进件建模切片，不在本 ADR 范围。
- 3 个无 prefab 的静态关卡件（2/5/6）不写 `gridBox`，继续是默认单格；它们从不进入 `ConstructionRules` 占格（`GameRoom.SetupFromLevel` 走 `Spawn` 旁路）。

## 参考

- 原版依据：`BasePart.cs:197-200`（四个序列化字段）、`ConstructionUI.cs:1279,1344`（按格盒判归属/清占位）、`ConstructionUI.GridPositionToWorldPosition`（网格坐标 ↔ 世界坐标，格心在整数）
- 代码：`tools/bple-grid/{extract-grid,apply-grid}.mjs`、`src/PigForge.Core/Construction/PartFootprint.cs`、`src/PigForge.Core/Content/PartContent{Document,Parser}.cs`、`schemas/part-content-v1.schema.json`、`clients/web/src/schema/{types,validateContent}.ts`、`clients/web/src/builder/playParts.generated.json`（`node tools/web-parts/generate-play-parts.mjs`）
- 测试：`tests/PigForge.Core.Tests/CellOccupancyTests.cs`、`FreePlacementTests.cs`、`ConstructionRulesTests.cs`、`PartContentTests.cs`、`clients/web/src/schema/validateReplay.test.ts`
- 相关：ADR-005/ADR-007（形状来自 BPLE collider）、ADR-011（焊接判据）、ADR-017（条件支架）、ADR-018（格子对齐与贴合；本 ADR 修订其决策 6）
