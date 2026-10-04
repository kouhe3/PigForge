# ADR-022: FanPropeller 一族按原版推力建模（风扇 / 螺旋桨 / 旋翼）

## 状态

Accepted

## 日期

2026-10-04

## 背景

原版把**风扇、飞机螺旋桨、旋翼**做成同一个类 `FanPropeller`（`FanPropeller.cs:6`，`m_partType` 只用来挑 IN 倍率，`:93-107`）。PigForge 此前给了它们三套互不相同的近似，差距清单记为 G50：

| 件 | 旧模型 | 问题 |
|---|---|---|
| `11` fan | `fan{thrustPerTick 1.2, directionX 1}` | 没有最高速上限（原版有 `LimitForceForSpeed`，`FanPropeller.cs:245-257`）；推力方向与 prefab 相反（`m_forceDirection: 2` = Left） |
| `37` rotor | `balloon{3.5}` + `activation: "trigger"` | **升力无上限**（实测一个簇升到 20 km）；开关走气球「放气**摧毁**」路径（实体下一帧消失）；而原版 `FanPropeller` 是 `HasOnOffToggle() = true`、关掉只是停转（`:49-56,265-296`） |
| `38` propeller | `wheel: true` + `motor{2.5}` | 被 `tools/bple-power` 的「有 `InitializeEngine()` 覆盖即驱动轮」判据误判成轮子——`FanPropeller` 确实覆盖了它，但它不是轮子。装配时被铰接成独立刚体，`RunMotors` 的冲量只推它**自己**，于是它只自转、**不产生推进** |

原版的推力数值链是：`powerFactor = GetEnginePowerFactor(this)`（`> 1` 时开 0.75 次方）→ `maximumSpeed = powerFactor × m_defaultSpeed × IN <X>Speed`、`maximumForce = m_force × powerFactor × IN <X>Force`（`:83-112`）→ 每 `FixedUpdate` 沿 `m_forceDirection` 施加 `LimitForceForSpeed` 之后的力（`:209`）；`m_isRotor` 再过速刹车（`:198-207`）。

## 决策

1. **一个内容键承载整族**：沿用 `capabilities.fan`，新增两个可选字段
   - `maxSpeed`：原版 `m_defaultSpeed × IN <X>Speed`，即**单位功率因子**下的轴向最高速（规则层再乘 `powerFactor`）。`PropellerSpeed = Infinity` 的原版没有上限，内容**不写**该字段；
   - `rotor`：原版 `m_isRotor`，开启过速刹车。缺省 false。
2. **真值唯一来源 `tools/bple-fans/`**（`extract-fans.mjs` + `apply-fans.mjs`，报告驱动、幂等、`--dry-run`），与既有 `tools/bple-*` 同级；读每个映射 prefab 的 `m_force`/`m_forceDirection`/`m_isRotor`/`m_defaultSpeed`/`m_partType`。**硬断言**（漂移即 `exit 1`）：FanPropeller prefab = 26、类型 6/10/10、`m_isRotor` 恰好 10 件且全在旋翼族、方向直方图 Left 7 / Right 9 / Up 9 / Down 1、换算一致、气球族同分母见证（见 3）。
3. **冲量换算取 ADR-013 决策 4 的同一条：`thrustPerTick = m_force × IN <X>Force / 60`。** 原版在 `FixedUpdate` 施加的是**每秒的力**（`AddForceAtPosition(..., ForceMode.Force)`，`FanPropeller.cs:209`），容器每 60 Hz tick 施加一次冲量，气球的 `23 N → 0.383333` 早就是这条（`tools/bple-lift`），容器自己的接缝阈值（`GameplayConfig.SeamBreakImpulse` = 10）也在同一单位里。
   > 被否掉的备选：以风扇当时出厂的手写值 `1.2`（= `m_force` 7 的 **10.3 倍**）做族内标定锚。实测否决——它把旋翼的每 tick 冲量放大到 `120/7 × 1.2 × powerFactor = 16.6`，**超过接缝阈值 10**，旋翼把自己的焊缝扯断（`CompoundSplitRoomTests` 的机制），且让「轻量推力」的风扇（`docs/part-texture-animation.md` 的原始描述）变成 3 倍于气球的升力。`1.2` 本身也从没有过标定依据（`25455eb` 起就是手写值）。
