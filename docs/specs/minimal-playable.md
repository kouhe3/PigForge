# Spec: 最小能玩（本机斜坡关）

> 状态：Draft。消费 `docs/intent/minimal-playable.md`。收窄 `web-client-spec.md` M3：本切片不做预测回滚、Naive UI、keep。
> 权威契约仍是协议 v2、`SnapshotWire`、ADR-001/002；本文件只补「能玩」缺口。

## Capability Map

| Module id | Responsibility | Depends on |
|---|---|---|
| slope-level | 斜坡关卡文档、静态坡、坡底目标区、零件子集 | — |
| command-uplink | 本机 WS 命令上行、回执、建造期快照、Start 后 60 Hz tick | slope-level |
| web-builder | 零件面板、自由放置/旋转/缩放手势、Start、胜负 HUD | command-uplink |

Build order: `slope-level` → `command-uplink` → `web-builder`

## Assumptions（写进规格，实现不得另猜）

1. 单人 `PlayerId = 1`，无账号。本机 `127.0.0.1` only。
2. 同一 WebSocket：下行 PGFS 快照，上行 PGFC 命令，下行 PGFA 回执。
3. `--demo-ws` 保持自动 Start 演示；本切片新增 `--play`。
4. 建造期没有物理 body 时，快照仍枚举施工布局（`physicsBodyId = 0`，速度全 0）。
5. 关卡 spawn 增加可选 `angle`（绕 Z，弧度，默认 0）；`schemaVersion` 仍为 1。
6. 第一局不要求轮子/发动机；猪靠重力沿坡下滑即可过关。
7. 超时 1200 tick（60 Hz × 20 s）→ `GameplayPhase.Failed`，不自动重开、不 keep。
8. 不引入 Naive UI / 贴图。

## Objective

本机 `PigForge.Server --play` + `clients/web` `pnpm dev`，进入固定斜坡关：

- 坡顶是建造起点，坡底是目标区。
- 玩家从面板拖零件，**自由角与缩放**（协议 v2 `PlacePart`），点 Start。
- 权威只在服务器。Web 只发建造/Start，不发位姿/接触/断裂/胜负。
- 猪进入目标区 → 可见胜利；出界或超时 → 可见失败。一次通关即成功。

用户：仓库维护者本人。

## Tech Stack

与现仓库一致：

- 服务端 `net10.0`，Bepu 房间，`GameRoom.Submit` / `Tick` / `TryPublishSnapshot`
- 协议 v2：`PlacePartCommand(Tick=0, Sequence, PlayerId, PartTypeId, PositionX, PositionY, Angle, Scale)`，`Scale ∈ (0, 4]`
- Web：Vue 3 + Canvas 2D + Pinia，`clients/web/`，不引用 .NET 程序集
- 内容：`schemas/level-content-v1.schema.json`、`schemas/part-content-v1.schema.json`

## Commands

```powershell
dotnet test PigForge.slnx
dotnet build PigForge.slnx -c Release
dotnet run --project src/PigForge.Server/PigForge.Server.csproj -c Release -- --play
# listen: http://127.0.0.1:5088/  path /play
```

```powershell
cd clients/web
pnpm test
pnpm build
pnpm dev
# 连接 ws://127.0.0.1:5088/play
```

`--demo-ws` 不得改语义（自动 Start + 只收快照）。

## Project Structure

```text
content/levels/slope-v1.json     # 斜坡关
content/parts.json               # 增加 pig + ramp-plank（静态长板）
schemas/client-command-v1.schema.json  # PGFC 字段文档（JSON 描述二进制布局）
src/PigForge.Protocol/CommandWire.cs   # PGFC / PGFA
src/PigForge.Server/PlayHost.cs        # --play
clients/web/src/live/commandSocket.ts  # 上行
clients/web/src/builder/               # 面板与手势→命令
docs/specs/minimal-playable.md         # 本文件
```

回放查看器路径保留，与建造模式用同一 renderer。

## 关卡几何（slope-level）

世界：x 右、y 上、z = 0。重力 `(0, -9.81, 0)`。

| 元素 | 值 |
|---|---|
| 坡板 `partTypeId` | 新静态件 `ramp-plank`：box halfExtents `[6, 0.25, 1]`，mass 0 |
| 坡中心 | `(0, 2, 0)` |
| 坡角 | `-0.35` rad（左高右低） |
| 目标区 | `min [4.5, -0.5, -2]` `max [7.5, 2.5, 2]`（坡底） |
| 地图边界 | `min [-20, -8, -8]` `max [20, 20, 8]` |
| 超时 | 1200 tick |
| 关卡 spawn | 仅静态坡板（带 `angle`）。**不**预置猪。 |

