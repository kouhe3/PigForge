# 挑战与收集：让关卡的挑战件真的能被收集（P5-b）

> 状态：**P5-b-1 / P5-b-2 / P5-b-3 全部交付**（2026-10-10 第三十一轮）。前置：`docs/specs/level-props.md`（P5-a 已把 4 个挑战箱的
> *画面*画出来，见该规格 §2/§6），本文档只谈**行为**：它们怎么被收集、状态怎么到客户端。
> 关联：`docs/specs/original-level-pack.md` §8 的 P5 行、差距 `G75`。

## 1. 现状与问题

P5-a 之后，`BoxChallenge`（269 实例）/`StarBox`（262）/`DynamicBoxChallenge`（12）/`DynamicStarBox`（1）的画面
已经画在关卡里，但**碰它们不会有任何反应**。这是本片自己造出来的"外观 ≠ 物理"：原版里这两个家族各有一套
装载即生效的规则，收集结果还会写进存档。

## 2. 原版机制（已逐行读出，`Assets/Scripts/Assembly-CSharp/`）

**进入判定**（`Goal.cs:18-55`）：触发体是 `OnTriggerEnter`；进来的 `Collider` 先 `FindPart` 回溯到它挂在的
`BasePart`，然后按目标类型判"是不是**连到猪**"：

```csharp
if (targetType != BasePart.PartType.Pig ? ContraptionRunning.IsConnectedTo(part, collider, basePart)
                                        : ContraptionRunning.IsConnectedToPig(part, collider)) { OnGoalEnter(basePart); }
// 否则：同一连通分量里、离进入的那个零件 2.5 米以内的任意零件也算达成（Goal.cs:44-53）
```

**收集**（`Collectable.cs`，`BoxChallenge` 的 `m_target` 与 `StarBox` 的基类就是它）：

| 时机 | 原版做什么 |
|---|---|
| `OnGoalEnter(part)`（`Collectable.cs:63-79`） | 若未收集且不在 `Completed` 状态：放收集特效（`collectedEffect`）、放 `bonusBoxCollected` 音效、`collected = true`、发 `ObjectiveAchieved`、`DisableGoal(true)` |
| `DisableGoal(true)` | **隐藏自己与所有子对象的 `Renderer` 与 `Collider`**，`Rigidbody.isKinematic = true`（箱子从画面上消失） |
| `OnReset()`（`:81-86`） | `collected = false`、位置回到 `originalPosition`、`DisableGoal(false)`（重置后重新出现） |

**挑战语义**（`WaypointChallenge.cs`，11 行）：`IsCompleted()` 就是 `m_target.collected` —— 挑战 = "那个箱子被收集了"，
`Type == ChallengeType.Box`；本片不做星级/进度上报。

**星箱**（`StarBox : OneTimeCollectable`）：`OnCollected` 只写进度（沙盒星、鼻币、经验 —— 本片全不做），
`IsDisabled() => false`；`GetNameKey` 在父对象既不是 `FloatingStarBox` 也不是 `StarBoxes` 时会
`DisableGoal()`（即"孤立的星箱"在装载时不显示 —— 实测本片这批 `StarBox` 实例没有显示问题，见 §7 分片验收）。

## 3. 触发体积：内容 **v8** 的道具 `triggers`

P4c 的 v7 `boxes` 里**故意跳过**了 trigger（那些是"有人经过这里"的标记，不是墙）。P5-b 需要它们，于是：

```json
"props": [
  { "id": "BoxChallenge", "x": 10, "y": 2, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1,
    "triggers": [ { "dx": 0, "dy": 0, "dz": 0, "sizeX": 1.328125, "sizeY": 1.328125, "sizeZ": 1 } ] }
]
```

- 与 `boxes` 同一套坐标约定（件自己的帧、`m_Center`/`m_Size`、实例 scale/镜像由建房的人施加），工具已经在读
  trigger（`tools/lib/props.mjs` 的 `colliders[].trigger`），只是没写出来。
- 只有**画出来的**实心件才带 `triggers`；`BoxChallenge`/`StarBox` 各 1 个、`GoalArea_*` 的终点区不进这里
  （终点区已有 `goalZone` ✓）、`Secret_*`/`PressureButton*` 的触发区留给各自的 P5 子项。
- schema：`schemas/level-content-v8.schema.json`（v7 + 可选 `triggers`，形状与 `boxes` 相同）；
  Core 的 `LevelContentParser` 与客户端的 `levelContent.ts` 照 v6/v7 的老规矩门控（v8 才允许、尺寸必须为正）。

## 4. 规则（房间侧，纯几何、不动物理）

**不在两个物理后端里加 sensor**（那是新能力，`IPhysicsWorld` 契约要动）：判定用**房间已经有的两份数据**——
关卡文档里的 `triggers`（静态件）与实体快照里的位姿——做一个每 tick 的 AABB 重叠测试：

- **谁算"进入"**：原版要的是"连到猪的零件"（§2）。本片取**猪所在的复合体**：房间的装配器知道每个实体属于哪个
  簇（`CompoundAssembler` 的 cluster），凡是与猪同簇的实体都算 ✓（`IsConnectedToPig` 的第一条分支）。
  原版还接受"同分量、离进入零件 2.5 米以内"的**别的**零件（§2 的第二条分支）——**本片先不做**（偏差 5）。
- **谁的盒子**：猪/零件的体是复合体，取它的**包围盒**（`PhysicsBodySnapshot` 已有位姿 ✓，尺寸取自装配后的形状
  ——若拿不到就用一个与猪体积同量级的盒，实测时标定）与 `triggers` 里的盒做 AABB 相交 ✓。
  判定是"经过"，不需要接触精度（偏差 3 已记）。
