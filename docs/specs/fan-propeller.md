# 规格：风扇 / 螺旋桨 / 旋翼（原版 `FanPropeller`）

状态：已实施（真值已核实并按 §4 落地）。
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
| `38` propeller | `wheel + motor{2.5}`，`toggle` | 被 `tools/bple-power` 的「有 `InitializeEngine` 覆盖即驱动轮」判据误判成**轮子**（`FanPropeller` 确实覆盖了它），装配时被铰接成独立刚体，冲量只推自己 → 只自转、**不推进** |

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
- **`maxSpeed`** = `m_defaultSpeed × IN <X>Speed`，即**单位功率因子**下的轴向最高速；规则层再乘 `powerFactor`（真值 2/3）。`PropellerSpeed = Infinity` 的原版没有上限 → **不写**该字段。
- **`rotor`** = 原版 `m_isRotor`；缺省 false。
- **`activation` 必须是 `toggle`**：解析器把 `fan` + 非 `toggle` 当内容错误拒绝（`FanPropeller` 永远是开关件，`trigger` 正是「放气摧毁」的来源）。

真值唯一来源 `tools/bple-fans/`（`extract-fans.mjs` + `apply-fans.mjs`，报告驱动、幂等、`--dry-run`）。硬断言：FanPropeller prefab = 26、类型 6/10/10、`m_isRotor` 恰好 10 件且全在旋翼族、方向直方图 Left 7 / Right 9 / Up 9 / Down 1、逐件换算一致、气球族同分母见证。

## 4. 决议

1. 沿用 `fan` **一个键**承载三个族的语义（三者在原版是同一个类，没有理由拆键）；新增 `maxSpeed`/`rotor`。
2. 换算取 **`m_force / 60`**（ADR-022 决策 3 记录了被否掉的「以手写 1.2 做族内锚」方案：它把旋翼冲量放大到 16.6 > 接缝阈值 10，旋翼会扯断自己的焊缝）。
3. 规则层逐字实现真值 5/6（`RunFans`），旋翼不再走 `RunBalloons`，`RunBalloons` 的「带功率数据的升力件」死分支删除。
4. **本切片只转风扇 + 旋翼**（16 件）；**螺旋桨 10 件缓办**：其 `PropellerSpeed = Infinity` → 转成 `fan` 就是一个**没有上限**的推进器（原版靠飞机阻力与重量收住），定上限属玩法决定。`tools/bple-fans` 照常给出它们的换算值并标注「缓办」，内容保持 `wheel + motor` 不动。
5. 不动协议：`maxSpeed`/`rotor` 只存在于内容与服务端规则态，PGFS/PGFC 不变。
6. 顺带修掉房间停帧（ADR-023）：拆簇会销毁「本 tick 指令所指」的 body，那些指令必须同步丢弃。

## 5. 落值（`tools/bple-fans` 输出）

| 内容 id | prefab | 类型 | 方向 | m_force | thrustPerTick | maxSpeed | rotor |
|---|---|---|---|---|---|---|---|
| `11`（+ `74`–`77`） | Part_Fan_01…05 | Fan | Left | 7 | 0.116667 | 18 | – |
| `78` fan-v06 | Part_Fan_06 | Fan | Left | **60** | **1.0** | 18 | – |
| `37`（+ `184`–`192`） | Part_Rotor_01…10 | Rotor | **Up**（`191` = **Down**） | 120 | 2.0 | 14 | true |
| `38`（+ `135`–`143`） | Part_PlanePropeller_01…10 | Propeller | Right（`143` = Left） | 37 | 0.616667（**缓办，未写入**） | ∞ | – |

## 6. 验收（已跑）

| 项 | 结果 |
|---|---|
| 单测 | Core **225** / Server **102** / Protocol **39** / Replay **11** / Physics **38**（6 Jolt + 32 非 Jolt，分开跑）/ web **224**；Release 构建 **0 警告 0 错误**；`playParts.generated.json --check` = `parts: 267` |
| 实机探针（真服务器 + 真内容 + 真 Bepu，`ws://…/play`） | 木框 + 包裹引擎（enginePower 150 / 旋翼消耗 200 → 因子 0.806）+ 旋翼：开关打开后竖直速度**稳定在 12.8 m/s**（上限 `14 × 0.806 ≈ 11.3` + 刹车平衡点），不再无限加速；关开关后速度按重力精确衰减（12.80 → 7.90 用 0.5 s）且**旋翼实体仍在**（`rotorPresent: true, active: false`） |
| 门控 | `ARotorWithoutAnEnclosedEngineInItsClusterDoesNotLift`（真内容）：簇内无引擎 → 一动不动 |

## 7. 不做

- **螺旋桨转换**（§4 决议 4）：先要一个上限决定。
- 施力点：原版加在 `transform.position + dir × 0.5`（真值 4），PigForge 仍按刚体中心施加，忽略那点力矩。
- 旋翼角阻尼（真值 7）：物理契约没有角阻尼项。
- 左向风扇的贴地/悬浮射线增益（`:166-197`，`StableLevitationFan`/`ReactionFan`）。
- 旋翼的 `m_rotorTargetDirection` 方向混合（`:156-161`）。
- 风扇关闭后的转速衰减曲线：**客户端已实现**（`clients/web/src/renderer/animation/spin.ts`），纯表现层不上线。
- 引擎按钮联动全部耗能件（`Engine.cs:29`），仍见 `docs/specs/power-system.md` §7。