建造提示区（非碰撞）：坡顶附近 `(-5, 5)`。猪必须由玩家 `PlacePart`（`role` 在内容层：`pig` 零件 `partTypeId` 新分配，运行时 `AddPig`：面板放置猪走与 `PlacePart` 相同命令，服务器按 `partTypeId` 映射角色）。

零件面板子集：

| partTypeId | name | 放置后角色 |
|---|---|---|
| 现有 1 | wooden-block | part |
| 新 | pig | pig（不可摧毁货物） |

禁止面板放置静态地面/坡板。TNT/轮/发动机本切片不做。

> 2026-09-09 更新：`ball-weight`（3）是 PigForge 自制件（原版无对应件、无贴图），已在变体目录切片后删除；配重改用原版沙袋 21/22/23。

`level-content` spawn 增补：

```json
"angle": { "type": "number" }
```

缺省 0；非有限值启动拒绝。`GameRoom.Spawn` 把 angle 写成绕 Z 的四元数。

## 命令与快照（command-uplink）

### 房间生命周期

1. `--play` 加载 `parts.json` + `slope-v1.json`，`SetupFromLevel`，停在 `RoomMode.Building`。**不**调用 `Start`。
2. 建造期：不 `Tick` 物理。连接后与每次已接受的 Place/Rotate/Remove 后发布一帧 PGFS（施工布局）。
3. 接受 `StartSimulationCommand`（Tick 0）后 `Start()`，然后 60 Hz `Tick` + 发布快照。
4. `GameplayPhase.Won` / `Failed` 后停止推进物理，仍重复发布最后一帧，直到进程退出。
5. `EnterBuildMode` / keep：**拒绝**（WrongMode 或本宿主不解码该 kind）。

### PGFC（客户端 → 服务器）

Little-endian。Magic `PGFC` (`50 47 46 43`)。

```text
magic:4 | version:u16=2 | kind:u8 | sequence:u32 | playerId:u32 | tick:u32 | payload
```

| kind | 名 | payload |
|---|---|---|
| 0 | PlacePart | partTypeId:u32, x:f32, y:f32, angle:f32, scale:f32 |
| 1 | RemovePart | entityId:u32 |
| 2 | RotatePart | entityId:u32, angle:f32 |
| 3 | StartSimulation | （空） |

建造命令 `tick` 必须为 0。`sequence` 从 1 递增。未知 magic/version/kind、非有限数、scale∉(0,4] → 回执拒绝，房间状态不变。

文本帧、JSON、带位姿的自定义消息：关闭或忽略，不得当权威。

### PGFA（回执）

Magic `PGFA`。

```text
magic:4 | version:u16=2 | sequence:u32 | status:u8 | constructionError:u8 | entityId:u32
```

`entityId`：成功 Place 为新实体，否则 0。`status` 对齐 `CommandStatus`。热路径无 JSON。

### 建造期 PGFS

现有 15B 头 + 68B/实体不变。Building：

- `tick = 0`
- `phase`：复用头里的 byte。约定：`0x10 = Building`；Running 仍写 `GameplayPhase`（Playing=0, Won=1, Failed=2）。Web 用 `0x10` 开建造 UI。
- 实体来自施工 `EntityTransform` + `PartLink`；`physicsBodyId = 0`；速度 0。
- 关卡静态坡板也在列表里。

Running 与今日 `TryPublishSnapshot` 相同。客户端不得把 `physicsBodyId` 或速度写回服务器。

## Web 建造（web-builder）

### 交互

- 左侧零件面板：点选当前零件。
- 画布点击（非拖相机）：对当前零件发 `PlacePart`（世界 x/y，当前角与缩放）。
- 滚轮：相机缩放（已有）。`Alt+滚轮`：当前缩放 ∈ (0.25, 4]，显示数字。
- `Q` / `E`：当前角 ±15°（内部仍是弧度）。
- 选中已放零件：`R` 发 `RotatePart`，`Delete` 发 `RemovePart`。
- **Start** 按钮：仅 Building 可用；发送 `StartSimulation`。
- 相机拖拽仍走 `gesture` 模块，不发命令。