4. **规则层逐字实现限速与旋翼刹车**（`GameplayRules.RunFans`）：`v∥ > maxSpeed` 时冲量除以 `(1 + v∥ - maxSpeed)`；`rotor` 且 `|v| > maxSpeed`、`dot(dir, v) > 0` 时再减 `4 × excess² / 60 × v̂`。两者都是「每秒的力」，用同一个 tick 换算（`GameplayRules.ForceSecondsPerImpulse`）。
5. **开关语义与解析器约束**：整族是 `activation: "toggle"`，且 `PartContentParser` 把 `fan` + 非 `toggle` 当作**内容错误**拒绝（与 `blaster` 必须 `trigger` 同款交叉校验）——`trigger` 正是旋翼「放气摧毁」的来源，不允许回归。
6. **旋翼不再走 `RunBalloons`**：`RunBalloons` 里「带功率数据的升力件」分支随之成为死代码，删除；真气球没有功率数据（`Balloon.cs` 也无底盘门控与功率项），保持无条件升力。
7. **本切片缓办螺旋桨**：它的 `PropellerSpeed` 是 `Infinity`（`INSettingsBExp.json:299-301`），`LimitForceForSpeed` **永不生效**——原版靠飞机自身阻力与重量收住它。把 10 件从「只自转」改成「无上限推进器」需要先定上限，那是玩法决定而不是提取。`tools/bple-fans` 在报告里**照常给出**它们的换算值（37/60 = 0.616667）并标注「缓办」，内容不动。

## 影响

- 内容 16 行（风扇 6 + 旋翼 10）：`fan` 值、方向、`maxSpeed`、`rotor`、`activation`。变体逐条核对：`78` fan-v06 的 prefab `m_force` 是 **60**（→ 1.0，比基础风扇强 8.6 倍，原版的 Epic 风扇）、`191` rotor-v09 的 `m_forceDirection` 是 **3**（Down，向下推）、`143` propeller-v10 是 Left（缓办，未写）。
- `schemas/part-content-v1.schema.json`（`fanCapability` + 两字段说明）、`PartContentParser`、`PartContentDocument`、客户端 `types.ts`/`validateContent.ts`、重生成 `playParts.generated.json`（`parts: 267`，`--check` 绿）。
- 规则层 `FanState`/`AddFan`/`RunFans`；`GameRoom` 传 `maxSpeed`/`rotor`。
- 实测（真服务器 + 真内容 + 真 Bepu，`ws://…/play` 探针）：旋翼 + 木框 + 包裹引擎（enginePower 150 / 消耗 200 → 因子 0.806）→ 竖直速度**稳定在 12.8 m/s**（上限 `14 × 0.806 ≈ 11.3` 加上刹车平衡点），不再无限加速；关开关后速度按重力精确衰减且**实体仍在**（`rotorPresent: true, active: false`）。
- 回归测试：`RotorThrustTests`（真内容 + 真 Bepu：有界、开关不销毁、无引擎不升）、`PropulsionGateTests`/`GameplayRulesTests`（限速/无限速/旋翼刹车/开关不出力）、`PartContentTests`（解析 + `fan` 必须 `toggle`）。

## 已知偏差

1. **不引入施力点**：原版加在 `transform.position + dir × 0.5`（`FanPropeller.cs:151-152`），PigForge 仍按刚体中心施加（`PhysicsCommand` 带位置参数，但本切片不动这片共享的体心施力管线），因此没有原版那点力矩。
2. **无角阻尼**：原版旋翼开/关会把 `rigidbody.angularDamping` 设为 `1000`/`1`（`:138-148`），物理契约没有角阻尼项。
3. **不做**左向风扇的贴地/悬浮射线增益（`:166-197`）与 `m_rotorTargetDirection` 方向混合（`:156-161`）。
4. **风扇推力方向 1 → -1 是可见行为变化**：`m_forceDirection: 2` = Left 是 prefab 真值，PigForge 的 x 轴未镜像（同 prefab 的螺旋桨碰撞体 `m_Center.x -0.3124` 被逐位抄进内容，可作证）。旧值 `1` 是 `25455eb` 手写的。
