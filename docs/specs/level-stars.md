# 关卡的挑战、星级、计时与结算（内容 v10 + PGFS v8）

> 本片把原版的**挑战 → 星级 → 结算**这条链搬进 PigForge：每关自己的挑战对象（`Challenge` 及其五个子类）、
> 原版自己的计时器、三颗星的判据、以及结算所需的每挑战达成状态。
> 前置：`docs/specs/level-challenges.md`（P5-b 已把**挑战箱**的收集做完：内容 v8 的 `triggers` → 房间规则 →
> PGFS v7 的 `collected` 段 → 客户端不画）。本文档只谈**星级/计时/结算**。
> 关联差距：`G74`（关卡数量/进度）、`G79`（星级/挑战）、`G81`（计时器）、`G84`（结算页）；
> 新记 `G114`（终点区要求 Egg/Pumpkin）。决策：`ADR-038`。

## 1. 一句话

每个原版关卡现在带着**它自己的**挑战清单（263 关有、14 关没有）、一份**原版公式的计时器**（猪离起点 ≥ 1 米
才开始走，进终点区冻结成 `CompletionTime`）、以及三颗星的判据（星 1 = 抵达终点区；星 2/3 = 按实例名排序后的
前两个挑战各自的 `IsCompleted()`）。房间在原版的位置做判断，把 `elapsedSeconds` 与每挑战的达成位放进快照尾部
（PGFS **v8**），客户端据此画倒计时、星与结算文案。

## 2. 原版真值

### 2.1 挑战集合 = 静态注册表 + 按实例名排序编号

```csharp
// Challenge.cs:35-44
public List<IconPlacement> m_icons;
public GameObject m_tutorialBookPage;
private static List<Challenge> s_challenges = new List<Challenge>();
[SerializeField] private int m_challengeNumber;
public static List<Challenge> Challenges => s_challenges;
```
```csharp
// Challenge.cs:62-81 —— 每个实例在 Awake 自注册，随后整体按 GameObject 名字排序并重新编号
protected virtual void Awake() { s_challenges.Add(this); Refresh(); }
private void Refresh()
{
	s_challenges.Sort(new ChallengeOrder());
	for (int i = 0; i < s_challenges.Count; i++) { s_challenges[i].m_challengeNumber = i + 1; }
}
// Challenge.cs:17-23
public int Compare(Challenge obj1, Challenge obj2) { return string.Compare(obj1.name, obj2.name); }
```

- **不经过** `FindObjectsOfType`（全仓 0 命中）：挑战就是关卡实例，类的身份来自**实例 → 调色板 prefab → `m_Script`**，
  不是 override 文本（`WaypointChallenge` 字面量在关卡字节里 0 命中）。
- `BaseGameMode.cs:96-99` `levelManager.Challenges = Challenge.Challenges;` —— **不过滤类型**；`LevelComplete`
  只消费前两个（`m_challenges[0]` = 星 2、`m_challenges[1]` = 星 3）。
- 挑战类型枚举（`Challenge.cs:7-15`）：`DontUseParts=0 / Time=1 / PerfectFlight=2 / Transport=3 / Box=4 / Max=5`；
  `NoChallenge`（`IsCompleted()=>true`）在 277 关里 **0 实例**、0 个 prefab 携带 ⇒ 不搬。

### 2.2 星级

```csharp
// LevelComplete.cs:259-267 —— 先清星；两个挑战里「只有第二个完成」时**换位**（只影响图标顺序）
m_starOne.SetActive(value: false); m_starTwo.SetActive(value: false); m_starThree.SetActive(value: false);
if (m_challenges.Count >= 2 && m_challenges[1].IsCompleted() && !m_challenges[0].IsCompleted())
{ Challenge value = m_challenges[0]; m_challenges[0] = m_challenges[1]; m_challenges[1] = value; }
```
```csharp
// LevelComplete.cs:287-294 / 308-346 —— 本次完成态与总星数（num9 = 1 通关 + 挑战达成的并集）
bool flag7 = m_challenges.Count >= 1 && m_challenges[0].IsCompleted();
bool flag8 = m_challenges.Count >= 2 && m_challenges[1].IsCompleted();
int num9 = 0;
if (flag) { num9++; } if (flag2) { num9++; } if (flag3) { num9++; }
if (!flag) { num9++; } if (flag7 && !flag2) { num9++; } if (flag8 && !flag3) { num9++; }
```
- **星 1 = 通关**，与挑战无关：`GoalBox.OnGoalEnter` → `LevelManager.NotifyGoalReached` →
  `GameMode.NotifyGoalReached`（`GameMode.cs:377-382`）写 `levelManager.CompletionTime = levelManager.TimeElapsed`、
  `HasCompleted = true`、切 `GameState.Completed`。
