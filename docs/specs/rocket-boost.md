# 规格：火箭族的三段点火（原版 `Rocket`）

状态：已实施（**G101 + G104 收口**，2026-10-06）。
来源：原版反编译脚本 `BPLE_Unity6/Assets/Scripts/Assembly-CSharp/Rocket.cs`、18 个 prefab
（`Part_Rocket_01..04_SET`、`Part_RedRocket_01..04_SET`、`Part_CokeBottle_01..05_SET`、`Part_SodaBottle_01..05_SET`）、
`Assets/TextAsset/INDeclarationSettingsExp.json`（**声明默认档**，唯一允许的倍率来源）。
提取器：`tools/bple-rockets/`。原版侧探针：`unity/PigForge.WeldProbe` 的 `RocketBurnProbe`（钉 2021.3.45f2）。

## 1. 要解决的偏差

| 差距 | 现象 |
|---|---|
| `G101` 火箭族内容值是手写的 | 内容里 `rocket{directionX/Y, thrustPerTick, durationTicks, explodeRadius, explodeImpulse}` 的 `thrustPerTick` 4/6/8、`durationTicks` 60/40/30、`explodeRadius 4` + `explodeImpulse 20` 全部来自 `49e9153` 手写；`firework-red`(30) 甚至被写成「会炸」，而它的 prefab `Part_RedRocket_01_SET` 的 `m_explodes` 是 0。汽水族（26/230–233）的方向被写成 `(0,1)`，而 18 个 prefab 的 `m_direction` 全是 `(1,0,0)`。 |
| `G104` 激活语义与三段时长 | 原版的烧完**不销毁零件**：`m_enabled = false` 只停推力（`Rocket.cs:281-284`），`m_explodes` 的件在烧完那一刻在原地爆（`:627-634`）；PigForge 之前把它**销毁**掉，等于凭空消失一个零件。三段时长（点火 / 全推 / 线性收尾）与 `m_maximumSpeed` 限速都没有。 |

## 2. 原版真值（逐条出处）

| # | 规则 | 出处 |
|---|---|---|
| 1 | 一次用品：`OnTouch` 的**声明默认分支**（`SwitchableCokeSodaRocket = false`）是 `if (m_boostUsed) return;` → 一生只点火一次，并 `ChangeOneShotPartAmount(..., -1)` | `Rocket.cs:570-581`；`INDeclarationSettingsExp.json` |
| 2 | 点火后按 `num = Time.time - m_timeBoostStarted` 分三段：`m_ignitionTime` → `m_boostDuration` 全推 → `m_boostEndDuration` 线性收尾 `num2 = 1 - (num - boost - ignition) / end` | `Rocket.cs:228-270` |
| 3 | **点火期只对带 `m_visualization` 的件静默**：`num < m_ignitionTime && m_visualization != null` → 提前 `return`（瓶族）。普通火箭 `m_visualization == null`，从第一帧就出力 | `Rocket.cs:235-240` |
| 4 | 出力：`AddForceAtPosition(LimitForceForSpeed(num2 × m_boostForce, dir) × dir, transform.position, ForceMode.Force)`，`dir = transform.TransformDirection(m_direction)` | `Rocket.cs:291-300`（轴与施力点见 `ADR-029`） |
| 5 | 限速：`v∥ = Dot(v̂, dir)`、`vector = v × v∥`，`|vector| > m_maximumSpeed` 时 `force / (1 + |vector| - m_maximumSpeed)` | `Rocket.cs:529-541` |
| 6 | 烧完：`m_explodes` 的件 `Explode()`（`OverlapSphere(transform.position, m_explosionRadius × RocketExplosionRadius)`，力 `RocketExplosionForce × m_explosionImpulse / max(\|vector\|,1)^1.5`，**含自身**、并点燃半径内的 TNT），然后 `m_enabled = false`——**零件不销毁** | `Rocket.cs:281-284,627-646`；`TNT.cs:210-231` |
| 7 | 档位（`m_partTier`）决定倍率：tier 4（Legendary）瓶族直接取 `AlienCokeForceValue 35 / AlienCokeSpeedValue 25` 或 `AlienSodaForceValue 150 / AlienSodaSpeedValue 150`；其余瓶族 `m_boostForce × CokeSodaForce(1.0)`、`m_maximumSpeed × CokeSodaSpeed(1.0)`；火箭族 `× RocketForce(1.0) / RocketSpeed(1.0)` | `Rocket.cs:180-200`；`INDeclarationSettingsExp.json` |
| 8 | 序列化真值（18 个 prefab，`m_direction` 全为 `(1,0,0)`） | 见下表 |

