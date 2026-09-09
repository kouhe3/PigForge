# Spec: 建造编辑工具（Advanced Building，本机）

> 状态：Draft。消费 `docs/intent/advanced-building.md`。
> 权威契约仍是协议 v2 头、`SnapshotWire`、ADR-001/002 与 `docs/specs/multiplayer-sandbox.md` 的沙盒语义；本文件只补「放置后精确变换」缺口。
> 与 `docs/specs/minimal-playable.md` 的边界差异：那里只允许 `Q/E` 定角、`Alt+滚轮` 定缩放、`R` 旋转、`Delete` 删除；本切片新增**移动/缩放已放置零件**，因此需要两条新的 PGFC 命令（用户已确认，见 intent）。

## Capability Map

| Module id | Responsibility | Depends on |
|---|---|---|
| transform-commands | PGFC kind 6/7（MovePart/ScalePart）与 Core `ConstructionRules` 变换规则 | — |
| sandbox-transform | `CommandValidator` 门控与 `GameRoom` 两条路径（沙盒 / 旧房间）接入 | transform-commands |
| web-editor-tools | 工具面板、单选、拖拽/键盘交互、本地预览、命令编码 | transform-commands |

Build order: `transform-commands` → `sandbox-transform`；`web-editor-tools` 只需 wire 契约，可与 `sandbox-transform` 并行。

## Assumptions（写进规格，实现不得另猜）

1. **单选 v1**：点击选中 / 点空取消；多选、Shift 加选、框选不在本切片。
2. 工具集固定为 **放置 / 选择 / 移动 / 旋转 / 缩放**，快捷键 `1`–`5`，默认「放置」（保持现有手感）。工具只在「建造」标签页出现。
3. 移动/旋转/缩放只对**自己且 Editing** 的零件生效；服务器仍最终校验（`NotOwnedByPlayer` / `WrongMode`）。选择工具可以选中任何零件（只读检查）。
4. 吸附：移动 0.5 m、旋转 15°、缩放 0.25；按住 `Alt` 不吸附（缩放仍钳制在 `[0.25, 4]`）。
5. 拖拽期间只画本地半透明预览（沿用 `bodyId === 0` 幽灵样式），**不连续发命令**；松手时发一条命令。服务器拒绝 → 下一帧快照恢复真实位姿，错误列表显示。松手到确认之间可能有 ≤2 帧回跳（本切片接受）。
6. 协议保持 v2：19B 头与既有 kind 0–5 布局不变，只**追加** kind 6/7。旧客户端不受影响；旧服务器对 6/7 回 `UnknownKind`。
7. 回放文档契约同步扩展（`MOVE_PART` / `SCALE_PART` 可被校验），但本切片不产出含这两种命令的回放。
8. 相机：左键拖空白仍平移、滚轮仍缩放（所有工具）；`Alt+滚轮` 仍是**放置**缩放（仅放置工具）。
9. 不引入本地权威：客户端不预测服务器结果，预览只是拖拽中的临时显示。

## Objective

`PigForge.Server --play` 的沙盒建造模式下：

- 选中自己的预览零件后，用移动/旋转/缩放工具把它调到目标位姿；两个标签都能实时看到拖动中的预览和最终确认后的位姿。
- 所有变换由服务器重算 footprint、连接与归属；非法变换（压住别的零件、超出缩放范围、动他人零件、已 Start）被拒绝且状态不变。
- 旧房间（斜坡关 Building 阶段）同样接受 Move/Scale（owner 0）。

## PGFC 线格式（v2 追加）

Little-endian，19B 头（magic/version/kind/sequence/playerId/tick）不变：

| kind | 名 | payload（头之后） | 总长 |
|---|---|---|---|
| 6 | MovePart | `entityId:u32`, `positionX:f32`, `positionY:f32` | 31 |
| 7 | ScalePart | `entityId:u32`, `scale:f32` | 27 |

