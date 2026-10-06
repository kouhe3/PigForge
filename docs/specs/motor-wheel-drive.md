# 规格：动力轮的出力方向与门控轴（G102）

状态：已实施（2026-10-06 第二十一轮）。
来源：原版反编译脚本 `BPLE 2022.1.9/Assets/Scripts/Assembly-CSharp/MotorWheel.cs`（编辑器 2021.3.45f2 的原版侧探针 `unity/PigForge.WeldProbe` 的 `MotorWheelProbe`）。
相关：`docs/specs/power-system.md`（功率因子与门控）、`ADR-029`（内容方向 = 零件自己的参考系）、`ADR-031`（接触法线 = 表面几何）。

## 1. 偏差

PigForge 的 `GameplayRules.RunMotors` 把动力轮的出力写成**世界 +X**、门控读**世界 X 速度**、施力点在**刚体中心**。原版三处都不是世界轴：

| 项 | 原版 | 出处 |
|---|---|---|
| 出力方向 | `Vector3.Cross(hitInfo.normal, Vector3.forward)` —— **脚下的地面切线**（`Vector3.forward` 是世界 +z，2.5D 下即把命中面法线在平面内转 −90°） | `MotorWheel.cs:288` |
| 门控轴 | `SpeedInDirection(base.transform.right)` —— **轮子自己的右轴**（建造角 × 车架实时姿态） | `MotorWheel.cs:287` |
| 施力点 | `hitInfo.point` —— 射线命中的地面点 | `MotorWheel.cs:298` |
| 地面判定 | 从 `m_wheelPivot`（轮胎中心）沿 `m_lastContactDirection` 射线 `m_radius + 0.1`，**打空即不出力** | `MotorWheel.cs:285-286` |
| 出力强度 | `m_thrust × m_maximumForce × sqrt(1 - \|v∥\|/max)`，`m_thrust = pow(min(m_thrustTimer, 1), 0.4)`、`m_thrustTimer` 每 FixedUpdate 加 `dt`（**1 秒斜坡**） | `MotorWheel.cs:266-271,292-299` |

`m_maximumForce = m_force × factor`、`m_maximumSpeed = 15 × factor` 见 `power-system.md` 真值 8。

## 2. 原版侧实测（`MotorWheelProbe`，Unity 2021.3.45f2 + 原版 ProjectSettings）

夹具：`Part_MotorWheel_01_SET` 的真实几何（`m_mass 1`、`m_force 50`、SphereCollider `r 0.45` 中心 `(0,-0.075,0)`、SupportCollider 盒 `0.6×0.2` 偏 `(-0.05,0.4)`、WheelPivot `(0,-0.075,0)`）+ 上方一个 1 kg 车体方块；`enginePowerFactor` 固定在 1（`m_maximumForce 50`、`m_maximumSpeed 15`）；每次 `Physics.Simulate(0.02f)` 前跑一次 `MotorWheel.FixedUpdate` 的转写。**探针自检**：转写的力与独立写出的期望（手推的 `cross(n,z) = (n.y, -n.x, 0)`）逐 tick 分歧 ≤ **8e-06 N**。

| 格 | 地面法线 | 切线（首 tick） | 100 tick 后位移 | 读数 |
|---|---|---|---|---|
| `flat_weld_contact` | (0,1,0) | (1, 0) | (11.330, 0) | 门控读 10.486（= 世界 X）、末力 27.43 N = `50 × sqrt(1-10.486/15)` ✓ |
| `slope15_weld_contact` | (−0.2588, 0.9659) | (0.9659, 0.2588) | (9.033, 2.420) | 沿坡上行 |
| `slope30_weld_contact` | (−0.5, 0.866) | (0.866, 0.5) | (7.234, 4.177) | 沿坡上行 |
| `slope45_weld_contact` | (−0.7071, 0.7071) | (0.7071, 0.7071) | (5.924, 5.924) | 沿坡上行 |
| `slope-30_weld_contact` | (0.5, 0.866) | (0.866, −0.5) | (15.951, −9.209) | 沿坡下行 |
| `flat_weld_wheel90_contact` | (0,1,0) | (1, 0) | (17.222, 0) | **轮子转 90°**：门控读 **0.000007**（自己的右轴 = 世界 +Y）而世界 X 读 **19.99** → 永不衰减，末力 **50.0 N**（= 满力） |

`m_thrust` 斜坡（`thrust` 序列，第 1 帧起）：`0.209128`（= `0.02^0.4`）、第 25 帧 `0.757858`（= `0.5^0.4`）、第 50 帧 `1.0`。

**施力点**：`flat_weld_contact`（接触点，冻结俯仰）行程 11.33 m、轮体角速度 **0**；`flat_weld_centre`（刚体中心，保留原版 2.5D 约束）行程 8.569 m、轮体角速度峰值 **7.909 rad/s**；换成 PigForge 的铰接拓扑（`flat_hinge_contact` / `flat_hinge_centre`）：接触点施力 → 行程 (0.414, 1.044)、车体 0.678 m/s（**轮子把自己往车上爬**），刚体中心施力 → 行程 (6.300, 0.070)、车体 8.520 m/s。

## 3. 实施

