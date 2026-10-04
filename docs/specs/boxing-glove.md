# Spec: 可交互的伸缩拳套（G95，含运行时子实体）

> 状态：Draft（实现前收口；消费 `docs/intent/spring-and-glove.md`）。
> 今天：28 `boxing-glove` 被当成弹簧皮肤共用 `"spring": 25.0` → **既不可交互、又自己乱弹**（`GameplayRules.RunSprings`）。
> 本切片把它做成原版的 `SpringBoxingGlove`：**第二刚体「手套」 + yDrive 弹簧驱动 + 触摸/开关触发 + 出拳打断身后相邻件的关节 + 回卷**。
> 机制层决策：`ADR-027`（运行时子实体）。

## 0. 结论

拳套是**可交互的伸缩拳套**，不是一个特效：本体上挂着一个独立刚体「手套」，平时被 yDrive 拉回贴身；
触发后目标位置改成「沿自身 Y 伸出 `m_targetDistanceY × BoxingGloveLength`」，弹簧驱动把手套**射出去**打东西，同时**把手套后面那件（`EffectDirection()` 方向）的关节全部打断**；
`m_ShootTime` 之后进入回卷（驱动变软、手套质量 0.01、碰撞体关掉），`m_WindingTime` 或回到 0.1 内后恢复贴身待机。

## 1. 原版真值

| 机制 | 真值 | 出处 |
|---|---|---|
| 结构 | 本体 + 独立刚体手套（`m_BoxingGlovePrefab` → `BoxingGlove*.prefab`，mass **0.5**，**SphereCollider r 0.3**） | `SpringBoxingGlove.cs:160-168,215-222`；`tasks/bple-springs-report.json` |
| **引子（要命的细节）** | `SpringBoxingGlove` **不覆写 `CustomConnectToPart`** → 它与相邻件的连接是**普通焊接**（`Contraption.AddFixedJoint`，5/5 皮肤），本切片只新增「本体↔手套」那条关节 | `tasks/bple-springs-report.json`（`jointPathHistogram: {Weld: 5}`） |
| **逐皮肤覆盖（不许用类默认值）** | `m_SpringYDrive **380**`（类默认 60）、`m_SpringYDriveDamper **3.5**`（3）、`m_ShootTime **0.4**`（1）、`m_targetDeviationX **0**`（0.01）、`m_WindingTime 1`、`m_checkRotation 0`、`m_targetDistanceY **2.5**`——**但 `Part_SpringBoxingGlove_05_SET`（id 245）是 `5`**（+ `wind.driveSpring` 随之 = 10×5×1 = 50），它是一支**打得更远**的皮肤 | `tasks/bple-springs-report.json`（`boxingGlove.prefabs[*].overrides`） |
| 关节（贴身态） | `ConfigurableJoint`：角三轴 Locked、`x/z Locked`、`yMotion Limited`（`limit 1`、`linearLimitSpring 0/0`）、`yDrive{positionSpring m_SpringYDrive 60, positionDamper m_SpringYDriveDamper 3, maximumForce ∞}`、`xDrive{1000, 5}`、`projectionMode PositionAndRotation`、`projectionDistance 0.1`、`projectionAngle 0`、`enablePreprocessing false`、`targetPosition (0,0,0)` | `SpringBoxingGlove.cs:170-207` |
| 触发 | `OnTouch()`：IN `SwitchableBoxingGlove` 为真 → **开关切换**（`CanBeEnabled()` 还看相邻件/超级胶/TNT）；否则**碰到就打** | `SpringBoxingGlove.cs:263-278`；`CanBeEnabled` `:88-96` |
| 出拳 | `targetPosition = (±m_targetDeviationX 0.01, m_targetDistanceY 2.5 × IN BoxingGloveLength, 0)`、`linearLimitSpring.spring = 0.1`；若目标件与本体同属一个 ConnectedComponent → **销毁它的全部 FixedJoints**（把身后那件打脱） | `SpringBoxingGlove.cs:224-262` |
| 回卷 | `m_ShootTime 1` s 后：`yDrive.positionDamper = 2.5`、`yDrive.positionSpring = 10 × m_targetDistanceY × BoxingGloveLength`、手套 mass → **0.01**、手套碰撞体 `enabled = false`；直到 `m_WindingTime 1` s 或 `|localPosition| < 0.1` 或 `localPosition.y > 0` → `InitilizeBoxingGlove()` 复位 | `SpringBoxingGlove.cs:280-330` |
| 其它 | 手套与本体/被包裹件 `IgnoreCollision`；手套 `solverIterations × 1.6`；`HasOnOffToggle() => false`（原版的开关取消，除非 IN 开关打开）；`CanBeEnclosed() => true`；`EffectDirection() = Rotate(Down, gridRotation)`；`m_checkRotation` 下 90° 要镜像美术；`BoxingGlove.cs` 只是成就上报（无物理） | `SpringBoxingGlove.cs:88-96,140-158,215-222,263` |

