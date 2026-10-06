# Spec: PLAY 零件开关（本机）

> 状态：已确认（2026-09-09 用户确认：PGFS v3 + flags、开关是唯一玩家触发；气球走 `trigger` 放气/摧毁、旋翼走 `toggle` 电机启停）。消费 `docs/intent/play-part-switches.md`。
> 权威契约仍是 ADR-001/002 与 `docs/specs/multiplayer-sandbox.md` 的归属/per-player 门控语义；本文件只补「零件开关」缺口。
> 取代：`multiplayer-sandbox.md` Assumption 5 与 `minimal-playable.md` 中「PGFS 15B 头 + 68B/实体不变」的条目——本切片把 PGFS 升到 **v3**（实体 69B，末尾追加 `flags:u8`）。PGFC 仍是 19B 头、版本 2，只追加 kind 8/9。

## Capability Map

| Module id | Responsibility | Depends on |
|---|---|---|
| activation-model | 内容 `capabilities.activation`、Core `ActivationStore`、GameplayRules 门控与重置重臂 | — |
| activation-wire | PGFC kind 8/9、PGFS v3 `flags`、`CommandValidator`/`GameRoom` 接入 | activation-model |
| web-gadgets | 开关条、类型热键、点零件切换、活跃渲染、快照解码 | activation-wire |

Build order: `activation-model` → `activation-wire` → `web-gadgets`。`clients/web/src/live/gadgets.ts`（纯函数）只依赖内容契约，可与 `activation-wire` 并行。

## Assumptions（写进规格，实现不得另猜）

1. **开关由内容声明**：`capabilities.activation: "toggle" | "trigger"`；缺省 = 无开关（零件按既有语义一直工作/被动生效）。
2. **两分语义 = 原版的两种控件**（`BasePart.HasOnOffToggle()`，`BasePart.cs:587`；`UIPartTriggerButtonInfo` 把它的值当成「开关 or 按钮」，`BasePart.cs:1418-1421`）：
   - `toggle`：**开关**——持续效果的档位，`Active` 一直保持到再次切换（电机、风扇、伞、齿轮箱反向、气球……）。原版这些件的 `HasOnOffToggle()` 是 `true`。
   - `trigger`：**按钮**——按下即动作，`Active` 在同一 tick 里被消费（火箭点火、气球放气、风箱一次气、抓钩/脱钩/TNT、拳套出拳）。原版这些件的 `HasOnOffToggle()` 是 `false`，且它的按钮就是 `ProcessTouch()`（`BasePart.cs:1428-1431`）——**一次性，不是档位**。
   - **按钮的按下必须被消费，即使该件自己的门控拒绝了效果**：`TryConsumeButtonPress` 在每条规则的最前面读走它（`GameplayRules.cs`），然后才判底盘/power/方向这些门。门控扣住按下会让开关条显示一个「卡在开位、再点也关不掉」的开关（用户实测报告），既不是按钮也不是开关。
   - **可重复**：按钮件按自己的节流重臂（风箱 = 原版 0.8 s + 0.3 s inflate，`Bellows.cs:64-67`；火箭/气球/TNT 烧完即止）。任何「按一次之后永远不再响应」的实现都不是按钮。
   - **物理接触不是按钮**：原版的 `ProcessTouch` 只由 UI 到达（`Contraption.OnButtonTriggered`/`ActivateOnePartOfType`/鼠标），从不由碰撞事件到达。拳套原来把 `ContactStarted` 当触摸，会「落地就出拳」，已删除（`docs/specs/boxing-glove.md` §5）。