- **星 2 = `m_challenges[0]`、星 3 = `m_challenges[1]`**（`LevelComplete.cs:422/460/519/557`）。
  换位只改「哪个挑战占哪个槽」，**不改星数**（星数 = 达成数）⇒ 本片按「星数 = 1 + 前两个挑战的达成数」。
- 写盘（本片不做，见 §8）：`<scene>_stars = num9`（`LevelComplete.cs:612`，`GameProgress.cs:853-866`）、
  `<scene>_challenge_<N> = 0/1/2`（`GameProgress.cs:218-233`）、`<scene>_completed = 1`（`GameProgress.cs:177-180`）。

### 2.3 计时器

```csharp
// BaseGameMode.cs:325-328 —— 进入 Running 时记起点、清零
levelManager.TimeElapsed = 0f; levelManager.PartsInGoal.Clear(); levelManager.TimeStarted = false;
levelManager.PigStartPosition = base.ContraptionProto.FindPig().transform.position;
```
```csharp
// LevelManager.cs:1431-1441 —— 只在 Running 里跑；猪离起点 ≥ 1 米才开始累加
private void UpdateRunning()
{
	if (m_timeStarted) { TimeElapsed += Time.deltaTime; }
	else if (Vector3.Distance(ContraptionRunning.FindPig().transform.position, PigStartPosition) >= 1f) { m_timeStarted = true; }
}
```
- **没有停表**：进终点区那一刻 `CompletionTime = TimeElapsed` 就是冻结值（`GameMode.cs:379`），此后房间已不在
  Running ⇒ `UpdateRunning` 不再被调用。
- `TimeLimit` = 所有 `TimeChallenge.TimeLimit()`（= `m_targetTime`）的**最大值**，**0 表示本关没有计时**
  （`BaseGameMode.cs:101-116`）：HUD 倒计时隐藏（`ScoreCounters.cs:51-55`）。
- **超时不判负**：`TimeLimit`/`TimeElapsed` 的消费者只有 HUD（`ScoreCounters.cs:67-91` 的 `D2.D2` 格式，
  负数变红加号）、加时按钮、成就、竞赛计时与 `TimeChallenge` 判据本身；全仓 `LevelFail|OutOfTime|GameOver` 0 命中。
- 加时（看广告 +2 s，`LevelManager.cs:1687-1702` + `RewardVideoButton.cs:46`）本片不做（§8 D8）。

### 2.4 五个子类的判据（逐行）

| 类 | 判据 | 出处 |
|---|---|---|
| `DontUsePartChallenge` | `!ContraptionProto.HasPart(m_partType)` —— **建造布局**里没有该 PartType（`Contraption.cs:491-501` 遍历串里的件） | `DontUsePartChallenge.cs:7-9` |
| `TimeChallenge` | `Mathf.Floor(CompletionTime * 100f) / 100f <= m_targetTime`（**先向下去到 0.01 s**） | `TimeChallenge.cs:9-12` |
| `PerfectFlightChallenge` | 运行中且 `!ContraptionRunning.IsBroken()`；`m_broken` 只在 `UpdateJointMap()` 里置 true（关节断开后那一帧），只有 `StartContraption()` 清零 | `PerfectFlightChallenge.cs:5-12`；`Contraption.cs:396-399/528-531/2430-2432/2810-2812` |
| `TransportChallenge` | `IsPartTransported(partToTransport)` = 该件与猪连通，**或**与猪同连通分量且距离 < 2.5 m | `TransportChallenge.cs:7-10`；`LevelManager.cs:1231-1254` |
| `WaypointChallenge`（挑战箱） | `m_target.collected`（`Collectable.OnGoalEnter` 置位，且 `gameState != Completed` 才计） | `WaypointChallenge.cs:3-10`；`Collectable.cs:68-81` |

