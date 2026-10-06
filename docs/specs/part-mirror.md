# 规格：零件镜像（原版 `SetFlipped`）与机翼/尾翼气动

状态：已实施（**G05 + G55 收口**，2026-10-06）。
来源：原版反编译脚本 `BPLE_Unity6/Assets/Scripts/Assembly-CSharp/{BasePart,Wings,Tail,Contraption,ResponseCurve}.cs`、
`Assets/GameObject/Part_{Wooden,Metal}{Wings,Tail}_0*_SET.prefab`（17 个）。
决策记录：`docs/decisions/ADR-030-part-mirror.md`。

## 1. 要解决的偏差

| 差距 | 现象 |
|---|---|
| `G05` 镜像/翻转未实现 | 原版点击已放置件是 `Flip`：`m_autoAlign == FlipVertically` 的件**翻转镜像**（`Wings`/`Tail` 族），其余件 90° 或 45° 步进旋转。PigForge 只有 yaw，**没有镜像**——一个 float yaw 表达不了**手性**，所以这不是「补一个角度」，而是缺一个自由度和一条线上位。 |
| `G55` 机翼/尾翼升力模型 | 原版升力是**零件自己参考系**里的曲线：`liftConstant × \|v\|² × curve(攻角)`，方向 `Cross(transform.forward, v̂)`，夹到 100 N；木翼 0.8 / 金属翼 1.5、木尾 0.2 / 金属尾 1.0。PigForge 是手写的 `wing{liftCoef 0.05, maxLift 6.0}`（恒定 **+Y 世界升力**）与 `tail 0.03`（速度阻尼）。**只要升力是常量世界轴，镜像就没有任何物理含义**——两条必须同批做。 |

## 2. 原版真值（逐条出处）

| # | 规则 | 出处 |
|---|---|---|
| 1 | `AutoAlignType { None = 0, Rotate = 1, FlipVertically = 2 }` 是**每件 prefab 字段** `m_autoAlign`；343 个 prefab 里 153 个 `0`、173 个 `1`、**17 个 `2`**（`WoodenWings_01..05`、`MetalWings_01..04`、`WoodenTail_01..04`、`MetalTail_01..04`） | `BasePart.cs:79-84,191`；prefab 字段（`grep -l 'm_autoAlign: 2'`） |
| 2 | `SetFlipped(bool)`：写 `m_flipped`，`localRotation = AngleAxis(180, Vector3.up)`（翻转）/ `identity`（不翻），再 `OnFlipped()` | `BasePart.cs:639-651` |
| 3 | `Wings`/`Tail` **覆写** `SetRotation`/`SetFlipped`/`GetRotation`：姿态是 **8 态** `rotation = gridRotation × 2 + (flipped ? 1 : 0)`，`SetRotation(int)` 解回 `localRotation = Euler(num3, num4, 90 × num2)`，其中 `num3 = 180`（翻转且 `num2 ∈ {1,3}`）、`num4 = 180`（翻转且 `num2 ∈ {0,2}`） | `Tail.cs:86-121`、`Wings.cs:130-165` |
| 4 | 上式**等价于** `Rz(yaw) ∘ Ry(180)`：镜像是在**零件自己的参考系**里绕自身 Y 轴转 180°，再套建造 yaw（代数验证见 §5.1；对平面图形 = 沿自身 Y 轴的**水平镜像**，这也是用户说的「水平镜像」） | 推导自 3；`Wings.OnFlipped` 只补美术（见 7） |
| 5 | `Contraption.Flip(part)`：`IsCustomRotated()` → `SetRotation(GetRotation() + 1)`（**8 态循环**）；`FlipVertically` → 最多试 3 次 `SetFlipped(!IsFlipped())`，连接方向合法或 `Any` 就停；`Rotate` → `RotateClockwise()` | `Contraption.cs:1918-1944` |
| 6 | 翻转件的 `jointConnectionDirection` 会 `Left↔Right`、`Up↔Down` 互换（`GetJointConnectionDirection` 里 `if (m_flipped)`） | `BasePart.cs:700-712` |
| 7 | `Wings.OnFlipped()`：把 `WingSprite` 子节点的 localPosition **z 取反**（抵消 5 号那条整体 z 翻转，让机翼美术留在原图层） | `Wings.cs:32-39` |
| 8 | 机翼升力（`FixedUpdate`，`IsRunning` 且 `!SwitchableWing \|\| m_enabled`，vanilla `SwitchableWing = false`）：<br>`v = velocity − WindVelocity`（`WindVelocity` 立即清零）<br>`right = transform.right`<br>`x = (IsFlipped() ? −1 : 1) × sign(Cross(v, right).z) × Angle(v, right)`<br>`force = liftConstant × \|v\|² × curve(x) × Cross(transform.forward, v̂)`，`ClampMagnitude(force, 100)`，`AddForce(force, ForceMode.Force)` | `Wings.cs:104-118` |
| 9 | 尾翼升力：同 8，但先算 `num = IsFlipped() ? −1 : 1`、`num2 = sign(Cross((1,0,0), right).z) × Angle((num,0,0), right)`、`right = AngleAxis(0.4 × (num2 − 30), transform.forward) × right`，再 `x = num × sign(Cross(v,right).z) × Angle(v,right)`（`num` **与镜像后的 `transform.right` 同时生效**，原版就是双重） | `Tail.cs:57-75` |
| 10 | 响应曲线（分段线性，两端夹住；`ResponseCurve.Get`）：机翼 `(−180,0) (−135,−0.2) (−90,0) (−45,−0.2) (−10,0) (10,1.5) (15,1.75) (19,0.8) (22,0.1) (45,0.2) (90,0) (135,−0.2) (180,0)`；尾翼 `(−180,0) (−135,−1.5) (−90,0) (−45,−1.5) (−10,0) (10,1) (45,1.5) (90,0) (135,−1.5) (180,0)` | `Wings.cs:76-88`、`Tail.cs:32-41`、`ResponseCurve.cs:26-52` |
| 11 | 序列化真值（17 个 prefab，逐族一致）：`liftConstant` 木翼 **0.8** / 金属翼 **1.5** / 木尾 **0.2** / 金属尾 **1.0**；`dragConstant` 木翼 0.8 / 金属翼 0.4，**`Wings.FixedUpdate` 从不读它**（死字段）；`m_flipped: 0`、`m_eightWay: 0` | prefab 字段（`tools/bple-aero` 硬断言） |
| 12 | 每件刚体阻尼已按 §4.G 落地：机翼/尾翼 `drag 1 / angularDrag 0.2`（`Wings.EnsureRigidbody`/`Tail.EnsureRigidbody`） | `Wings.cs:91-102`、`Tail.cs:44-55` |