3. **默认关闭**：沙盒 Start 后所有 `toggle` 零件 `Active = false`、`trigger` 零件未触发（`Active = false`），等玩家操作。**例外：气球的升力是被动效果**（原作即如此，Start 后就有升力），开关只负责放气摧毁；旋翼是 `toggle`（见 Assumption 2），Start 后推力关闭，开关只启停电机、不摧毁零件（`docs/specs/fan-propeller.md`）。旧关卡房间（`PlayHost.CreateSlopeRoom`/`CreateTerrainRoom`）**不种开关条目**（只有沙盒种），因此斜坡/地形与 `--demo-ws` 走本切片前的既有自动路径，行为逐字不变。
4. **一次性零件不再自动触发**（有意行为变更）：火箭不再 t0 自燃；风箱/抓钩不再触地自动触发；开关是唯一玩家触发。气球的升力是被动效果（见 Assumption 3），开关只做放气摧毁；旋翼改为 `toggle`，原文「点一下放气」的被动升力不再适用（`docs/specs/fan-propeller.md`）。ADR-002 的撞击语义不变：TNT 强撞击仍自燃、脱钩件强撞击仍分离、蛋仍会摔碎。
5. **齿轮箱修正**：只有 `Active` 的齿轮箱才让同 body 的电机反向（现状是「放了齿轮箱就永远反向」）。
6. **按钮 = 零件类型**：开关条只列该玩家自己、已 Materialized、可开关的零件类型，按 `partTypeId` 升序；按钮状态 = 该类型存在任一 `Active` 零件——但只有 `toggle` 组会因此**常亮**，`trigger` 组的按下在同一 tick 被消费（Assumption 2）。原作「发动机按钮联动所有动力零件」的特例不做。
7. **热键按 BPLE 风格、按类型**：`1`–`9`、`0`、`A`–`Z` 依按钮顺序；只在运行中（Materialized）生效；输入框焦点保护。建造期工具热键 `1`–`5` 不受影响（两个相位互斥）。
8. **点零件**：运行中点击自己可开关的零件 = 切换（同时选中）；他人或不可开关零件只选中。客户端零预测：按下后状态以下一帧快照为准。
9. **PGFS**：本切片立起 `flags`（v3），ADR-009 追加 `attachYaw`（v4），**ADR-028 追加运行期子实体标志（v5）**：实体 73B，末尾 `flags:u8`；bit0 = `Active`，bit1 = 该实体是某个件的运行期子实体（拳套的拳头、拉断弹簧的端点，ADR-027），bit2–7 保留（写入必须 0，读取忽略）。`SnapshotFrame.CurrentVersion = 5`；PGFC 与回放版本号不变。
   子实体在线上借宿主的 `partTypeId`，没有 bit1 客户端就会拿宿主的整张合成图去画它（用户实测：拳套弹出的是「另一个拳套」而不是拳头）。客户端渲染只读 `clients/web/src/renderer/atlas.ts` 的 `subEntityTexture`。
   **位只在实体真的在帧里时才出现**：机器处于「停用」态的子实体（缠绕态的拳套手套，原版 `SetActive(false)`，`SpringBoxingGlove.cs:215-222`）**根本不发布**——
   否则它会以宿主的合成图贴在宿主原点上盖住宿主（用户实测：未触发时看到的是拳套，原版是盒子）。见 `docs/specs/boxing-glove.md` §3/§4。
   **画序**：发布的子实体在客户端画在**它所属的件之下**（`renderer/draw.ts` 的 `drawOrder`；原版按 z 排序，拳头的 z 0.15 比盒子的 0.1 远），所以收回时拳头滑到盒子背后而不是凭空消失（同文件 §4.1）。
10. **归属与门控沿用沙盒规则**：只能操作自己的零件；他人零件 → `RuleRejected` + `NotOwnedByPlayer`；不可开关零件 → `RuleRejected` + `PartNotSwitchable`；相位不符 → `WrongMode`。

## Objective

`PigForge.Server --play` 沙盒里：

- Start 后零件不动；玩家点开关条按钮/类型热键/自己的零件后，对应零件开始或停止工作。
- 开关状态是服务器权威，随每帧快照下发；所有客户端都能看到哪个零件开着，但只有 owner 能切换。
- 一次性零件（火箭/风箱/抓钩/脱钩/TNT）只被开关触发（TNT/脱钩的撞击路径保留）。
- RESET → 重新 Start 后所有开关回到关闭。

## 内容契约（part-content-v1，追加可选字段）

`schemas/part-content-v1.schema.json` 的 `$defs.capabilities.properties` 追加：

```json
"activation": { "enum": ["toggle", "trigger"] }
```

