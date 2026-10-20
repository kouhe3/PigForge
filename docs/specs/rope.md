# Spec: 绳的运行时实例化（Rope 件 → 8 段链）

> 2026-10-19。**状态：已实现**（交付读数见 §15）。范围：`G63`（绳）。决策依据 `ADR-027`（运行时子实体：一个放置件 → N 个实体/刚体）。
> 原版真值全部逐行取自 `BPLE 2022.1.9`（原版自己的编辑器版本），调研底稿：
> `tasks/rope-sandbag-materialization-report.md`（只读报告，未入库）。

## 1. 一句话

原版的绳不是一个刚体，而是**放置一个 1 格件、START 时实例化 8 个带球形碰撞体的真实刚体**，端点用
HingeJoint 铰到相邻件、段与段之间**没有关节**（一串自研力链），整条绳再用 LineRenderer 画样条。
PigForge 现在把绳当成一根 3 格木板焊进车架（`CompoundAssembler.CanMerge` 直接把它并簇），完全不是原版行为。

本片把「**放置一件 → 8 个运行时子实体**」落地，并按原版重写段内力链、端点铰、张力断与**点击剪切**。

## 2. 范围

| 做 | 不做（本片） |
|---|---|
| 8 段子实体（ADR-027）、段体形状/质量/材质/阻尼 | 绳段的 LineRenderer 样条（客户端本片画逐段精灵，见 §9、D2） |
| 端点 `Revolute`（= 原版 `HingeJoint axis=forward`） | B 档 IN 分支：`StableRope` / `UnbreakableRope` / `CollidableRope`（D3） |
| **剪切两条入口**：① UI 按钮（`activation: trigger`，kind 8）→ 剪**中间**；② 世界点击 → 最近段（0.5 m，clamp 2..n−2），新增 **PGFC v4 `CutRope`** | `HingePlate` 变体（Rope_05..08，vanilla `HingePlate=false`，D7） |
| 段内力链（拉/推/空气阻/逐节阻尼） | 单发计数 `ChangeOneShotPartAmount`（D11） |
| 张力断裂（内段 1.25 m / 端段 1.75 m，vanilla 开着） | 段体的 `-90°` gridRotation 贴图朝向细节（D10） |
| 绳→绳串联（同朝向） | |
| 建造期预览 = 单件美术；运行期只发 8 段 | |
| 连接性（`transport` 挑战的 `AreConnected`）——T5 | 竞速/其它模式对绳的消费者 |

## 3. 原版真值（逐行）

| 事实 | 出处 |
|---|---|
| 绳件 `Part_Rope_01..04_SET`：`m_partType 30`、`m_mass 1`、1 格（`gridX/Y 0..0`）、`m_autoAlign 1`(Rotate)、`m_jointConnectionType 2`(Target)、`m_jointConnectionDirection 7`(None)、`m_customJointConnectionDirection 5`(**LeftAndRight**)、`m_jointConnectionStrength 0`(Weak)；**prefab 没有任何 Collider** | `Part_Rope_01_SET.prefab:50-85` |
| `RopeSegment.prefab`：Rigidbody `m_Mass 0.05`、`m_Drag 0.5`、`m_AngularDrag 0.5`、`m_Constraints 56`；SphereCollider `r 0.09`、材质 `Contraption_LowFriction`（friction 0）；layer 15 `NonCollidingPart` | `RopeSegment.prefab:63-82` |
| 段数 = `INFeature.RopeSegmentCount` = **8**（声明默认档）；`StableRope=false`、`UnbreakableRope=false`、`CollidableRope=false`、`HingePlate=false` | `INDeclarationSettingsExp.json`；`Rope.cs:266` |
| 段位置：`p = 绳件.position − 0.5·right`，每段沿 `0.5·(rotation·right)` 前进；段自转在 `±74.4°`（绕 forward）之间交替；右端是非绳件时最后一段贴到 `+0.5·right` | `Rope.cs:254-300` |
| 端点：在**相邻件**的 GameObject 上加 `HingeJoint`（`connectedBody` = 端点段，`axis=forward`，`breakForce/breakTorque=+∞`，`enablePreprocessing=false`，anchor = 段.position − 相邻件.position） | `Rope.cs:233-242,266-285` |
| 选相邻件：沿 `LeftAndRight`（否则 up/down）取两格，**只接受** 同 `gridRotation` 的 Rope、`Frame`、`Kicker` | `Contraption.cs:2499-2525` |
| 绳→绳：本绳最后一段移到右绳第一段位置，并铰接两者 | `Rope.cs:130-143` |
| 绳件自身：`Start` 里 rigidbody `isKinematic=true, mass=0`；加 LineRenderer（世界坐标、宽 0.2、色 (255,60,90)）；`renderer.enabled=false` | `Rope.cs:98-119,293-296` |
| 段内力（每 FixedUpdate，50 Hz，`ForceMode.Force`）：<br>• 每个节点：`v_rel = |v − 0.4·v_prev − 0.4·v_next|²`，力 `−0.02·v_rel·v̂`；`drag = 0.5 + 6/(1+4·V²)`，`V = max(首段,末段速度)`<br>• 相邻两段 `d>0.5`：`e = d−0.5`，`e<1 ? e^0.75 : e^2`，clamp 2，两端各 `70·e·d̂`<br>• `d<0.4`：两端各 `50·(d−0.4)·d̂`（推开） | `Rope.cs:334-395` |
| `StableRope` 的额外速度上限（vanilla 关） | `Rope.cs:357-364` |
| 张力断裂（`UnbreakableRope=false` 时）：内段（`1<j<n−2`）`>1.25`、端段 `>1.75` → 在该 link 处 Cut | `Rope.cs:399-427` |
| 拉伸端点拉：`total / (1.18·0.5·n) − 1` 经 `ResponseCurve`（`sigmoid(8(t−0.5))` 归一化到 (0.01798/0.9641)）→ `factor≤0.4`；`total>1.18·0.5·n` 时端点力 `clamp(750·factor, 0, 300)`，作用于最外两段；`factor` 下降时再 ×0.7 | `Rope.cs:429-462` |
| Cut：`m_ropeCut=true`、记 `m_cutIndex`、第二条 LineRenderer、单发计数 −1、重算连通分量；内力链**跳过被剪的那一条 link**，`PullEndpoints` 整体停止 | `Rope.cs:308-327,370,393` |
| 用户剪切：`OnTouch(hasPosition)` → 最近段（`interactiveRadius 0.5`，clamp 到 2..n−2），无位置时 `n/2` | `Rope.cs:169-190` |
| 原版固定步长 0.02 s（50 Hz） | `ProjectSettings/TimeManager.asset:6` |

