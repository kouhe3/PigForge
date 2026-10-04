# 刚体默认值：每件阻尼与全场角速度上限

> 状态：**已交付**（2026-10-04，G86 + G88）。原版真值全部来自**原版自己的编辑器**（Unity 2021.3.45f2，
> 真 PhysX 4.1）实测 + 反编译源逐行核对；提取器 `tools/bple-damping`，内容键写在 `content/parts.json`。

## 0. 结论

原版 *Bad Piggies* 的每个零件刚体都吃两条**它自己从不覆盖的 Unity/PhysX 默认值**：

1. **每件阻尼**：`Rigidbody.drag = 0.2` / `angularDrag = 0.05`（`BasePart.EnsureRigidbody`），少数类覆盖
   （机翼/尾翼 `1/0.2`、气球 `2/0.5`、沙袋 `1/10`、猪王 `0.5/1`）。
2. **全场角速度上限**：`Physics.defaultMaxAngularSpeed = 7` rad/s（原版 `ProjectSettings/DynamicsManager.asset`
   的 `m_DefaultMaxAngularSpeed`），PhysX 按**模长**截断。没有任何脚本写 `maxAngularVelocity`，343 个
   `Part_*.prefab` 里 0 个序列化 `m_MaxAngularVelocity`。

两条都是「让载体有终端速度」的机制：没有它们，动力轮、螺旋桨、火箭、翻滚的框全都无限加速/无限自旋。
PigForge 此前两条都没有（`IPhysicsWorld` 契约里连字段都没有），所以实测整车能到 17.775 m/s 且仍在加速。
本轮把两条一起补上（用户 2026-10-04 拍板：同一族、同一处改动，一趟验收）。

## 1. 真值与出处

| 类 | linear (`drag`) | angular (`angularDrag`) | 出处 |
|---|---|---|---|
| `BasePart`（默认，205 件里的 202 动态件） | **0.2** | **0.05** | `BasePart.cs:1192-1205` |
| `Wings`（9 件） | 1.0 | 0.2 | `Wings.cs:91-102` |
| `Tail`（8 件） | 1.0 | 0.2 | `Tail.cs:44-55` |
| `Balloon`（24 件） | 2.0 | 0.5 | `Balloon.cs:83,129-132` |
| `Sandbag`（14 件） | 1.0 | 10.0 | `Sandbag.cs:61,132-135` |
| `KingPig`（7 件） | 0.5 | 1.0 | `KingPig.cs:79-82` |
| `GoldenPig` | 0.5 | 1.0 | `GoldenPig.cs:14-18`（**我们内容里 0 件**） |
| `Pig` | 0.2 | 0.05（与默认同） | `Pig.cs:236-237` |

类的判定方式：prefab 的 `m_Script` guid → `.cs.meta` → 类名，再在反编译源里解析**继承链**上的赋值
（`EnsureRigidbody` 链 → `Initialize` 链，`INContraption.cs:813-816` 的生成顺序）。任何在别的**生成期**方法里
写阻尼的类都会让提取器**报错**；已知的**运行期**覆盖被显式列在报告的 `runtimeOverrides` 里：

| 运行期覆盖（本轮**不做**，见 §6） | 出处 |
|---|---|
| `FanPropeller.FixedUpdate`：旋翼开时 `angularDrag = 1000`、关时 `1` | `FanPropeller.cs:145-154` |
| `Rope.FixedUpdate`：每节按拉伸重算 `drag` | `Rope.cs:365-366` |
| `Pig.FixedUpdate`：`\|v\| < 1` 时 `drag = angularDrag = 0.2 + 2.5(1-\|v\|)` | `Pig.cs:249-262` |
| `INContraption.FixedUpdateSelf`：`NoDrag` 开关把全场阻尼清零 | `INContraption.cs:329-341` |
| `HingePlate.EnsurePlateRigidbody`：IN 扩展件的两块板体 | `HingePlate.cs:243-262` |

**角速度上限**：`m_DefaultMaxAngularSpeed: 7`（`ProjectSettings/DynamicsManager.asset` 末行）。
Unity 2021.3 文档 `<Rigidbody.maxAngularVelocity>`：*"The maximum angular velocity of the rigidbody measured in
radians per second. (Default 7) ... The angular velocity of rigidbodies is clamped to maxAngularVelocity to avoid
numerical instability with fast rotating bodies. Because this may prevent intentional fast rotations on objects
such as wheels, you can override this value per rigidbody."* 原版没有任何 `maxAngularVelocity` 赋值
（`grep -rn maxAngularVelocity Assets/Scripts` = 0 命中），也没有 prefab 序列化它，所以**每件**都是 7 rad/s。