原版没做、本规格也不做的部分见 §7。

## 3. 内容模型

```json
"capabilities": {
  "mirror": true,                    // = prefab m_autoAlign == 2（FlipVertically），仅 17 件
  "wing": { "liftConstant": 0.8 },   // 取代旧的 { "liftCoef": …, "maxLift": … }
  "tail": 0.2                        // 取代旧的 0.03（值换成原版的 liftConstant）
}
```

`dragConstant` **不进内容**：原版 `Wings.FixedUpdate` 不读它，写进去只会变成第二处真值来源。
一律由 `tools/bple-aero/` 从 prefab 写出（`extract` 报告 + `apply` 幂等），**不许手写**。

## 4. PigForge 设计

### 4.1 镜像是一个独立的姿态位

建造姿态 = `(yaw, mirrored)`：`mirrored` 不参与 yaw，姿态四元数 = `Rz(yaw) ∘ Ry(180)`。
`ConstructionRules` 新增 `_mirroredEntities` 集合与 `IsMirrored(entity)`；它**不随 `FreezeAll`（Start 时把建造位姿换成刚体位姿）丢失**，
所以镜像必须自己存一份，不能从冻结后的四元数反推。

- 占格/连接几何（`PartFootprint`）：镜像 = 形状 `offset` 与 `gridBox` 中心 x 取反，再套 yaw。
- 物理几何（`CompoundAssembler`）：不需要特判——`transform.Rotation`（含 `Ry(180)`）本来就会把形状 `offset` 转过去，
  且镜像后仍是**正常旋转**（行列式 +1），不会产生负缩放刚体。
- 规则层：镜像在装配时随成员位姿发布（`LinkBody(..., mirrored)`），气动按 §4.3 使用。

### 4.2 线上

