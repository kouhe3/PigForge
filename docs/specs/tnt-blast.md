# 炸药族的爆炸（TNT / TimeBomb / AlienTNT）

> 2026-10-13。把「冲量从代码常量来」改成「从原版 prefab 来」：`GameplayConfig` 的
> `TntBlastRadius` / `TntBlastImpulse` / `TntIgniteImpactSpeed` 从此只是**没有内容数值时的兜底**
> （手写夹具），凡是从内容放置的炸药都用自己的数。工具 `tools/bple-tnt`。

## 1. 一句话

原版每种炸药 prefab 自己带三个数 —— 爆炸半径、冲量、撞击阈值 —— 爆炸形状是
`impulse / max(distance, 1)^1.5`，而 `AlienTNT` 是一个**不销毁、可反复引爆、别人连锁不到它**的子类。
PigForge 之前把半径/冲量写成全局常量（4 / 25）、用线性衰减、并把所有炸药一视同仁；本片全部按 prefab 与类走。

## 2. 原版真值（逐行）

| 事实 | 出处 |
|---|---|
| 撞击点火：`OnCollisionEnter` 里 `c.relativeVelocity.magnitude > m_triggerSpeed` → `Explode()` | `TNT.cs:78-83` |
| 爆炸范围：`Physics.OverlapSphere(transform.position, m_explosionRadius * IN TNTExplosionRadius)` | `TNT.cs:129` |
| 每个被击中的刚体：`FindParentWithRigidBody` → `CountChildColliders` → `AddExplosionForce(go, IN TNTExplosionForce / 子碰撞体数)` | `TNT.cs:130-150` |
| `AddExplosionForce`：`mag = factor * m_explosionImpulse / max(\|目标 − 炸药\|, 1)^1.5`，`mass < 0.1` 再乘 `mass`、`mass < 0.4` 再乘 `mass/0.4`；目标是猪时先 `PrepareForTNT` 再多给 **×1.15** | `TNT.cs:211-233` |
| 连锁：半径内每个 `component is TNT && !(component is AlienTNT) && !HasGeneratorRef` 的件调用它自己的 `Explode()`；`BlasterTNT` 在 2 单位内走 `ExplodeSpecial()` | `TNT.cs:152-165` |
| 普通炸药炸完 `contraption.RemovePart(this)` ⇒ 零件离开世界 | `TNT.cs:170` |
| `AlienTNT.Explode()`：`m_explodeTime` + `AlienTNTExplosionCoolingTime` 冷却门控 → 设 `m_triggered = true` → 爆炸（用 `AlienTNTExplosionRadius/Force`）→ **不 RemovePart**；`LateUpdate` 把 `m_triggered = false` ⇒ **可反复引爆** | `AlienTNT.cs:8-52,58-61` |
| `AlienTNT.OnCollisionEnter` 是**空**的 ⇒ 撞击不点火 | `AlienTNT.cs:54-56` |
| 设定档（vanilla 声明默认）：`TNTExplosionRadius/Force = 1.0`、`AlienTNTExplosionRadius/Force = 1.0`、`AlienTNTExplosionCoolingTime = 0.0`、`BlasterTNT = false` | `INDeclarationSettingsExp.json`（工具断言这六项） |

prefab 实参（`tools/bple-tnt` 读出）：`Part_TNT_01..06_SET`、`Part_TimeBomb_01_SET` 全是
**impulse 25 / radius 8 / triggerSpeed 4**；`Part_TNT_07_SET`（BlasterTNT）是 25 / 8 / **32**，且它没有
`tnt` 能力（它是 `blaster`）。

## 3. 内容契约（`tnt` 能力）

```json
"tnt": { "fuseTicks": 5, "radius": 8, "impulse": 25, "triggerSpeed": 4 }
```
`AlienTNT`（51 `tnt-v06`）多两个类标志：
```json
"tnt": { "fuseTicks": 5, "radius": 8, "impulse": 25, "triggerSpeed": 4, "igniteOnImpact": false, "repeatable": true, "chainTarget": false }
```

| 键 | 含义 | 来源 |
|---|---|---|
| `fuseTicks` | 从点火到爆炸的 tick 数（PigForge 自己的引信；原版是立即炸） | 手写 |
| `radius` | `m_explosionRadius × IN <类>ExplosionRadius` | `tools/bple-tnt` |
| `impulse` | `m_explosionImpulse × IN <类>ExplosionForce` | 同上 |
| `triggerSpeed` | `m_triggerSpeed`：撞击点火的门槛 | 同上 |
| `chainDetonate` | 只在这件**炸的时候**要不要连锁别人（默认 true） | 手写 |
| `igniteOnImpact` | 只写 false（`AlienTNT` 不吃撞击） | 同上 |
| `repeatable` | 只写 true（`AlienTNT` 炸后留在世界、复位 `Ignited` 并按 `fuseTicks` 重新上膛） | 同上 |
| `chainTarget` | 只写 false（别人连锁不到这件，`TNT.cs:152`） | 同上 |