### 2.5 结算页

`InGameGUI.cs:131-133` 只做路由（`GameState.Completed` → `levelCompleteMenuGo`），文案全在预制件
`Assets/GameObject/InGameLevelCompleteMenu.prefab`（`StarPanel` 三颗星 + `ObjectiveSlot1..3` + 空 `TextMesh`
`ObjectiveText`）。**预制件里没有任何本地化键、也不写目标文字**（只有图标），代码里也没有写 `ObjectiveText`
的语句 ⇒ 原版结算页只显示图标与星，不显示目标描述（§8 D5）。

## 3. 内容契约 v10

在 v9 之上加一个**必填**（可为空数组）的顶层属性 `challenges`：

```json
"challenges": [
  { "name": "BoxChallenge", "kind": "box", "propIndex": 5 },
  { "name": "TimeChallenge", "kind": "time", "targetTime": 4 }
]
```

| 字段 | 类型 | 含义 |
|---|---|---|
| `name` | string，必填 | 关卡文件里的实例 label（原版排序键；`LevelLoader.cs:127` 把它写成 `gameObject.name`） |
| `kind` | 枚举，必填 | `"dontUseParts"` / `"time"` / `"perfectFlight"` / `"transport"` / `"box"`（= `ChallengeType` 的五个值） |
| `propIndex` | 非负整数，仅 `box` | 该挑战箱在**本文档 `props` 数组**里的下标（收集段用的就是它） |
| `targetTime` | 有限数，仅 `time` | 关卡自己的 `m_targetTime`（秒） |
| `partTypeId` | 正整数，仅 `dontUseParts` / `transport` | PigForge **基座** family id（`tools/lib/part-types.mjs` 从 `BasePart.PartType` 枚举解析） |

规则：

- **元素顺序 = 原版的编号顺序**（按 `name` 排序后的 `m_challengeNumber` 1..N），所以 `challenges[0]` = 星 2、
  `challenges[1]` = 星 3；**第 3 个（只有一关有）不参与星级**，与原版一致。
- **v10 必填**（可以为 `[]`：14 关本来就没有挑战）；**v1–v9 出现即硬错误**（与 v5 `cameraLimits`、v6 `props` 同一套门控）。
- `name` 允许**重复**（3 关有同名实例，§7）；未知键、非法 `kind`、`box` 缺 `propIndex`、`propIndex` 越界、
  `time` 缺/负 `targetTime`、`dontUseParts`/`transport` 缺 `partTypeId` 一律拒。
- `propIndex` 必须指向本文档里存在的 `props` 元素（挑战箱是画得出来的实心件之一）。

解析后的文档结构：`LevelContentDocument.Challenges`（`IReadOnlyList<LevelChallengeDefinition>`，
`LevelChallengeDefinition(string Name, LevelChallengeKind Kind, int PropIndex, float TargetTime, uint PartTypeId)`；
`PropIndex = -1` / `TargetTime = 0` / `PartTypeId = 0` 表示该 kind 不用它）。

## 4. 规则层

### 4.1 计时（`GameplayRules`）

- `GameplayConfig` 不需要新字段：计时对每一关都成立（原版 `PigStartPosition` 无条件记录）。
- **开局**：房间在 `Start()` 装配完之后调 `rules.BeginRun()`（本片新增；= 原版 `BaseGameMode.cs:325-328` 的
  Running 入口），记录**猪自己的 transform 位置**（`ResolvePartFrame`，与出界判定同一份数据）为起点、
  `ElapsedSeconds = 0`、`TimerStarted = false`。
- **每 tick（Playing）**：`!TimerStarted` 且 `距离(猪自己的位置, 起点) >= 1` → `TimerStarted = true`；
  已启动则 `ElapsedSeconds += timeStep`。
- **冻结**：`Phase` 一旦不再是 `Playing`（进终点区 → `Won`）就不再加；房间把它当 `CompletionTime` 用。
- 读数：`public float ElapsedSeconds { get; }`；`Retry`/清空回到 `Building` 时归零（复用既有的规则重置路径）。

### 4.2 挑战判定（`GameRoom`，进终点区那一刻一次）

