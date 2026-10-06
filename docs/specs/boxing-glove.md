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
  "activation": "trigger"   // 按钮：按下/点击出拳（见下）
}
```

- **内容选的是按钮分支**（`activation: "trigger"`，2026-10-04 用户实机报告后定）：原版这个零件的**控件**就是按钮而不是开关——
  `SpringBoxingGlove.HasOnOffToggle() => false`（`SpringBoxingGlove.cs:81-84`），而 `UIPartTriggerButtonInfo` 正是拿这个值决定控件形状
  （`BasePart.cs:1418-1421`）；`BasePart.OnButtonTriggered` 又只是 `ProcessTouch()`（`BasePart.cs:1428-1431`）。所以按下 = 出拳，回卷由
  `m_ShootTime` 自己完成，按钮不留在「开位」。
- **与 IN `SwitchableBoxingGlove = true` 的偏差（有意，可回退）**：`INSettingsBExp.json:504-508` 实测为 `true`，那条分支里 `OnTouch` 切的是
  `m_enabled`（档位），`Update` 只在 `!m_enabled` 时才进回卷。PigForge 仍然实现这条分支（`activation: "toggle"`：关 = 中断回卷，开 = 出拳，
  且快照里的 `Active` 一直是 1 —— 客户端的开关条就会把它画成一个真正的开关，这正是用户报告的「预期是按钮而不是开关」），
  但**发布内容不再选它**；把 `tools/bple-springs/apply-springs.mjs` 的 `activation` 常量改回 `"toggle"` 即可整体回退。
- `BoxingGloveLength = 1`（`INDeclarationSettingsExp.json:1172`，非覆盖）⇒ 伸出距离 = `2.5 × 1 = 2.5`。
- **拳头的图**（2026-10-04 补做，用户实机报告「弹出的不是拳头」）：`m_BoxingGlovePrefab` 指向的 `BoxingGlove*.prefab`（`SpringBoxingGlove.cs:38`）
  有自己的 `Visualization` 精灵，由 `tools/bple-textures/extract.mjs` 抽成清单里的 `subSprites`（schemaVersion 5，5 个皮肤各一张）；
  快照用 `flags` bit1 标出子实体（PGFS v5，`docs/specs/play-part-switches.md` Assumption 9），客户端 `subEntityTexture` 拿它换掉宿主的合成图。
  偏移仍在子实体自己的原点系（与零件的参照系约定一致）。

## 3. 子实体（`ADR-027`）

- 物化时（`StartPlayer`/`Start`/`MaterializeLevelActors`）给拳套注册一个子实体「手套」：`PartLink`（host 的 partTypeId）、自己的刚体（mass = `glove.mass`）、自己的形状（`glove.shapes`），**不进** `ConstructionRules`。
- 关节：本体 body ↔ 手套 body，`yMotion` 限位 + `yDrive`（贴身态按 `glove.limit/yDrive`）；手套与本体所在簇 `IgnoreCollision`（拳套与它自己刚体之间）。
- 生命周期：host 的 RESET / 离开 / 拆簇摧毁 / 规则销毁 → 子实体同处销毁（先 `ForgetJointsForBody` 再 `DestroyBody`）。
- 快照：子实体照 73 B 记录发布，`flags` bit1 = 1（PGFS v5，ADR-028）；`MaxSnapshotEntityCount` 必须把它算进去（否则沙盒大帧被静默丢弃）。
- **静止时不参与快照**（2026-10-04 第十三轮补，**用户实机报告**「未触发时我看到的是拳套，原版是盒子」）：原版 `InitilizeBoxingGlove()` 的**最后一步是
  `m_BoxingGlove.SetActive(false)`**（`SpringBoxingGlove.cs:215-222`），出拳才 `SetActive(true)`（`:224-262`）——被停用的 GameObject **既不渲染、也不参与模拟**。
  我们保留子实体的刚体（yDrive 要在出拳那一刻把手套从贴身位置推出去），但**缠绕态不把它放进已发布的帧**：`GameRoom._stowedSubEntityIds` 在
  `ApplyGloveState`（`WindedUp` → stowed）/`SpawnGlove`（出生即缠绕）/`DestroySubEntity`/`ClearSubEntities` 维护，`FillConstructionOrder`/`FillEntityOrder`
  跳过它。⇒ 客户端在静止时只画盒子，出拳/回卷时才有拳头（`Shoot`/`Winding` 都发布）。拉断弹簧的端点不属于任何机器，永远发布。

## 4. 状态机

```text
WindedUp --按下 / off→on--> Shoot --m_ShootTime 秒--> Winding --m_WindingTime 秒 / 回到 0.1 内--> WindedUp
```

| 状态 | 刚体/关节改写 |
|---|---|
| `WindedUp` | 手套 mass `glove.mass`、碰撞体开、`yDrive = glove.yDrive`、`targetPosition = 0`、`linearLimitSpring = 0/0`（等 `InitilizeBoxingGlove`）；**且子实体被 stow**（原版 `SetActive(false)`：不进快照、不渲染） |
| `Shoot` | `targetPosition = (±deviationX·rand, shoot.distanceY·BoxingGloveLength, 0)`、`linearLimitSpring.spring = shoot.limitSpring`、碰撞体开；**并把 `EffectDirection()` 相邻件的关节全部断掉**（同簇才断） |
| `Winding` | `targetPosition = 0`、`yDrive = { wind.driveSpring, wind.driveDamper }`、手套 mass `wind.mass`、碰撞体关 |
| 复位 | 满足 `Winding` 退出条件 → 回到 `WindedUp` 的全套初值 |

> 一句话：**stow ⇔ `WindedUp`**，其余两态都在快照里（客户端因此只在拳头真的在外/回卷时画它）。

### 4.1 画序：宿主盖住子实体（2026-10-04 第十三轮，**用户实机报告**「拳头收回后突然消失」）

原版同一 sorting order 的精灵按**到相机的距离**排序（相机在 `z = -15` 看向 +z，`IngameCamera.cs:440`），而两件东西的 z 不同：

| 节点 | local z | 谁在上面 |
|---|---|---|
| 零件自己的 `Visualization`（盒子，`Part_SpringBoxingGlove_01_SET.prefab`） | **0.1** | ✅ 更近 → 后画 → **盖住拳头** |
| 拳套的 `Visualization`（拳头，`BoxingGlove.prefab`，挂在件原点下） | **0.15** | 更远 → 先画 |
| 零件自己的 `SpringVisualization`（弹簧线） | 0.2 | 最远 |

所以原版收回时拳头是**贴着盒子滑进去**、被盒子的美术盖住，而不是凭空消失（盒子自己的美术里本来就画着收在里面的拳头与弹簧，静止时那一步是无缝的）。
PigForge 的渲染器跨实体只按快照顺序（实体升序 = 创建序），子实体的 id 大于宿主 → 拳头画在盒子**上面**，才让收尾那一下显眼。

修法：`clients/web/src/renderer/draw.ts` 的 `drawOrder(entities)` 把每个子实体**紧挨着它所属的件之前**画（归属 = 同 `partTypeId` 里最近的那个非子实体；找不到就留在原位），
其余顺序与实体数完全不变。⇒ 拳头在收缩过程中始终位于盒子之下，`WindedUp` 把它从帧里去掉时它已经在盒子底下了（与 `SetActive(false)` 的那一瞬一致）。
测试：`draw.test.ts` 的 `drawOrder` 组 + `paints the host part over its own sub-entity`（真 `drawFrame` 的 `drawImage` 顺序）。

## 5. 触发路径（服务器权威）

- **按钮式**（`activation: trigger`，发布内容的选择）：`SetPartActive`（既有命令通道，条上的按钮与点零件都走它）→ `GameRoom.RunGloves`
  在 `_rules.TryConsumeButtonPress(host)` 上出拳；按下一律被消费（即使 `CanBeEnabled` 拒绝），这样按钮永远不留在开位。
  **物理接触不出拳**：原版的 `ProcessTouch` 只由 UI 到达（`Contraption.OnButtonTriggered`/`ActivateOnePartOfType`/鼠标），从不来自碰撞事件；
  早先实现把 `ContactStarted` 当触摸，会「落地就出拳」，已删除。
- **开关式**（`activation: toggle`，原版 IN 那条）：`SetPartActive` 读成档位，off→on 沿出拳，on→off 中断/回卷。
- 两条路都只从 `WindedUp` 出拳；`CanBeEnabled`：手套身后那件有 SuperGlue 且不是 TNT → 不可用（原版 `m_CanBeEnabled` 的近似，写清口径）。

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
| 可重复 | — | 再按一次 → 第二次同样打出 ≥ 2.25 m | — |
| 子实体标志 | — | 手套的 `flags` bit1 = 1、宿主 = 0（PGFS v5） | — |
| 静止时帧里没有拳头 | — | 缠绕态：帧里只有盒子（`SubEntityCount` 仍是 1，刚体在）；出拳后拳头才出现在帧里、回卷结束又消失（`APlacedGloveSpawnsAFistTheFrameHidesUntilItIsThrown`、`APunchThrowsTheGloveToTheSkinsDistanceThenWindsItHome`） | 原版 `SetActive(false)`（`:215-222`） |
| 按钮不留在开位 | — | 出拳中途再按一次 → 快照 `Active` 仍为 0（按下一律被消费） | — |
| toggle 分支 | — | 同一装置把内容改成 `toggle`：off→on 出拳、level 一直为 1、再按无效（`AToggleGloveThrowsOnItsSwitchLevelAndStaysLatchedOn`） | — |
| 确定性 | 双跑逐 tick 轨迹相同 | 双跑 `ComputeStateHash()` 相同 | — |
| 子实体清场 | — | RESET / 离开后 `SubEntityCount == 0`，重开再建 | — |

**真客户端读数（2026-10-04 第十三轮，浏览器跑 vite 页面 + 真房间）**：页面的 `viewState.entities` 里读到拳头的 `subEntity === true`（与宿主相距 2.12 m 时），
`loadPartTextures()` 给出 part 28 的 `subSprites` = 1 张（`IngameAtlas.png`，rect `(1694,1122,99×85)`，世界尺寸 1.0313×0.8854、`rot −1.5708`），
把它画进 canvas 得到 **5822 个不透明像素**（图集确实加载到了）；录屏（7 s，30 fps）里拳头出现在拳套箱旁约 2.5 m 处——**弹出的确实是拳头，不再是一个拳套零件**。

三个测试层（2026-10-04 第十三轮收工）：Core 266、Server 129、Physics 66（非 Jolt）+ 16（Jolt）全绿；Release 全量重建 0 警告 0 错误。
拳套的一条 `_jointedPairs` 抑制让手套与**它所属的那个刚体**（并簇后即整簇）互不接触，正是原版的 `IgnoreCollision`。

## 7. 已知偏差（排队或写清，别只写在 ADR 里）

- **拳头的图只有一张静态精灵**（2026-10-04 已补）：`m_BoxingGlovePrefab` 的 `Visualization` 抽进清单的 `subSprites`，子实体按它画；
  弹簧线（`SpringVisualization`，原版按手腕到拳头的距离建一条拉伸的线）仍**没有**视觉——它需要客户端按两个实体的位置自己生成，与弹簧的线视觉同一批（P1）。
  没有 `subSprites` 的子实体（拉断弹簧的端点）继续借宿主的图（现状不变）。
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