## 4. 模型（关键决策）

### 4.1 一条绳 = 一个**无刚体 host** + 8 个运行时子实体

- 放置的绳件（实体 A）继续留在 `ConstructionRules`：占 1 格、有 owner、收 `Remove/Move/Rotate/Scale`。
- **START 时 A 不进 `CompoundAssembler` 的实体列表**，因此不并簇、不焊接、**不创建刚体**。
  运行期 A 不发布（`FillEntityOrder` 只遍历有 body 的实体），与原版 `renderer.enabled=false` 等价。
- 段体由 `CreateSubEntity`（ADR-027）创建：每段一个实体（借 host 的 `partTypeId`）、一个自己的刚体。
  快照带 `FlagSubEntity`（bit1），客户端换 `subSprites` 美术。
- 生命周期由 host 决定：RESET / Retry / 断连 / host 被摧毁 → `DestroySubEntitiesOf(host)`（已有机制）。
- 这样**不动协议**：子实体已经是普通 73 B 记录；host 在建造帧照发（预览）。

### 4.2 段体的几何与姿态

在原版里按零件本地系算（`right` = 件的本地 +X 经建造 yaw/镜像）：

```
p0 = host.position − 0.5·R·x̂
θ  = +74.4° (i=0)
for i in 0..7:
    if i == 7 and rightNeighbour is not Rope: p_i = host.position + 0.5·R·x̂
    create segment(i) at p_i, rotation = R·Rz(θ)
    p_{i+1} = p_i + 0.5·(R·Rz(θ)·x̂)
    θ = −θ        // 交替
```

- 段刚体：dynamic、mass **0.05**、shape = sphere **r 0.09**、`PlanarConstraintMask`（prefab 的
  `m_Constraints 56` 就是 2.5D 冻结）、`linearDamping 0.5`（每 tick 会被 §4.5 覆写）、
  `angularDamping 0.5`、`maximumAngularSpeed 7`、材质 friction **0**、restitution 0。
- **段碰撞全部关闭**（`SetBodyCollisionEnabled(body,false)`；vanilla `CollidableRope=false`、layer 15）。
- 段体旋转用上面的 `Rz(θ)`；它只影响精灵朝向（球体无惯性差异）。

### 4.3 端点关节

- 左端（`p0`）连到左邻件；右端（`p7`）连到右邻件。邻件查找见 §6.3。
- 关节种类：`PhysicsJointKind.Revolute`（轴 = 两体的本地 forward，两锚点在世界空间重合于端点段的
  球心；`breakForce = 0`、`breakTorque = 0` = 不破；`enablePreprocessing` 在 PigForge 无对应概念）。
  - 原版是把 HingeJoint 加在**邻件**上、`connectedBody` = 段；Revolute 是对称的，语义相同。