挑战的判据需要房间自己的东西（件表、串的连通分量、已收集的挑战箱、断开记录），所以**判定在房间**做，
在原版同一个位置：`Phase` 刚从 `Playing` 变 `Won` 的那个 tick（`GameplayRules.CheckObjectives` 已经保证
「进终点区 ⇒ Won」）。

| kind | 判据 | 依据 |
|---|---|---|
| `dontUseParts` | 房间里**没有任何**件（含关卡预置实例）的 family（`VariantOf ?? PartTypeId`）等于 `partTypeId` | `DontUsePartChallenge.cs:7-9` |
| `time` | `floor(ElapsedSeconds * 100) / 100 <= targetTime` | `TimeChallenge.cs:9-12` |
| `perfectFlight` | 本局**没有发生过断开**（不是 `_contraptionBroke`：缝拆、焊点断、弹簧断、脱离命令都置位） | `PerfectFlightChallenge.cs:5-12`（`m_broken` 由 `UpdateJointMap` 置位）+ `Contraption.cs:2810-2812` |
| `transport` | 房间里存在该 family 的件，且它与**猪在同一个连通分量**（`rules.AreConnected(pig, part)`，PigForge 的 joint graph = 原版 `m_jointMap`） | `TransportChallenge.cs:7-10` + `LevelManager.cs:1233-1250` |
| `box` | `propIndex` 在 `_collectedProps` 里（P5-b 的收集，`level-challenges.md` §4） | `WaypointChallenge.cs:9` |

- 结果落在 `_challengeFlags`（`byte`，bit i = `challenges[i]` 达成）+ `_starCount`（= 1 + 前两位里达成的个数，
  仅当 `Phase == Won`；未通关时 `0`）。
- `_contraptionBroke` 由房间自己在每个断开点置位：`BreakWeldsFromAppliedCommands`（缝/焊点）、
  `HandleBrokenSprings`/`BreakSpringsFromPull`（弹簧）、`_rules` 的 `DetachedEntities`（脱离）、
  拳套打断邻件关节（若该路径存在则同样置位）。
- `Retry` / 回到建造态：`_challengeFlags = 0`、`_starCount = 0`、`_contraptionBroke = false`、计时归零
  （与原版切 `Building` 后 `OnReset` 清收集、下一次 Running 重置计时一致）。
- 新增公开读数（供测试/探针）：`ChallengeFlags`、`StarCount`、`ElapsedSeconds`。

## 5. 状态上线：PGFS **v8**

尾部在 v7 的 `collected` 段**之后**追加固定两段（**总是**写，运行/建造/沙盒三个发布路径都写，与 `collected` 同规矩）：

```
float32  elapsedSeconds       // 本局的 TimeElapsed（进终点区后冻结）
uint8    challengeFlags       // bit i = challenges[i] 达成；未通关一律 0
```

- 追加 **5 字节**：`SnapshotFrame.ElapsedByteCount = 4`、`SnapshotFrame.ChallengeFlagsByteCount = 1`；
  `GetMaxByteCount(entityCount, collectedCount)` 相应加常量；`TryWriteRunState`/`TryReadRunState`（span 版，无分配）。
- **`stars` 不上线**：客户端由 `phase == Won` 与 `challengeFlags` 的前两位算出（1 + 达成数），与房间同一个公式。
- 客户端解码：`decodeSnapshot` 在 `collected` 之后读这 5 字节（v8 才读；老版本读到尾部即空），
  `playSocket`/`snapshotSocket` 透传，`App.vue` 用它画倒计时/星/结算。
- 回放/`DemoSnapshotHost` 共用同一编解码，回放 schema 不变（回放记的是命令与快照字节）。

## 6. 客户端

- 校验器 `clients/web/src/schema/levelContent.ts`：v10 的 `challenges`（§3 逐条），版本门控与 `build` 同一套；
  另给出 `timeLimit(challenges)`（= 所有 `time` 挑战 `targetTime` 的最大值，原版 `BaseGameMode.cs:106-113`）。
