# Spec: 多玩家持续沙盒（本机）

> 状态：Draft。消费 `docs/intent/multiplayer-sandbox.md`。
> 本切片取代 `docs/specs/minimal-playable.md` 中「把 `--play` 加第二玩家需 Ask first」的边界：明确引入多客户端与 per-player 状态。
> 权威契约仍是协议 v2、`SnapshotWire`、ADR-001/002；本文件只补「多人沙盒」缺口。

## Capability Map

| Module id | Responsibility | Depends on |
|---|---|---|
| player-ownership | 零件归属（`ConstructionRules` owner 化）、每玩家建造状态与命令门控 | — |
| sandbox-room | 常驻运行房间、预览/实体两态、per-player RESET、混合快照 | player-ownership |
| sandbox-web | 幽灵渲染、自有实体跟踪、Start/RESET、多标签即多玩家 | sandbox-room |

Build order: `player-ownership` → `sandbox-room` → `sandbox-web`

## Assumptions（写进规格，实现不得另猜）

1. 多客户端 loopback（`127.0.0.1`）；**每连接一个 player id**，服务端单调递增分配、进程内不复用。PGFC 的 `playerId` 字段由宿主按连接覆盖，客户端固定填 0——该字段在本切片不承载身份。
2. 房间启动即 `RoomMode.Running`，世界 60 Hz 永不暂停；无 Building 阶段，快照 `phase` 恒为 `(byte)GameplayPhase.Playing = 0`。
3. 目标/胜负关闭（`GameplayConfig.ObjectivesEnabled = false`）；出界只影响该玩家自己（见「出界清理」）。
4. 玩家断开后零件留在世界；重连是一个新玩家，不回收旧零件。
5. 协议保持 v2：PGFS 15B 头 + 68B 实体、PGFC 19B 头布局不变，不新增帧类型。
6. 不引入持久化、远程监听、账号、地形编辑、竞速与计时器。
7. 每玩家零件上限沿用 `ConstructionLimits.Default`（`MaxParts: 256` / `MaxConnectionsPerPart: 6` / `MaxFootprintCells: 64`）。

## Objective

本机 `PigForge.Server --play` 启动一个**持续运行**的世界，多个浏览器客户端同时连入：

- 每个玩家有独立的建造布局；编辑期只有**预览**（`physicsBodyId = 0`，无物理体），对所有玩家可见。
- 点 Start 只把自己的布局装配成物理体；世界 tick 不因任何人 Start/RESET 而暂停或重置。
- RESET 只销毁该玩家自己的实体与布局，其他玩家的载具与布局不受影响。
- 玩家之间可以互相碰撞（同一个物理世界），但**不能**操作对方的零件。

用户：仓库维护者本人 + 本机多开的玩家（多标签/多窗口）。

## Tech Stack

与现仓库一致：

- 服务端 `net10.0`，Bepu 房间，`GameRoom.Submit` / `Tick` / `TryPublishSnapshot`
- 协议 v2（布局不变）：`PlacePartCommand(Tick, Sequence, PlayerId, PartTypeId, PositionX, PositionY, Angle, Scale)`，`Scale ∈ (0, 4]`
- Web：Vue 3 + Canvas 2D + Pinia，`clients/web/`，不引用 .NET 程序集
- 内容：`content/parts.json` + `content/levels/terrain-v1.json`（沿用；含一条 34 m 三段长坡，见「关卡几何（terrain-v1）」；目标区因 `ObjectivesEnabled = false` 被忽略）

## Commands

```powershell
dotnet test PigForge.slnx
dotnet build PigForge.slnx -c Release
dotnet run --project src/PigForge.Server/PigForge.Server.csproj -c Release -- --play
# listen: http://127.0.0.1:5088/  path /play（沙盒房间）
```

```powershell
cd clients/web
pnpm test
pnpm build
pnpm dev
# 每个浏览器标签页各开一条 ws://127.0.0.1:5088/play 即一个玩家
```

`--demo-ws` 不得改语义（自动 Start + 只收快照）。`PlayHost.CreateSlopeRoom()` / `CreateTerrainRoom()` 保留给既有测试与后续竞速切片，不再由 `--play` 使用。