| 方向 | 改动 |
|---|---|
| PGFC **v3** | `PlacePart`（kind 0）与 `RotatePart`（kind 2）payload 末尾各加 `mirrored:u8`（0/1）。角度仍是**绝对值**，镜像也是**绝对值**，与既有语义一致。 |
| PGFS **v6** | 实体 `flags` 的 **bit2 = mirrored**（bit0 = 开关、bit1 = 运行期子实体，`ADR-028`）。bit3–7 保留、写 0。 |
| 拒绝 | 内容未声明 `capabilities.mirror` 的件带镜像 → `ConstructionError.PartNotMirrorable`（新枚举尾项，PGFA 的 `constructionError` 字节）。 |

`SnapshotFrame.CurrentVersion` 与客户端 `SNAPSHOT_VERSION` 同批改；`CommandFrame.Version` 与 `COMMAND_VERSION` 同批改（旧客户端整帧/整命令拒绝是有意的）。

### 4.3 气动

`GameplayRules.RunWings`/`RunTails` 换成原版公式（§2 表 8/9/10），唯一的单位换算是既有的
`ForceMode.Force → /60`（`ADR-013` 决策 4，与风扇、气球同一分母）。曲线表是**源码常量**（代码里带 `file:line`），
`liftConstant` 来自内容。`WindVelocity` 恒为零（PigForge 没有风区，记为偏差）。
施力点是刚体质心（原版 `AddForce` 不传位置 = 质心），取轴用零件自己的参考系（`ResolvePartFrame`，`ADR-029`）。

### 4.4 客户端

- 建造：`F` 键翻转选中件（等价于原版点击 `Flip` 的 `FlipVertically` 分支）；待放置姿态也带镜像（`F` 切换）。
  仅对内容声明 `mirror` 的件可用（按钮/键位对其它件不生效）。
- 渲染：镜像实体的精灵在**零件参考系**里 `ctx.scale(-1, 1)`（= 水平镜像），件内精灵的 z 取反；
  唯一例外是 `WingSprite`（`z` 保持，见 §2 表 7），件内绘制顺序随之重算。
- 解码：`decodeSnapshot` 读 `flags` bit2，`DrawEntity.mirrored`。

## 5. 验收

### 5.1 代数验证（`R_flipped = R_grid ∘ Ry(180)`）

`Tail.SetRotation(int)`（§2 表 3）在 4 种 `num2` 下的矩阵，与 `Rz(90 × num2) · Ry(180)` 逐元素比较：

| `num2` | 原版 `Euler(num3, num4, num5)` | `Rz(90·num2)·Ry(180)` |
|---|---|---|
| 0 | `Ry(180)` = `diag(−1, 1, −1)` | `diag(−1, 1, −1)` ✓ |
| 1 | `Rx(180)·Rz(90)` = `[[0,−1,0],[−1,0,0],[0,0,−1]]` | 同 ✓ |
| 2 | `Ry(180)·Rz(180)` = `diag(1,−1,−1)` | 同 ✓ |
| 3 | `Rx(180)·Rz(270)` = `[[0,1,0],[1,0,0],[0,0,−1]]` | 同 ✓ |

（Unity 的 `Quaternion.Euler(x,y,z)` 按 Z→X→Y 施加，上表即按此顺序相乘验证。）

### 5.2 必做断言与实测

- **协议**：PGFS v6 实体往返（含 bit2）；v5 帧被拒；PGFC v3 的 `PlacePart`/`RotatePart` 往返含 `mirrored`；v2 命令被拒。
- **Core**：`IsMirrored` 在 `Place`/`Rotate` 后为真、`FreezeAll` 后仍为真、移除后消失；
  镜像件的 `PartFootprint` 碰撞几何与未镜像件**互为 x 镜像**（翼的 `offset x −0.5 ↔ +0.5`）；
  未声明 `mirror` 的件带镜像 → `PartNotMirrorable`。
- **规则**：同一机翼/尾翼在 `vx` 相同时，镜像与不镜像的升力**方向相反**（`GameplayRulesTests.AMirroredWingAndTailPushTheOtherWay`：
  速度 `(10,5,0)` 下不镜像推向下、镜像推向上）；响应曲线的节点/插值/两端夹住另有
  `TheAerodynamicResponseCurvesAreTheOriginalsTables`，100 N 夹紧与零速无出力有
  `AWingLiftsAlongItsOwnFrameAndIsClampedAtOneHundredNewtons`，尾翼的自带 trim 有
  `ATailIsTrimmedByItsOwnTwistTerms`。