解码校验（与既有命令同风格，失败即拒绝整帧）：

- `entityId != 0`
- `positionX` / `positionY` 有限
- `scale` 有限且 `0 < scale <= 4`

`ClientCommandKind` 追加 `MovePart = 6`、`ScalePart = 7`（枚举顺序即字节值）。

## Core：`ConstructionRules` 变换规则

```csharp
public ConstructionResult Move(EntityId entity, float positionX, float positionY, uint owner);
public ConstructionResult Scale(EntityId entity, float scale, uint owner);
public ConstructionResult Rotate(EntityId entity, float angle, uint owner);   // 既有签名
```

三条命令共用私有 `Retransform(entity, owner, positionX?, positionY?, angle?, scale?)`，语义 = 用「未提供的分量取当前值」组成目标位姿，然后：

1. `!IsAlive` → `EntityNotFound`；无 `PartLink` → `NotAConstructionEntity`；owner 不符 → `NotOwnedByPlayer`；冻结 → `FrozenEntity`。
2. 参数有限性：位置 → `InvalidPosition`；角度 → `InvalidRotation`；缩放 ∉ (0, 4] → `InvalidScale`。
3. 目标 footprint 的 AABB 面积 > `MaxFootprintCells` → `FootprintTooLarge`。
4. 与其它实体（排除自己，`-OverlapTolerance` 余量）重叠 → `TransformBlocked`。
5. 连接上限：目标邻接数 > `MaxConnectionsPerPart`，或任一邻居变换后的度数超限 → `ConnectionLimitReached`。
6. 通过后：`UnsetFootprint` → `SetFootprint` → 写 `TransformStore` → `Reconnect(self)` → 对**旧邻居**逐个 `Reconnect`。

规则收紧（有意，写入测试）：

- `ConstructionError.RotationBlocked` **重命名为** `TransformBlocked`（枚举位置与字节值不变，wire 无影响）；旋转撞占位从此也报这个名字。
- `Rotate` 改走 `Retransform`，因此**首次**执行面积/连接上限校验（此前只有 `Place` 校验）。

## Server 接入

### `CommandValidator`

| 场景 | MovePart / ScalePart |
|---|---|
| 沙盒 + Editing | `Accepted` |
| 沙盒 + Materialized | `WrongMode` |
| 旧房间 Building（tick 0） | `Accepted` |
| 旧房间 Running | `WrongMode` |

per-player `(PlayerId, Sequence)` 语义不变。

### `GameRoom`

- 旧房间 `ExecuteCommand`：`owner = 0`（与 Place/Rotate/Remove 一致）。
- 沙盒 `ExecuteSandboxCommand`：`owner = command.PlayerId`；成功回 `(Accepted, None, entityId)`，规则失败回 `(RuleRejected, error, 0)`。
- 预览快照的位姿来自 `TransformStore`，无需改 `TryPublishSandboxSnapshot`。

## Web 客户端（web-editor-tools）

### 模块

```text
clients/web/src/editor/tools.ts        # 新增：ToolId、标签/快捷键、吸附常量与纯函数
clients/web/src/gesture/canvasGestures.ts  # 唯一指针事件源；按工具产出语义消息
clients/web/src/schema/types.ts        # ClientCommand 6/7、GestureMessage 新成员
clients/web/src/schema/encodeCommand.ts# kind 6/7 编码
clients/web/src/live/playerSession.ts  # CommandKind 扩展（6/7 不改 ownEntityIds）
clients/web/src/viewState.ts           # 增加拖拽预览位姿（非响应式）
clients/web/src/App.vue                # 工具面板、快捷键、命令派发、预览渲染
```

### 工具与交互（唯一事实来源）