`PartCapabilities`（`src/PigForge.Core/Content/PartContentDocument.cs`）追加末位参数：

```csharp
public enum PartActivation { None, Toggle, Trigger }

public sealed record PartCapabilities(
    …, float? GrappleDirectionY = null,
    PartActivation Activation = PartActivation.None);
```

解析器（`PartContentParser.ParseCapabilities`）按既有严格风格：`activation` 可选；非字符串或不在枚举 → 错误累加（`{path}.capabilities.activation: must be "toggle" or "trigger".`）。

v1 映射（`content/parts.json` 与 `clients/web/src/builder/slope.ts` 的 `PLAY_PARTS` 同步）：

| activation | 零件（partTypeId） |
|---|---|
| `toggle` | engine 8、motor-wheel 17、propeller 38、fan 11、black-umbrella 35、electric-umbrella 36、gearbox-lever 39、rotor 37 |
| `trigger` | rocket 13、soda-bottle-black 25、soda-bottle-green 26、firework-red 30、bellows 40（+88–94）、grappling-hook 46、detacher 43、tnt 9/42/47/48/49/50、balloon 10/19/20、**boxing-glove 28（+242–245）** |
| 无 | 结构件、轮子、猪/猪王、沙袋、绳、蛋、弹簧 12、翼 31/32、尾 33/34、灯 44/45 |

注：气球对应原作的「点一下放气」——升力与今天一样**被动生效**；开关触发即 `DestroyEntity`（与火箭自毁同一路径），升力立即停止、实体从快照与施工布局消失。旋翼改走 `toggle`：原作 `FanPropeller.SetEnabled(false)` 只停电机、不销毁零件（`docs/specs/fan-propeller.md`）。复合体成员的形状重建沿用既有 `UnbindEntity` 语义，见 Open Questions。

## Core：`ActivationStore` 与 `GameplayRules`

```csharp
// src/PigForge.Core/Runtime/GameplayStores.cs
public readonly record struct ActivationState(PartActivation Kind, bool Active);
public sealed class ActivationStore(EntityStore entities) : ComponentStore<ActivationState>(entities);
```

`GameplayRules` 构造参数在 `GrappleStore` 之后追加 `ActivationStore activations`，并新增：

```csharp
public void AddActivation(EntityId entity);                       // 声明开关；Active = false
public void SetActive(EntityId entity, bool active);              // 只改 Active，不校验归属/可开关性（调用方已校验）
public bool IsPartActive(EntityId entity);                        // 快照/哈希用；无条目 = false
```

门控规则（**无 `ActivationState` 条目 = 该零件没有开关**，走本切片前的既有路径：持续件一直工作，一次性件保留触地/撞击/t0 自燃触发）：

- `RunMotors` / `RunFans` / `RunUmbrellas`：条目存在且 `!Active` → 跳过。
- `RunBalloons`：升力照旧被动生效（不随 `Active` 门控）；条目 `Active` 时 `DestroyEntity(entity, output)`（放气，升力消失）。
- `CollectGearboxBodies`：只收 `Active` 的齿轮箱。
- `RunRockets`：有条目且未点火且 `!Active` → 跳过（等开关）；一旦点火就烧到 `DurationTicks` 归零并自毁（点火不可中断）；无条目 → 保持 t0 自燃。
- `RunBellows` / `RunGrapples` / `RunRockets`：**先** `TryConsumeButtonPress(entity)`（按钮的按下一律被消费，见 Assumption 2），**再**判该件自己的门控（底盘/power/方向）；无条目 → 保持触地触发、离地重臂。
- `RunBellows` 的节流：按下 puff 一次，然后按原版的 `0.8 s + 0.3 s inflate`（`Bellows.cs:64-67`，`BellowsState.ReadyAtTick`）才接受下一次；推力沿零件自己的局部 +X（`m_direction`，`Bellows.cs:84-87`）打在 `transform.position + dir × 0.5`。
- `RunGrapples`：有条目 → 按下触发一次，`FiredRecently` 为**永久已用**（钩子只有一发）；无条目 → 保持触地触发、离地重臂。
- `RunDetachers`（新增）：条目 `Active` → `output.DetachedEntities.Add(entity)`；无条目 → 只有 `DetachOnImpact` 的撞击路径。
- `RunTntFuses` / `IgniteTntOnBody`：激活沿 `Ignited = true`（与撞击路径共用既有状态）。
- `RunSprings`、翼/尾、灯：不变（被动）。
- `ResetForRebuild()` / `ResetAll()`：所有条目 `Active = false`，并清 `BouncedRecently`/`ReadyAtTick`/`FiredRecently`（重臂）。
- `CleanupEntityStores` / `DestroyEntity`：`_activations.Remove(entity)`。
- 确定性：`Active` 只在 `Submit`（命令）与触发消费（tick 内确定性路径）改变，不在 tick 之外乱序修改。