| 族 | prefab | `m_boostForce` | ignition | boost | end | `m_maximumSpeed` | `m_explodes` |
|---|---|---|---|---|---|---|---|
| Rocket / RedRocket 01–04 | 8 个 | 50 | 1 s | 3 s | 1 s | 18 | 仅 `_03`（半径 8 / 冲量 25） |
| CokeBottle 01–04 | 4 个 | 35 | 1 s | 1 s | 0.5 s | 10 | 0 |
| CokeBottle_05（Legendary） | 1 个 | 35（AlienCoke） | 1 s | **500 s** | 0.5 s | 25（AlienCoke） | 0 |
| SodaBottle 01–04 | 4 个 | 35 | 1 s | 1 s | 0.5 s | 10 | 0 |
| SodaBottle_05（Legendary） | 1 个 | **150**（AlienSoda） | 1 s | 1 s | 0.5 s | **150**（AlienSoda） | 0 |

## 3. 内容模型与换算

```json
"rocket": { "directionX": 1, "directionY": 0,
            "thrustPerTick": 0.833333, "ignitionTicks": 60, "boostTicks": 180, "endTicks": 60,
            "maxSpeed": 18.0, "visualization": true?, "explodeRadius": 8.0?, "explodeImpulse": 25.0? }
```

- `thrustPerTick = m_boostForce(×IN) / 60`：`ForceMode.Force` 是「每秒的力」，PigForge 每 tick 施加一个冲量，+ 与风扇/气球/风箱同一个分母（`ADR-013` 决策 4）。
- 三段时长按 **60 Hz** 折算成 tick（原版 50 Hz 的秒 × 60）：`ignitionTicks = m_ignitionTime × 60` 等，全部是整数（60 / 180 / 60 / 30 / 30000）。
- `maxSpeed` 是 m/s，已乘过声明默认档的倍率；`visualization` 只对瓶族为 true（prefab 的 `m_visualization`，工具用 `BottleVisualization` 子物体作见证并交叉校验）。
- 只有 `m_explodes` 的件带 `explodeRadius`/`explodeImpulse`（成对出现）。
- 全部由 `tools/bple-rockets/` 写出（直方图硬断言 18 件、`m_direction (1,0,0)`、`m_explodes` 恰 2 件），**不许手写**。

## 4. PigForge 实现

- `RocketState`（`GameplayStores.cs`）换成三段状态：`IgnitionTicks/BoostTicks/EndTicks/MaxSpeed/Visualization/Ignited/ElapsedTicks`，`ElapsedTicks` 从点火那一 tick 记 0。
- `GameplayRules.RunRockets`：
  1. 按钮/开关消费（`ADR-028` 的「按下必被消费」）与底盘门控不变；
  2. 点火 → `ElapsedTicks = 0`，之后每 tick +1；
  3. `ElapsedTicks ≥ ignition + boost + end` → **移除该件的火箭角色**（= `m_boostUsed` 的一次性），有 `explodeRadius` 的件用**自己位姿**做一次径向冲量（`RadialBlast`，**含自身**，与 `ADR-019` 的 TNT 同源），零件本身**留在世界里**；
  4. `visualization && ElapsedTicks < ignition` → 该 tick 不出力（瓶族的静默点火期）；
  5. 否则 `phase = 1`，进入收尾段后 `phase = 1 - (ElapsedTicks - ignition - boost) / end`；`force = phase × thrustPerTick`，再走 `LimitForceForSpeed`（沿零件自己的轴），最后 `ADR-029` 的取轴/施力点。
- 客户端/内容键同步：schema、`PartContentParser`、`PartContentDocument`、`clients/web/src/schema/{types,validateContent}`、`playParts.generated.json`。