## 2. 公式与顺序（源 + 实测双重确认）

PhysX 4.1 的非约束速度积分（`physx/source/lowleveldynamics/src/DyBodyCoreIntegrator.h::bodyCoreComputeUnconstrainedVelocity`）：

```cpp
linearVelocity += gravity * dt;                                  // ① 先加重力
linearVelocity  *= fsel(1 - linearDamping*dt, 1 - linearDamping*dt, 0);   // ② 再乘 max(0, 1 - c*dt)
angularVelocity *= fsel(1 - angularDamping*dt, 1 - angularDamping*dt, 0);
if (angVelSq  > maxAngularVelocitySq) angularVelocity *= PxSqrt(maxAngularVelocitySq / angVelSq);  // ③ 模长截断
if (linVelSq  > maxLinearVelocitySq)  linearVelocity  *= PxSqrt(maxLinearVelocitySq  / linVelSq);
```

**不是** `1/(1 + c·dt)`，**也不是**指数型 `exp(-c·dt)`——是显式欧拉 `max(0, 1 - c·dt)`（下面 §3 的实测把
三者区分开，前两者都被否掉）。Jolt 的同类代码逐字同式（`MotionProperties.inl::ApplyForceTorqueAndDragInternal`：
先积分力/重力，再 `*= max(0, 1 - damping*dt)`，最后 `ClampLinearVelocity()`/`ClampAngularVelocity()` 按模长截断）。

离散不动点（原版 50 Hz，dt = 0.02）：`(g/c)(1 - c·dt) = 49.05 × 0.996 = 48.8538 m/s`；
PigForge 60 Hz（dt = 1/60）：`49.05 × 0.996667 = 48.8865 m/s`。

## 3. 原版编辑器实测（唯一证据源）

探针：`unity/PigForge.WeldProbe`（钉 **Unity 2021.3.45f2** = `BPLE 2022.1.9/ProjectSettings` 钉的版本，真 PhysX 4.1；
运行期从 `BPLE 2022.1.9/ProjectSettings/{DynamicsManager,TimeManager}.asset` 读设置），入口
`PigForge.WeldProbe.Probe.BodyDefaultsProbe.Run`，输出 `unity/PigForge.WeldProbe/replays/body-defaults-probe.json`
（**两次运行逐字节一致**；整个编辑器日志只有 LicensingClient 的 pid 行不同）。

| 量 | 实测 | 对照/判定 |
|---|---|---|
| 角速度上限 | 种子 `ω = 100` → **第一步就是 7**；每步 `+10 N·m` 的扭矩把它推到 **7 并停住** | 上限调到 1000 → 同种子读 **100**、同扭矩读 **119.9999**（证明非空）；上限 0 → **0**（Unity/PhysX 的 0 是「角速度清零」，不是「不限」） |
| 线性阻尼每步系数 | **0.996**（50 步全部，比值 0.9959999–0.9960001 的浮点噪声） | `1 - c·dt` 预测**每步误差 0**（51 个采样最大绝对误差 **0**，v0 10 → v50 8.184021）；指数型最大误差 0.00328；`1/(1+c·dt)` 最大误差 0.00657 |
| 角阻尼每步系数 | **0.999**（50 步全部） | 同判定；`ω` 全程 ≤ 5 rad/s，7 rad/s 的 clamp 没干扰（交叉轴恒 0） |
| 自由落体 | 5 s **30.9174 m/s**、50 s **48.85183 m/s** | 显式欧拉预测 48.85151（2501 个采样最大误差 0.00033），指数型 48.9496，`1/(1+c·dt)` 49.04809；**重力在阻尼之前**（反序预测误差 0.1959） |

## 4. 落地

### 4.1 物理契约

`BodyDefinition` 新增三个字段（`PigForge.Physics.Abstractions/PhysicsContracts.cs`）：

