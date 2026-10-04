# 规格：风扇 / 螺旋桨 / 旋翼（原版 `FanPropeller`）

状态：已实施（真值已核实并按 §4 落地；**§5.2 的螺旋桨转换于 2026-10-04 补做，见 §4 决议 4**）。
来源：原版反编译脚本 `BPLE_Unity6/Assets/Scripts/Assembly-CSharp/FanPropeller.cs`、`BasePropulsion.cs`、
`Assets/GameObject/Part_{Fan,PlanePropeller,Rotor}_01_SET.prefab`、`Assets/TextAsset/INSettingsBExp.json`。
决策记录：`docs/decisions/ADR-022-fan-propeller-thrust.md`（模型）与 `ADR-023-split-drops-pending-commands.md`（顺带修掉的房间停帧）。

## 1. 要解决的偏差

差距清单 G50（`tasks/original-vs-implemented.md` §6）：三个零件在原版是**同一个类** `FanPropeller`
（`m_partType` 区分 `Fan`/`Propeller`/`Rotor`），PigForge 却用了三套互不相同的近似：

| 件 | 改动前 | 问题 |
|---|---|---|
| `11` fan | `fan{thrustPerTick 1.2, directionX 1}`，`toggle` | 缺最高速上限（原版 `LimitForceForSpeed`）；方向与 prefab 相反（`m_forceDirection: 2` = Left） |
| `37` rotor | `balloon{3.5}`，`trigger` | **升力无上限**（实测一个簇升到 20 km）；开关 = 气球式「放气**摧毁**」；不是原版的推力 |
| `38` propeller | `wheel + motor{2.5}`，`toggle` | 手写成**轮子**：`FanPropeller` 确实和驱动轮一样覆盖 `InitializeEngine`（`:83`），但它是推进器（`tools/bple-power` 的驱动轮判据按类名 `*Wheel` 收口，从未算进这一族；`2.5` 也不是它对 `m_force` 37 的换算值 1.628）。装配时被铰接成独立刚体，`RunMotors` 又把带 `wheel` 的马达门控在地面接触上 → **既不推进也不出力** |

## 2. 原版真值（逐条出处）

| # | 规则 | 出处 |
|---|---|---|
| 1 | 开关：默认关（`Awake`），`OnTouch` 翻转，`HasOnOffToggle() = true`；关掉只是停止出力，**零件不消失** | `FanPropeller.cs:49-56,64-68,260-262,265` |
| 2 | 引擎钩子：`powerFactor = GetEnginePowerFactor(this)`，`> 1` 时 `pow(factor, 0.75)`；`maxSpeed = powerFactor × m_defaultSpeed`、`maxForce = m_force × powerFactor`、`maxRotationSpeed = 1000 × powerFactor`（`> 0` 时再加 700） | `FanPropeller.cs:83-112` |
| 3 | IN 全局倍率：`FanForce 1.0 / FanSpeed 6.0`、`PropellerForce 1.0 / PropellerSpeed Infinity`、`RotorForce 1.0 / RotorSpeed 2.0`，按 `m_partType` 分别乘到力与最高速 | `INSettingsBExp.json:284-311`；`FanPropeller.cs:93-107` |
| 4 | 出力：`force = LimitForceForSpeed(maxForce, dir)`，沿 `dir = TransformDirection(GetDirectionVector(m_forceDirection))`，加在 `position + dir × 0.5`；Force 模式（每秒的力） | `FanPropeller.cs:151-165,209` |
| 5 | 限速：`v∥ = \|v\| × dot(v̂, dir)`，`v∥ > maxSpeed` 时 `force = maxForce / (1 + v∥ - maxSpeed)`；否则全额 | `FanPropeller.cs:245-257` |
| 6 | 旋翼附加刹车：`m_isRotor && !enclosed && enabled` 且 `\|v\| > maxSpeed && dot(dir, v) > 0` 时 `force += -4 × (\|v\| - maxSpeed)² × v̂` | `FanPropeller.cs:198-207` |
| 7 | 旋翼姿态阻尼：启用时 `angularDamping = 1000`、关闭时 `1` | `FanPropeller.cs:138-148` |
| 8 | 推进件底盘门控：至少 1 个底盘（框）邻居，否则无效 | `BasePropulsion.cs:7-20` |
| 9 | 旋转视觉：`m_angle += m_rotationSpeed × dt`；`m_isRotor ? Vector3.up : Vector3.right` | `FanPropeller.cs:118-142,319-324`（客户端已实现） |
| 10 | 序列化真值（基座 prefab） | `Part_Fan_01_SET.prefab:115,141-145`（force 7 / dir 2=Left / isRotor 0 / speed 3 / 耗能 30）；`Part_PlanePropeller_01_SET.prefab:89,115-119`（37 / 0=Right / 0 / 16 / 100）；`Part_Rotor_01_SET.prefab:107,133-137`（120 / 1=Up / 1 / 7 / 200） |