**逐皮肤值**（`m_targetDistanceY`、`m_SpringYDrive`… 的 prefab 序列化覆盖）与三个 IN 键（`BoxingGloveLength`、`SwitchableBoxingGlove`、
以及弹簧族用的两个开关）由 `tools/bple-springs` 提取并对直方图/常量**硬断言**；本规格不手写它们，实现时把提取器报出的表抄进本节。

### 1.1 原版实测（2026-10-04，`tasks/boxing-glove-probe.json`；`unity run unity/PigForge.WeldProbe -- -executeMethod PigForge.WeldProbe.Probe.BoxingGloveProbe.Run`，钉 2021.3.45f2 + 原版物理设置）

探针把 host 固定（kinematic），按 `InitilizeBoxingGlove()` 原样建关节，再按 `Shoot`/`Winding` 的原样改驱动。
**第一版探针用错了参数（类默认驱动 60/3、`ShootTime 1`；prefab 覆盖了它们）**，探针已按皮肤参数化并重跑：
prefab 值（380/3.5/0.4）那一组是**参照**，类默认那一组留作对照（证明参数不可想当然）。

| 读数 | prefab 值，`len = 1` | prefab 值，`len = 2` | 类默认对照（`len = 1`） |
|---|---|---|---|
| 驱动 / `ShootTime` | 380 / 3.5 / 0.4 s | 380 / 3.5 / 0.4 s | 60 / 3 / 1 s |
| 目标距离 `2.5 × len` | 2.5 | 5.0 | 2.5 |
| **出拳过冲峰值**（≈+30%） | **3.248 @ 0.1 s** | **6.496 @ 0.1 s** | 3.194 @ 0.3 s |
| 稳定位置（`localPosition.y`，**负号见下**） | **−2.5**（≈0.2 s 起） | −5.0 | −2.5 |
| `ShootTime` 结束时的位置 | −2.561 | −5.121 | −2.546 |
| 回卷到 `|offset| < 0.1`（驱动 `10·2.5·len`、damper 2.5、mass 0.01） | **0.34 s** | **0.22 s** | 0.34 s |

prefab 的 380 驱动让手套在 **0.1 s 内冲过目标约 30%**（`3.25 > 2.5`、`6.50 > 5.0`）再回到目标位置——这就是「打出去」的手感；`m_targetDeviationX = 0` 意味着**没有横向偏移**。

**两条读数直接改写了实现细节**：

1. **伸出方向是零件的 local −Y**：`targetPosition` 给的是 **+Y**，而手套走到 **−Y** —— 与 `EffectDirection() = Rotate(Down, gridRotation)` 一致；
   PigForge 的驱动目标必须换算到零件的 −Y（否则手套会往身体里缩）。这也决定「打断身后相邻件」的方向是 `Down × gridRotation`。