- `LinearDamping` / `AngularDamping`：有限、非负；`0` = 无阻尼。
- `MaximumAngularSpeed`：有限、非负；**`0` = 不限**（PigForge 自己的约定；注意 Unity 的
  `maxAngularVelocity = 0` 是把角速度清零，Jolt 自己也是——所以 Jolt 后端要显式翻译成哨兵值，见 §4.3）。

### 4.2 内容（`content/parts.json`）

顶层 `physics` 块**必填**，声明原版的工程默认（`tools/bple-damping` 写）：

```json
"physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } }
```

件级 `damping` 是**覆盖**，只在原版类覆盖了 `BasePart` 的那个值时写——264 个动态件里 **62 个**（9 翼 + 8 尾 +
24 气球 + 14 沙袋 + 7 猪王）。静态件**不许**写（原版的静态关卡件根本不是刚体；解析器硬拒）。
解析器（`PartContentParser`）与客户端校验器（`clients/web/src/schema/validateContent.ts`）同规则；schema 在
`schemas/part-content-v1.schema.json`。

### 4.3 两个后端

- **Bepu 2.4.0**：`BodyDescription` 只有 `Pose/Velocity/LocalInertia/Collidable/Activity`——**没有阻尼，也没有角速度上限**
  （元数据反射验证）。所以按已有 `_constraintsByHandle` 的先例加一张 `BodyMotionSettings[] _motionByHandle` 表，
  在 `PoseIntegratorCallbacks.IntegrateVelocity` 的**逐 lane 循环**里施加（顺序：重力 → 阻尼 → 模长截断 → 冻结自由度）。
  表越界（静态件/已释放槽）返回全 0 → 不写任何东西，与既有 `ConstraintsOf` 的容忍一致。
- **Jolt（JoltPhysicsSharp 2.22）**：原生支持，三个字段直填——`LinearDamping`、`AngularDamping`、
  `MaxAngularVelocity`。Jolt 自己的默认值**不是** PigForge 的（阻尼 0.05、上限 `0.25π·60 ≈ 47.12` rad/s，
  `BodyCreationSettings.h`），所以三个都显式写。`MaximumAngularSpeed = 0` 翻译成 `1e18f` 哨兵
  （Jolt 的 `ClampAngularVelocity` 用 `max/|ω|` 缩放，0 会冻住旋转；`1e18² = 1e36` 仍有限且远高于任何真实自旋）。

### 4.4 装配（一个 body 只能有一个阻尼）

`CompoundCluster.Damping` = 成员按**质量加权平均**（`sum(m·d)/sum(m)`），与 body 的 `Mass`（成员质量之和）同一套权重；
`CompoundMember` 携带自己的 `Damping`（来自内容解析，`PartContentLibrary.DampingOf`），所以拆簇重建（`Rebuild`）与合并
（`BuildCluster`）都用同一个 `FoldDamping`。**单个成员逐位保留自己的值**（轮子/气球/沙袋都是单成员簇）。
**宿主挂件不贡献**（`CompoundAttachment` 不是成员，与它不贡献质量一致）：铰链轮的支撑盒挂在父 body 上，父 body
只吃父零件自己的阻尼。`Rigidbody` 级「原版每件一个 body」在 PigForge 的复合体里无法逐件表达，这是最诚实的折叠，
和弹性（取最强）、摩擦（取最高优先级合成模式）是同一种处理。

## 5. 验收

- **物理层（Bepu）** `tests/PigForge.Physics.Tests/BodyDampingTests.cs`：每步系数 `1 - c·dt` 逐步相等（60 步）；
  一秒后 8.184958 m/s；角阻尼 60 步后 `5·(1-0.05/60)^60`；自由落体 3000 步落到 `(g/c)(1-c/60) = 48.8865 ± 0.05`；
  种子 100 rad/s 在**一步**后**恰好 7**、上限 1000 时保持 100、种子 3 不受影响；`0` 阻尼/`0` 上限不扰动刚体。
- **物理层（Jolt）** `JoltBodyDampingTests.cs`：同四条（阻尼系数、终端速度、7 截断、`0` = 不限）。
  两个 Jolt 测试类放进同一个 `[Collection("Jolt")]`：**两个 Jolt 类并行跑会把测试宿主崩在原生求解器里**
  （仓库已知的 flaky；单类跑一直正常，实测确认根因是并发 world）。
