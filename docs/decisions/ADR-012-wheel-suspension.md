# ADR-012: 轮子悬挂 —— 原版唯一一处弹性轮连接

## 状态

Accepted。补充 ADR-008/009（轮子自转模型）与 ADR-011（装配模型）：轮子铰接的**轴向自由度**从「硬轴」改为原版显式声明的线性限位弹簧。

## 日期

2026-10-03

## 背景

用户试玩报告「关节不是弹性的」。父任务澄清该抱怨主要指框↔框的焊接刚度（`m_jointPreprocessing`，见 `tasks/bple-jointstrength-*.json`，另开切片），但轮子的连接确实带着 PigForge 自己的一处硬编码偏差：`BepuPhysicsWorld.CreateJoint` 对 `Revolute` 一律用 `Hinge` + `SpringSettings(30,1)`，注释写的是「the spring settings are stiff so it behaves as a rigid axle」。原版不是这样：`OffRoadWheel.CustomConnectToPart` 明确给轮子的局部 Y 加了线性限位弹簧。

## 原版真值（逐条出处）

1. **谁 override 谁才有弹簧**：`BasePart.CustomConnectToPart`（`BasePart.cs:1206`）返回 `null`，`PartGeneratorManager.CreateJoints` 于是走 `Contraption.AddFixedJoint`（`Contraption.cs:1507-1546`）建一个六轴全 `Locked` 的 ConfigurableJoint 刚性焊接。轮子族里**只有 `OffRoadWheel` override**（`CartWheel`/`MotorWheel`/`StickyWheel` 都直接继承 `BasePart`，没有该方法的覆写）。因此原版其余轮子都是刚性连接，**没有任何弹簧字段可提取**。
2. **弹簧声明**（`OffRoadWheel.cs:202-220`）：角运动三轴 `Locked`，`xMotion = Locked`、**`yMotion = Limited`**、`zMotion = Locked`；`linearLimitSpring.spring = m_springStiffness`（`:213`）、`linearLimitSpring.damper = 5f`（`:214`，常量）、`linearLimit.limit = 0f`（`:215`）、`linearLimit.bounciness = 0f`（`:218`）。`OffRoadWheel.cs:49` 是类级序列化默认值 `public float m_springStiffness = 150f;`。
3. **全量 negative evidence**：扫描原版全部 343 个 `Part_*.prefab`，唯一序列化 `m_springStiffness` 的是 `Part_MotorWheel_08_SET.prefab:160`（值 **50**，即该 prefab 覆盖了类默认 150）；其余 38 个轮子 prefab 既无该字段也不 override。所以轮子悬挂是**逐 prefab** 的值（默认在类上、唯一覆盖在 08），不是「全轮子共用 50」。
4. **轴没有序列化**：ConfigurableJoint 的 `axis`/`secondaryAxis` 用 Unity 默认的 `(1,0,0)`/`(0,1,0)`，所以 `yMotion` 就是轮子自身局部 Y；又因角运动全 `Locked`，该轴在运行期与底盘刚性对齐。
5. **这个零件此前不在 PigForge 目录里**：`BasePart.OffRoadWheel = new PartTypeInfo(PartType.MotorWheel, 7)`（`BasePart.cs:509`，即第 8 个 MotorWheel prefab = `Part_MotorWheel_08_SET`），出厂设置在 `INSettingsBExp.json` 里 `OffRoadWheel: value true`，由 `INPartFactoryManager.cs:71-74` 注册 —— 它是 **IN 扩展件**，不在 `GameData.m_customParts` 里，所以 `tools/bple-variants/import-variants.mjs` 当初没有导入它（同 BlasterTNT 52 的情况）。换句话说：只提取不补零件，这唯一的弹性轮在 PigForge 里没有宿主。
6. **其余关节的「软」另有来源**（只记录，不实现）：`Contraption.AddFixedJoint` 的 `joint.enablePreprocessing = part.JointPreprocessing && other.JointPreprocessing`（`Contraption.cs:1540`），而全量 343 个 prefab 中 `m_jointPreprocessing: 1` 的只有 27 个（11 木框 + 14 铁框 + `Part_ColoredFrame` + `Part_TimeBomb_01_SET`）。也就是说原版只有框族是刻意刚的，其余焊接在物理上都是软的。PigForge 一律刚性焊（ADR-011），本轮不模拟这个逐件开关。

## 决策