- 邻件是 Rope 时：本绳 `p7` 与右绳 `p0` **本来就重合**（相邻 1 格、各自 ±0.5），直接在两段之间建同一种
  Revolute，不做原版的运行期 `setPosition`（PigForge 没有 set-pose API，也没必要）。
- 邻件缺失 → 该端自由（原版 joint 为 null）。
- 邻件不是 Frame/Kicker/Rope（例如木块）→ **不连接**，该端自由。规则：
  `neighbour.Capabilities.CanEnclose == true`（Frame 族）或 `Detacher`（Kicker）或 `Rope is not null`；
  Rope 还要求同 yaw。

### 4.4 段内力链（规则层，每 tick）

对每条未剪的 link `j = 1..7`（`j == CutLink` 时跳过）：

```
p_prev, p_cur  ← 本 tick 快照位置（_kinematicsByBody）
d = |p_cur − p_prev|
if d > 0.5:
    e = d − 0.5
    e = e < 1 ? pow(e, 0.75) : pow(e, 2)
    e = clamp(e, 0, 2)
    F = 70 · e · (p_cur − p_prev)/d                // 指向 cur
    impulse(prev, +F/60); impulse(cur, −F/60)      // prev 被拉向 cur，cur 被拉向 prev
else if d < 0.4:
    F = 50 · (d − 0.4) · (p_cur − p_prev)/d        // F 为负 = 推开
    impulse(prev, +F/60); impulse(cur, −F/60)
```

- 力的符号照原版：`vector = (p_cur−p_prev)` 整形后，`rigidbody4.AddForce(70*vector)` 给**前一段**、
  `rigidbody5.AddForce(70*(-vector))` 给后一段；实现时用同一个向量，别各自取反。
- 50→60 Hz：`ForceMode.Force` 的物理效果是 `F·dt`，所以每 tick 的冲量 = `F/60`（与 `bellows`/`fan`
  的 `thrustPerTick = F/60` 同一约定）。
- 空气阻：`q = v_i − 0.4·v_{i−1} − 0.4·v_{i+1}`，对本节点施加 `−0.02·|q|²·v̂_i / 60`（端点用自身代替缺的邻居）。
- `StableRope` 分支不实现（vanilla false）。

### 4.5 逐节阻尼

原版每个 FixedUpdate 对**整条绳的所有节点**写同一个 `drag = 0.5 + 6/(1+4V²)`，
`V = max(首段速度, 末段速度)`（`Rope.cs:366`），angularDrag 不动（prefab 0.5）。

PigForge：`GameplayTickOutput` 增加一个 body-keyed 的阻尼覆写列表（段体没有 compound 成员表，
现有 entity-keyed `DampingOverrides` 不适用），房间层在 tick 的最后统一 `_world.SetBodyDamping`。
值为 `linear = 0.5 + 6/(1+4V²)`、`angular = 0.5`。

### 4.6 断裂

- 每 tick 在施加内力后、未剪时执行 `PullEndpoints` 的断判：对每个 link 取 `d`，
  `j>1 && j<6` 时 `d>1.25`、否则 `d>1.75` → `CutLink = j`（一次，第一条命中的 link，按 `j` 升序）。
- `CutLink` 置位后：内力链跳过该 link；拉伸端点拉与后续断判停止；段体继续存在、继续受重力。
- 不产生 `BrokenJoints` 事件（原版 rope cut 不是 joint detach，`perfectFlight` 不该因剪绳丢星）。
- 拉伸端点拉（`PullEndpoints` 的 endpoint force）在**未剪**时执行：按 §3 的 total/factor 公式；
  只对存在邻件的一端施加（`LeftAttached/RightAttached`），施力点 = 最外段的位置。

### 4.6b 剪切：两条入口（按钮 = 中间，点击 = 最近段）

原版有两个入口（`BasePart.cs:924-938`）：

- **UI 按钮**：`ProcessTouch()`（无位置）→ `OnTouch(hasPosition:false)` → `Cut(m_nodes.Count / 2)`：
  8 段时索引 **4**，即剪掉节点 3–4 之间那条 link。这是玩家记忆里的「剪绳子按钮」。
- **世界点击**：`ProcessTouch(Vector3)` → `OnTouch(hasPosition:true)` → `FindInteractiveSegment`：
  最近节点（世界 z=0，距离 ≤ `m_interactiveRadius 0.5`），索引 **clamp 2..n−2**；没有命中就 `Cut(n/2)`
  （只有带位置且触达该件时才会派发，所以实际等价于必须落在段附近）。