- **核心层** `tests/PigForge.Core.Tests/BodyDampingTests.cs`：文档默认 + 覆盖 + 静态件 0 的解析；出厂内容的
  7 / 0.2 / 0.05 与五个覆盖族逐值断言；质量加权折叠（1 kg @1.0 + 3 kg @0.2 → 0.4）；单成员逐位保留；
  铰链轮的挂件阻尼不泄漏到父 body；解析器拒「静态件带 damping」「缺 `physics`」「damping 未知键」。
- **房间层**：`BalloonLiftTests` 改成**阻尼上升的递推**（`v ← (v + a·dt)(1 - c·dt)`，每 tick 与实测速度
  差 < 0.05 m/s）；`FanThrustTests` 安定窗口 120 → 240 tick（阻尼让落地安静下来更慢），30 tick 转角速度
  **3.84 rad/s**（无阻尼 4.63）；`PigBounceTests` 回弹 **1.415 m**（无阻尼 1.766，系数 0.484 → 0.387）。
- **实机（真服务器 + 真内容 + 真 Bepu + 真 PGFS）**：见 §5.1。

### 5.1 实机探针

| 量 | 值 |
|---|---|
| 木框 + 包裹引擎 + 双马达轮的车架峰值 \|vx\| | 见 `tasks/HANDOFF.md` §4.G（本轮实机读数） |
| 轮子 ω | 线格式不含角速度 → 由物理层测试断言 7 rad/s 截断（§5） |

## 6. 已知偏差 / 本轮不做

1. **60 Hz vs 原版 50 Hz**：离散不动点因此是 48.8865 而不是 48.8538（差 0.07%）。
2. **Unity 自己的文档说 `Physics.defaultMaxAngularSpeed` 默认 50**——实测与工程资产都是 **7**，文档过时；
   以 `DynamicsManager.asset` 为准。
3. **`0` 上限的语义差异**：PigForge 契约里 `0` = 不限；Unity/Jolt 的 `0` = 角速度清零。内容永远写正值（7），
   所以差异只在测试夹具可达。
4. **运行期覆盖未做**（§1 表）：`Pig.FixedUpdate` 的慢速增阻（`|v| < 1` 时最高 2.7）、旋翼的
   `angularDrag 1000/1`、绳的逐节阻尼、`NoDrag` 设置开关、IN 铰链板的板体。旋翼那条属于
   `docs/specs/fan-propeller.md` §7 的四条链（推力轴 + 角阻尼 + `m_rotorTargetDirection` + 左向射线增益），
   要一起做。
5. **`m_BounceThreshold: 2`（原版）vs 我们的 `MinimumBounceApproachSpeed = 0.5`**（`GameplayRules.cs:175`）：
   原版在相对速度 < 2 m/s 时**忽略弹性**，我们的合成弹性阈值更低 → 低速接触我们会弹、原版不弹。G89。
6. **`m_DefaultSolverIterations = 6` / 速度迭代 1 / `m_EnableAdaptiveForce: 0` / `m_ContactPairsMode: 0` /
   `m_FrictionType: 0`**：还没逐条核（焊点链的拟合值里其实已隐含了迭代残差的等效行为）。
7. **Bepu 没有 `MaxLinearVelocity`**：PhysX 的 `maxLinearVelocitySq` 与原版无关（Unity 的 `Rigidbody` 不暴露它，
   原版也没设），故不实现。

## 7. 复现

```bash
node tools/bple-damping/extract-damping.mjs                                  # 报告 → tasks/bple-damping-report.{json,md}
node tools/bple-damping/apply-damping.mjs                                    # 写内容（第二次必须 0 改动）
node tools/bple-damping/apply-damping.mjs --dry-run                          # 只报会改几行
# 原版编辑器实测（钉 2021.3.45f2，headless）
unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 --timeout 1200 \
  -- -executeMethod PigForge.WeldProbe.Probe.BodyDefaultsProbe.Run -logFile -
# 分层测试
for p in Core Server Protocol Replay; do dotnet test tests/PigForge.$p.Tests/PigForge.$p.Tests.csproj; done
dotnet test tests/PigForge.Physics.Tests/PigForge.Physics.Tests.csproj --filter "FullyQualifiedName!~Jolt"
dotnet test tests/PigForge.Physics.Tests/PigForge.Physics.Tests.csproj --filter "FullyQualifiedName~Jolt"
```