- **客户端**：`decodeSnapshot` bit2、`encodeCommand` 字节数（v3 20+1 / 8+1）、draw 的镜像画笔（水平翻转的精灵 x）、件内顺序规则。
- **实机**：真服务器 + 真内容 + 真 Bepu + 浏览器 WS，摆一架「框 + 引擎 + 机翼（不镜像/镜像）」两轮读数，验证镜像真的把升力/姿态翻过去（读数记在 §5.3）。

### 5.3 实机读数

真服务器（`--play`，Release）+ 真内容 + 浏览器可用的客户端编解码器（Bun 直连 `ws://127.0.0.1:5088/play`，
探针跑完已删）：同一连接上放 `1` 木框、两件 `31` 木翼（同一 `angle 0.4`，一件镜像、一件不镜像），
读建造/预览帧里的实体：

| 件 | `flags` | `rotation`（x,y,z,w） | 判定 |
|---|---|---|---|
| `31` 镜像 | **4**（bit2） | **(−0.19867, 0.98007, 0, 0)** | = `Rz(0.4) ∘ Ry(180)`：`x = −sin(0.2)`、`y = cos(0.2)`、`z = w = 0` ✓ |
| `31` 不镜像 | 0 | (0, 0, 0.19867, 0.98007) | = `Rz(0.4)` ✓ |
| `1` 木框 | 0 | (0, 0, 0, 1) | 未声明 `capabilities.mirror`，`flags` 干净 ✓ |
| 同一件再发 `RotatePart(angle 0.4, mirrored: false)` | **0** | (0, 0, 0.19867, 0.98007) | 绝对值语义：镜像被**清掉**、yaw 不变 ✓ |

帧版本 `frameVersion = 6` ✓。`PlacePart(mirrored: true)` 打到未声明镜像的件上由房间层拒绝
（`ConstructionError.PartNotMirrorable`，`PartMirrorRoomTests` 覆盖）。

原版侧的量（8 态姿态与两条曲线的实测力）由 `unity/PigForge.WeldProbe` 的 `MirrorAeroProbe`
在**原版编辑器 2021.3.45f2** 上跑出（`replays/mirror-aero-probe.json`，抄进 `tasks/mirror-aero-probe.json`）。

## 6. 已知偏差

1. **自由 yaw 仍在**：原版 vanilla 下 `FlipVertically` 件只能镜像（yaw 由 `AutoAlignType.Rotate` 路径决定），
   8 态循环要 IN `RotatableWing = true`（B 档）。PigForge 保留既有的自由旋转 UI（`Q`/`E`/`R` + 拖拽），
   镜像在其上再开一个自由度。这与既有偏差 `G96`（建造自动对齐）同源，不新增记录。
2. **不做原版 `Flip` 的「连接方向合法性重试」**：原版翻 3 次里只要有一次连接方向合法就停；
   PigForge 的焊接是邻近几何判定（`ADR-017/018`），没有「方向合法性」这个概念，所以直接翻转。
3. **`WindVelocity` 恒为零**（无风区）。
4. **`dragConstant` 不落地**（原版死字段，见 §3）。
5. 响应曲线在代码里而不是内容里：它是**类的常量**，不是 prefab 字段，任何提取器都读不到（与 `MotorWheelMaximumSpeed = 15`、
   `SeamBreakImpulse` 同类）。
6. **件内绘制顺序不因镜像重排**：原版绕 Y 的 180° 把每个精灵的 z 取反（`Wings.OnFlipped` 又把 `WingSprite` 的 z 补回来）。
   贴图清单没有 z 字段，而这 17 件里会互相遮挡的只有机翼/尾翼的两态支架——同一时刻只有一个可见（`condition`）——
   所以镜像后的相对次序没有可观测差别；渲染器只做 x 镜像与旋转反向（`renderer/draw.ts`）。

## 7. 不做

- 其余 326 个 prefab 的镜像：原版也只对 `FlipVertically` 的 17 件开放（§2 表 5），照抄。
- `RotatableWing`/`RotatableTail`（B 档）的 8 态**行为差异**：那是 mod 档位，与 G105 一起处理。
