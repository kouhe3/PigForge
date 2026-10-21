# ADR-002: 运行规则语义以原版实测行为为准——无伤害系统

## 状态

Accepted

## 日期

2026-08-30

## 背景

PigForge 的运行规则（发动机、轮子、猪、TNT、断裂、关卡结果）需要从原版《捣蛋猪》迁移。反编译产物只覆盖了固定逻辑，规则语义需要从原版实测行为反推。设计讨论中曾按通用物理游戏惯例引入了"伤害/血量"模型（接触冲击伤害、爆炸范围伤害、血量归零死亡、猪全灭过关）；经逐项对照原版行为，该模型与原版不符：

- 原版没有血条，没有任何伤害累积系统。
- 猪受力不会死亡，只会以高弹性弹开；猪王与鸟蛋则完全无弹性（restitution 为 0）——弹性是每零件材质属性。
- TNT 爆炸不直接摧毁任何东西，只给半径内动态体施加冲量。
- 结构断裂的机制是冲量使关节应力超过阈值、关节断开、零件脱落——没有伤害这个中间变量。
- 过关条件是猪进入终点触发区（投递），不是猪全灭。
- 猪飞出地图边界会导致关卡重开（状态重置），不是死亡表现。

## 决策

1. **无伤害系统原则**：运行规则层只存在四个原语——冲量、关节断开阈值、位置触发器（终点区/地图边界）、状态重置。禁止引入伤害、血量、护甲等任何数值化中间层，无论可见或隐性。
2. **材质属性进内容层**：每零件的 restitution、friction 是 `part-content` 格式的材质字段（猪 ≈ 高弹性，猪王/鸟蛋 = 0，结构零件 = 低弹性），不是代码常量，也不是全局参数。当前后端硬编码的 friction 0.8 / restitution 0 是占位，迁移材质属性后废除。
3. **断裂 = 关节阈值**：唯一破碎机制是接触产生的冲量使关节应力超过内容层断裂阈值，关节断开、零件脱落。判定是即时的、无累积状态：`break = impactImpulse > jointBreakThreshold`。未来若引入合并刚体（compound）以降低关节数，断裂语义相应改写为"冲量超过缝隙阈值 → 沿预设缝拆分实体组"，仍无伤害累积。
4. **TNT = 纯动量源**：爆炸只产生确定性的排序冲量命令，半径与冲量强度进内容/关卡参数。
5. **猪 = 不可摧毁货物**：猪不接受任何死亡判定；运行时对猪的全部规则是位置判定——进入终点区（胜利）、离开地图边界（触发重开）。
6. **重开 = 确定性状态重置**：回放路径从 initialState 重放即可；实时路径是房间状态机的重置转移。两者共享同一 initialState。
7. **反推原则**：凡原版行为可观察的规则，实现前先从原版行为反推属性并用内容格式表达；禁止借用手感相近的其他游戏（Angry Birds、Besiege 等）的通用模型。本 ADR 未覆盖的规则（轮子抓地、发动机、关卡触发器细节）在实现前同样按此原则先做语义确认。

## 影响与偏差清单

当前实现（`GameplayRules`）与原版语义的已知偏差，修订运行规则时必须连同测试一起重写：

- 接触冲击伤害杀猪（应删除：猪不可摧毁）。
- 爆炸范围伤害（应删除：TNT 只施加冲量）。
- `Won = 猪全灭`（应改为：猪进入终点触发区）。
- `Failed = 超时`（应确认原版失败条件；已确认的是猪出界触发重开）。
- 结构断裂尚未实现；实现时按本 ADR 第 3 条执行，不引入 DamageStore 累积模型。
- 关卡内容格式尚不存在：终点区、地图边界、出生点是关卡格式的一部分，定义关卡格式时纳入。

## 参考

- ADR-001：net10 物理后端边界
- `docs/specs/pigforge-headless-spec.md`：运行规则迁移的验收框架
- `docs/implementation-plan.md` Task 9：运行规则迁移范围

## 2026-10-13 修正：撞击的「大小」怎么量（差距 `G116`）