## PGFC 线格式（v2 追加 kind 8/9）

Little-endian，19B 头（magic/version/kind/sequence/playerId/tick）不变：

| kind | 名 | payload（头之后） | 总长 |
|---|---|---|---|
| 8 | SetPartActive | `entityId:u32`, `active:u8` | 24 |
| 9 | SetPartTypeActive | `partTypeId:u32`, `active:u8` | 24 |

- `ClientCommandKind` 追加 `SetPartActive = 8`、`SetPartTypeActive = 9`。
- 记录：`SetPartActiveCommand(Tick, Sequence, PlayerId, EntityId, Active)`、`SetPartTypeActiveCommand(Tick, Sequence, PlayerId, PartTypeId, Active)`。
- 解码校验（失败即拒绝整帧）：`entityId != 0` / `partTypeId != 0`；`active` 只能是 0 或 1。
- 语义：`active` 是**目标状态**（幂等），不是盲切换。
  - `toggle`：`SetActive(active)`。
  - `trigger`：`active = true` 就是**按下**——服务器在同一 tick 里消费它（`TryConsumeButtonPress`），所以快照随后看到的 `Active` 又是 0（按钮不留在开位）。`active = false` → Accepted no-op。
  - kind 9 作用于该玩家、该 `partTypeId`、可开关的全部存活零件（实体升序）；`toggle` 组客户端发 `active = !组内任一 Active`（原作「有任一个关着 → 全开；否则全关」），`trigger` 组发 `active = true`。

## PGFS v3（本切片）／v4（ADR-009）／v5（ADR-028，当前版本）

```
magic "PGFS" | version:u16(=5) | tick:u32 | phase:u8 | entityCount:u32
entity: entityId:u32 | physicsBodyId:u32 | partTypeId:u32 | position:3f |
        rotation:4f | linearVelocity:3f | angularVelocity:3f | scale:f | attachYaw:f | flags:u8
```

- v5（当前）：`CurrentVersion = 5`，`flags` bit1 = 运行期子实体（ADR-027：拳套的拳头、拉断弹簧的端点）。线上它借宿主的 `partTypeId`，客户端靠这一位换成清单里的子实体美术（`subEntityTexture`）；`CurrentVersion` 一改，`clients/web/src/schema/decodeSnapshot.ts` 的 `SNAPSHOT_VERSION` 必须同批改。
- v4：`EntityByteCount = 73`，在 `scale` 与 `flags` 之间追加 `attachYaw:f`——零件**不自转的贴图**所刚性附着的参考坐标系的世界 Z 朝向（铰链轮 = 父刚体坐标系，其余零件 = 自身坐标系）。见 `docs/decisions/ADR-009-wheel-spin-model.md`。

- v3（本切片）：`CurrentVersion = 3`、`EntityByteCount = 69`，`SnapshotEntity` 追加 `byte Flags`（已由 v4 取代）。
- `flags` bit0 = `Active`，bit1 = 子实体；其余位保留，写入 0、读取忽略。
- `GameRoom` 三处 `TryEncodeHeader` 改用 `SnapshotFrame.CurrentVersion`（当前误用 `ProtocolVersion.Current`）；三条发布路径（Running/Building/Sandbox）都写 `SnapshotFlags(entity)`（bit0 + bit1，`GameRoom.cs`）；预览与未绑定实体为 0。
- `ComputeStateHash()` 的 Running 分支把激活条目（按 entityId 升序的 `entityId + Active`）纳入哈希。
- 旧客户端会因版本不符拒绝 v3 帧——本仓库客户端与服务器同批升级（无已部署客户端）。