## 关卡几何（terrain-v1）

世界：x 右、y 上、z = 0；重力 `(0, -9.81, 0)`；bounds `[-30, -12, -8] … [30, 30, 8]`。

| 元素 | 内容 |
|---|---|
| 地板 | `ground-slab`（part 2）×3，中心 `(-20, -3.5)`、`(0, -3.5)`、`(20, -3.5)`，连成 x ∈ [-30, 30]、顶面 y = -3 的整块地板 |
| 台阶 | `terrain-box`（part 5）×3：`(4, -2.5)`、`(7, -1.5)`、`(10, -0.5)`，最高一级顶面 y = 0.5 |
| 长坡 | `ramp-plank`（part 6，12 × 0.5 × 2）×3，同角度 `0.25` rad（≈14.3°）首尾相接：中心 `(-18.125, -1.758)`、`(-6.934, 1.1)`、`(4.257, 3.957)`；坡底在 x ≈ -24 与地板顶面齐平，坡顶 x ≈ 10、y ≈ 5.7，全长 ≈ 34 m、落差 ≈ 9.2 m |

- 三段坡板沿坡向中心距 11.55（板长 12），端面重叠 0.45：坡面连续，滚动物体过缝不卡。
- 坡底低端顶面与地板齐平（y = -3.0），从坡上到平地没有台阶。
- 台阶在长坡下方，净空 ≥ 4.4 m；零件装配测试的落点（x ∈ [-9, -7]）在长坡低端上方，净空 ≥ 0.3 m——`SandboxRoomTests` 的两条真实房间测试直接摆在这些坐标上，改坡位必须复算。
- 坡底到左边界只有 6 m 平跑段：高速下坡的载具会从左边界越界消失（与旧关卡 x = -10 的悬崖同性质）；要停住请在坡底自建挡墙。
- `goalZone`（`[12, -0.5] … [16, 2.5]`）在沙盒里被忽略（Assumption 3），保持原值；客户端 `clients/web/src/builder/slope.ts` 的 `GOAL_ZONE` / `MAP_BOUNDS` 副本继续一致。

## Project Structure

```text
src/PigForge.Core/Construction/ConstructionRules.cs  # owner 化：Place/Rotate/Remove/ResetOwned/Forget
src/PigForge.Core/Runtime/GameplayRules.cs           # GameplayConfig.ObjectivesEnabled
src/PigForge.Server/GameRoom.cs                      # SandboxMode、per-player 状态、混合快照
src/PigForge.Server/SandboxPlayers.cs                # 建议新增：每玩家状态与 materialize/reset
src/PigForge.Server/CommandValidator.cs              # per-player 门控
src/PigForge.Server/PlayHost.cs                      # 连接分配 playerId；--play → CreateSandboxRoom
clients/web/src/App.vue                              # 本地状态机、Start/RESET、own-id 跟踪
clients/web/src/schema/toDrawEntities.ts             # 保留 physicsBodyId
clients/web/src/renderer/draw.ts                     # 预览半透明渲染
docs/specs/multiplayer-sandbox.md                    # 本文件
```

## 玩家模型

### 身份

- `PlayHost` 每接受一条 `/play` 连接，`uint playerId = Interlocked.Increment(ref _nextPlayerId)`（从 1 起，进程内不复用）。
- 收到 PGFC 后**先** `command = command with { PlayerId = playerId }` 再 `room.Submit(command)`；wire 上的值被覆盖，客户端不得依赖它。
- PGFA 回执布局不变（无 playerId 字段）；客户端通过 `entityId` 跟踪自己的实体。
- 断开：连接移除，实体留在世界（owner 为该 player id）。重连=新 id。

### 状态机（每玩家独立）

```text
Editing --Start(装配成功)--> Materialized
   ^                              |
   +---------- RESET ------------+
   ^                              |
   +---- 实体集合变空（炸光）------+
```

