# ADR-030: 零件镜像是一个独立的姿态位（PGFS v6 / PGFC v3）

## 状态

Accepted

## 日期

2026-10-06

## 背景

原版点击已放置零件是 `Contraption.Flip`（`Contraption.cs:1918-1944`）：`m_autoAlign == FlipVertically` 的件**翻转镜像**、`Rotate` 的件 90° 步进、八向件 45° 步进。343 个 prefab 里 **17 个**声明 `m_autoAlign: 2`（`FlipVertically`）——就是**机翼与尾翼两族**（`WoodenWings_01..05`、`MetalWings_01..04`、`WoodenTail_01..04`、`MetalTail_01..04`）。

翻转的实现是 `BasePart.SetFlipped`（`BasePart.cs:639-651`）：`localRotation = Quaternion.AngleAxis(180, Vector3.up)`；`Wings`/`Tail` 又把姿态编码成 **8 态** `rotation = gridRotation × 2 + (flipped ? 1 : 0)`（`Tail.cs:106-121`），解回 `Quaternion.Euler(num3, num4, num5)`。这等价于 **`Rz(yaw) ∘ Ry(180)`**：镜像发生在**零件自己的参考系**里、在建造 yaw **之前**（`docs/specs/part-mirror.md` §5.1 有逐 `num2` 的矩阵验证，`unity/PigForge.WeldProbe` 的 `MirrorAeroProbe` 在 Unity 2021 自己的 `Quaternion` 上复核）。

**关键点：这是一个手性（handedness），不是一个旋转。** 平面图元上它等于「沿自身 Y 轴的水平镜像」，但任何 float yaw 都表达不了：把 8 态投影成四分之一圈，`num2 = 0, flipped` 与 `num2 = 2, unflipped` 的**图形**不同却是同一段 yaw。PigForge 之前只有 yaw（差距 `G05`），所以「把滑翔翼翻过来」在 PigForge 里根本不存在；而升力必须按零件自己的参考系算（差距 `G55`），否则镜像连物理含义都没有。

## 决策

1. **姿态 = `yaw + mirrored`**：建造姿态是二元组，姿态四元数 = `Rz(yaw) ∘ Ry(180)`（`src/PigForge.Core/Construction/BuildPose.cs`）。`ConstructionRules` 用 `HashSet<uint> _mirroredEntities` 记住手性，因为**它不能从四元数反推**（四分之一圈上的镜像姿态恰好是一个绕平面内轴的 180° 旋转，`(yaw, mirror)` 与 `(yaw + 180, mirror)` 可以描述同一个旋转），也因为 `FreezeAll` 会把姿态换成刚体位姿（`Start` 之后）。
2. **线上两位**：
   - **PGFS v6**：实体 `flags` 的 **bit2 = mirrored**（bit0 开关、bit1 运行期子实体，`ADR-028`）；bit3–7 保留写 0。
   - **PGFC v3**：`PlacePart`（kind 0）与 `RotatePart`（kind 2）各加一个**绝对**的 `mirrored:u8`。旋转与手性都是绝对值同批发送，客户端永远发「我现在显示的那个」——**不是** toggle。
   - 两个版本常量同批改（`CommandFrame.Version = ProtocolVersion.Current = 3`、`SnapshotFrame.CurrentVersion = 6`），旧客户端整帧/整命令拒绝是有意的。
3. **内容门控**：只有声明 `capabilities.mirror` 的件接受镜像（= prefab 的 `m_autoAlign == 2`，由 `tools/bple-aero` 提取）；其余件带镜像 → `ConstructionError.PartNotMirrorable`。UI 侧 `F` 键只对这类件生效。
4. **几何有一处真值、两处投影**：
   - 物理几何**不用特判**：`CompoundAssembler` 把形状 `offset` 乘以零件姿态四元数，`Ry(180)` 已经把 x、z 取反，且镜像后仍是正常旋转（行列式 +1），不会出现负缩放刚体。
   - 建造平面投影（占格与连接几何，`PartFootprint`）显式取反形状 `offset.x` 与 `gridBox` 中心 x，再套 yaw。
5. **气动同批重做**：`RunWings`/`RunTails` 换成原版的曲线（`Aerodynamics.cs`，源码常量 + `file:line`），取轴走 `ResolvePartFrame`（`ADR-029`），`IsFlipped()` 的符号从装配时发布的手性位读。**镜像因此是真的物理输入**：同一速度下不镜像的机翼向下推、镜像的向上推（`GameplayRulesTests`）。
6. **客户端**：镜像件在**零件自己的参考系**里 `ctx.scale(-1, 1)` 画（精灵 x 偏移取反、精灵自身旋转反向），并与清单里已有的 `flipX`（原版的负节点缩放）**相乘**——两者都是同一个节点上的标量。

## 取舍与后果

- **换来的**：`G05` 与 `G55` 双双收口；机翼/尾翼的升力终于按零件自己的参考系算，转向与镜像都能改变它；协议里第一次有一个真正的「非旋转自由度」。
- **已知偏差 / 不做**：
  1. **自由 yaw 仍在**：原版 vanilla 下 `FlipVertically` 件只能镜像（8 态循环来自 B 档 `RotatableWing`），PigForge 保留既有的自由旋转 UI，镜像在其上再开一位（与 `G96` 同源，不新记差距）。
  2. **不做原版 `Flip` 的「连接方向合法性重试」**（`Contraption.cs:1928-1944` 最多翻 3 次里有一次连接方向合法就停）：PigForge 的焊接是邻近几何判定（`ADR-017/018`），没有「方向合法性」这个概念。
  3. **件内绘制顺序不因镜像重排**：原版 180° 绕 Y 会把每个精灵的 z 取反（`Wings.OnFlipped` 又把 `WingSprite` 的 z 补回来）。清单没有 z 字段，而 17 件里会互相遮挡的只有机翼/尾翼的两态支架——同一时刻只有一个可见（`condition`）——所以镜像后的相对次序没有可观测差别；`renderer/draw.ts` 只做 x 镜像与旋转反向。
  4. **`WindVelocity` 恒为零**（无风区），**`dragConstant` 不落地**（`Wings.FixedUpdate` 从不读它，是死字段）。
  5. **响应曲线在代码里**：它是类的常量（`Wings.Start` / `Tail.Start`），不是 prefab 字段，任何提取器都读不到（与 `MotorWheelMaximumSpeed = 15`、`SeamBreakImpulse` 同类）。