## Server 接入

### `CommandValidator`

| 场景 | SetPartActive / SetPartTypeActive |
|---|---|
| 沙盒 + Materialized | `Accepted` |
| 沙盒 + Editing | `WrongMode` |
| 旧房间 Running | `Accepted` |
| 旧房间 Building | `WrongMode` |

per-player `(PlayerId, Sequence)` 语义不变。

### `GameRoom`

- `ConstructionError` 追加 `PartNotSwitchable`（末尾追加，字节值不变既有项）。
- `ConstructionRules` 追加 `public uint? OwnerOf(EntityId entity)`（`_ownerByEntity` 查询）。
- `RegisterPlacedRole` 与 `Spawn`：**仅沙盒模式**（`_sandboxMode`）为 `capabilities.Activation != None` 的零件 `_rules.AddActivation(entity)`；旧关卡房间不种条目，既有自动路径不变。
- `ExecuteCommand`（owner 0）与 `ExecuteSandboxCommand`（owner = `PlayerId`）共用：

```text
SetPartActive:
  !IsAlive            -> RuleRejected, EntityNotFound
  OwnerOf != owner    -> RuleRejected, NotOwnedByPlayer
  不可开关             -> RuleRejected, PartNotSwitchable
  否则 SetActive      -> Accepted, entityId 回显

SetPartTypeActive:
  该 owner 下该类型的可开关存活零件为空 -> RuleRejected, PartNotSwitchable
  否则逐个 SetActive -> Accepted, entityId = 0
```

- `StartPlayer` 不需要额外动作：条目在放置时已按「关闭」种下；RESET 销毁实体即销毁条目。
- 快照 flags 与 `ComputeStateHash` 见 PGFS v3 一节。

## Web 客户端（web-gadgets）

```text
clients/web/src/schema/decodeSnapshot.ts   # v3：SNAPSHOT_VERSION=3、69B、flags→active
clients/web/src/schema/types.ts            # SnapshotEntity.active、DrawEntity.active、ClientCommand 8/9、activation 字段
clients/web/src/schema/toDrawEntities.ts   # 透传 active
clients/web/src/schema/encodeCommand.ts    # kind 8/9 编码（24B）
clients/web/src/live/playerSession.ts      # CommandKind 0|1|2|3|5|6|7|8|9；ack 不改 ownEntityIds
clients/web/src/live/gadgets.ts            # 新：分组/标签/热键纯函数
clients/web/src/App.vue                    # 开关条、热键、点零件切换
clients/web/src/renderer/draw.ts           # 活跃零件描边
clients/web/src/style.css                  # 开关条样式
```

### `live/gadgets.ts`（纯函数，无 DOM/Vue）

```ts
export interface GadgetGroup {
  partTypeId: number; kind: "toggle" | "trigger"; label: string; count: number; active: boolean; hotkey: string;
}
export function gadgetGroups(
  entities: readonly DrawEntity[],
  ownEntityIds: ReadonlySet<number>,
  content: PartContentDocument | null,
): GadgetGroup[];
export function gadgetHotkey(index: number): string | null;  // 0→"1" … 8→"9", 9→"0", 10→"A" … 35→"Z"
```

- 只收 `ownEntityIds.has(entityId) && bodyId !== 0 && 内容 activation 非空` 的实体。
- 按 `partTypeId` 升序分组；`kind` 取内容 `activation`；`label` 优先 `PALETTE` 中文名，否则 content `name`；`active = 组内任一 active`；`hotkey` 由组下标得出，超出 36 组为 `""`（不显示）。
- 组数量随快照变化：零件被炸掉后按钮自动消失。

### 交互