规则层量一次撞击强度的地方有两处：本 ADR 决策 3 的关节/缝隙断裂（走冲量），以及按内容阈值判定的三条撞击规则
（TNT 撞击点火、蛋撞击破碎、脱钩件撞击脱离 —— `GameplayRules.ProcessEvents` 的 `ContactStarted`）。

后者原先读「本 tick 与上一 tick 的快照速度差」（`VelocityChange`，取 A/B 较大者），这**测不到真正的落地**：
求解器的穿透恢复弹簧在形状看起来还没接触时就开始吸能，接触事件比最后一个自由 tick 晚两拍 —— 实测（真 Bepu，
一个箱子落到静态盒上）自由落体到 **11.61 m/s**，`ContactStarted` **两拍之后**才到、它自带的 `ApproachSpeed` 只剩
**2.60 m/s**，而本层「上一 tick 速度」也只剩 2.60；于是一个 10.7 m/s 落地的 TNT 在那只箱子上**永远不点火**
（第三十三轮 smoke 的现象）。

修正后两条规则共用同一个估计（`GameplayRules.PairApproachSpeed`）：**后端自己的接触前相对法向速度**、
**上一 tick 的速度**、与**最近 2 tick 的峰值**（峰值寿命 2 tick 是 ADR-010「已知偏差 3」量出来的）三者取最大，
再交给各自的阈值（`TntIgniteImpactSpeed` / `EggBreakImpactSpeed` / 脱钩阈值）；弹跳门控（ADR-025 的
`m_BounceThreshold`）读同一个值。峰值必须在里面：上面那次落地只有它还记得 11.61。

回归：`GameplayRulesTests.ALateContactEventStillIgnitesTheChargeTheFallLoaded`（事件与上一 tick 都只剩残值时，
峰值仍要点火）与 `ImpactRulesRoomTests`（真内容 + 真 Bepu：一次 10.7 m/s 的落地点火并炸掉，一次轻放不点火）。

## 2026-10-21 修正：点火量是相对速度的**模**，不是法向投影

上一节把点火归到 `PairApproachSpeed`（法向投影）是错的：原版写的是
`c.relativeVelocity.magnitude > m_triggerSpeed`（`TNT.cs:78-83`；关卡木箱 `TNTBox.cs:22-24` 同一个量），
而法向投影在原版里只出现在**音效**（`BasePart.PlayCollisionAudio` 遍历 `c.contacts` 取
`Dot(relativeVelocity, contact.normal)` 的最大值）和**破件**
（`Dot(rigidbody.GetPointVelocity(point), contact.normal)`，`BasePart.cs:975-985`）。投影量永远 ≤ 模长，
所以旧实现对"贴着地面高速滑擦"这一整类接触判为 0：原版会炸，PigForge 不会。

修正后点火与木箱读 `GameplayRules.PairRelativeSpeed`：后端在 narrow phase 记录的
`PhysicsEvent.RelativeSpeed`（`|vA − vB|`，与 `ApproachSpeed` 同一时刻同一对速度）、上一 tick 的速度、
最近 2 tick 的峰值三者取最大，再把同一事件的法向接近速度作为**下界**并入（投影不可能超过模长，所以这一步
只可能抬高、不会伪造）。蛋破碎、脱钩、弹跳三条仍读 `PairApproachSpeed` —— 那里法向才是物理上正确的量。

回归：`GameplayRulesTests` 既有的点火 / 木箱 / 连锁用例按新语义复核后仍绿（事件里只给 `ApproachSpeed`
的正面撞击仍是撞击，因为投影并入下界）；新增
`AChargeScrapedAlongASurfaceIgnitesWithNoApproachSpeed` / `ASlowSlideUnderTheThresholdLeavesTheChargeDark` /
`ALevelCrateBlowsUpWhenARigScrapesAcrossItAtSpeed`（规则层），
`ContactRelativeSpeedTests`（真 Bepu：滑行对报模长、法向为 0；静止对不报速度），
`FrictionIgnitionTests`（真 Bepu + 规则：6 m/s 滑擦点火、2 m/s 不点火）。