原版没做、本规格也不做的部分见 §7。

## 3. 内容模型与换算

```json
"fan": { "thrustPerTick": <finite>, "directionX": -1|0|1, "directionY": -1|0|1,
         "maxSpeed": <finite>?, "rotor": true? }
```

- **`thrustPerTick = m_force × IN <X>Force / 60`**：原版在 `FixedUpdate` 施加的是每秒的力，容器每 60 Hz tick 施加一次冲量；这条换算与 ADR-013 决策 4 给气球的（`tools/bple-lift`：23 N → 0.383333）同源，也与容器接缝阈值 `GameplayConfig.SeamBreakImpulse = 10` 同单位。
- **`maxSpeed`** = `m_defaultSpeed × IN <X>Speed`，即**单位功率因子**下的轴向最高速；规则层再乘 `powerFactor`（真值 2/3）。`PropellerSpeed = Infinity` 的原版没有上限 → **不写**该字段，规则层（`RunFans`：`fan.MaxSpeed > 0` 才限速）与旋翼刹车（同样要求 `maxSpeed > 0`）都会跳过它。缺省**不等于** 0 上限。
- **`rotor`** = 原版 `m_isRotor`；缺省 false。
- **`activation` 必须是 `toggle`**：解析器把 `fan` + 非 `toggle` 当内容错误拒绝（`FanPropeller` 永远是开关件，`trigger` 正是「放气摧毁」的来源）。

真值唯一来源 `tools/bple-fans/`（`extract-fans.mjs` + `apply-fans.mjs`，报告驱动、幂等、`--dry-run`）。硬断言：FanPropeller prefab = 26、类型 6/10/10、`m_isRotor` 恰好 10 件且全在旋翼族、方向直方图 Left 7 / Right 9 / Up 9 / Down 1、逐件换算一致、气球族同分母见证。

## 4. 决议

1. 沿用 `fan` **一个键**承载三个族的语义（三者在原版是同一个类，没有理由拆键）；新增 `maxSpeed`/`rotor`。
2. 换算取 **`m_force / 60`**（ADR-022 决策 3 记录了被否掉的「以手写 1.2 做族内锚」方案：它把旋翼冲量放大到 16.6 > 接缝阈值 10，旋翼会扯断自己的焊缝）。
3. 规则层逐字实现真值 5/6（`RunFans`），旋翼不再走 `RunBalloons`，`RunBalloons` 的「带功率数据的升力件」死分支删除。
4. **~~本切片只转风扇 + 旋翼（16 件）~~ → 2026-10-04 补做：26 件全部转换**。螺旋桨当时被缓办，理由是「`PropellerSpeed = Infinity` → 转成 `fan` 就是一个没有上限的推进器，给不给上限是玩法决定」——那是因为当时 PigForge **完全没有阻尼**。ADR-025 把原版每件刚体的 `linearDamping 0.2` 落地后，终端速度就有了原版自己的机制（`v_eq = m_force × IN PropellerForce / (m × drag)`，即每 tick 冲量与每 tick 衰减的平衡），不需要任何人拍一个上限；于是 10 件照原版真值转成 `fan{ thrustPerTick 0.616667, directionX ±1 }`（**不写 `maxSpeed`**）并去掉 `wheel`/`motor`。
   `tools/bple-fans` 的 `deferred` 分类整个删除（26 件全部写入），新增两条硬断言：没有 `maxSpeed` 的件**恰好 10 件且全在 `Propeller` 族**、`maxSpeed` 缺省 ⇔ `IN <X>Speed` 是 `Infinity`。`tools/bple-power` 另加一条**内容级交叉断言**：不许有件同时带 `motor` 与 `fan`（这一族覆盖同一个钩子，正是 G50 的成因）。