1. **出力方向 = 地面切线**（`GameplayRules.RunMotors`）：`tangent = (normal.Y, -normal.X, 0)`，即 `Cross(normal, Vector3.forward)` 的手推等价式；法线来自 `_groundNormalByBody`（`ProcessEvents` 里按 `+ContactNormal` 分离 `BodyA`、`-` 分离 `BodyB` 定向，取该 body **Y 最大**的一个）。没有法线 = 原版射线打空 = **完全不出力**。`directionX`（内容，目前恒 1）继续作为正负号乘子，倒挡仍由变速箱翻。
2. **门控轴 = 轮子自己的右轴**：`ResolveWheelRight` → `轮子非自转的参考系 × (1,0,0)`。铰接轮的自转参考系由 `LinkWheelAttach`（放置层在 `BindWheelHinges` 里连同 `_attachByEntity` 一起声明：`父件实体 + 父件装配姿态⁻¹ × 轮子装配姿态`）给出，因为**轮子自己的刚体在绕轴自转**，"yaw 与自转都是绕 z" 之后无法从位姿反推（见 `ADR-009`）。没有声明的轮子退回零件自己的参考系（`ResolvePartFrame`）。
3. **接触法线改成表面几何法线**（`ADR-031`，取代 `ADR-010` 决策 3）：Bepu 记录 manifold 原样（只做 key 序交换），Jolt 记录 `WorldSpaceNormal`（朝向按实测取反）。**这是本片的前置条件**——按旧语义（用相对速度定符号）量出来的"法线"在静止接触上逐 tick 翻号。
4. **施力点仍是刚体中心**（有意偏差，见 §5）：原版的 `hitInfo.point` 只在**焊接轮**（原版拓扑）上与"刚体中心"可换；PigForge 的轮子是铰接的（`ADR-008/009`），把冲量打在接触点会让轮胎自转吃掉推力（探针 `flat_hinge_contact` 的读数）。

## 4. 验收

**原版侧**：`unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 -- -executeMethod PigForge.WeldProbe.Probe.MotorWheelProbe.Run -logFile -`，输出 `replays/motor-wheel-probe.json`（抄进 `tasks/motor-wheel-probe.json`）。

**单元/房间级**（全绿）：

| 用例 | 断言 |
|---|---|
| `GameplayRulesTests.AMotorDrivesAlongTheTangentOfTheGroundItStandsOn` | 30° 坡面法线 → 冲量 `(2·cos30, 1, 0)`（改回世界 X 即红） |
| `GameplayRulesTests.AMotorGatesOnTheWheelsOwnRightAxis` | 轮子建了个 90°：世界 X 已 16 m/s（超过 15 上限）仍给满冲量 |
| `GameplayRulesTests.MotorDrivesWheelOnlyWhileWheelTouchesSomething` | 无法线 = 不出力；平地的 tangent 仍是 +X |
| `MotorWheelRoomTests.ADrivenCartClimbsTheSandboxRamp` | 真内容 + 真 Bepu + terrain-v1 斜坡：120→180 tick 窗口 `dx 10.4 / dy 2.65`，`dy/dx 0.255` 与坡面 `tan(0.25) = 0.2553` 相符，整体净爬升 > 1 m |
| `PowerSystemRoomTests.AMotorCartNeverExceedsItsTopSpeed` | 真车（两动力轮 + 包裹引擎）峰值必须**超过**接地上限 12.089（= 离地折抵生效）——**这条同时是门控轴接线的守卫**：`LinkWheelAttach` 缺失时轮子自己的自转会污染门控，峰值掉到 **5.99 m/s**（红） |

**实机**（真服务器 `--play` Release + 真内容 + 真 Bepu，Bun 直连 `ws://127.0.0.1:5088/play`，用客户端自己的编解码器；同一 fixture 每轮重启服务）：

| 场景 | 读数 |
|---|---|
| 平地（木框 + 包裹引擎 + **双**马达轮，x = −8 起） | 峰值 \|vx\| **15.03 m/s**（`15 × (150/110)^0.585 = 17.98` 上限之下，比第六轮同 fixture 的 **10.86 m/s** 快——旧值是世界轴门控 + 世界轴出力），位移 9.40 m 后撞上 terrain-v1 的第一个台阶停住 |
| 斜坡（同一辆车摆在斜坡中段） | dx **21.28 m** / dy **4.70 m**（比值 0.22），峰值 \|vx\| **15.44**，末速度 `(14.90, 3.14)` —— 沿坡上行 |

## 5. 已知偏差 / 不做

1. **施力点 = 刚体中心，不是接触点**：原版打 `hitInfo.point`。探针给了理由：原版轮子是**焊接**在车架上的（一对相邻件一条关节，只有 `OffRoadWheel` 重写 `CustomConnectToPart`），冲量打在接触点上时转矩进整车；PigForge 的轮子是**铰接**的（`ADR-008/009`），同一个冲量打在接触点会让轮胎自转（探针 `flat_hinge_contact`：行程只剩 0.414 m、车体 0.678 m/s，轮子甚至"爬"到自己车上）。打刚体中心是 PigForge 拓扑下等价的线性驱动，转矩差异记在这里。
2. **没有 1 秒的 `m_thrust` 斜坡**（`MotorWheel.cs:266-271`）：原版点火后 `m_thrust = pow(timer, 0.4)` 到 1（第 1 帧只有满力的 20.9%），PigForge 一上来就是满力。**这是新记的差距 `G110`**，本片不动（它只影响起步那 1 秒，与取轴无关）。
3. **地面判定用接触事件而不是射线**：原版从轮胎中心沿上次接触方向射线 `r + 0.1`（还能在"刚好离开地面一点"时命中）。PigForge 用该 body 本 tick 的接触法线，因此**完全没有接触的那一 tick 不出力**（空中/微分离时与原版差一两个 tick）。同一个代理也被 `power-system.md` 的离地折抵用着。
4. **多面同时接触取"最朝上"的那个法线**（原版只有它射线指的那一面），见 `ADR-031` 的精度边界。
5. **左向风扇的贴地射线增益**（`FanPropeller.cs:166-197`，纯倍率，需要给契约加射线查询）仍未做，与 `G90`/`G102` 无关。