### 显示

- renderer 仍是唯一碰 Canvas 2D 的模块；输入 = 当前快照实体 + 相机。
- HUD：模式（建造/运行）、tick、缩放、角度、胜负文案（「过关」/「失败：出界或超时」）。
- 目标区画半透明矩形（客户端本地用关卡 JSON，不经服务器权威）。
- 回执失败：错误列表，不移动本地权威状态（没有本地权威）。

### 禁止

- 发送位置/四元数/速度/断裂/胜负。
- 本地预测与回滚。
- keep / 再建造。
- 连接非回环地址（默认 URL 写死 `ws://127.0.0.1:5088/play`）。

## Code Style

命令解码只走 span，与 `SnapshotFrame` 同形：

```csharp
public static class CommandFrame
{
    public const uint Magic = 0x43464750; // 'PGFC' little-endian
    public const ushort Version = 2;

    public static bool TryDecode(ReadOnlySpan<byte> source, out ReplayCommand command, out string error);
}
```

Web：`gesture` 只产出判别联合（`PlaceRequested` / `RotateRequested` / …），由 `commandSocket` 编码。禁止把指针事件推进 Pinia。

## Testing Strategy

### slope-level

- 解析 `slope-v1.json`：angle 非有限、倒置目标区、缺坡板 → 启动拒绝。
- `SetupFromLevel` 后施工哈希稳定；坡板 transform.rotation 非 Identity。

### command-uplink

- PGFC 往返；坏 magic/version/截断拒绝。
- Building 接受 Place+Start；Running 再 Place → WrongMode。
- `--play` 房间在 Start 前 `Tick()` 抛错或宿主根本不 Tick。
- 脚本：坡顶放置 pig（可加 block），Start，有限 tick 内 `Won`；双跑哈希一致。
- 出界或 1200 tick → `Failed`。
- 快照字节不含 JSON；建造帧含坡板与已放零件。

### web-builder

- vitest：编码器与 C# 夹具字节一致；`decodeSnapshot` 识别 `phase 0x10`。
- 手势序列 → 命令 kind 序列（不连真服务器）。
- `pnpm test` && `pnpm build`。
- 手验（Success Criteria）：本机通关一次。

## Boundaries

**Always**

- 权威在 `GameRoom`。固定 60 Hz。UTF-8 LF。
- 内容与命令启动前校验。
- 新 wire 有往返与拒绝测试。
- 客户端只显示快照 + 提交协议命令。

**Ask first**

- 改 PGFS 头布局或 magic。
- 改斜坡几何/目标区导致脚本夹具失败。
- 把 `--play` 绑到非回环或加第二玩家。
- 超时 tick 数。
- 给猪自动预置在坡顶（会取消「必须手摆」）。

**Never**

- Core 引用 Unity 或 Web。
- 客户端提交位姿/断裂/胜负。
- 本切片实现 keep / EnterBuildMode。
- 建造热路径 JSON。
- 静默忽略缺失零件或坏 angle。
- 改 `--demo-ws` 使其变成可玩房间。

## Success Criteria

1. `dotnet test PigForge.slnx` 与 `pnpm test` / `pnpm build` 绿。
2. `dotnet run --project src/PigForge.Server -- --play` 监听 `ws://127.0.0.1:5088/play`，房间停在 Building，快照含斜坡。
3. Web 连接后可斜着摆、缩放、删除；Start 后实体按权威 tick 运动。
4. 手摆猪（可加方块）从坡顶到坡底目标区 → HUD 过关。
5. 猪出界或 1200 tick 未进区 → HUD 失败。
6. DevTools/抓包：WS 客户端→服务器仅 PGFC；无位姿上传。
7. `--demo-ws` 行为与本切片前相同。

## Open Questions

- 无。访谈已锁定：本机、手摆、不要 keep、斜坡顶→底。超时 1200 tick 为规格默认；要改走 Ask first。

## References

- `docs/intent/minimal-playable.md`
- `docs/specs/web-client-spec.md`（M3 被本文件收窄）
- `docs/specs/pigforge-headless-spec.md`
- `docs/decisions/ADR-002-runtime-rules-semantics.md`
- `src/PigForge.Protocol/SnapshotWire.cs`、`ReplayContracts.cs`
- `src/PigForge.Server/GameRoom.cs`（`Submit` / `Start` / `TryPublishSnapshot`）