5. 不动协议：`maxSpeed`/`rotor` 只存在于内容与服务端规则态，PGFS/PGFC 不变。
6. 顺带修掉房间停帧（ADR-023）：拆簇会销毁「本 tick 指令所指」的 body，那些指令必须同步丢弃。

## 5. 落值（`tools/bple-fans` 输出）

| 内容 id | prefab | 类型 | 方向 | m_force | thrustPerTick | maxSpeed | rotor |
|---|---|---|---|---|---|---|---|
| `11`（+ `74`–`77`） | Part_Fan_01…05 | Fan | Left | 7 | 0.116667 | 18 | – |
| `78` fan-v06 | Part_Fan_06 | Fan | Left | **60** | **1.0** | 18 | – |
| `37`（+ `184`–`192`） | Part_Rotor_01…10 | Rotor | **Up**（`191` = **Down**） | 120 | 2.0 | 14 | true |
| `38`（+ `135`–`143`） | Part_PlanePropeller_01…10 | Propeller | Right（`143` = Left） | 37 | 0.616667 | **不写**（原版 ∞） | – |

## 6. 验收（已跑）

| 项 | 结果 |
|---|---|
| 单测 | Core **225** / Server **102** / Protocol **39** / Replay **11** / Physics **38**（6 Jolt + 32 非 Jolt，分开跑）/ web **224**；Release 构建 **0 警告 0 错误**；`playParts.generated.json --check` = `parts: 267` |
| 实机探针（真服务器 + 真内容 + 真 Bepu，`ws://…/play`） | 木框 + 包裹引擎（enginePower 150 / 旋翼消耗 200 → 因子 0.806）+ 旋翼：开关打开后竖直速度**稳定在 12.8 m/s**（上限 `14 × 0.806 ≈ 11.3` + 刹车平衡点），不再无限加速；关开关后速度按重力精确衰减（12.80 → 7.90 用 0.5 s）且**旋翼实体仍在**（`rotorPresent: true, active: false`） |
| 门控 | `ARotorWithoutAnEnclosedEngineInItsClusterDoesNotLift`（真内容）：簇内无引擎 → 一动不动 |

### 6.1 螺旋桨转换（2026-10-04 补做）的验收