- **Editing**：布局只存在于 `ConstructionRules` 与 `_transforms`；无 `PhysicsBodyLink`；快照里是预览（`physicsBodyId = 0`）。
- **Materialized**：布局已装配为物理体；不得再 Place/Remove/Rotate。
- 世界实体（关卡 spawn，owner 0）不属于任何玩家，任何 RESET 都不动它们。

## 命令（PGFC 语义）

Little-endian，19B 头不变。沙盒下 `tick` 字段一律忽略（客户端编辑/Start 填 0，RESET 填当前 liveTick）。

| kind | 名 | 沙盒语义 | 允许状态 |
|---|---|---|---|
| 0 | PlacePart | 加入该玩家预览布局 | Editing |
| 1 | RemovePart | 删除自己的布局零件；非自己的 → 拒绝 | Editing |
| 2 | RotatePart | 旋转自己的布局零件；非自己的 → 拒绝 | Editing |
| 3 | StartSimulation | 装配该玩家布局为物理体；布局为空 → 拒绝 | Editing |
| 5 | Retry | **RESET**：销毁该玩家全部实体与布局，回到 Editing；幂等 | 任意 |
| 4 | EnterBuildMode | wire 不可编码（现状），沙盒不使用 | — |

- 状态不符 → `CommandStatus.WrongMode`；动他人零件 → `CommandStatus.RuleRejected` + `ConstructionError.NotOwnedByPlayer`。
- 序列校验沿用 `CommandValidator` 的 per-player `(PlayerId, Sequence)` 语义（重复幂等、陈旧拒绝）。
- 命令结果照旧进 `OutcomeLog`（含被覆盖后的 `PlayerId`）。

## 快照（PGFS）

15B 头 + 68B/实体布局不变；沙盒每 tick 广播一帧**混合帧**：

- `phase = (byte)GameplayPhase.Playing`（= 0，永不为 `0x10`）。
- `entityCount` = 所有带 `PartLink` 的实体（世界实体 + 已装配实体 + 所有玩家的预览）。
- 实体按 `entityId` 升序。
- 已绑定的实体：与今日 Running 帧相同（`physicsBodyId = 真实 id`，世界位姿 + 速度，复合体成员按 `WorldPose` 展开）。
- 未绑定的实体（预览）：`physicsBodyId = 0`，位置/旋转取 `TransformStore`，速度全 0，`scale` 取 transform。
- 玩家归属**不上线**：客户端无法从帧里区分预览属于谁；自有实体由 PGFA 的 `entityId` 在本地跟踪。

## 服务器要点

### `GameRoomOptions.SandboxMode`（bool，默认 false）

沙盒模式下：

1. `SetupFromLevel`：spawn 关卡实体后立即把 owner 0 实体装配成物理体（单件成簇），并置 `Mode = RoomMode.Running`。世界从第 0 tick 起就在跑。
2. 每玩家状态存于 `Dictionary<uint, SandboxPlayer>`；`SandboxPlayer` 至少含 `bool Materialized`。
3. `Submit` 路由：沙盒命令按上表执行；旧房间路径（Building→Running、room-wide Start/Retry/EnterBuildMode）完全不变。
4. `StartSimulation`（该玩家）：取 `_construction.PlacedEntitiesOf(playerId)` 升序 → `CompoundAssembler.Assemble(...)` → `BindCluster` 每簇 → `Materialized = true`。
5. RESET（该玩家）：对 `PlacedEntitiesOf(playerId)` 升序逐个执行现有删除序列（`_construction.Remove` → `_rules.CleanupEntityStores` → `UnbindEntity(destroyBodyIfOrphan: true)`）→ `Materialized = false`。不调用 `_rules.ResetAll()`（那是全房间的）。
6. 出界清理：每 tick 末尾，若某玩家**全部**实体位置都在 `GameplayConfig.MapBounds` 外 → 对该玩家执行 RESET。预览实体不参与判定。
7. 实体在运行中被摧毁（TNT/断缝）时，除现有清理外调用 `_construction.Forget(entity)`，避免残留 footprint 卡住后续摆放。
8. `TryPublishSnapshot`：沙盒走混合帧；旧房间仍走 Building/Running 两条现有路径。

### `ConstructionRules` owner 化（Core）