2. **回卷比 `m_WindingTime` 快得多**：原版 `Update` 是「时间到 **或** 回到 0.1 内」两个条件取先到者，实测 0.22–0.34 s；实现必须照这个「或」，只按 1 s 计会把回卷做慢。
3. 探针自身的 `peakDistanceY`/`peakStepTime` 第一版用了「取正峰」而序列是负的 → 恒为 0；**已修**为取绝对值峰（现在的 `3.248 @ 0.1 s` 就是这么来的）。

## 2. 内容

```jsonc
// 28 boxing-glove（+ 242..245 皮肤）
"capabilities": {
  "jointConnectionDirection": "any", "jointConnectionStrength": "normal",
  "jointConnectionType": "target", "powerConsumption": 0, "enginePower": 0,
  "glove": {
    "mass": 0.5,
    "shapes": [ /* 手套本体的碰撞体，从 BoxingGlove*.prefab 提取 */ ],
    "limit": 1.0,
    "yDrive": { "spring": 60, "damper": 3 },
    "xDrive": { "spring": 1000, "damper": 5 },
    "projectionDistance": 0.1,
    "shoot": { "distanceY": 2.5, "deviationX": 0.01, "time": 1.0, "limitSpring": 0.1 },
    "wind": { "time": 1.0, "mass": 0.01, "driveSpring": 25, "driveDamper": 2.5 }
    // wind.driveSpring = 10 × m_targetDistanceY × BoxingGloveLength（以 IN 值为准）
  },
  "activation": "trigger"   // IN SwitchableBoxingGlove=true 时改为 "toggle"（按提取结果写）
}
```

- `activation` 的取值由提取器读出的 IN 值决定，**两种都实现**。
- **实测的 IN 值：`SwitchableBoxingGlove = true`**（`INSettingsBExp.json:519`）⇒ **内容写 `activation: "toggle"`**（点一下开=出拳、再点关=中断回卷），`trigger`（碰到就打）作为另一条已实现路径保留；
  `BoxingGloveLength = 1`（`INDeclarationSettingsExp.json:1172`，非覆盖）⇒ 伸出距离 = `2.5 × 1 = 2.5`。
- 手套的**美术**不在本切片：子实体用 host 的 `partTypeId`（客户端按 host 的美术/形状画），拳头自己的图另记 P1。

## 3. 子实体（`ADR-027`）

- 物化时（`StartPlayer`/`Start`/`MaterializeLevelActors`）给拳套注册一个子实体「手套」：`PartLink`（host 的 partTypeId）、自己的刚体（mass = `glove.mass`）、自己的形状（`glove.shapes`），**不进** `ConstructionRules`。
- 关节：本体 body ↔ 手套 body，`yMotion` 限位 + `yDrive`（贴身态按 `glove.limit/yDrive`）；手套与本体所在簇 `IgnoreCollision`（拳套与它自己刚体之间）。
- 生命周期：host 的 RESET / 离开 / 拆簇摧毁 / 规则销毁 → 子实体同处销毁（先 `ForgetJointsForBody` 再 `DestroyBody`）。
- 快照：子实体照 73 B 记录发布；`MaxSnapshotEntityCount` 必须把它算进去（否则沙盒大帧被静默丢弃）。

## 4. 状态机

```text
WindedUp --触发(OnTouch / 开)--> Shoot --m_ShootTime 秒--> Winding --m_WindingTime 秒 / 回到 0.1 内--> WindedUp
```

| 状态 | 刚体/关节改写 |
|---|---|
| `WindedUp` | 手套 mass `glove.mass`、碰撞体开、`yDrive = glove.yDrive`、`targetPosition = 0`、`linearLimitSpring = 0/0`（等 `InitilizeBoxingGlove`） |
| `Shoot` | `targetPosition = (±deviationX·rand, shoot.distanceY·BoxingGloveLength, 0)`、`linearLimitSpring.spring = shoot.limitSpring`、碰撞体开；**并把 `EffectDirection()` 相邻件的关节全部断掉**（同簇才断） |
| `Winding` | `targetPosition = 0`、`yDrive = { wind.driveSpring, wind.driveDamper }`、手套 mass `wind.mass`、碰撞体关 |
| 复位 | 满足 `Winding` 退出条件 → 回到 `WindedUp` 的全套初值 |

