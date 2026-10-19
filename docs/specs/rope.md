# Spec: 绳的运行时实例化（Rope 件 → 8 段链）

> 2026-10-19。范围：`G63`（绳）。决策依据 `ADR-027`（运行时子实体：一个放置件 → N 个实体/刚体）。
> 原版真值全部逐行取自 `BPLE 2022.1.9`（原版自己的编辑器版本），调研底稿：
> `tasks/rope-sandbag-materialization-report.md`（只读报告，未入库）。

## 1. 一句话

原版的绳不是一个刚体，而是**放置一个 1 格件、START 时实例化 8 个带球形碰撞体的真实刚体**，端点用
HingeJoint 铰到相邻件、段与段之间**没有关节**（一串自研力链），整条绳再用 LineRenderer 画样条。
PigForge 现在把绳当成一根 3 格木板焊进车架（`CompoundAssembler.CanMerge` 直接把它并簇），完全不是原版行为。

本片把「**放置一件 → 8 个运行时子实体**」落地，并按原版重写段内力链、端点铰与实际断裂。

## 2. 范围

| 做 | 不做（本片） |
|---|---|
| 8 段子实体（ADR-027）、段体形状/质量/材质/阻尼 | 用户点击绳段剪切（`OnTouch`，需要位置或协议位，见 §11 D1） |
| 端点 `Revolute`（= 原版 `HingeJoint axis=forward`） | 绳段的 LineRenderer 样条（客户端本片画逐段精灵，见 §9、D2） |
| 段内力链（拉/推/空气阻/逐节阻尼） | B 档 IN 分支：`StableRope` / `UnbreakableRope` / `CollidableRope`（D3） |
| 张力断裂（内段 1.25 m / 端段 1.75 m，vanilla 开着） | `HingePlate` 变体（Rope_05..08，vanilla `HingePlate=false`，D7） |
| 绳→绳串联（同朝向） | 单发计数 `ChangeOneShotPartAmount`（D11） |
| 建造期预览 = 单件美术；运行期只发 8 段 | 段体的 `-90°` gridRotation 贴图朝向细节（D10） |
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
- 不写 `activation`（用户剪切不在本片）。

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
- `AreConnected`（transport 挑战）：把每条未剪绳的左右邻件 body 视为一条边——`PowerClusterKey` 不同但
  被绳相连 → 视为连通（T5）。剪断后去掉该边。
- 常量全部按 §3 的 `Rope.cs` 行号写在代码里，注释引用；不散落魔数。

## 8. 房间层（`GameRoom`）

| 位置 | 改动 |
|---|---|
| `Start` / `ResetPlayer` / `Retry` 的 `CollectPartEntities()` | 过滤掉 `capabilities.rope` 的 host（它们不进 `Assemble`） |
| `Start`（`BindAttachments` 之后） | `MaterializeRopes(entities)`：每条绳创建 8 个子实体 + 端点 `Revolute` + 绳→绳铰；`_rules.AddRope(...)` |
| `RegisterPlacedRole` | 识别 `capabilities.rope`（不注册 activation/motor） |
| `DestroySubEntitiesOf` / `ClearSubEntities` | 已有；确认 rope 段也走同一条路 |
| `RebindSubEntityJoints` | rope 分支：重建端点/绳间铰 |
| `EnsureBuffers` | `MaxSnapshotEntityCount` 按 host+段数计（`CreateSubEntity` 已进 `_parts`/`_bodyByEntity`，自动） |
| `ApplyDampingOverrides` | 增加 body-keyed 的段阻尼（§4.5） |
| `BindAttachments` 等 | 无 body 的 host 已被现有 `TryGet` 跳过，不需额外判断 |

## 9. 客户端与协议

- **协议零改动**：段是普通实体，`partTypeId` = host 的 41（或 214..216），`flags` bit1 = 子实体。
  host 在运行帧不出现（无 body），客户端不会重复画。
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

### Server（`RopeRoomTests`，真内容 + 真 Bepu + 合成 v10 文档）