- `Place(uint partTypeId, float positionX, float positionY, float angle, float scale, uint owner)`
- `Rotate(EntityId entity, float angle, uint owner)` / `Remove(EntityId entity, uint owner)`
- `ResetOwned(uint owner) → List<uint>`（按实体升序销毁，仅该 owner）
- `PlacedEntitiesOf(uint owner) → IReadOnlyCollection<uint>`
- `ComputeLayoutHash(uint owner) → long`
- `Forget(EntityId entity)`：从 footprint/连接/PartStore/TransformStore 中清除（实体可能已死，幂等）
- 新错误值 `ConstructionError.NotOwnedByPlayer`
- 每个 owner 独立计数 `MaxParts`；跨 owner 不建立连接；跨 owner 重叠仍按 `CellsOccupied` 拒绝（全房间一份占位空间）
- 旧调用点（`GameRoom` 旧房间路径、测试）显式传 owner 0，不保留无 owner 重载

### `GameplayRules`

- `GameplayConfig` 增加 `bool ObjectivesEnabled = true`；为 false 时 `CheckObjectives` 直接返回（不产生 Won/Failed，`Phase` 恒 Playing）。

### `CommandValidator`

- `Validate` 增加沙盒门控输入（是否沙盒 + 该玩家是否 Materialized），沙盒下按「命令（PGFC 语义）」表判定；旧房间矩阵不变。

## Web 客户端（sandbox-web）

- **身份**：所有发送点 `playerId: 0`（`App.vue:47/158/179/188/218` 的硬编码 1 全部改掉）。
- **幽灵渲染**：`DrawEntity` 保留 `physicsBodyId`；`bodyId === 0` 画半透明、无光晕、无标签底色的预览；`bodyId !== 0` 走现有样式。
- **自有实体跟踪**：处理 PGFA（`ack.entityId`，当前被完全忽略）——Accepted 的 Place 记入 `ownEntityIds`；RESET 成功后清空；连接/断开时清空。
- **本地状态机**：Start 被 Accepted → Materialized；RESET 被 Accepted → Editing；初始 Editing。门控改由本地状态决定，**不再**看 `phase === 0x10`。
- **工具栏**：Start（Editing 且自有布局非空）、RESET（Materialized 或自有布局非空）；移除对全局 RETRY 的依赖。
- **修复**：`App.vue:15` 的 `entitiesRef` 在 `viewState.entities` 每次快照整体替换后失效，导致运行时命中测试/选中不工作；改为每次使用时读取 `viewState.entities`。
- **不做**：重连、玩家列表/名字、按 owner 着色（需要 owner 上线）、预测。
- live 视图不再画本地 `GOAL_ZONE`（沙盒无目标语义）；回放视图不变。

## Code Style

命令与快照解码仍只走 span；per-player 逻辑用显式小类，不引入 DI：

```csharp
// 建议形状（实现可调整命名，契约是行为）
internal sealed class SandboxPlayers
{
    public bool IsMaterialized(uint playerId);
    public void MarkMaterialized(uint playerId);
    public void MarkEditing(uint playerId);
    public IReadOnlyCollection<uint> KnownPlayers { get; }
}
```

Web：`gesture` 仍只产出判别联合；`App.vue` 只做状态机与命令映射，渲染/解码不碰 DOM 事件。

## Testing Strategy

### player-ownership（Core）

- `Place/Rotate/Remove` 带 owner：错 owner → `NotOwnedByPlayer`；每 owner 上限独立（owner A 满不影响 B）。
- 跨 owner 重叠 → `CellsOccupied`；跨 owner 相邻不建立连接。
- `ResetOwned(A)` 后：A 的 footprint/连接/实体清空，B 的布局哈希与连接不变；`ComputeLayoutHash(owner)` 双跑稳定。
- `Forget`：已死实体清除后可在同格重新摆放。

### sandbox-room（Server）