PigForge 两条都做：

**① 按钮路径（零协议改动）**

- 内容给四个绳件写 `activation: "trigger"`（`Rope` 没有覆写 `HasOnOffToggle` → 原版就是按钮型控件，
  ADR-028）；`RegisterPlacedRole` 照 G115 注册 activation。
- `RunRopes` 每条未剪绳先 `TryConsumeButtonPress(host)`；为真 → `CutIndex = segments / 2`（**中间**）。
- 按钮由开关条发 kind 8（host 实体 id），`SetPartActive` 的归属/可开关门已覆盖（host 在 `ConstructionRules` 里）。
- **host 必须在运行帧可见**，否则开关条看不到它 → §9 的 `bodyId = 0` 约定。

**② 位置路径（PGFC v4 `CutRope`）**

```
CutRope { tick, sequence, playerId, x:f32, y:f32 }        // 世界坐标（z 忽略）
```

服务器（权威）：

1. 只在 **Running** 接受；查找**本命令 owner 的**未剪绳（沙盒 = 该玩家；关卡房间 owner 0）。
2. 对每条绳的段取当前世界位置（`_kinematicsByBody`），去掉 z，找最近的段，距离 ≤ **0.5**；
   都没有 → `RuleRejected / RopeNotAtPoint`（新增错误 19）。
3. 命中段索引 `i` → `CutIndex = clamp(i, 2, n-2)`，置剪断。
4. 已剪的绳：**Accepted 且 no-op**（原版 `OnTouch` 在 `m_ropeCut` 时什么都不做），不报错。
5. 归属校验与 `SetPartActive` 同一条（`_construction.OwnerOf(host) == owner`，关卡房间 owner 0）。

客户端：运行期在画布上命中一个**绳段子实体**（`flags` bit1 + 内容该 part 有 `capabilities.rope`）时，
用该点的世界坐标发 `CutRope`；服务端仍按最近段规则裁决，客户端命中测试只是减少无效命令。

### 4.7 视觉

- 建造帧：host 照旧画它自己的单张绳图（清单 part 41 已有）。
- 运行帧：host 不发布，8 段各画 `subSprites`（`RopeSegment` 的 `Visualization` 精灵，本地缩放 0.4）。
- 不做 LineRenderer 样条（D2）。段在 0.5 m 间距下看起来是一串短段；原版样条是平滑的。

### 4.8 生命周期与确定性

- 创建顺序：host 实体值升序 → 每条绳段索引 0..7 升序；绳→绳铰在第二遍按 host 升序建立。
- RESET / Retry / 断连 / host 被摧毁 → 段与段-邻件铰一并销毁（`DestroySubEntitiesOf` 先关节后刚体）。
- `RebindSubEntityJoints` 增加 rope 分支：段落重建后，端点/绳间铰按当前 pose 重建。
- 双跑 `ComputeStateHash` 必须相等（段实体 id 由 `EntityStore.Create` 的确定性槽位给出）。

## 5. 内容契约（`part-content-v1`）

四个绳件（41 `rope`、214/215/216 `rope-v02..v04`）写：

```json
"capabilities": {
  "jointConnectionDirection": "none",
  "jointConnectionStrength": "weak",
  "jointConnectionType": "target",
  "activation": "trigger",
  "rope": {
    "segments": 8,
    "segmentMass": 0.05,
    "segmentRadius": 0.09,
    "segmentFriction": 0.0
  }
}
```

- `segments` 来自 `INFeature.RopeSegmentCount`；`segmentMass`/`segmentRadius`/`segmentFriction` 来自
  `RopeSegment.prefab`。**其余常量留在代码**（`Rope.cs` 的类常量，逐皮肤不变；与 `bellows` 的
  「时长留在代码」同一判据），工具对源码字面量做硬断言（§6.1）。
- `shapes`：改为**单格建造标记** `box 0.5`（原版无碰撞体；占格由 `gridBox` 默认 `0..0` 决定，本来
  就是 1 格）。旧内容里手写的 `box [1.5, 0.06, 0.06]` 没有原版对应物（ADR-005 §4），且会让
  `PartFootprint._all`（连接邻近）横跨 3 格。
- `mass` 保留 0.3（host 无刚体，不被使用；仅维持 schema/目录稳定）。
- `activation: "trigger"`：原版的按钮型控件（剪中间）。


## 6. 工具

### 6.1 `tools/bple-ropes/extract-ropes.mjs` + `apply-ropes.mjs`

