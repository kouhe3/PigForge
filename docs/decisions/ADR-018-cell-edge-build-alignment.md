# ADR-018: 建造对齐以格子与连接方向为准

## 状态

Accepted（修订 ADR-017 决策 3 的客户端半边）

## 日期

2026-10-03

## 背景

ADR-017 让条件支架形状参与拖拽贴合，于是汽水/火箭的贴合框变成支架外缘（`halfX 0.62`）。甲方复看后要求更简单、也更接近原版建造的规则：

> 「对齐到格子。确切地说是对齐到零件的正方形的边。不看贴图了，只看零件在此边是否有关节。有上下左右四个建立关节的地方 → 对齐上下左右。只在左边有建立关节的地方（螺旋桨）→ 只对齐左边」

即：**尺寸取格子**，**哪条边能吸附取连接方向**。原版的建造本来就发生在格子上，零件按格子落位、按格子相邻焊接；支架贴图只决定"这条边看起来有连接点"。

零件的连接方向在原版是每件的 `m_jointConnectionDirection`（`BasePart.cs:118-127`），实测与甲方描述完全一致：`propeller` / `fan` = Left、`rotor` / 电伞 = Down、轮族 = Up、`spring` = UpAndDown、其余多为 Any。

## 决策

1. **提取连接方向进内容**：`tools/bple-joints` 输出 `m_jointConnectionDirection`（8 个枚举名），`apply-joints.mjs` 写 `capabilities.jointConnectionDirection`；schema / C# 解析器 / 客户端类型与校验同步（264 个映射件；3 个无 prefab 的静态件缺省 `any`）。
2. **尺寸取真实碰撞箱（含条件支架），不取整、不归零**：客户端 `snapBoxOf` 仍用零件全部形状的并集 AABB，并保留它相对原点的偏移。原版零件的碰撞箱本来就不居中（风扇 `x[-0.39, 0.07]`、螺旋桨 `x[-0.45, -0.17]`、滑翔翼 `x[-1.45, 0.45]`、含支架的火箭 `x[-0.62, 0.62]`），**正是这个偏心**让它们唯一可连接的边在贴合时落在邻居的格线上。

   > 第一版曾把盒取整并居中到原点，结果风扇贴不上木框：它的碰撞箱偏左 0.16，居中后那条唯一可连的边离木框面还差 0.11。零件"不是四方的"这一点必须保留。
3. **有支架的零件对齐到支架**：原版美术里带 `frame` 支架的零件（滑翔翼族 **9 件**：31/32/144–147/178–180）用**支架边界**作对齐盒，由 texture manifest 的 `condition.kind === "frame"` sprite 生成（`tools/web-parts/generate-play-parts.mjs` → `frameBoxes.generated`，幂等、`--check` 可验）。滑翔翼的 collider 是 `1.9 × 0.6122` 且**偏左半格**，支架是 `1.1102 × 1.017` 且**偏右 0.3147**——照 collider 对齐把翅膀插进邻居半格，照原点格对齐差 0.255，照整格居中又是另一回事。原版对齐的是支架：**支架左边缘贴住邻居的右边缘**（见用户提供的原版截图）。支架近乎方形，故只缩放、不随 yaw 旋转。
4. **吸附边取方向**：`connectionEdges` 由 `jointConnectionDirection` 与 `jointConnectionType` 推出四条边（`none` 类型或 `none` 方向的零件一条都没有），再按 `entity.yaw` 旋转到世界方向；`snapMoveToParts` 只在被拖零件的**对应边**可连时才吸附（落到邻居右侧用它的左边，依此类推）。方向门控必须由**调用方**把 `edges` 一路带上，否则退化成"四边全允许"（见修订记录）。
5. **服务端连接判定不变**：仍是 ADR-017 的几何语义（`PartFootprint.Touches` 含条件支架、`Overlaps` 不含）——格子对齐后相邻件中心距恰为一格，几何判定照常成立。
6. **占格也用支架**：`PartFootprint` 的 `_body`（重叠 / `CellsOccupied`）在零件带 `frame` 条件形状时取该支架，主体只留在 `_all`（连接判定，`Touches`）。滑翔翼的 collider 是翅膀本体、宽 1.9，而它**必须**与焊接对象相邻——按 collider 占格时数学上摆不下：实测把滑翔翼放在它正确的吸附位（1.7404）紧挨木框，服务端直接回 `CellsOccupied`（error 6），玩家看到的就是"移动松手后弹回原位"。支架数据由 `tools/bple-brackets/apply-brackets.mjs` 从原版 sprite manifest **幂等**写入 `content/parts.json`（`condition.kind = "frame"`），服务端与客户端读同一份内容（客户端原先那份独立生成表已删除）。