## 5. 触发路径（服务器权威）

- **触发式**（`activation: trigger`）：host 的**接触事件**（`ContactStarted`，与火箭的 `TryConsumeTrigger` 同一形状）→ 若处于 `WindedUp` 且 `CanBeEnabled` → 进入 `Shoot`。
- **开关式**（`activation: toggle`）：`SetPartActive`（既有命令通道）→ 开 = 出拳、关 = 中断回卷。
- `CanBeEnabled`：手套身后那件有 SuperGlue 且不是 TNT → 不可用（原版 `m_CanBeEnabled` 的近似，写清口径）。

## 6. 验收

- **原版编辑器探针**：`unity/…/Editor/BoxingGloveProbe.cs`，入口 `-executeMethod PigForge.WeldProbe.Probe.BoxingGloveProbe.Run`
  → `tasks/boxing-glove-probe.json`。**已跑**，验收数字取 §1.1：伸展距离 = `2.5 × BoxingGloveLength`（±0.1%）、到位 ≈0.4–0.5 s、
  回卷到 0.1 内 0.22–0.34 s、方向 = 零件 −Y。**待补**：出拳时身后件的关节真的断（探针要加一个相邻件单元）。
- **单测**：Core（状态机：触发→Shoot→Winding→WindedUp 的转移与驱动改写；`CanBeEnabled` 的两条）；Physics（真 Bepu：手套被驱动伸出到目标距离 ±10%）；
  Server（子实体随 RESET/离开清场；拳套不再走 `RunSprings`）。
- **实机**（真服务器 + 真内容 + 真 Bepu，读数写进本节）：点一下/碰一下 → 手套**射出去**打到东西 → 身后那件被打脱 → 1 s 后回卷 → **可重复**；
  拳套不再自己弹跳。

### 6.1 落地读数（2026-10-04，实现完成；真 Bepu / 真房间）

`PhysicsJointKind.Configurable`（新契约种类）就是 §3 那条关节：驱动轴（零件 local −Y）的位置伺服 + 横向（零件 local X，
`xDrive` 1000/5）伺服 + 第三轴刚性锁 + 锁定的相对旋转（angular XYZ Locked）+ 驱动轴上的线性限位（贴身 hard、
出拳换成皮肤自己的 `limitSpring` 0.1 N/m）。弹簧 N/m 仍由 `GameRoom.TrySpringResponse` 换算（380/3.5 在 1 kg 量级
两端上 = **4.393 Hz / ζ 0.127**），不新增第三套换算。

| 读数 | 真 Bepu 装置（`tests/PigForge.Physics.Tests/BoxingGloveJointTests.cs`） | 真房间（`tests/PigForge.Server.Tests/BoxingGloveRoomTests.cs`） | 原版探针 §1.1 |
|---|---|---|---|
| **出拳过冲峰值** | **3.368 m @ 0.117 s**（目标 ×1.35） | **3.143 m @ 0.117 s**（目标 ×1.26） | 3.248 @ 0.1 s（×1.30） |
| `ShootTime` 结束时的位置（0.4 s） | **2.513**（+0.5%） | **2.4995**（−0.02%） | −2.561（≈目标） |
| **回卷到 offset 绝对值 < 0.1** | **0.35 s**（21 tick） | **0.333 s**（20 tick） | 0.34 s（len 1）/ 0.22 s（len 2） |
| 横向/旋转 | x、z 位移 0，相对偏航 0（±1e-4） | — | x/z Locked、角三轴 Locked |
| 出拳打断身后件 | — | 同簇的 1×1 木块被打脱（各自成体，双方存活） | 目标件 FixedJoints 全断 |
| 可重复 | — | 关/再开一次 → 第二次同样打出 ≥ 2.25 m | — |
| 确定性 | 双跑逐 tick 轨迹相同 | 双跑 `ComputeStateHash()` 相同 | — |
| 子实体清场 | — | RESET / 离开后 `SubEntityCount == 0`，重开再建 | — |

