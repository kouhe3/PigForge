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
2. **尺寸取格子**：客户端 `snapBoxOf` 用零件**非条件**形状的并集算跨度，再取整到整格（`max(1, round(span))`），中心归到零件原点（忽略形状偏移与支架外扩）。于是火箭 1×1、滑翔翼 2×1、旋翼 3×1。
3. **吸附边取方向**：`connectionEdges` 由 `jointConnectionDirection` 与 `jointConnectionType` 推出四条边（`none` 类型或 `none` 方向的零件一条都没有），再按 `entity.yaw` 旋转到世界方向；`snapMoveToParts` 只在被拖零件的**对应边**可连时才吸附（落到邻居右侧用它的左边，依此类推）。
4. **服务端连接判定不变**：仍是 ADR-017 的几何语义（`PartFootprint.Touches` 含条件支架、`Overlaps` 不含）——格子对齐后相邻件中心距恰为一格，几何判定照常成立。

## 影响

实测（真实内容的 `snapBoxOf`）：

| 零件 | direction | 对齐框（半宽） | 可吸附边 |
|---|---|---|---|
| rocket / 汽水 / 滑翔翼 | any | 1×1（翼 2×1） | 上下左右 |
| propeller / fan | left | 1×1 | 左 |
| rotor | down | 3×1 | 下 |
| wheel 族 | up | 1×1 | 上 |
| spring | upAndDown | 1×1 | 上下 |
| electric-umbrella | down | 1×1 | 下 |
| pig / engine（type none） | any | 1×1 | 无 |

回归：`tools.test.ts`（整格取整、方向推出四边、轮子只有上边、球按格）、`canvasGestures.test.ts`（拖拽落点为格边、轮子在方块下一格）。

## 未做

- **服务端连接仍未按 `jointConnectionDirection` 限制**：客户端现在不会把螺旋桨往右吸附，但服务端仍接受任意几何接触的焊接（原版会拒绝）。这是 ADR-011 起就存在的保真缺口，记录待办。
- 支架随连接状态切换实心/trigger 的双向语义（ADR-017 决策 5 仍未做）。

## 参考

- 代码：`tools/bple-joints/{extract-joints,apply-joints}.mjs`、`clients/web/src/editor/tools.ts`（`snapBoxOf` / `connectionEdges` / `snapMoveToParts`）、`src/PigForge.Core/Content/PartContent{Parser,Document}.cs`
- BPLE 依据：`BasePart.cs:118-127`（方向枚举）、`BasePart.cs:681-720`（方向随 gridRotation/flip 旋转）
- 相关：ADR-011（焊接判据）、ADR-017（支架作为条件形状；本 ADR 修订其客户端贴合规则）