1. 木框 + 绳 + 木框：START 后 8 个段 body、host 无 body、段碰撞关闭（与地面不接触）。
2. 两端铰住：N tick 后两端段与邻件距离 < 0.05；自由端下垂。
3. 绳→绳：16 段、一个段-段铰、无重复 host。
4. 快速拉开一端 → 出现剪断（`CutLink >= 0`），断后两半自由。
5. Retry / 断连 / RESET：段与铰全部销毁，host 回到预览；再次 Start 段重建。
6. 快照：运行帧段实体带 bit1、host 缺席；双跑 hash 相等。
7. `transport` 挑战：两车被绳相连 → `AreConnected` 为真；剪断后为假（T5）。

### web

1. `part-textures.json` part 41 有 `subSprites`；`subEntityTexture` 返回它。
2. `playParts.generated.json` 含 `rope` 能力（生成器一致性）。
3. `pnpm test` / `pnpm build`。

### 工具

`node tools/bple-ropes/extract-ropes.mjs && node tools/bple-ropes/apply-ropes.mjs`：
第一次写入 4 行、第二次 0 改动；`bple-textures` 重跑 0 警告。

## 11. 偏差（有意）

| # | 偏差 | 理由 |
|---|---|---|
| D1 | 用户点击绳段剪切不做 | 原版按点击位置选段（`clamp 2..n−2`）；我们的 `SetPartActive` 只有整件开关，且 host 运行期不发布（没有开关条入口）。后续二选一：kind 8 剪中段，或给线上加「位置」/子实体 id |
| D2 | 不画 LineRenderer 样条 | 客户端本片逐段画精灵；样条需要把同 host 的段分组（线上没有 host id）或加线协议 |
| D3 | `CollidableRope`（B 档）不建模 | vanilla false；B 档还会把 Rare/Epic（Rope_03/04）段提升到 Contraption 层 |
| D4 | 50→60 Hz | 每 tick 冲量 = `F/60`（同一物理力的正确换算） |
| D5 | HingeJoint 用 `Revolute` | 轴向一致、锚点一致；`enablePreprocessing` 在 PigForge 无对应（求解器无预处理开关） |
| D6 | `m_gridVisualizationNode`（仅 Rope_03）不画 | 建造期网格提示，纯表现 |
| D7 | Rope_05..08（`HingePlate`）不进内容 | vanilla `HingePlate=false`；它是另一个类（两块板体） |
| D8 | host 无刚体 | 原版是 kinematic mass 0；语义等价，且避免 host 美术在运行帧重复出现 |
| D9 | 绳→绳不做运行期 setPosition | 相邻格的端点本来就重合；不需要 teleport API |
| D10 | 段体自转的 `gridRotation != 0` 额外 `−90°` 不逐字复刻 | 只影响精灵朝向；链的轴向/物理在零件本地系里一致 |
| D11 | 单发计数不建模 | PigForge 没有 `m_oneShotPartAmount` 的消费者；剪断只记布尔 |
| D12 | 段体 `angularDamping` 取 prefab 的 0.5 | 原版只改 `drag`，angularDrag 保持 prefab 值 |
| D13 | 非线性段碰撞 | 段碰撞关闭；原版靠 layer 15，B 档才有例外 |

## 12. 分片与验收

| 片 | 内容 | 验收 |
|---|---|---|
| **T1** | `tools/bple-ropes` + 内容 `capabilities.rope` + schema/parser/客户端类型 + 单格 shapes | 工具两次幂等；内容 290 件仍通过加载；`PartContentTests` 新用例读 8/0.05/0.09/0 |
| **T2** | host 过滤 + 8 段子实体 + 端点 `Revolute` + 内力链（拉/推/空气阻/阻尼） | `RopeRoomTests` 1–3、Core 1–2；实机：框-绳-框 START 后 8 段、下垂且两端不脱开 |
| **T3** | 张力断 + 拉伸端点拉 + 绳→绳 | Core 3–5；`RopeRoomTests` 4；实机快速拉一端观察到断 |
| **T4** | 贴图 `subSprites` + 浏览器验证 | web 1–3；真 Chromium 里一段绳的 8 个精灵可见、无 host 重复图 |
| **T5** | `AreConnected` 绳边（transport） | Core 6 / Server 7；剪断前后双向 |

检查点：T2 后（段真的被两端拉住、不再焊成木板）、T4 后（画面正确）、T5 后（挑战语义）。

## 13. 开放问题

1. **用户剪切**（D1）走哪条路：`SetPartActive` 剪中段（零协议改动）还是加子实体定位/线协议位？
2. **视觉**是否要在本片就做样条（需要决定 host 分组方式）。
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