| 项 | 结果 |
|---|---|
| 快照/幂等 | `extract-fans.mjs` → 26 件（断言「无 `maxSpeed` 的件恰好 10 件且全在 `Propeller` 族」）；`apply-fans.mjs` 第一次改 10 行、第二次 **0 改动**；`extract-power.mjs` / `web-parts --check`（`parts: 284`）均通过 |
| 内容断言 | `PartContentTests.TheRealContentModelsThePlanePropellerAsAnUncappedFan`：`38`/`135`–`143` 全是 `fan`、`thrustPerTick 0.616667`、`FanMaxSpeed == null`、非 rotor、`toggle`、**不是** `wheel`、**没有** `motor`；`143` 是 Left；同时断言 `11` 的 18 与 `37` 的 14 仍在（防止「解析器把所有 maxSpeed 都丢了」也能通过） |
| 房间行为 | `PropellerThrustTests.APropellerPushesTheClusterItIsWeldedTo`（真内容 + 真 Bepu）：螺旋桨与木框**同一个 body**（`wheel` 建模时是各自一个，实测 body 11 ≠ 10）；开关关闭时 30 tick 内水平位移 ≤ 0.05 m，打开后 30 tick 位移 **1.7667 m**（旧内容同 fixture 实测 **0 m** —— 它自己的铰接刚体从不碰地，`RunMotors` 把驱动整个门控掉了） |
| 实机（真服务器 + 真内容 + 真 Bepu + 真 PGFS，空中） | 木框 + 包裹引擎（150 / 螺旋桨消耗 100 → 因子 `1.5^0.585 = 1.2678`）+ 螺旋桨：开关打开后 `vx` **2.961 → 18.441 m/s**（1.6 s 内每 0.2 s 约 +2.85 m/s，与 `0.616667 × 1.2678 / 2.9 kg × 60 ≈ 17 m/s²` 一致；没有平台期，之后只被阻尼压着逼近 `v_eq`），螺旋桨与框 body 相同、`active: true`、12 个实体、5 条 ack 全 `status 0 / error 0`、零停帧 |
| 实机（同一 fixture 落在 terrain-v1 地面上） | 静止后开开关：`vx` **0.389 → 2.932 m/s**（1.6 s，约 1.6 m/s²）——地面摩擦与螺旋桨薄板的接触把净推力吃掉大半，但不是零：推进确实发生在簇上 |
| 客户端 | 真客户端（vite，`localhost:5173`）走 UI 放「木块 + 发动机 + 螺旋桨」、连接房间、Start、开开关：画布渲染正常、零控制台错误；螺旋桨的旋转来自贴图清单的 `sprite.spin`（`FanPropeller.m_fanVisualization` 节点，`maxDegreesPerSecond 1700`，`axis x`），与内容里的 `wheel` 无关，故去掉 `wheel` 不影响它 |
| 计数 | Core **263** / Server **126** / Protocol 39 / Replay 11 / Physics **82**（16 Jolt + 66 非 Jolt，分开跑）/ web **221**；Release **0 警告 0 错误** |

## 7. 不做

- ~~**螺旋桨转换**（§4 决议 4）~~ —— **2026-10-04 已补做**（10 件转成 `fan`，**不写** `maxSpeed`）。
- ~~**推力轴不跟零件的实时姿态**~~ —— **2026-10-04 已补做，见 §7.2**（原版 `transform.TransformDirection` 已落地；旋翼的 `m_rotorTargetDirection` 混合一并接上，运行期 `angularDamping` 见下一条）。
- 旋翼的运行期角阻尼（真值 7，`:138-148`）：契约现在有角阻尼字段（ADR-025 的 `BodyDefinition.AngularDamping`），但这是一条**运行期覆盖**、不是 prefab 值，`tools/bple-damping` 把它报在 `runtimeOverrides` 里；落地排在 G90（与 `Pig.FixedUpdate` 的慢速增阻、绳逐节阻尼、`NoDrag` 一起）。
- 左向风扇的贴地/悬浮射线增益（`:166-197`）。
- ~~旋翼的 `m_rotorTargetDirection` 方向混合（`:156-161`）~~ —— **2026-10-04 已补做，见 §7.2**。
- **建造时按连接方向的自动对齐**（G96）：原版放置/邻居变化时把 `m_autoAlign == Rotate` 的件转到「连接口朝邻居」（`Contraption.cs:1889-1900`、`BasePart.RotationTo`），PigForge 的客户端只在拖拽吸附时选边、不设 yaw。推力现在跟着玩家设的角度走，但**同一个摆法的默认朝向**仍与原版不同。
- 风扇关闭后的转速衰减曲线：**客户端已实现**（`clients/web/src/renderer/animation/spin.ts`），纯表现层不上线。
- 引擎按钮联动全部耗能件（`Engine.cs:29`），仍见 `docs/specs/power-system.md` §7。

### 7.1 施力点（2026-10-04 补做）