- 脚本夹具（无真实 socket）：玩家 1 Place+Start、玩家 2 Place 不 Start → 单帧同时含 `bodyId !== 0`（1 的）与 `bodyId === 0`（2 的），按 entityId 升序。
- 玩家 1 RESET → 1 的实体消失、2 的实体与布局不变；双跑状态哈希一致。
- 关卡加载：`terrain-v1` 的分数坐标静态件（坡板）在 `SetupFromLevel` 里必须保持 primitive 静态体（Core 回归 `CompoundAssemblerTests.StaticPartAtFractionalPositionKeepsPrimitiveBody`；单成员簇的质心按成员自身坐标系求，不能有浮点残差）。
- 出界：构造某玩家全部实体越界 → 该玩家被 RESET，他人不受影响。
- `ObjectivesEnabled = false`：任何情况不产生 `Won`/`Failed`；`CurrentTick` 只增不减。
- per-player 序列：重复幂等、跳号拒绝、玩家间互不影响。

### Protocol

- 现有 PGFC/PGFA/PGFS 往返与拒绝测试保持绿（布局未变）；不新增帧。

### sandbox-web

- vitest：`toDrawEntities` 保留 bodyId；`bodyId === 0` 走幽灵样式；ack `entityId` 进入 `ownEntityIds`；本地状态机门控（Editing/Materialized 下按钮与命令）；编码 `playerId === 0`。
- `pnpm test` && `pnpm build`。

### 手验（Success Criteria）

两个浏览器标签连同一 `--play` 房间，按 Success Criteria 走一遍。

## Boundaries

**Always**

- 权威只在 `GameRoom`；归属校验在服务器。
- 固定 60 Hz、手动 tick；世界不因玩家操作暂停。
- 新行为有对应测试（Core/Server/Web 至少各一处）。
- UTF-8 LF。

**Ask first**

- 改 PGFS/PGFC 布局或协议版本（本切片明确不改）。
- 在 wire 上加 owner/playerId 字段。
- 引入持久化、远程监听、账号。
- 改 `--play` 回退为单机斜坡或再加模式。
- 提前做地形编辑/竞速。

**Never**

- 客户端权威、预测、回滚。
- 沙盒内按玩家隔离物理世界（那是竞速切片的语义）。
- 玩家可 Place/Remove/Rotate 他人实体，或 RESET 影响他人。
- 静默忽略坏命令或不可编码 kind。
- 热路径 JSON / 反射。

## Success Criteria

1. `dotnet test PigForge.slnx`、`pnpm test`、`pnpm build` 全绿。
2. `--play` 启动后房间即 `Running`；无任何客户端时 `CurrentTick` 也在增长。
3. 两个标签连入：A 摆放 → 两个标签都看到 A 的半透明预览（`bodyId = 0`）。
4. A 点 Start → A 的零件变为实体（`bodyId !== 0`）并在世界运动；B 能看到且能撞到。
5. B 摆放并 Start → B 的实体独立存在；A 的载具不受影响。
6. A 点 RESET → A 的实体从两个标签消失，B 的实体与预览不变；`CurrentTick` 不重置。
7. 任一客户端无法删除/旋转对方的零件（服务器回 `RuleRejected`）；抓包显示客户端只发 PGFC，无位姿上传。
8. `--demo-ws` 行为与本切片前相同。

## Open Questions

- 按玩家着色/玩家列表需要 owner 上线，本切片不做（Assumption 5）；竞速切片再评估。
- 预览对他人是否全量可见：本切片选「全量半透明可见」，若社群反馈要隐藏，另开切片。
- 玩家上限：本切片无硬上限（loopback）；若出现性能问题，另定。

## References

- `docs/intent/multiplayer-sandbox.md`
- `docs/specs/minimal-playable.md`（其「第二玩家 Ask first」边界被本文件取代）
- `docs/decisions/ADR-001-net10-physics-backend-boundary.md`、`ADR-002-runtime-rules-semantics.md`
- `src/PigForge.Server/GameRoom.cs`、`PlayHost.cs`、`CommandValidator.cs`
- `src/PigForge.Core/Construction/ConstructionRules.cs`、`CompoundAssembler.cs`
- `src/PigForge.Protocol/SnapshotWire.cs`、`CommandWire.cs`
- `clients/web/src/App.vue`、`schema/toDrawEntities.ts`、`renderer/draw.ts`