- 开关条：`activeTab === "live" && playerPhase === "materialized" && groups.length > 0` 时显示在画面中下方；`toggle` 组点击 → kind 9（`active = !group.active`）；`trigger` 组点击 → kind 9（`active = true`，一次性动作，按钮不保持 on 状态；气球组即「放气」）。
- **按钮画成按钮**：`App.vue` 的 `gadget-button` 只对 `toggle` 组加 `on` 高亮（`trigger` 组用虚线边框、不做常亮，`aria-pressed` 也只给 `toggle`）。否则按钮件会显示成一个「卡在开位」的开关——用户实测报告的原话就是「预期是按钮而不是开关」。
- 热键：运行中按 `1`–`9`/`0`/`A`–`Z`（大小写均可）触发对应按钮，然后 `return`（不落到工具键）；建造期行为不变。
- 点零件：`SelectEntity` 消息后，若运行中且该实体是自己可开关的零件 → 额外发 kind 8（`active = !entity.active`）；否则只选中。
- 零预测：不发本地状态变更；按钮与描边只跟随快照。

### 渲染

- `draw.ts` 对「内容声明了 activation 且 `entity.active`」的实体加一圈琥珀色描边（`#ffd166`，lineWidth 2），其余不变；不引入阴影/动画。

## 回放契约（追加）

- `ReplayContracts` 追加 `SetPartActiveCommand` / `SetPartTypeActiveCommand`；`ReplayDocumentValidator` 映射 `ClientCommandKind` 并校验 id 非零、`active` 为布尔。
- `schemas/physics-replay-v2.schema.json` 追加 `SET_PART_ACTIVE`（entityId + active）与 `SET_PART_TYPE_ACTIVE`（partTypeId + active）；`schemas/client-command-v1.schema.json` kind 枚举扩到 0–9。
- `PhysicsReplaySimulation.ApplyCommand` 对 8/9 抛 `NotSupportedException`（没有 rules 契约，与 Move/Scale 同一先例）；本切片不产出含 8/9 的回放，回放实体帧**不**记录开关状态。

## Code Style

- .NET：命令记录与 wire 编解码仍用 `BinaryPrimitives`/`Span`；激活状态用显式小 record + store，不引入 DI；新错误值只追加。
- Web：`gadgets.ts` 纯函数（无 DOM/Vue）；`App.vue` 只做状态机与命令映射；渲染改动收敛在 `draw.ts`。

## Testing Strategy

### Core（`tests/PigForge.Core.Tests`）

- `PartContentTests`：`activation` 解析（合法/非法/重复键）；`parts.json` 映射逐条断言（至少 toggle/trigger 各若干）。
- `GameplayRulesTests`：
  - toggle 门控：motor 关 → 无命令；开 → 有命令；fan/balloon/umbrella 同理；齿轮箱关 → 不反向、开 → 反向。
  - trigger：火箭关 → 不点火、开 → 推满 `DurationTicks` 后自毁；风箱/抓钩/脱钩有开关时**激活触发一次**（无条目保持既有触地/撞击路径，原用例不改）；TNT 激活点火 + 撞击点火仍成立。
  - 重置：`ResetForRebuild` 后条目 `Active=false`、已用标记清空。
  - 双跑：含开关脚本的确定性哈希一致。
- 既有直接 `AddMotor`/`AddBalloon`/`AddBellows` 等无条目用例保持通过（无条目 = 既有路径）。

### Protocol（`tests/PigForge.Protocol.Tests`）

- kind 8/9 往返；`entityId=0`/`partTypeId=0`/`active=2` 被拒；截断帧被拒。
- PGFS v3：69B 往返含 flags；`EntityByteCount`/`GetMaxByteCount` 断言更新；版本 2 帧被拒。

### Server（`tests/PigForge.Server.Tests`）

- 门控矩阵（沙盒 Editing/Materialized、旧房间 Building/Running）。
- 沙盒：A 的零件被 B 切换 → `RuleRejected` + `NotOwnedByPlayer`；不可开关零件 → `PartNotSwitchable`；kind 9 只影响自己的该类型零件（他人的不动）；接受后下一帧 flags 正确。
- 旧房间不种条目：flags 全 0，`SlopePlayTests`/`PhysicsDrivenLevel` 行为不变。
- 双跑状态哈希含开关状态。

### Web（vitest）