- HUD（`App.vue`，只在关卡房间有意义）：
  - **倒计时**：有 `time` 挑战时显示 `D2.D2` 格式的剩余时间（`TimeLimit - elapsedSeconds`；负数显示 `+` 与红色，
    照抄 `ScoreCounters.cs:67-91`）；没有计时挑战则不显示时钟（`ScoreCounters.cs:51-55`）。
  - **三颗星**：星 1 = `phase == Won`；星 2/3 = `challengeFlags` 的前两位（未通关时暗）。
  - **结算文案**：`phase == Won` → 「过关 · N 星」+ 三个目标各自的达成标记与用时；`phase == Failed` → 「失败」；
    运行中显示「运行中」。目标描述用客户端文案（原版只有图标，§8 D5）：箱 =「收集挑战箱」、时 =「在 N 秒内完成」、
    禁件 =「不使用 <件名>」、完美飞行 =「不损坏载具」、运输 =「运送 <件名>」（件名取自 `builder/playParts.generated.json`，
    查不到就打 id）。
- 沙盒房间（`challenges` 为空、`ObjectivesEnabled = false`、`phase` 恒为 Playing）不显示星与结算。

## 7. 普查（2026-10-11，pristine `BPLE 2022.1.9` + 本仓库工具）

| 项 | 值 |
|---|---|
| 挑战实例 | **527** 个，落在 **263** 关 |
| 每关实例数 | 0 个 **14** 关（10 个 `Level_Sandbox_*` + 3 个 `Episode_6_* Sandbox` + `MMSandbox`）、2 个 **262** 关、3 个 **1** 关（`episode_6_level_25`） |
| `WaypointChallenge`（挑战箱） | **281**（`BoxChallenge` 269 / 217 关、`DynamicBoxChallenge` 12 / 11 关） |
| `TimeChallenge` | **113**（105 关）；112 个实例在关卡 override 里写了 `m_targetTime`（33 个不同值：3…80），只有 `episode_6_level_V` 用 prefab 默认 **30** |
| `DontUsePartChallenge` | **79**（77 关）；79/79 都在 override 里写 `m_partType`（31 个不同枚举值） |
| `PerfectFlightChallenge` | **44**（44 关，无字段） |
| `TransportChallenge` | **10**（10 关，`partToTransport` 全在 prefab = enum 32 `KingPig`） |
| 参数出处 | `time`/`dontUseParts` 的逐关参数**只在 `PrefabOverrides`**；`transport`/`perfectFlight`/`box` 只由 prefab 决定 |
| 重名实例（排序无法唯一定序） | 3 关：`episode_6_level_25`（两个 `DontUsePartChallenge`）、`episode_6_level_32`、`episode_6_level_IX`（各两个 `BoxChallenge`） |

## 8. 偏差（有意，不做就是不做）

| # | 偏差 | 理由 |
|---|---|---|
| D1 | 排序用**序数**比较（原版是文化敏感的 `string.Compare`），重名实例按关卡文件的出现顺序 | 要确定性；原版对同名键的 `List.Sort` 本就不稳定（§7 的 3 关无数据依据） |
| D2 | 计时按 **60 Hz tick** 累加（`ticks * 1/60`），原版是 `Time.deltaTime`（50 Hz） | 既有口径（`docs/specs/original-level-pack.md` 的 60/50 Hz 偏差） |
| D3 | 不写存档（`_stars`/`_challenge_N`/`_completed`/最佳成绩） | 进度持久化的「存哪」未拍板（A3）；本片只到「房间知道这局几星」 |
| D4 | 没有「历史 ∪ 本次」的合并（原版 `num9` 取并集） | 无存档即无历史 |
| D5 | 结算的目标文字由客户端生成（原版只显示图标） | 图标（`m_icons[].icon` 的 6 组 guid）不解析、不含在内容里 |
| D6 | 挑战换位（`LevelComplete.cs:262-267`）只影响图标顺序 | 星数 = 达成数，换位不改星数；客户端按「已完成者排前」显示 |
| D7 | `PerfectFlight` 用「本局是否发生过任何断开」近似 `m_broken` | PigForge 的断开点是一组方法（缝/焊/弹簧/脱离），原版是 `UpdateJointMap` 一个入口 |
| D8 | 看广告加时（+2 s）不做 | 无广告层；`AddToTimeLimit` 的实现（`LevelManager.cs:1687-1702`）已在真值里记下 |
| D9 | `Transport` 只认「与猪同一个连通分量」，不做 2.5 m 邻居兜底 | 与 `level-challenges.md` 偏差 5 同一条 |
| D10 | `DontUseParts` 的房间口径 = 房间里所有件（含关卡预置的 20 个实例） | 原版读 `ContraptionProto`（串里的件）；那 20 个实例是否进串未考证（与 `level-build-limits.md` D7 同源） |
| D11 | 终点区仍只认**猪**：`PlayerHasRequiredObjects` 的「本关发放了 Egg/Pumpkin 就要它也在终点区」不做 | 属过关判定（`G77` 邻域），记为新差距 **`G114`**（`EggRequired = 关卡的 `m_partTypeCounts` 里 Egg 的发放数 > 0`，`LevelManager.cs:413-431`） |