`radius`/`impulse`/`triggerSpeed` 缺席或为 0 时退回 `GameplayConfig` 的同名值 —— 这条只服务于
**手工构造的测试夹具**；内容永远带全。

## 4. 规则层

- 点火门槛：每件自己的 `triggerSpeed`（`IgniteTntOnBody` 取 `state.TriggerSpeed`，0 回退 config）。
  比较的量是**接触对的相对速度模**（`GameplayRules.PairRelativeSpeed`：事件自带的 `PhysicsEvent.RelativeSpeed`、
  上一 tick 的速度、最近 2 tick 的峰值，外加同一事件的法向接近速度作下界），**不是法向投影** ——
  原版写的是 `c.relativeVelocity.magnitude > m_triggerSpeed`（`TNT.cs:80`），所以贴着地面高速滑擦
  （法向为 0、模长过门槛）在原版里会炸，这一条与关卡木箱共用（`docs/specs/level-props.md` §3.10）。
- 爆炸：`GameplayRules.ApplyBlast(origin, radius, impulse, output)` —— 与关卡木箱（
  `docs/specs/level-props.md` §3.10）**同一个实现**：`impulse / max(d,1)^1.5`、质量 <0.1/<0.4 折减、
  排序后的 body 逐条 `ApplyImpulse`（确定性）。
- 销毁：普通炸药 `DestroyEntity`；`repeatable` 的只把 `Ignited` 清掉、`FuseTicks` 复位成
  `ReloadTicks`（= 内容里的 `fuseTicks`）⇒ 下一次点火照旧。
- 连锁：半径取**源件自己的 `radius`**（`TNT.cs:129`），目标必须 `ChainTarget`；连锁不递归（同 tick 只点一次火）。

## 5. 与内容/房间的接线

| 位置 | 做什么 |
|---|---|
| `tools/bple-tnt/extract-tnt.mjs` | 按 `content/parts.json` 里带 `tnt` 的 7 件解析 prefab 的类与三个字段，断言类直方图 `{TNT 5, TimeBomb 1, AlienTNT 1}`、`impulse {25:7}`、`radius {8:7}`、`triggerSpeed {4:7}` 与六个 IN 倍率 = vanilla |
| `tools/bple-tnt/apply-tnt.mjs` | 把 `tnt` 写成 `{ fuseTicks, radius, impulse, triggerSpeed, igniteOnImpact?, repeatable?, chainTarget? }`（后三个只在偏离默认时写），幂等 |
| `PartContentParser` / `part-content-v1.schema.json` / 客户端 `validateContent.ts` | 同一套形状与校验（数值非负、布尔可选） |
| `GameRoom.RegisterPlacedRole` / `Spawn` | 把能力里的五个值传进 `GameplayRules.AddTnt`（关卡自带的炸药 actor 也走同一份能力） |

## 6. 已知偏差

1. **冲量落在目标 body 的质心，没有原版「每碰撞体 1/N」的除数**：PigForge 的复合体是一个 body，原版是每个
   collider 一个 rigidbody（与 `ADR-029`、`docs/specs/level-props.md` §3.10 同一类近似）。
2. **猪的 ×1.15 未建模**：它作用在猪**自己的** rigidbody 上，而猪与载具共用一个 body。
3. **引信**：原版 `OnCollisionEnter` 立刻 `Explode`，PigForge 先点火再按 `fuseTicks` 烧（内容 5 tick）——
   既有设计，本片不改；`BattleTNT` 的连锁也因此晚 5 tick。
4. `AlienTNT` 的「可反复引爆」在 vanilla 冷却时间是 0（等价于每帧可再炸），PigForge 按**下一次撞击/开关**再点火，
   不模拟「同一帧连炸」。
5. `BlasterTNT`（52）不在 `tnt` 能力里：它是 IN 扩展件，走 `blaster`（见 `ADR-006`）；其 2 秒扩张波仍未做。

## 7. 验收

- `node tools/bple-tnt/extract-tnt.mjs` 全绿（6 个断言）；`apply-tnt.mjs` 第二次 `0 updated`。
- Core：`AChargesOwnRadiusAndImpulseDriveItsBlast`（6 m 处仍被击中、3 m/6 m 的冲量对上 `25/d^1.5`）、
  `ARepeatableChargeSurvivesItsBlastAndFiresAgain`、`AChargeThatRefusesTheChainStaysDark`（双向）。
- Server：`TntBlastRoomTests.AContentChargeChainsOneSixMetresAway`（房间配置故意留 4 m，连锁只能来自内容的 8 m）。
- 弹簧两处夹具按新衰减重标（`docs` 引用见夹具注释）：pull 的炸药从 2 格挪到 1 格（锚点 12 tick 拉开 3.63 m），
  split 的炸药放到 1.5 m（拆成两个 body，弹簧关节仍在）。