三个测试层：Core 262、Server 125、Physics 66（非 Jolt）+ 16（Jolt）全绿；Release 全量重建 0 警告 0 错误。
拳套的一条 `_jointedPairs` 抑制让手套与**它所属的那个刚体**（并簇后即整簇）互不接触，正是原版的 `IgnoreCollision`。

## 7. 已知偏差（排队或写清，别只写在 ADR 里）

- **拳头美术**：子实体暂无自己的贴图 → 用 host 的美术/形状（P1 客户端项：`BoxingGlove*.prefab` 的精灵进清单、按子实体单独画）。
- **`solverIterations × 1.6`**：Bepu 没有 per-body 求解迭代数 → 不实现，记偏差。
- **`projectionMode PositionAndRotation` / `projectionDistance 0.1`**：Bepu 无投影项 → 用更硬的驱动/限位近似，记偏差。
- **`m_checkRotation` 的 90° 美术镜像**：属渲染层（客户端），与滑翔翼水平镜像（协议）同一批，本切片不做。
- 拳套的 `EffectDirection` 断开只作用于**同簇**相邻件（原版 `ConnectedComponent` 的判据），跨簇不波及——这一条是**有意**，写进本规格。

### 7.1 落地时新增/收口的偏差（2026-10-04）

- **回卷的第三个退出条件未单独建模**：原版 `Update` 是「`m_WindingTime` 到 **或** 位移 < 0.1 **或** `localPosition.y > 0`」，
  本实现只做前两条。第三条是「冲过了原位」——穿过原位必然先满足位移 < 0.1，所以它在实践中不可达（§6.1 的实测回卷耗时全部由前两条给出）。
- **`deviationX` 的 ± 随机未建模**：原版出拳时 `targetPosition.x = ±m_targetDeviationX`；提取出的五个皮肤 `deviationX` 全为 0，
  机器因此把横向目标设为 `+deviationX` 常量。将来若出现非零皮肤，需要一个确定性符号源（记在这里，不猜随机）。
- **`IgnoreCollision` 只覆盖 host 所在的刚体**：`PhysicsJointKind.Configurable` 抑制「手套 ↔ host 所属刚体」这一对（并簇后就是整簇，
  与原版 `IgnoreCollision(glove, part)` 同效）。今天拳套与它的 Source 邻件必然并簇，所以它就是整簇；跨焊缝链的邻件理论上仍会与手套接触，
  留作将来的多体切片一起收（与 §7 第一行同一批）。
- **`xDrive` 是建模的**：横向（x）轴是一条真的位置伺服（1000 N/m、5 N·s/m），不是刚性锁；`deviationX = 0` 时它的目标就是 0，
  效果与原版的「x Locked」一致，但没有把 prefab 里的 drive 丢掉。
- **契约新增（不是偏差，是落点）**：`PhysicsJointKind.Configurable` + `ConfigurableJointDefinition`（驱动轴/横向轴/目标/三组弹簧/限位带）、
  `IPhysicsWorld.SetBodyMass` 与 `SetBodyCollisionEnabled`（回卷的 0.01 kg 与关碰撞体）。Bepu 三个都实现；Jolt 照既有约定
  `NotSupportedException`（`SupportedJointKinds` 仍只有 weld）。
- **出拳打断的两种载体**：并簇的邻件走「拆掉它的**全部接缝**」（`CompoundAssembler.SplitAlongSeams`，本切片新增，是 `SplitAlongSeam` 的推广）；
  跨体的邻件走「销毁它的**全部焊缝**」。今天的拳套邻件必然是并簇的（拳套与任何 Source 邻件都并簇），所以房间测试钉的是接缝那条；
  焊缝那条按原版「销毁目标件全部 FixedJoints」的原意实现，等有多体刚体邻件的切片再用到。