| 工具 | 按下 | 拖动 | 松手 | 滚轮 |
|---|---|---|---|---|
| 放置 | 命中零件 → 选中；空白 → 记录起点 | 相机平移 | 空白且未移动 → `PlaceRequested(x,y)` | 缩放相机；`Alt` → 放置缩放 |
| 选择 | 命中 → 选中；空白 → 清空选中 | 相机平移 | — | 缩放相机 |
| 移动 | 命中**可编辑自有**零件 → 开始移动拖拽；否则选中命中项（如有）并相机平移 | 候选位姿 = 起始位姿 + 世界位移，吸附 0.5（`Alt` 自由） | `MoveRequested(entityId, x, y)` | 缩放相机 |
| 旋转 | 命中可编辑自有零件 → 记录「指针相对零件中心的角度」与当前 yaw | 候选 yaw = 起始 yaw + 指针角差，吸附 15°（`Alt` 自由） | `RotateRequested(entityId, angle)` | 缩放相机 |
| 缩放 | 命中可编辑自有零件 → 记录「指针到中心的距离」与当前 scale | 候选 scale = 起始 scale × 距离比，吸附 0.25、钳制 `[0.25, 4]`（`Alt` 不吸附但仍钳制） | `ScaleRequested(entityId, scale)` | 缩放相机 |

- 拖拽阈值 4 px（沿用现有 `moved` 判定）；指针捕获/释放沿用现有实现。
- 拖拽中每次移动发一条 `ToolPreview`（含完整候选位姿）；松手发请求消息后发 `ToolPreview: null`。
- 旋转/缩放的起始距离 < 0.05 m 时角差/比值按 0 处理（避免除零/抖动）。

### 键盘（`window` 监听）

- `1`–`5`：切换工具。
- `Q`/`E`：放置角 ±15°（不变）；`R`：旋转选中 +15°（不变，仅可编辑自有）；`Delete`/`Backspace`：删除选中（不变）。
- 方向键：移动工具且选中可编辑 → ±0.5 世界单位发 `MoveRequested`。
- **焦点在输入框/下拉框/可编辑元素时一律不响应快捷键**（修复现有 `onKey` 在 URL 输入框里按 Delete 会删零件的隐患）。

### 状态与门控

- 工具状态放 `App.vue` 的 `ref`；`isEditable(id) = canEdit && ownEntityIds.has(id)` 传给 gesture。
- `canEdit` 变 false（Start 成功）时自动回落到「选择」工具。
- 工具面板按钮：放置/移动/旋转/缩放 在 `!canEdit` 时禁用；选择始终可用。
- `viewState.preview` 非响应式；`paint()` 在预览存在时用候选位姿替换该实体的绘制副本（`bodyId: 0` → 现有幽灵样式），渲染器本身不改签名。

### 不做

- 多选/框选/镜像/复制/撤销重做、数值输入面板、拖拽中连续发命令、客户端预测。

## Code Style

- .NET：命令记录与 wire 编解码仍用 `BinaryPrimitives`/`Span`，不引入 JSON/反射；新错误值不改变既有字节。
- Web：`editor/tools.ts` 纯函数（无 DOM/无 Vue）；`gesture` 只产出判别联合；`App.vue` 只做状态机与命令映射。

## Testing Strategy

### Core（`tests/PigForge.Core.Tests/ConstructionRulesTests.cs`）

- `Move` 成功：位置更新、footprint 迁移（原格可再摆）、连接按新位置重算（断开旧的、接上新的）。
- `Scale` 成功：scale 更新、footprint 随缩放变化、`MaxFootprintCells` 超限 → `FootprintTooLarge`。
- 失败矩阵：非 owner → `NotOwnedByPlayer`；冻结 → `FrozenEntity`；重叠 → `TransformBlocked`；`scale = 0/NaN/5` → `InvalidScale`；`NaN` 位置 → `InvalidPosition`；`NaN` 角度 → `InvalidRotation`；不存在的实体 → `EntityNotFound`。
- 变换脚本双跑 `ComputeLayoutHash` 一致（确定性）。
- 既有 `Rotate` 用例改为断言 `TransformBlocked`。