- `decodeSnapshot.test.ts`：v3 帧、flags bit0、版本 2 拒绝、69B 截断拒绝。
- `encodeCommand.test.ts`：kind 8/9 字节布局与 `active` 编码。
- `live/gadgets.test.ts`：过滤（他人/预览/无开关）、分组顺序、`active` 聚合、热键映射（`1`–`9`,`0`,`A`…）。
- `playerSession.test.ts`：kind 8/9 ack 不影响 `ownEntityIds`。
- `pnpm test` && `pnpm build`。

### 手验（Success Criteria）

两个浏览器标签连同一 `--play` 房间走一遍。

## Boundaries

**Always**

- 权威只在 `GameRoom`：开关状态与合法性（归属/可开关/相位）由服务器判定。
- 客户端零预测；按钮与描边只跟随快照。
- 新行为有测试（Core/Protocol/Server/Web 至少各一处）。
- UTF-8 LF。

**Ask first**

- 改 PGFS/PGFC 布局或版本（本切片只做规格写明的 PGFS v3 + PGFC 8/9）。
- Besiege 式单个零件热键绑定/自定义按键、开关组多选、跨会话持久化。
- 把开关状态写进回放实体帧或新增帧类型。

**Never**

- 客户端权威、预测、回滚。
- 允许操作他人零件，或让 RESET 影响他人。
- 用「重放一条撞击事件」模拟开关触发。
- 静默忽略坏命令或不可编码 kind。
- 热路径 JSON / 反射。

## Success Criteria

1. `dotnet test PigForge.slnx`、`pnpm test`、`pnpm build` 全绿。
2. 两个标签：A Start 后载具静止；A 点「发动机」按钮（或按 `1`，或点发动机零件）→ 载具开动；再点 → 停；B 看到 A 的零件描边亮起，点它被拒且错误可见。
3. 火箭/礼花/风箱/抓钩/TNT 只在开关触发后动作（不再 t0 自燃/触地自动触发）；气球 Start 后即提供升力，开关触发后消失；旋翼是 `toggle`，Start 后推力关闭、开关只启停电机；TNT 强撞击仍自燃。
4. RESET → 重新 Start 后所有开关回到关闭，开关条重建。
5. 旧房间（斜坡/地形）行为与本切片前逐字一致（不种开关条目，走既有自动路径），`--demo-ws` 只升到 v3 帧。
6. 抓包：客户端→服务器只有 PGFC（含 24B 的 kind 8/9），无位姿或状态上传。

## Open Questions

- 气球放气后，若它属于复合体，该 body 的碰撞形状仍含气球（既有 `UnbindEntity` 只解除实体绑定，火箭/TNT 自毁同样如此）。要彻底移除需要「成员被摧毁时重建复合体」的独立切片。
- 原作「发动机按钮联动所有动力零件」：v1 不做，按钮严格按类型。若手感需要再开切片。
- Besiege 式单个零件热键（可绑定）：需要绑定 UI 与持久化，另开切片。
- 不活跃的可开关零件是否要视觉变暗：v1 只给活跃件加描边。

## References

- `docs/intent/play-part-switches.md`
- `docs/specs/multiplayer-sandbox.md`（归属/per-player 门控）、`docs/specs/advanced-building.md`（追加式协议演进先例）
- `docs/decisions/ADR-002-runtime-rules-semantics.md`（TNT/脱钩撞击语义）
- 原作参考：`C:\tmp\BAD_PIGGIES\BPLE_Unity6\Assets\Scripts\Assembly-CSharp\{BasePart,Contraption,Engine,MotorWheel,FanPropeller,Gearbox,GadgetButton,GadgetButtonList}.cs`
- `src/PigForge.Core/Runtime/{GameplayRules,GameplayStores}.cs`、`src/PigForge.Protocol/{ProtocolContracts,CommandWire,SnapshotWire,ReplayContracts,ReplayDocumentValidator}.cs`
- `src/PigForge.Server/{GameRoom,CommandValidator,SandboxPlayers}.cs`、`clients/web/src/{App.vue,schema/*,live/*,renderer/draw.ts}`