1. **内容**：新增 `capabilities.suspension = { stiffness, damper, restOffset }`（N/m、N·s/m、轴上静止偏移）。唯一来源 `tools/bple-springs/extract-springs.mjs`（解析脚本声明 + 每个 prefab 的序列化覆盖，找不到就抛错），`tools/bple-springs/apply-springs.mjs` 报告驱动、幂等、支持 `--dry-run`、重新推导校验。**没有该 key 的轮子保持刚性** —— 这正是原版的负证据，不是缺省值。`apply-springs.mjs` 在「声明了弹簧的 prefab 没有映射到任何零件」时直接失败，避免再次出现「数据提取到了但没有宿主」。
2. **缺失的零件留在目录之外，等一个明确决定**：`Part_MotorWheel_08_SET` 要进目录必须补 `tools/bple-variants/variant-overrides.json` 的 `extras`（同 52 BlasterTNT 的机制），但它的**贴图**由原版的运行时 IN sprite 系统给出：prefab 根 `m_IsActive: 0`（全项目 42 个 IN 扩展件都这样，由 `INPartFactoryManager.SetPart` 在运行时实例化并激活），三张精灵走 `INSerializedSprite` 的**名字**查表（`IngameAtlas4_TextAsset.txt` 里有 `OffRoadWheel_Wheel/Spoke/SupportSprite`），且实测解出的布局与碰撞体不自洽（轮胎图 1.302 单位/轴心 -0.1687，而球碰撞体是 r 0.9/偏移 -0.5，同类轮子如 7/15 的图与碰撞体是 1:1 的）。也就是说它需要先搞清楚 IN sprite 系统的缩放/定位，并按 `docs/specs/part-variant-catalog.md` 的规则 5（贴图解析失败的条目不得入目录）处理；这属于目录/美术切片，不在本任务范围。**因此本 ADR 只落地机制与数据：`tools/bple-springs` 已经把它提取成报告，`apply-springs.mjs` 每次运行都会指出「prefab 有弹簧但没有内容宿主」，一旦该零件入目录，悬挂会自动写入。**
3. **物理契约**：`JointDefinition` 增加 `localSuspensionAxis`（A 体局部、单位向量）与 `suspensionRestOffset`，并复用 `springFrequency`/`springDampingRatio`；括号里注明只在 `Revolute` 上有意义。校验互反：给了轴就必须有正的频率与非负阻尼比，反之亦然；`Fixed` 之类仍不受影响。
4. **Bepu 2.4 实现**：`Hinge` 的线性部分是一个刚性 BallSocket（`Hinge` = `BallSocket` + `AngularHinge`，v2.4.0 源码），表达不了柔度，所以弹性轮用三条约束合成：
   - `AngularHinge`：绕轴自转（原版的角运动 Locked 在这里被 ADR-008/009 的自转模型取代，仍是记录在案的偏差）；
   - `PointOnLineServo`：B 锚点锁在「A 锚点 + 轴向」这条直线上 = 原版的 x/z `Locked`（两自由度刚性）；
   - `LinearAxisServo`：`TargetOffset = restOffset` + 提取出的频率/阻尼比 = 原版 `yMotion = Limited` + 线性限位弹簧。`ServoSettings.Default` 速度与力都不设上限，因此伺服项退化为纯弹簧，冲量可正可负——与 Unity 的**对称**软限位（limit 0 时两侧都拉回）一致。
   轴必须挂在 A（父体、不自转）的局部系里：PigForge 的轮体要自转（ADR-009），轴挂在轮体上就会被自转带着转，约束会把父体甩出去。
5. **单位换算沿用同一个函数**：`GameRoom.TrySpringResponse`（ADR-011 引入的 `omega = sqrt(k/m)`、`zeta = c / (2 sqrt(k m))`，`BindAttachments` 与悬挂共用，不再有第二套口径）。质量取**两个刚体的质量**：轮体的质量（mount 形状之外的部分）与父体整个簇的质量（含托管在其上的 mount）。这样求解器实际施加的刚度等于原版声明的 N/m，静垂度 = 载荷 / 刚度（Unity 的关节同样是与相邻零件刚体之间的力弹簧，与质量无关）。若沿用「双方零件质量」的近似，父体是多件簇时有效 k 会被放大（实测 2 框横梁下 50 → 64 N/m，垂度少 22%）。
6. **客户端不需要改动**：悬挂只是轮体与父体之间的相对位移，快照与渲染管线无需新字段。

## 记录在案的偏差