- 读 `Part_Rope_01..04_SET`、`RopeSegment.prefab`、`INDeclarationSettingsExp.json`、`Rope.cs`。
- 写 `capabilities.rope`（四行，幂等）。
- **硬断言**（漂移即红）：
  - 类普查 `{Rope: 4, HingePlate: 4}`；`m_partType 30`；四个 Rope prefab 的 `m_customJointConnectionDirection = 5`；
  - `RopeSegment`：mass 0.05、radius 0.09、material 的 friction 0；`m_Constraints = 56`；
  - IN：`RopeSegmentCount 8`、`StableRope/UnbreakableRope/CollidableRope/HingePlate` 全 false；
  - 源码字面量：`74.4f`、步进 `0.5f`、`70f`/`50f`、`0.4f`、`1.25f`/`1.75f`、`1.18f`、
    `750f`/`300f`、`0.02f`、`0.5f + 6f/(1f+4f*...)`、`Sigmoid(8f*(t-0.5f))`、`0.01798f/0.9641f`。
- 报告 `tasks/bple-ropes-report.{json,md}`（gitignored）。

### 6.2 `tools/bple-textures/extract.mjs`

- `SUB_ENTITY_PREFAB_FIELDS` 加 `"m_segment"` → 绳件清单自动带 `subSprites`（RopeSegment 的
  Visualization 精灵，缩放 0.4，一张图集）。
- 清单 schemaVersion 不变（`subSprites` 自 v5 已有）。

### 6.3 邻件查找（`ConstructionRules`）

新增只读查询：`TryGetPlacedAtCell(int cellX, int cellY, out EntityId)`（用 `_footprintByEntity` 的
`_body` 矩形；一个格最多一个件由建造规则保证）。房间层按 §4.3 的本地 X ±1 格查左右邻件。

## 7. 规则层（`GameplayRules`）

- 新状态：`AddRope(EntityId host, ReadOnlySpan<(EntityId Entity, PhysicsBodyId Body)> segments,
  PhysicsBodyId? leftAnchor, PhysicsBodyId? rightAnchor)`；`ClearRopes()`。
- `RunRopes(GameplayTickOutput output)`：放在 `RunMotors` 之后、`ApplyDampingOverrides` 之前
  （内力只读本 tick 快照；顺序固定保证确定性）。
- 输入：`_kinematicsByBody`（已有，记录**所有** body，包括子实体）、质量、`_rotationByBody` 不需要。
- 输出：`output.Commands` 的 `ApplyImpulse`（世界点 = 段中心；端点拉 = 最外段位置）与 §4.5 的
  阻尼覆写。
- `RunRopes` 里对每条未剪绳先 `TryConsumeButtonPress(host)`；为真 → `CutIndex = n/2`（按钮 = 中间）。
- `CutRope(uint owner, float x, float y)`：按 §4.6b 解析 owner 的绳与最近段，返回 `(bool cut,
  ConstructionError error)`；由房间在 `ExecuteCommand` 调用。剪断只改 rope 状态（`CutLink`），不动刚体。
- `AreConnected`（transport 挑战）：把每条未剪绳的左右邻件 body 视为一条边——`PowerClusterKey` 不同但
  被绳相连 → 视为连通（T5）。剪断后去掉该边。
- 常量全部按 §3 的 `Rope.cs` 行号写在代码里，注释引用；不散落魔数。

## 8. 房间层（`GameRoom`）

| 位置 | 改动 |
|---|---|
| `Start` / `ResetPlayer` / `Retry` 的 `CollectPartEntities()` | 过滤掉 `capabilities.rope` 的 host（它们不进 `Assemble`） |
| `FillEntityOrder` | 把**无刚体的 rope host** 也加进 `_entityOrder`（`bodyId = 0`），让开关条在运行帧能看到它的按钮 |
| `RegisterPlacedRole` | `activation: trigger` 走既有 `AddActivation`（G115），host 无 body 不影响 |
| `Start`（`BindAttachments` 之后） | `MaterializeRopes(entities)`：每条绳创建 8 个子实体 + 端点 `Revolute` + 绳→绳铰；`_rules.AddRope(...)` |
| `RegisterPlacedRole` | 识别 `capabilities.rope`（不注册 activation/motor） |
| `DestroySubEntitiesOf` / `ClearSubEntities` | 已有；确认 rope 段也走同一条路 |
| `RebindSubEntityJoints` | rope 分支：重建端点/绳间铰 |
| `EnsureBuffers` | `MaxSnapshotEntityCount` 按 host+段数计（`CreateSubEntity` 已进 `_parts`/`_bodyByEntity`，自动） |
| `ApplyDampingOverrides` | 增加 body-keyed 的段阻尼（§4.5） |
| `ExecuteCommand` | 新 case `CutRopeCommand`：Running 下调用 `_rules.CutRope(owner, x, y)`，失败映射到 `ConstructionError.RopeNotAtPoint` |
| `CommandValidator` | `CutRopeCommand`：Building → `WrongMode`；Running → `Accepted`（与 `SetPartActive` 同一条） |
| `BindAttachments` 等 | 无 body 的 host 已被现有 `TryGet` 跳过，不需额外判断 |