## 9. 验收

```powershell
# 内容（幂等：第二次 0 changed）
node tools/bple-levels/build-levels.mjs
#   -> 277 levels, 527 challenge instances / 263 levels, 281 box / 113 time / 79 dontUseParts
#      / 44 perfectFlight / 10 transport；14 关 challenges: []

# .NET（Jolt 项目分开跑）
dotnet build PigForge.slnx -c Release          # 0 警告 0 错误
dotnet test tests/PigForge.Core.Tests/PigForge.Core.Tests.csproj     # 计时 + 解析
dotnet test tests/PigForge.Server.Tests/PigForge.Server.Tests.csproj # 房间出星
dotnet test tests/PigForge.Protocol.Tests/PigForge.Protocol.Tests.csproj  # PGFS v8 往返

# 客户端
cd clients/web && pnpm test && pnpm build

# 真机：把关卡自己的挑战真的跑一遍
dotnet run --project src/PigForge.Server -c Release -- --play --level original/episode_1_levels/Level_05.json
curl http://127.0.0.1:5088/level    # v10 带 challenges：BoxChallenge(box) + TimeChallenge(time, 4)
```

实机读数（2026-10-11，真服务器 `--play --level` + 真内容 + 真 Bepu，用**客户端自己的编解码器**在 Node 里直连 WS）：

- `/level`（真关卡 `original/episode_1_levels/test28.json`）返回 **v10**，带
  `challenges = [BoxChallenge(box, propIndex 3), TimeChallenge(time, 7)]` 与 `build.origin = (-26.980446, 7.1228085)`、
  `rows = [31,31,0,0]`、4 条配额。
- **「把猪放进终点区」在真关卡里做不到**：终点区在 x ≈ −25，而建造网格的列是 x ∈ [−29, −25]（锚点 `floor(-26.98) = -27`，
  `xMin = -2`…`xMax = 2`）—— 往终点区或挑战箱所在位置放置会被 `OutsideConstructionGrid`（error 18）拒；
  往网格外的格子放（cell (-24,7)）同样拒。这是 v9 那条规则在真内容上的实证，也是本片 smoke 必须换思路的原因。
- 配额同样在真内容上生效：同一房间再放一只猪 ⇒ `PartAllowanceExceeded`（error 17；猪的配额是 1）。
- **跑表**：在网格内放猪（cell (−25, 7)，世界 (−24.5, 7.5)）⇒ `Start` 被接受 ⇒ `elapsedSeconds` 一路升到 **12.2 s**，
  猪随后离开关卡自己的相机矩形 ⇒ 房间按 `ADR-036` 回到建造态（帧 `phase` = 0x10），跑表归零 —— 时钟、冻结与回建造态三件事一次读数。
- **关卡房间里的零件开关是关的**（本轮新发现，见差距 `G115`）：向一个已放置、且内容声明了 `activation` 的风箱发
  `SetPartActive`（kind 8）⇒ **status 5 / error 15**（拒绝）。
- 星级（`challengeFlags` 非零）在真关卡里要**真的把车开进终点区**，所以整条星级的端到端证据来自服务端房间测试
  （`LevelStarRoomTests`，真 Bepu + 合成 v10 文档）：通关出 1 星、时间挑战成/败、`dontUseParts`/`transport` 双向、
  箱子+终点区同 tick ⇒ 3 星、`Retry` 收星、载具被撕开 ⇒ `perfectFlight` 丢星。驾驶 smoke 不在本片（`玩家试玩` 是文档里的第一优先）。
