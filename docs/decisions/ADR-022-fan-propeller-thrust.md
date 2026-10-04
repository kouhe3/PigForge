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
| `38` propeller | `wheel: true` + `motor{2.5}` | 手写成驱动轮：`FanPropeller` 虽然和驱动轮一样覆盖 `InitializeEngine()`（`:83`），但它是推进器不是轮子（`tools/bple-power` 的驱动轮判据是按类名 `*Wheel` 收口的，从未把这一族算进去；`motor.thrustPerTick 2.5` 也不是它对 `m_force` 37 的换算值）。装配时被铰接成独立刚体，而 `RunMotors` 又把带 `wheel` 的马达门控在地面接触上，于是它**既不推进也不出力**（见 2026-10-04 补做） |

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
7. ~~**本切片缓办螺旋桨**~~ —— **2026-10-04 补做，见下**：当时缓办的理由是「原版 `PropellerSpeed = Infinity`（`INSettingsBExp.json:299-301`），`LimitForceForSpeed` **永不生效**，改成无上限推进器就要自己定一个上限，而那是玩法决定不是提取」。缓办成立的前提是 PigForge **完全没有阻尼**——一旦 ADR-025 把原版每件刚体的 `linearDamping 0.2` 落地，终端速度就有了**原版自己的**机制，不需要任何人拍一个上限。

## 2026-10-04 补做：螺旋桨转成同一族（G50 收口）

- **决策**：10 件螺旋桨（`38` + `135`–`143`）改成 `fan{ thrustPerTick 0.616667, directionX ±1 }`、**不写 `maxSpeed`**，`activation` 保持 `toggle`（解析器本来就要求 fan 是 toggle）。`wheel`/`motor` 一并去掉：`CanMergePair` 因此把小件并进簇（不再铰接成独立刚体），`RunFans` 的底盘门控 + 簇内引擎门控接管，和风扇/旋翼走同一条路径。
- **为什么不自己定上限**：原版没有上限（`PropellerSpeed = Infinity` 是 prefab 真值），终端速度来自 `drag 0.2`——`v_eq = m_force × IN PropellerForce / (m × drag)`（每 tick 冲量与每 tick 衰减的平衡）。手写一个上限就是给一个原版定义过的零件编一个原版没有的数（HANDOFF §1 硬规矩）。
- **`tools/bple-fans` 的 `deferred` 分类整个删除**：26 件全部写入；新的硬断言是「没有 `maxSpeed` 的件恰好 10 件且全在 `Propeller` 族」，以及「`maxSpeed` 缺省 ⇔ `IN <X>Speed` 是 Infinity」。
- **`tools/bple-power` 补一条内容级交叉断言**：任何件都不允许同时带 `motor` 与 `fan`（这一族覆盖同一个钩子，是 G50 的成因）。驱动轮判据本身没有变——它按类名 `*Wheel` 收口，`FanPropeller` 从来不在其中，此前的记录把它写成「判据误判」是错的。

## 影响

- 内容 **26 行**（风扇 6 + 螺旋桨 10 + 旋翼 10）：`fan` 值、方向、`maxSpeed`、`rotor`、`activation`。变体逐条核对：`78` fan-v06 的 prefab `m_force` 是 **60**（→ 1.0，比基础风扇强 8.6 倍，原版的 Epic 风扇）、`191` rotor-v09 的 `m_forceDirection` 是 **3**（Down，向下推）、`143` propeller-v10 是 Left（原版唯一左向螺旋桨）。
- `schemas/part-content-v1.schema.json`（`fanCapability` + 两字段说明）、`PartContentParser`、`PartContentDocument`、客户端 `types.ts`/`validateContent.ts`、重生成 `playParts.generated.json`（`parts: 267`，`--check` 绿）。
- 规则层 `FanState`/`AddFan`/`RunFans`；`GameRoom` 传 `maxSpeed`/`rotor`。
- 实测（真服务器 + 真内容 + 真 Bepu，`ws://…/play` 探针）：旋翼 + 木框 + 包裹引擎（enginePower 150 / 消耗 200 → 因子 0.806）→ 竖直速度**稳定在 12.8 m/s**（上限 `14 × 0.806 ≈ 11.3` 加上刹车平衡点），不再无限加速；关开关后速度按重力精确衰减且**实体仍在**（`rotorPresent: true, active: false`）。
- 回归测试：`RotorThrustTests`（真内容 + 真 Bepu：有界、开关不销毁、无引擎不升）、`PropulsionGateTests`/`GameplayRulesTests`（限速/无限速/旋翼刹车/开关不出力）、`PartContentTests`（解析 + `fan` 必须 `toggle`）。

## 已知偏差

1. **施力点已对齐，但只到簇质心这一层**：`RunFans` 现在把冲量打到原版的 `transform.position + dir × 0.5`（`FanPropeller.cs:151-152`，`:209`），单件刚体时逐位一致，多成员簇则相对簇质心而不是零件自己的刚体（近似；实测见 `docs/specs/fan-propeller.md` §7.1、`FanThrustTests`）。~~**推力轴仍不跟零件的实时姿态**~~ —— **2026-10-04 已补做**（用户报「风扇推力方向反了」）：轴的取法现在逐字对应 `transform.TransformDirection`（刚体姿态 × 成员局部旋转 × 内容方向），旋翼的 `m_rotorTargetDirection` 混合按 `:73-79`/`:156-161` 一并落地（施力点仍用未混合的轴，与原版 `:152` 一致）。第一次接的时候只接了轴、没接混合，`RotorThrustTests` 的转子簇实测下沉 −1.45 m——**这条真值是一条链，单接一环会倒退**。读数与测试见 `docs/specs/fan-propeller.md` §7.2。
2. **旋翼的运行期角阻尼仍未施加**：原版旋翼开/关会把 `rigidbody.angularDamping` 设为 `1000`/`1`（`:138-148`）。契约现在有角阻尼字段了（ADR-025 的 `BodyDefinition.AngularDamping`），但这是一条**运行期覆盖**（不是 prefab 值），`tools/bple-damping` 把它报在 `runtimeOverrides` 里而没有折进内容——和 `Pig.FixedUpdate` 的慢速增阻、绳逐节阻尼、`NoDrag` 一起排在 G90。
3. **不做**左向风扇的贴地/悬浮射线增益（`:166-197`，纯倍率，需要物理射线查询）；~~`m_rotorTargetDirection` 方向混合（`:156-161`）~~ 已于 2026-10-04 落地（偏差 1）。另记 **G96**：建造时按连接方向的自动对齐（`Contraption.cs:1889-1900`）未实现——推力现在跟着玩家设的角度走，但同一个摆法的**默认朝向**仍与原版不同。
4. **风扇推力方向 1 → -1 是可见行为变化**：`m_forceDirection: 2` = Left 是 prefab 真值，PigForge 的 x 轴未镜像（同 prefab 的螺旋桨碰撞体 `m_Center.x -0.3124` 被逐位抄进内容，可作证）。旧值 `1` 是 `25455eb` 手写的。