1. **连接方向的判据**：原版 `Part_MotorWheel_08_SET` 的 `m_customJointConnectionDirection = 2 (Up)`，只有「轮子是 joint 属主」的那一侧才会调用它自己的 `CustomConnectToPart`（`PartGeneratorManager.cs:472-546`）。PigForge 的父体选择本来就是「最低 id 的非轮邻居，否则最低 id 邻居」（ADR-008/009 的既有偏差），悬挂加在这个关节上，不再判方向；典型堆叠（框在上、轮在下）与原版一致。
2. **`m_jointType = 1` 未建模**：该 prefab 标着 HingeJoint，原版在非自定义连接时会建 ±0.1° 的 HingeJoint 而不是刚性 ConfigurableJoint。PigForge 不读 `m_jointType`，本轮不动（另开切片）。
3. **`bounciness = 0` 无对应项**：PigForge 的关节没有弹性项，接触弹性归 ADR-010，行为与原版一致。
4. **自转仍是偏差**：原版轮子角运动全 Locked、靠贴图假转；PigForge 让轮体真自转（ADR-008/009 不变），因此悬挂轴只能挂父体，见决策 4。
5. **其余轮子不弹性**：不给它们发明弹簧值；原版对它们是刚性焊接（本轮只把 PigForge 的「刚性」从 `SpringSettings(30,1)` 的硬轴换成「无悬挂参数的同一刚性轴」）。

## 验证

- 提取链：`node tools/bple-springs/extract-springs.mjs` → 39 个轮子 prefab、其中 1 个声明弹簧（`Part_MotorWheel_08_SET`，stiffness 50 / class default 150 / damper 5 / restOffset 0）；`apply-springs.mjs` → `content/parts.json` 只有 270 带 `suspension`，重跑零 diff（字节可复现）。目录侧 `import-variants.mjs` 只追加 270（additions 1，warnings 0），`apply-materials.mjs` / `apply-shapes.mjs` 只改这一条（`git diff --stat` 17 insertions / 0 deletions）。
- 物理实测（`tasks/spring-probe`，真 Bepu 后端，跑完即删）：夹具（底盘 2 kg、轮 1 kg、r 0.5、50 N/m / 5 N·s/m → 1.3783 Hz、ζ 0.4330）加载后轴向偏移 **0.3910**（理论 `2*9.81/50 = 0.3924`，−0.4%），侧向漂移 **0.0000**；把底盘重量用冲量抵消后回到 **0.0014**。（此前用临时内容补过 270 件时，真房间 terrain-v1 上同一夹具也测到 0.1965 对理论 0.1962、普通轮 0.0008，随零件一并撤回。）
- 提取链的状态：`extract-springs.mjs` 报 39 个轮子 prefab、1 个声明弹簧（`Part_MotorWheel_08_SET`，stiffness 50、类默认 150、damper 5、restOffset 0、`unmappedSpringPrefabs: [Part_MotorWheel_08_SET]`）；`apply-springs.mjs` 报「would update 0 parts」并打印缺口，重跑对 `content/parts.json` 零 diff（本任务未改动任何现有轮子的摩擦/材质值）。
- 测试：`dotnet test PigForge.slnx` 通过 / 0 失败（Core 172、Physics 28、Server 82、Protocol 39、Replay 11），含新增 `WheelSuspensionTests`（4：加载-卸载-回位、自转与线锁、刚性对照、双跑哈希）与 `WheelSuspensionRoomTests`（1：夹具内容经解析→装配→Bepu 全链路双跑 `ComputeStateHash` 一致；房间级的垂度测量仍待在夹具几何上调通，垂度证据由前者给出）；`cd clients/web && pnpm test` 193 通过、`pnpm build` 通过（`slope.test.ts` 的目录漂移守卫随 `generate-play-parts.mjs` 重新生成后保持绿）。
- 无既有断言被反转：所有原有测试（含双跑确定性）在改动后仍绿。

## 未做

- **补上 OffRoadWheel 零件本身**（目录 + 美术）：见决策 2，需要先决定 IN sprite 系统的贴图解包方案；`tools/bple-springs` 的数据与 `apply-springs.mjs` 已经就位。
- 其余轮子族不弹性（原版无数据）；`m_jointPreprocessing` 的逐件软/硬、`m_jointConnectionStrength` 断力、`m_jointType` HingeJoint 均未建模。
- 客户端不渲染悬挂行程（轮子贴图仍固定在轮体上）；`BalloonBalancer`、`Spring` 部件、抓钩/绳索弹簧与本轮无关。