## 5. 验收

### 5.1 用例

- `GameplayRulesTests`：三段曲线（静默点火 → 全推 → 半推 → 烧完）、普通火箭点火期也出力、`LimitForceForSpeed`（超速除 `1+v-max`、低于上限全额、横向速度不参与）、零长烧不尽不出力、烧完**不销毁**且下一 tick 不再出力、爆心在零件自己的位姿（含自身）。
- `PartContentTests.TheRealContentCarriesTheExtractedRocketBurn`：18 件的键集合、火箭族 0.833333/60/180/60/18、瓶族 0.583333/60/60/30/10 + `visualization`、Legacy 皮肤 30000 tick / 25 m/s 与 2.5/tick / 150 m/s、只有 158/228 爆炸。
- 原版侧：`unity/PigForge.WeldProbe` 的 `RocketBurnProbe`（2021.3.45f2）——三段力的实测表 + 限速表 + 瓶族静默表。

### 5.2 实机读数

**原版侧**（`RocketBurnProbe`，Unity **2021.3.45f2** 的 PhysX，质量 1 kg、无阻尼的裸刚体、`Physics.Simulate(0.02)`，50 Hz）：
300 步的力表逐行与「公式独立推算」一致，最大分歧 **2.2e-05 N**（`tasks/rocket-burn-probe.json`）：

| 步（num） | 曲线 `num2 × 50` | PhysX 实测 | 说明 |
|---|---|---|---|
| 0 (0 s) | 50 | **50** | 普通火箭点火期从第一帧就满力（无 `m_visualization`） |
| 19 (0.38 s) | 50 | **25** | 限速**首次生效**：沿轴速度 19 > `m_maximumSpeed 18` → `50 / (1 + 19 - 18)` |
| 50 (1 s) | 50 | **8.2** | 速度 50 → `50 / (1 + 50 - 18)` |
| 200 (4 s) | 50 | **2.6** | 速度 ~100 |
| 201 (4.02 s) | 49 | **2.55** | 收尾段开始（`num2 = 1 - (num - 4)/1`） |
| 250 (5 s) | 0 | **~0** | 烧完 |
| 251 (5.02 s) | — | 又一次（`m_enabled = false` 那一帧仍走完出力） | 我们不复刻这一帧（见 §6 偏差 3） |

瓶族配置（`ignitionSuppressesThrust = true`）前 1 s 的力全为 **0**，第 51 步起满力 ✓。

**我们这边**（真服务器 `--play` + 真内容 + 客户端的编码器读 `PGFS`）：14 m 高处的「木框 + 火箭」点火后，
boost 窗口内 `vx` 12.5 → 20.3 m/s，烧完 300 tick（60+180+60）之后实体**仍在帧里**（`present: true`，
`destroyed: false`），随后落地停住（地形交互让后段读数噪声大）。见 HANDOFF §4.T。

## 6. 已知偏差

1. **爆炸的衰减形状仍是 PigForge 的线性 `1 - d/r`**，而原版是 `impulse / max(d,1)^1.5` 加轻物件的质量因子（`TNT.cs:210-231`）。这是 TNT 与火箭**共用**的既有偏差，改它要连 TNT 的验收一起重做，故不在本切片。
2. **爆炸不点燃半径内的 TNT**（原版 `Explode` 会对每个重叠的 `TNT` 调 `Explode()`）：我们的 TNT 连锁是另一条规则（`IgniteChargesInRadius`），本切片不动。
3. **烧尽那一 tick 的负向力**（原版 `num2` 在 `num` 刚过 `ignition+boost+end` 时为微小负值，随后 `m_enabled = false`）没有复刻：我们在那一 tick 直接停止出力。
4. `RocketExplosionCoolingTime 0.0` / `AvoidanceRocket` / `TrackingRocket`（声明默认皆 0/false）意味着原版的追踪/规避火箭分支不生效，故不实现。
5. 只有 `m_explodes` 的**件**被销毁？——**不**：原版 `Explode()` 不销毁零件，我们也一样；`explodeRadius` 只决定「炸一次」。