- **命中即收集**：该道具标 `collected`，同一 tick 内不重复记；已收集的件**不再参与判定** ✓。
- **生命周期**：`collected` 随房间；`RESET`/回到建造态**清空**（原版 `OnReset` 会把箱子放回并重新显示 ✓ 本片不动
  位置、只清标记 ✓）。不写存档（偏差 1）。

## 5. 状态上线：PGFS **v7** 的 `collected` 段

收集结果必须到客户端（客户端要把已收集的道具**不再画出来**），所以快照要带上它：

- PGFS 版本 **7**：头部不变（`CurrentVersion` 加一），实体段之后**总是**追加一段
  `uint16 count` + `count × (uint32 propIndex)`（索引指向**关卡文档的 props 数组** —— 两侧都读同一份文档，
  不需要传 id 字符串）。"总是"很关键：空集合也写两个字节的 0，客户端才能把"没收集"与"帧被截断"分开。
  写入顺序 = 房间侧**按索引排好**（`GameRoom` 用 `List<uint>` 二分插入）⇒ 同样的命令序列发布同样的字节。
  缓冲：`SnapshotFrame.GetMaxByteCount(entityCount, collectedCount = 0)`；三个发布路径（运行/建造/沙盒）都写这一段。
- 客户端解码 v7：`decodeSnapshot` 多读一段（老版本读到尾部即空 ✓ 版本门控）；`drawFrame`/`propDepthBuckets`
  跳过索引在集合里的道具。
- 已收集的视觉：原版是"箱子消失/星飞走"，本片先做**不画**（最保守、不需要新美术；飞星的动效归 P6 之后）。
- `DemoSnapshotHost`/`ReplayRunner` 跟着升级（PGFS v7 的编解码是共享的 ✓），回放 schema 不变（回放记的是命令与
  快照字节）。

## 6. 有意偏差（先记下，实现时逐条落进本文档）

1. **不写存档**：原版把收集结果写 `GameProgress`，本片只在房间生命周期内有效（与 `DessertPlace` 的甜品点同一条
   溯源：本片没有存档层）。
2. **路点顺序不建模**：`WaypointChallenge` 如果要求"按顺序经过"，本片先按"经过即达成"。
3. **收集判定用 AABB 近似**（不是物理接触事件），命中范围与"贴着边缘经过"的边界情况会有差别。
4. **不做 HUD/星级/计时**：原版的星数、计时挑战（`TimeChallenge`）、`DontUsePartChallenge` 属于进度系统，
   本片只到"房间知道某件已被收集、客户端不再画它"。
5. **不做"同分量 2.5 米内的邻居零件"那条兜底**（`Goal.cs:44-53`）：只认猪所在的复合体本身；贴着触发盒边缘、
   靠别的零件擦过去的边界情况会与原版不同。
6. **已收集的视觉只有"不画"**：原版是隐藏渲染器 + 收集特效 + 音效 + （星箱）飞出鼻币；本片先只让客户端不画
   该道具（特效/音效/飞星属表现层，另行开片）。
7. **星箱的"孤立即隐藏"（`StarBox.GetNameKey` 的 `DisableGoal()`）不建模**：本片按"装载即可见"处理。

## 7. 分片与验收

| 片 | 内容 | 验收 |
|---|---|---|
| **P5-b-1 ✅** | 内容 v8 的 `triggers`（工具 + schema + Core/客户端门控 + 重建关卡） | 工具第二次 **0 改动**；**948** 个道具带 trigger（共 948 个盒，每个道具 1 个）；Core **375**（+2 用例：v8 解析 + 5 条门控）、Server **156**、web **378** |
| **P5-b-2 ✅** | 房间的 AABB 收集规则（`LevelChallengeTriggers.Build` + `GameplayRules.CheckChallenges` + `GameRoom.CollectedPropCount`） | `ChallengeRoomTests` 3 条：猪在标记里 → 收集 1 件且不重复；猪在 50 米外 → 0 件；`Retry` 清空并可在下一轮再收 |
| **P5-b-3 ✅** | PGFS v7 的 `collected` 段（`SnapshotFrame` 的 `TryWriteCollected`/`TryReadCollected`、三个发布路径、`GetMaxByteCount(n, collected)`）+ 客户端解码（`decodeSnapshot`、两种 socket、`App.vue` 的 `applyCollected`）+ `propDepthBuckets` 跳过 | Protocol **43**（+1 往返、两处版本/尺寸期望）；web **380**（+2 尾段解码与截断）；实机：把猪放在 `Level_05` 的 `BoxChallenge`（props 索引 5）上 → 113 帧里 `collected` 由 `[]` 变 `[5]`，关掉该件后一帧少画 **2 个四边面** |

**不做的**（本片范围外，另行开片）：存档、星级、计时、HUD、收集特效/音效/飞星、`Dynamic*` 的动态体
（这条另有原因：**动态道具的位姿变化没有上线的通道** —— 道具不是实体、不进快照，一动起来客户端就与服务器
脱节 ✗；要让它们动，得先把道具变成可按索引上线的实体，属独立一刀）。

**决策记录（需要一条 ADR）**：快照版本升 **PGFS v7**（尾部 `collected` 段）。备选是"把道具变成实体"（能一并解决
动态体，但要新实体种类 + 索引协议），本片取前者：改动小、客户端只多读一段、老版本读到尾部即空。