## 9. 客户端与协议

- **PGFC 升 v4**：新增 `ClientCommandKind.CutRope = 10`，载荷 `x:f32, y:f32`（世界坐标，z 忽略），
  帧长 19 + 8 = **27 B**。`ProtocolVersion.Current` 3 → 4；`schemas/client-command-v1.schema.json`
  的 `version` 与 `kind` 枚举同步；客户端 `encodeCommand.ts` 与 `SNAPSHOT/CMD` 版本常量同批改。
  旧客户端会被整帧拒绝（与 ADR-030 升 v3 同一策略）。
- **PGFS 版本不变**：段是普通实体，`partTypeId` = host 的 41（或 214..216），`flags` bit1 = 子实体。
- **`bodyId = 0` 的语义扩展（无线上改动）**：建造帧里 `bodyId 0` 是预览（照画）；**运行帧里出现
  `bodyId 0` 的实体 = 有归属但没有物理体的 host（绳件）**。客户端保留它（开关条要用它的 trigger 按钮），
  **不画它的美术**（原版 `renderer.enabled=false`）。运行帧原本没有任何 `bodyId 0` 实体（所有件都有体），
  所以这条规则无歧义。实现落点：`decodeSnapshot` 后的绘制过滤 `phase != Building && bodyId == 0`。
- 客户端：现有 `subEntityTexture(texture, subEntity)` 在段实体上取 `subSprites`（新增 1 条清单数据），
  不需要新代码路径；`draw.test` 加一条「绳段用 subSprite」。
- 建造帧与预览不变（host 的普通精灵）。
- 后续（不在本片）：用同一 host 的连续段 id 画平滑折线；或加线协议段（需要新的 wire 决定）。

## 10. 测试

### Core（`RopeRulesTests`，合成 level）

1. 静止链（d=0.5）不出力；`d=0.75` 的内力对上 `70·0.25^0.75`；`d=0.35` 的推力对上 `50·(−0.05)`。
2. 阻尼：`V=0` → 6.5；`V=1` → 1.7（每 tick 覆写全部段）。
3. 断判：内段 `d=1.26` 剪、端段 `1.26` 不剪、端段 `1.76` 剪；剪后跳过该 link 且不再断。
4. 拉伸端点拉：`total > 1.18·4` 时端点力方向与 `clamp(750·factor, 0, 300)`；`factor` 下降时 ×0.7。
5. 未连接端没有端点力。
6. 按钮：`SetActive(host,true)` 后 `RunRopes` 消费一次，`CutIndex == segments/2`（8 → 4）；再按 no-op。

### Protocol

1. `CutRope` 编码/解码（27 B，x/y 为 float32 往返）；v4 帧头；v3 命令被拒。
2. `client-command-v1` schema 的 kind 枚举含 10、version 4。

### Server（`RopeRoomTests`，真内容 + 真 Bepu + 合成 v10 文档）

1. 木框 + 绳 + 木框：START 后 8 个段 body、host 无 body、段碰撞关闭（与地面不接触）。
2. 两端铰住：N tick 后两端段与邻件距离 < 0.05；自由端下垂。
3. 绳→绳：16 段、一个段-段铰、无重复 host。
4. 快速拉开一端 → 出现剪断（`CutLink >= 0`），断后两半自由。
5. Retry / 断连 / RESET：段与铰全部销毁，host 回到预览；再次 Start 段重建。
6. 快照：运行帧段实体带 bit1、host 缺席；双跑 hash 相等。
7. **按钮**：Running 下对 host 发 kind 8 → `status 0`，`CutIndex == 4`；开关条能拿到 host（运行帧里 `bodyId == 0`）。
8. **点击剪切**：在段 i 的世界位置发 `CutRope` → `CutIndex = clamp(i,2,6)`；点在外侧段（i=0/7）被 clamp；
   点离所有段 > 0.5 → `RopeNotAtPoint`；对同一绳再点 → Accepted 且 `CutIndex` 不变；Building 下 → `WrongMode`；
   沙盒里点别人的绳 → 拒绝。
9. `transport` 挑战：两车被绳相连 → `AreConnected` 为真；剪断后为假（T5）。

### web

