# ADR-038：关卡内容 v10 与 PGFS v8 —— 挑战、星级、计时与结算

- 状态：**已接受**（2026-10-11）
- 相关：`ADR-033`（关卡 v2 `terrain` + `GET /level`）、`ADR-036`（v5 `cameraLimits`）、`ADR-037`（v9 建造限制）、
  `ADR-026`（玩家生命周期与 RESET）、`docs/specs/level-stars.md`（本片的规格，含全部原版真值与偏差）、
  `docs/specs/level-challenges.md`（P5-b：挑战箱的收集与 PGFS v7 的 `collected` 段）
- 差距：`G74`（关卡数量/进度）、`G79`（星级/挑战）、`G81`（计时器）、`G84`（结算页）；新记 **`G114`**
  （终点区要求 Egg/Pumpkin）

## 背景

P5-b 之后，挑战箱碰得到、收集得上线，但**星级与计时完全不存在**：`GameplayRules` 只有
`Playing/Won/Failed` 三态，HUD 没有时钟与星，结算页没有，快照的 `phase` 字节也没有人读成「几星」。

原版的这条链不长，但每一条判据都有明确的出处（`docs/specs/level-stars.md` §2 逐行列出）：

- **挑战集合** = 场景里每个 `Challenge` 组件在 `Awake` 自注册进一个**进程级静态表**，然后按
  **GameObject 名字**排序并编号 1..N（`Challenge.cs:39,62-81`）；类的身份只能经「实例 → 调色板 prefab →
  `m_Script` guid」得到（`WaypointChallenge` 这个字面量在关卡字节里 0 命中）。五个子类：
  `DontUseParts / Time / PerfectFlight / Transport / Box`，参数**分三层**：`m_targetTime` 与
  `m_partType` 只在**关卡的 `PrefabOverrides`** 里，`partToTransport` 只在 prefab，`PerfectFlight` 无字段。
- **星 1 = 抵达终点区**（与挑战无关），**星 2/3 = 排序后前两个挑战各自的 `IsCompleted()`**
  （`LevelComplete.cs:288-289`、`:308-346`）；星数是「达成数」而不是「槽位」，所以那处**换位**只影响
  图标顺序。
- **计时**：进入 Running 时记 `PigStartPosition` 并清零，**猪离起点 ≥ 1 米**才 `TimeElapsed += deltaTime`
  （`LevelManager.cs:1431-1441`），进终点区那一刻冻结成 `CompletionTime`（`GameMode.cs:379`）；
  `TimeLimit` = 所有计时挑战目标的**最大值**，`0` 表示本关没有计时（HUD 隐藏）；**超时不判负**。

## 决策

### 1. 内容升 v10：`challenges` 数组，顺序就是原版的编号顺序

```json
"challenges": [
  { "name": "BoxChallenge", "kind": "box", "propIndex": 5 },
  { "name": "TimeChallenge", "kind": "time", "targetTime": 4 }
]
```

- **v10 必填**（可为 `[]`：14 关本来就没有挑战），v1–v9 出现即硬错误 —— 与 v5 的 `cameraLimits`
  同一条门控纪律。
- 元素顺序 = 按 `name` 排序后的 `m_challengeNumber` 顺序（`challenges[0]` = 星 2、`challenges[1]` = 星 3）；
  第 3 个（只有 `episode_6_level_25` 有）不参与星级，与原版一致。
- **允许重名**（3 关真有同名实例）；排序用**序数**比较而不是原版的文化敏感 `string.Compare`，同名时按
  关卡文件的出现顺序（偏差 D1：原版对等键的 `List.Sort` 本身没有数据依据）。
- `partTypeId` 是 PigForge **基座** family（经 `tools/lib/part-types.mjs` 从 `BasePart.PartType` 解析），
  与 v9 的配额同一个解析器；`box` 的 `propIndex` 指向本文档 `props` 数组（收集段用的就是它）。

### 2. 时钟在规则层，判定在房间层

- **时钟**（`GameplayRules`）：新 `BeginRun(EntityId pig, PhysicsVector3 ownPosition)` —— 房间在 `Start()`
  装配完之后调一次，交的是**猪自己的 transform 位姿**（与出界判定同一份数据，原版也是 `FindPig()` 的
  `transform.position`）；随后每 tick 比较一次，越过 1 米那一 tick **只上弦不加时**（原版 `UpdateRunning`
  上弦后直接 return），此后 `ElapsedSeconds = (tick − 上弦 tick) × 1/60`。**冻结是免费的**：`Tick` 只在
  `Playing` 时推进 tick 计数器，所以进终点区那一 tick 就是最后被计数的一 tick，房间读到的就是原版的
  `CompletionTime`。`ResetAll`/`ResetForRebuild` 清空它（`Retry` 与回到建造态都走这两条）。
- **判定**（`GameRoom`）：挑战的判据需要房间自己的东西（件表、串的连通分量、已收集的挑战箱、断开记录），
  所以判定在房间侧、在原版同一个位置 —— `Phase` 刚从 `Playing` 变 `Won` 的那一 tick，**一次**，结果写
  `_challengeFlags`（每挑战一位）与 `StarCount`（`1 + 前两位的达成数`）。
