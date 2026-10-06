# ADR-031: 接触法线是表面的几何法线（取代 ADR-010 决策 3）

## 状态

Accepted。**取代 `ADR-010` 决策 3**（「法线朝向不信任引擎约定：Bepu 侧用求解前相对速度定符号」）。

## 日期

2026-10-06

## 背景

差距 `G102` 要求动力轮像原版那样**沿脚下的地面切线**出力：`MotorWheel.FixedUpdate` 从轮心沿上次接触方向射线（`MotorWheel.cs:285-287`），把命中面的法线交给 `Vector3.Cross(hitInfo.normal, Vector3.forward)`（`:288`）。规则层没有射线，最初的做法是**从接触事件里取地面法线**——于是量到事件里的 `ContactNormal` 在「静止的轮子压在地面上」时**逐 tick 翻号**（同一对 `a=Static/2, b=Dynamic/10`：196–200 tick `(0,-1,0)`、201–203 tick `(0,1,0)`；同一个 fixture 的 `tangent` 因此在 `(1,0)` 与 `(-1,0)` 之间来回，车几乎不动，峰值只有 5.99 m/s，而修好后是 15.03）。

根因在 `ADR-010` 决策 3 本身：Bepu 后端的 `RecordContactImpact` **不信任 manifold 的符号**，改用求解前相对速度定向（`oriented = along < 0f ? normal : -normal`），而它对 `approachSpeed <= 0` 直接 `return`。于是事件里的法线其实是**冲击方向**：

- 静止/滑动的接触**没有条目** → 事件发的是 `(0,0,0)`（或上一次冲击的残留）；
- 相对速度反向的那几个 tick（弹跳回落、微分离）会把符号**翻过来**。

而在原版自己的编辑器上量的 Bepu 原始 manifold 法线是**几何的、稳定的**：

```
RAW a=Dynamic/1 b=Static/2 n=(0,1,0)      （静止的球压在地面：原样就是「分离 pair.A」）
RAW a=Dynamic/1 b=Static/2 n=(0,-0,1)     ...
```

即 Bepu 的 `IContactManifold.GetNormal` 本来就满足 `PhysicsEvent.ContactNormal` 文档承诺的朝向（「把 `BodyA` 沿 `+Normal` 推、`BodyB` 沿 `-Normal` 推即可分离」），`ADR-010` 那次「不信任」是多余的。

## 决策

1. **事件里的 `ContactNormal` = manifold 的几何法线**，按事件自己的 key 序（`ContactPair` 按 body id 排序）定向：`BepuPhysicsWorld.RecordContactNormal` 只做 key 序交换时的取反，**不再按相对速度定符号**；并列取分量字典序最小者，与 manifold 遍历顺序无关。
2. **Jolt 同契约**：`JoltPhysicsWorld.RecordContact` 读 `manifold.WorldSpaceNormal` 并按 key 序定向。它的朝向与 Bepu 相反（实测：静止的箱子对「地面在前」的对报 `(0,-1,0)`，即 Jolt 的法线分离回调对的**第二个** body），这个符号由 `JoltPhysicsWorldReportsTheSurfaceNormalABodyRestsOn` 钉住。
3. **接近速度仍归速度**：`RecordContactImpact` 继续按「求解前相对速度在法线上的投影的绝对值」记录 `ApproachSpeed`（静止/分离为 0，取最强接近做确定性归约），它仍是 `ADR-010` 弹性合成的唯一输入。**只有符号来源**从速度改成几何——对真正在接近的接触两者本来就一致（实测：把法线改回速度定向，34 个静止 tick 里 11 个翻号）。
4. **契约文档同步**：`PhysicsEvent.ContactNormal` 的注释写明「它是表面自己的法线，静止/滑动的接触也一直报」（`src/PigForge.Physics.Abstractions/PhysicsContracts.cs`）。

## 取舍与后果

- **换来的**：`G102` 需要的「脚下地面」有了稳定来源（规则层取该 body **最朝上**的那个接触法线）；静止接触第一次带上法线（此前恒为零）；Jolt 后端从「完全没有法线」变成有法线。
- **代价**：事件里 `ContactNormal` 的语义从「冲击方向」变成「表面几何」，任何按旧语义写的消费者都会读错——本仓库已知的消费者只有弹性合成（决策 3 已覆盖）与新的动力轮。
- **精度边界**：一个 body 与多个面同时接触时，后端在**每对**里只留一个法线（字典序最小），规则层再在 body 的所有对里取 `Y` 最大者。多面同时接触的几何（例如卡在夹角里）仍可能选到墙面——原版的射线同样只看它当时指向的那一面（`m_lastContactDirection`），两者都是「取一个」而不是「取全部」。
- **测试**：`tests/PigForge.Physics.Tests/ContactNormalTests.cs`（Bepu：静止 40 tick 每 tick 都是 `(0,1,0)`；30° 斜面法线 `(-0.5, 0.866)` 且它给出的切线正是坡面方向）、`JoltPhysicsContractTests.JoltWorldReportsTheSurfaceNormalABodyRestsOn`。非空验证：把 `RecordContactNormal` 改回按速度定向，静止用例 34 个 tick 里 11 个翻号（红）。