1. `part-textures.json` part 41 有 `subSprites`；`subEntityTexture` 返回它。
2. `playParts.generated.json` 含 `rope` 能力与 `activation: trigger`（生成器一致性）。
3. 运行帧里 `bodyId 0` 的 host 不进绘制列表（`drawFrame` 不画它），但开关条把它列成「剪绳子」按钮。
4. `pnpm test` / `pnpm build`。

### 工具

`node tools/bple-ropes/extract-ropes.mjs && node tools/bple-ropes/apply-ropes.mjs`：
第一次写入 4 行、第二次 0 改动；`bple-textures` 重跑 0 警告。

## 11. 偏差（有意）

| # | 偏差 | 理由 |
|---|---|---|
| ~~D1~~ | ~~用户点击绳段剪切不做~~ **2026-10-19 收进本片，两条入口都做**：①按钮（kind 8）剪中间；②世界点击（PGFC v4 `CutRope(x,y)`）按最近段裁决 | 原版 `ProcessTouch()`/`ProcessTouch(Vector3)` 两个入口（`BasePart.cs:924-938`、`Rope.cs:169-190`）；见 §4.6b / §9 |
| D2 | 用折线代替 LineRenderer 的 Catmull-Rom 样条 | 客户端把同一绳的连续段 id 连成折线（`ropeChains`），美术上与原版 20 点平滑样条有差异；段 prefab 本身没有精灵（`RopeSegment` 的 Visualization 是禁用的 MeshRenderer），所以不能逐段贴图 |
| D3 | `CollidableRope`（B 档）不建模 | vanilla false；B 档还会把 Rare/Epic（Rope_03/04）段提升到 Contraption 层 |
| D4 | 50→60 Hz | 每 tick 冲量 = `F/60`（同一物理力的正确换算） |
| D5 | HingeJoint 用 `Revolute` | 轴向一致、锚点一致；`enablePreprocessing` 在 PigForge 无对应（求解器无预处理开关） |
| D6 | `m_gridVisualizationNode`（仅 Rope_03）不画 | 建造期网格提示，纯表现 |
| D7 | Rope_05..08（`HingePlate`）不进内容 | vanilla `HingePlate=false`；它是另一个类（两块板体） |
| D8 | host 无刚体 | 原版是 kinematic mass 0；语义等价，且避免 host 美术在运行帧重复出现 |
| D9 | 绳→绳在**创建时**把上一条绳的最后一段放到下一条绳的第一段位置（不是原版的运行期 `setPosition`） | 没有 teleport API；创建时算好等价，且铰点在两段中心重合 |
| D10 | 段体自转的 `gridRotation != 0` 额外 `−90°` 不逐字复刻 | 只影响精灵朝向；链的轴向/物理在零件本地系里一致 |
| D11 | 单发计数不建模 | PigForge 没有 `m_oneShotPartAmount` 的消费者；剪断只记布尔 |
| D12 | 段体 `angularDamping` 取 prefab 的 0.5 | 原版只改 `drag`，angularDrag 保持 prefab 值 |
| D13 | 非线性段碰撞 | 段碰撞关闭；原版靠 layer 15，B 档才有例外 |
| D14 | 无刚体 host 在运行帧用 `bodyId = 0` 表达 | 不新增 flag/版本；建造帧的 `bodyId 0` 仍是预览（照画），运行帧的 `bodyId 0` = 有归属无物理体（不画美术、只进开关条）。运行帧原本没有 `bodyId 0` 实体，语义无歧义 |

## 12. 分片与验收

| 片 | 内容 | 验收 |
|---|---|---|
| **T1** | `tools/bple-ropes` + 内容 `capabilities.rope` + schema/parser/客户端类型 + 单格 shapes | 工具两次幂等；内容 290 件仍通过加载；`PartContentTests` 新用例读 8/0.05/0.09/0 |
| **T2** | host 过滤 + 8 段子实体 + 端点 `Revolute` + 内力链（拉/推/空气阻/阻尼） | `RopeRoomTests` 1–3、Core 1–2；实机：框-绳-框 START 后 8 段、下垂且两端不脱开 |
| **T3** | 张力断 + 拉伸端点拉 + 绳→绳 | Core 3–5；`RopeRoomTests` 4；实机快速拉一端观察到断 |
| **T3c** | **按钮剪中间**：`activation: trigger` + `RunRopes` 消费按下 + host 的 `bodyId 0` 发布 | Core 6；`RopeRoomTests` 7；web 3；实机按钮剪中间 |
| **T3d** | **世界点击剪切**：PGFC v4 `CutRope` + `RopeNotAtPoint` + 服务器最近段裁决 | Protocol 1–2；`RopeRoomTests` 8；实机点段即断、点空白不报错 |
| **T4** | 贴图 `subSprites` + 客户端点击命中 + 浏览器验证 | web 1–3；真 Chromium 里 8 段可见、点某段该段断开 |
| **T5** | `AreConnected` 绳边（transport） | Server 9；剪断前后双向 |