## 影响

实测（真实内容的 `snapBoxOf`）：

| 零件 | direction | 对齐框（半宽） | 可吸附边 |
|---|---|---|---|
| rocket（含支架）/ 汽水 | any | `x[-0.62, 0.62]` | 上下左右 |
| 滑翔翼族（9 件） | any | 支架 `x[-0.2404, 0.8698]`（collider `x[-1.45, 0.45]` 只做碰撞） | 上下左右 |
| propeller / fan | left | `x[-0.45,-0.17]` / `x[-0.39,0.07]` | 左 |
| rotor | down | `x[-1.27, 1.27]` | 下 |
| wheel 族 | up | `x[-0.32,0.34] y[-0.54,0.49]` | 上 |
| spring | upAndDown | `y[0.30, 0.50]` | 上下 |
| electric-umbrella | down | 1 格 | 下 |
| pig / engine（type none） | any | — | 无 |

贴合实测（木框在 x=1，右边缘 1.5）：风扇吸附到 x=1.885、螺旋桨 1.9524、火箭 2.12；**滑翔翼 31/32/145 分别吸附到 1.7404 / 1.7943 / 1.6252，三者的支架左边缘都是 1.5** ✓ 只往下/上连的 rotor/wheel/spring 在 x 轴**完全不吸附**；king-pig（`none`）任何边都不吸附。

回归：`tools.test.ts`（保留碰撞箱偏心、方向推出四边、轮子只有上边、球按半径、滑翔翼按支架边界）、`canvasGestures.test.ts`（拖拽贴合与形状偏移、`edges` 必须随拖拽状态传递）。生成器：`node tools/web-parts/generate-play-parts.mjs --check` 保证 `frameBoxes.generated.json` 与 manifest 同步。

## 修订记录

- 2026-10-03：第一版把对齐盒取整并居中到原点，实机复看发现**风扇贴不上木框**（它的碰撞箱偏左 0.16，居中后唯一可连的边离木框面差 0.11）。决策 2 改为「用真实碰撞箱、不取整、不归零」，方向门控不变。
- 2026-10-03：改用真实碰撞箱后**滑翔翼对错了边**——它的主体跨两格、四条边却都有连接点，贴出来是翼体边缘压格线而支架跑出格外。补决策 3。同一轮发现拖拽状态漏传 `edges`，木轮仍然四边可吸，已一并修正。
- 2026-10-03：决策 3 第二版改成「整格数、居中于原点」，仍是错的——用户给出原版截图，对齐的是**支架**：支架贴住邻居，翅膀向一侧盖过邻格。第三版改为**支架边界**（来自 texture manifest 的 `frame` sprite）。
- 2026-10-03：支架只修了**客户端对齐**，服务端仍按 collider 占格；滑翔翼一旦贴住木框，`MovePart` 全部被 `CellsOccupied` 拒绝（用户报"上下拖拽松手弹回原位"）。补决策 6：支架进内容、占格改用支架。干净房间实测：木框 → 滑翔翼 → 移动 y=1 全部 Accepted，滑翔翼丢在木框同格仍拒。

## 未做

- **服务端连接仍未按 `jointConnectionDirection` 限制**：客户端现在不会把螺旋桨往右吸附，但服务端仍接受任意几何接触的焊接（原版会拒绝）。这是 ADR-011 起就存在的保真缺口，记录待办。
- 支架随连接状态切换实心/trigger 的双向语义（ADR-017 决策 5 仍未做）。

## 参考

- 代码：`tools/bple-joints/{extract-joints,apply-joints}.mjs`、`clients/web/src/editor/tools.ts`（`snapBoxOf` / `connectionEdges` / `snapMoveToParts`）、`src/PigForge.Core/Content/PartContent{Parser,Document}.cs`
- BPLE 依据：`BasePart.cs:118-127`（方向枚举）、`BasePart.cs:681-720`（方向随 gridRotation/flip 旋转）
- 相关：ADR-011（焊接判据）、ADR-017（支架作为条件形状；本 ADR 修订其客户端贴合规则）