### Protocol（`tests/PigForge.Protocol.Tests/CommandWireTests.cs`）

- kind 6/7 往返保留字段；截断帧、`entityId = 0`、非有限/越界 scale 被拒。

### Server（`tests/PigForge.Server.Tests/SandboxRoomTests.cs`）

- 沙盒 Editing：Move/Scale 被接受，下一帧预览位姿/缩放更新（`bodyId = 0`）。
- Materialized：Move/Scale → `WrongMode`；动他人实体 → `RuleRejected` + `NotOwnedByPlayer`。
- 旧房间 Building：Move/Scale 接受（owner 0）。
- 门控矩阵用例覆盖两种房间路径。

### Web（vitest）

- `editor/tools.test.ts`：吸附取整、`Alt` 自由、缩放钳制、指针角度/距离比。
- `schema/encodeCommand.test.ts`：kind 6/7 字节布局。
- `gesture/canvasGestures.test.ts`：五种工具的按下/拖动/松手消息序列；拖他人零件不发变换请求（改平移相机）。
- `live/playerSession.test.ts`：kind 6/7 的 ack 不影响 `ownEntityIds` / 状态机。

### 手验（Success Criteria）

两个浏览器标签连同一 `--play` 房间走一遍。

## Boundaries

**Always**

- 权威只在 `GameRoom`；变换的合法性（归属/占位/连接/缩放范围）在服务器判定。
- 拖拽期间不发命令；松手一条命令；无位姿流。
- 新行为有测试（Core/Protocol/Server/Web 至少各一处）。
- UTF-8 LF。

**Ask first**

- 改 PGFS 布局或协议版本（本切片只追加 PGFC kind）。
- 多选/框选/镜像/复制/对称、数值输入面板、撤销重做。
- 在 wire 上加 owner/playerId 字段或位姿流。

**Never**

- 客户端权威、预测、回滚。
- 用 `Remove + Place` 模拟移动/缩放（丢 entityId 与原子性）。
- 动他人零件或让 RESET 影响他人。
- 静默忽略坏命令或不可编码 kind。
- 热路径 JSON / 反射。

## Success Criteria

1. `dotnet test PigForge.slnx`、`pnpm test`、`pnpm build` 全绿。
2. 两个标签连入：A 选中自己的预览零件，移动/旋转/缩放 → B 实时看到同样的预览位姿；A 松手后两边位姿一致。
3. A 尝试变换 B 的零件 → 服务器拒绝、错误显示、位姿不变；B 无感知。
4. A 点 Start 后（Materialized）工具不可用，拖拽只平移相机；RESET 后恢复可用。
5. 拖拽期间抓包：客户端→服务器只有松手时的一条 PGFC；无连续位姿帧。
6. 旧房间 Building 阶段 `MovePart`/`ScalePart` 可用（owner 0），`--demo-ws` 行为不变。
7. 移动后原位置可重新摆放；旋转/缩放撞到别的零件时被拒且连接不变。

## Open Questions

- 松手到服务器确认之间的 ≤2 帧回跳：本切片接受；若手感需要，再开「本地确认态」切片。
- 多选与数值面板：Besiege 的核心便利项，但需要批处理命令与非原子失败语义，另开切片。
- 吸附步长是否可配置：本切片固定常量。

## References

- `docs/intent/advanced-building.md`
- `docs/specs/multiplayer-sandbox.md`（沙盒与归属语义）、`docs/specs/minimal-playable.md`（命令/工具起点）
- `docs/decisions/ADR-001-net10-physics-backend-boundary.md`、`ADR-002-runtime-rules-semantics.md`
- `src/PigForge.Core/Construction/ConstructionRules.cs`、`src/PigForge.Protocol/CommandWire.cs`、`src/PigForge.Server/GameRoom.cs`
- `clients/web/src/gesture/canvasGestures.ts`、`clients/web/src/App.vue`