检查点：T2 后（段真的被两端拉住、不再焊成木板）、T4 后（画面正确）、T5 后（挑战语义）。

## 13. 开放问题

1. **视觉**是否要在本片就做样条（需要决定 host 分组方式）。
2. `CutRope` 的世界坐标是唯一 payload（本片按原版：只在段附近才切）；按钮路径已经覆盖「不点段也剪中间」。
3. **T5 的连接性**是否与 `power cluster` 合并方式一致（`AreConnected` 之外还有没有别的消费者）。
4. Jolt 后端（不在 `PigForge.slnx`）对 `Revolute` 抛 `NotSupportedException` —— 绳只在 Bepu 可用，
   与轮子/弹簧的既有约定一致，是否需要文档标注（`docs/specs/physics-replay-v2.md`）。

## 14. 参考

- `docs/decisions/ADR-027-runtime-sub-entities.md`（子实体）
- `docs/specs/body-defaults.md` §6.4（逐节阻尼公式，原本写着「待绳实现」）
- `docs/specs/level-props.md` §3.9（**关卡里的绳桥** `Bridge` 的 `StepRope` 实例化；与本片的绳件不同）
- `tasks/rope-sandbag-materialization-report.md`（只读调研；本片只取绳，沙袋/气球留 G52/G53）
- 原版：`Rope.cs`、`RopeSegment.prefab`、`Part_Rope_01..08_SET.prefab`、`Contraption.cs:2499-2525`、
  `INDeclarationSettingsExp.json`


## 15. 交付记录（2026-10-19）

**实现**：内容 `capabilities.rope`（四行）+ `activation: trigger` + 单格建造盒；`tools/bple-ropes` 提取/应用
（源码字面量硬断言）；`ScenarioLoader` 的 rope 能力解析；`GameRoom` 把 host 排除出装配、物化 8 段子实体
（球 r0.09、mass 0.05、2.5D、碰撞关闭）、端点 `Revolute`、绳→绳铰；`GameplayRules` 力链（拉/推/空气阻/
逐节阻尼）、张力断、拉伸端点拉、按钮剪中间、`AreConnected` 绳边；PGFC **v4** `CutRope(x,y)` + 客户端点击
命中；运行帧的 bodyId 0 host（开关条）与 `ropeChains` 折线；`rope-bench` 场景 + `bench-v1` 测试关卡。

**读数**（真服务器 + 真 Bepu + 真 Chromium，`--play --dev`）：

| 步骤 | 读数 |
|---|---|
| `scenario=rope-bench`（框 + 绳 + 框） | Start 后 8 段、host 运行帧 `bodyId 0`、开关条 `1绳索`；红折线连接两框 |
| 按「绳索」按钮 | 服务端 `CutIndex = 4`（`RopeRoomTests.TheButtonCutsTheMiddle`），客户端无错误 |
| 点击绳段（`clickAt`） | 选中实体 1048588（段，`subEntity`），无 `RopeNotAtPoint`；`CutRope` 被接受 |
| 绳→绳 | 16 段、一条段-段铰；两框间的端点距离 < 0.1 m（`TwoRopesChainTheirEndSegments`） |
| `AreConnected` | 未剪为真、剪后为假（`AnIntactRopeConnectsItsTwoSidesAndACutRopeDoesNot`） |

**测试**：Core **438**（+5 `Rope*` 规则用例，另改 `GameplayHarness` 链接）、Server **191**（+7
`RopeRoomTests`）、Protocol **47**（+2 CutRope）、Replay 11、Physics 96（75+21）、web **436**
（+2 `ropeChains`/gadget 段、+1 CutRope 编码；`COMMAND_VERSION=4`）；Release 0 警告 0 错误；
`bple-ropes` 第二次 0 改动；`scenarios.mjs --check` 4 ok（`rope-bench`）。

**实测期间修掉的三处**：①规则层丢弃了段体命令（段未 `LinkBody` → `_dynamicBodies` 不含它）——
`CreateRope` 现在给每段 `LinkBody`；②绳→绳的中心相距 0.5 m 是因为没有复刻原版的「把最后一段移到位」：
创建时把它放到下一绳的第一段位置（D9）；③发布 bodyId 0 的 host 时 `TryPublishSnapshot` 假定每个实体都有
刚体（键不存在崩溃）——现在按无刚体写 build 位姿，帧头计数用 `_entityOrder.Count`。