- **`Transport`** 用 `GameplayRules.AreConnected(a, b)`（新）：原版的连通分量**就是**它的 joint map
  （`Contraption.cs:1293` 把每条 `m_jointMap` 边 union 起来），而 PigForge 同一个图由
  `LinkPowerCluster`（合并体、缝、焊点、轮铰链、弹簧边）维护，所以问「功率簇」就是问
  `ContraptionRunning.IsConnectedToPig`。
- **`PerfectFlight`** 的「损坏」= 房间在跑动中丢过任何关节：所有运行期拆分都汇进
  `RebuildSplitBody`（缝被冲量撕开、脱离件、拳套扯掉邻件），另有 `BreakWeldsTouching`、
  `BreakWeldsFromAppliedCommands`、`BreakSpringLink`（拉断与后端力断两条路都汇在这里）与
  `UnbindEntity`（跑动中被打掉的件，它的关节随体消失 —— 原版同样是 `OnJointBreak` →
  `UpdateJointMap`），再加后端自己上报的 `BrokenJoints`。原版只有一个入口（`m_broken` 由
  `UpdateJointMap` 置位，`Contraption.cs:2810-2812`），PigForge 的断开点是一组方法，这是偏差 D7。

### 3. PGFS 升 v8：尾部追加**运行状态**段，`stars` 不上线

在 v7 的 `collected` 段之后追加固定 5 字节：

```
float32 elapsedSeconds     // 本局的 TimeElapsed（进终点区后冻结）
uint8   challengeFlags     // bit i = challenges[i] 达成；未通关一律 0
```

- **总是写**（运行/建造/沙盒三个发布路径都写，与 `collected` 同一规矩）：客户端才能把「没有」与「帧被
  截断」分开。`GetMaxByteCount(entityCount, collectedCount)` 相应加常量。
- **`stars` 不写**：客户端由 `phase == Won` 与 `challengeFlags` 的前两位算出（1 + 达成数），与房间同一个
  公式；在线一个冗余字面量就是一份会漂移的真相。
- 倒计时所需的 `TimeLimit` 也**不上线**：它是关卡文档的静态属性（`challenges` 里 `time` 目标的最大值），
  客户端本来就通过 `GET /level` 拿着那份文档。
- `challengeFlags` 只在通关后非零（原版也只在结算页读 `IsCompleted()`），所以「运行中」三颗星里只有
  第一颗由 `phase` 决定，其余留暗。

### 4. 不做：存档、结算美术、加时

- **不写存档**（`_stars`/`_challenge_N`/`_completed`/最佳成绩）：进度存哪（A3）仍未拍板，本片只到
  「房间知道这局几星」。因此也没有「历史 ∪ 本次」的合并（D3/D4）。
- **结算页只做文字**：原版结算页的文案是空的（预制件里 `ObjectiveText` 是空 `TextMesh`、无本地化键），
  它只显示**图标**；图标（`m_icons[].icon` 的 6 组 guid）不在本片解包范围，所以目标描述由客户端按
  `kind` + 件名生成（D5）。
- **看广告加时**（+2 s，`LevelManager.cs:1687-1702`）不做（D8）：没有广告层。

### 5. 顺带记一条新差距：`G114` 终点区要求 Egg/Pumpkin

原版的「过关」不只是猪进终点区：`GameMode.PlayerHasRequiredObjects`（`GameMode.cs:356-375`）在
`EggRequired`/`PumpkinRequired` 时还要对应的件也进过终点区，而这两个标志的判据是
`levelManager.GetPartTypeCount(Egg | Pumpkin) > 0`（`LevelManager.cs:413-431`）—— 也就是**关卡自己发放了
那件**（`m_partTypeCounts` 里配额 > 0，非沙盒）。这份数据 PigForge **已经有了**（v9 的 `build.partCounts`），
但本片不改过关判定（属 `G77` 邻域），所以：**发放了 Egg 的关卡，PigForge 现在只用猪就能过关**；
发放了 Pumpkin 的 14 关还多一层 `G91`（没有基座）。记为新差距 `G114`，不写进内容（不需要新字段，
判定时查 `build.partCounts` 即可）。

## 后果

- 263 关的挑战、277 关的计时与星级规则进入内容与房间；HUD 有关卡自己的倒计时（原版 `D2.D2` 格式，
  超时显示 `+` 与红色）、三颗星与结算行；沙盒房间（无挑战、`ObjectivesEnabled = false`）不受影响。
- 已知偏差（规格 §8 逐条）：**D1** 序数排序 + 同名按文件顺序；**D2** 计时按 60 Hz tick（原版 50 Hz）；
  **D3/D4** 无存档、无历史合并；**D5** 目标描述用文字而不是图标；**D6** 换位只影响图标顺序；
  **D7** `PerfectFlight` 的断开点是一组方法而不是一个入口；**D8** 无加时；**D9** `Transport` 不做 2.5 m
  邻居兜底；**D10** `DontUseParts` 的房间口径含关卡预置的 20 个零件实例（原版读串的件，那 20 个实例是否
  进串未考证）；**D11** 终点区仍只认猪（见 §决策 5 的 `G114`）。
- 进度持久化与关卡选择仍然是这一线的下一块（`A3` 待拍板），历史星数合并必须等它。

## 验证

见 `docs/specs/level-stars.md` §9（内容普查、.NET 与 web 的计数、真机读数：`Level_05` 进终点区后
`challengeFlags` 由 0 变 `0b01`、先撞挑战箱再进终点区变 `0b11`，14 个无挑战关卡通关只有 1 星）。