原版把力加在 `transform.position + dir × 0.5`（真值 4，`:151-152`，`:209` 用 `AddForceAtPosition`），
`RunFans` 现在把冲量打到同一个点：零件世界位置（`body.Position + body.Rotation.Rotate(成员局部偏移)`）
`+ 方向 × 0.5`。单件刚体时这与原版**逐位一致**（原版刚体的质心就是自身形状中心，Unity 自动质心，
杠杆臂相同）；多成员簇则相对簇质心而不是零件自己的刚体——近似，见 `ADR-022` 偏差 1。
实测（`FanThrustTests`，真房间 + 真 Bepu）：木框 + 包裹引擎 + 风扇放在质心上方一格，开开关后 30 tick
角速度 **4.63 rad/s**（改前 1.2e-7，即零力矩）。

### 7.2 推力轴 = 零件自己的 transform（2026-10-04 补做，用户报「风扇推力方向反了」）

**症状**：把风扇转过去（或换个朝向摆），推力方向不变——永远按内容里的 `Left` 推 −x，与画面上
看到的朝向相反。**根因**：`RunFans` 把**件局部**的内容方向当成**世界**轴用，原版读的是
`transform.TransformDirection(GetDirectionVector(m_forceDirection))`（真值 4，`:155`）——零件的
建造角度（`Contraption.SetRotation`，绕 **z**；`BasePart.Rotate(Left, Deg_180) == Right`）与刚体的
实时姿态都在里面。风扇的 `m_forceDirection 2`(Left) 与它的连接口 `m_jointConnectionDirection 3`(Left)
在**同一侧**，所以「转 180° 摆」在原版里推力也翻 180°。

**落地**：
- `GameplayRules` 记录每个成员在体坐标系里的旋转（`LinkBody(..., localRotation)`，`GameRoom.BindCluster`
  传 `CompoundMember.LocalRotation`；同一份成员姿态，`CompoundAssembler` 用它算世界位姿），
  `RunFans` 用 `(bodyRotation * localRotation)` 旋转内容方向——**逐字**对应原版的 transform。
- 旋翼的 `m_rotorTargetDirection`（`:73-79` 在 `Initialize()` 抓一次，`:156-161` 在 `dot > 0` 时按
  0.5 混回）：新表 `_fanSpawnDirectionByEntity` 在零件**第一个被模拟的 tick**（在读开关之前，所以
  一开始关着的旋翼也抓到的是建造姿态）记录当时的轴，`y < 0` 时按原版把 y 抬到 1。限速与过速刹车
  都改用混合后的轴（原版 `vector2`），而**施力点**仍用未混合的 `vector`（`:152`）。

**读数**（真服务器 + 真内容 + 真 Bepu + 真 PGFS，`tasks/` 里的探针已删）：同一套「木框 + 包裹引擎 +
风扇」在**空中**开开关 0.5 s 后：正摆（yaw 0，风扇在车架右侧）`vx = −2.842 m/s`；转 180° 摆（风扇在
车架左侧）`vx = +2.842 m/s`——**符号相反、幅度相同**（改前转过去的那个是 −2.751，即同一个方向）。
房间用例 `PropellerThrustTests`/`FanThrustTests` 与新增的 `AFanTurnedAroundPushesTheOtherWay`
（改前红：**−2.7513 m/s**，期望 > 2）守住这条路。真客户端（vite）走 UI：放木块 + 发动机 + 风扇，
用 Q/E 把放置角设到 180° 再放到车架左侧 → Start → 点开关栏的「风扇」→ 车向右走了 **1.24 m**
（之后落在 `ramp_plank` 上停住，与之前几轮「沙盒关卡没有长直道」一致）。

**仍差的两条**（都不改方向）：左向风扇的贴地射线增益（`:166-197`，纯倍率，需要物理射线查询）与旋翼的
运行期 `angularDamping 1000/1`（排在 G90）；建造时的自动对齐见 G96。

**测试**：Core `AFanPushesAlongTheRotationItWasBuiltWith`、`AFanFollowsTheTiltOfTheBodyItIsWeldedTo`
（都做过非空验证：分别去掉成员旋转/刚体旋转即红）、Server `AFanTurnedAroundPushesTheOtherWay`
（改前红）。计数 Core **265** / Server **127** / Protocol 39 / Replay 11 / Physics **82**（66 + 16）/ web **221**；
Release **0 警告 0 错误**。
