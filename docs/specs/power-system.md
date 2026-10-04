# 规格：动力系统（引擎纯供能 + 动力轮门控）

状态：待实施（真值已核实，无待拍板项）。
来源：原版反编译脚本 `BPLE_Unity6/Assets/Scripts/Assembly-CSharp/` 与 `Assets/TextAsset/INSettingsBExp.json`。

## 1. 要解决的两处偏差

1. **引擎直接推自己所在刚体** —— PigForge 现在给引擎 `motor` 能力并施加推力；原版引擎**不推任何刚体**，只对同分量供能。
2. **动力轮不受门控** —— PigForge 的动力轮（`motor-wheel` 17、`propeller` 38 等）无条件出力；原版动力轮的力与最高速乘「该分量的引擎功率因子」，没有引擎就是 **0**。

## 2. 原版真值（逐条出处）

| # | 规则 | 出处 |
|---|---|---|
| 1 | 每件的功率数据是 prefab 上的序列化字段：`m_powerConsumption`、`m_enginePower` | `BasePart.cs:162,164`（模板赋值 `:1445-1446`） |
| 2 | 「耗能件」= `m_powerConsumption > 0`；「引擎」= `m_enginePower > 0` | `BasePart.cs:601-608` |
| 3 | 引擎**不推刚体**：它只激活同分量内可启用的耗能件 | `Engine.cs:29`、`Engine.cs:138 ActivateAllPoweredParts` |
| 4 | 引擎必须被包裹才算有效零件 | `Engine.cs:61 ValidatePart() => m_enclosedInto != null` |
| 5 | 分量功率 = 累加成员；**只有启用中的耗能件计入消耗** | `Contraption.cs:891-920`、`:1034` |
| 6 | 功率因子：`raw = min(enginePower / powerConsumption, 10 × EnginePowerLimit)`（消耗 > 1）；`raw = 1`（有引擎且消耗 ≤ 1）；否则 `raw = 0`；`factor = pow(raw, raw > 1 ? 0.585 : 0.75)` | `Contraption.cs:540-556` |
| 7 | 开关默认打开、上限默认 4：`DynamicPowerSystem = true`、`EnginePowerLimit = 4.0` | `INSettingsBExp.json` |
| 8 | 动力轮/推进件把 `factor` 乘到力与最高速：`m_maximumForce = m_force × factor`、`m_maximumSpeed = 15 × factor` | `MotorWheel.cs:101-109`、`OffRoadWheel.cs:172-180` |
| 9 | 推进件必须至少相邻 1 个底盘邻居（`Frame` 是唯一 `IsPartOfChassis()` 为真的类）；风扇/螺旋桨/旋翼、火箭/喷气、风箱、机翼、尾翼都走这条 | `BasePropulsion.cs:13-20`、`Frame.cs:37-40`、`Wings.cs:14-31`、`Tail.cs:12-29` |

补充：`Contraption.cs:558-571` 是 `DynamicPowerSystem = false` 的旧分支（还会把动力轮的消耗乘 0.9 折抵），默认不走，**本规格不实现**。

## 3. PigForge 现状

| # | 现状 | 出处 |
|---|---|---|
| 1 | 引擎走 `capabilities.motor`（`thrustPerTick`/`directionX`），`RunMotors` 把冲量加给引擎**自己所在的 body** | `content/parts.json`（partTypeId 8）、`GameplayRules.RunMotors` |
| 2 | 动力轮（17/38）也有 `motor` 能力，自己出力，无门控 | `content/parts.json` |
| 3 | 内容里**没有**任何 `powerConsumption`/`enginePower` 字段 | `content/parts.json` |
| 4 | ~~**没有「最高速」量**~~（2026-10-04 已补）：`RunMotors` 现在按原作 `m_maximumSpeed = 15 × factor` 守上限（见 §4 决议 4 的补丁）。**仍缺的是整车的线性阻尼**：原版每个零件刚体 `linearDamping 0.2`（`BasePart.cs:1192`）、机翼/尾翼 `1.0`（`Wings.cs:99`/`Tail.cs:52`），PigForge 的物理契约**没有这一项**，所以只有「有上限的推进件」会停，别的推力仍可无限加速 | `GameplayRules.RunMotors`；原版 `BasePart.cs:1184-1196` |
| 5 | **轮子是与车体不同的刚体**：装配器把轮子铰接在邻件上（`CompoundAssembler.CollectHinges`，ADR-008/009），而原作的功率分量是**关节图**（`Contraption.cs:1293` 对 `m_jointMap` 全部并查集），铰接轮与底盘同分量 | `CompoundAssembler.cs`、ADR-011 §背景 3 |

## 4. 决议

1. **内容新增两字段** `capabilities.powerConsumption` / `capabilities.enginePower`，真值唯一来源是新增提取器 `tools/bple-power/extract-power.mjs` + `apply-power.mjs`（与 `tools/bple-joints/` 同级的独立目录；读取每个 `Part_*.prefab` 的 `m_powerConsumption`/`m_enginePower`，并写出全部映射件的取值，含显式 `0`），**禁止手写**。猪 prefab 带 `m_enginePower = 20`：提取器照抄真值，规则层按 `BasePart.cs:601-608`「`enginePower > 0` 即引擎」处理。
2. **引擎改为纯动力源**：引擎不再产生 `ApplyImpulse`；它的作用是把「本簇有效」这一事实提供给功率计算（对应原版 #3）。`Engine.ValidatePart`（#4）体现为放置规则：引擎未被包裹时**不供能**（而不是拒绝建造，保留可搭建性）。
3. **功率因子按簇计算**：规则层对每个簇累加成员 `enginePower` 与**启用中**的 `powerConsumption`（#5），按 #6 的公式取 `factor`（`raw` 的两段指数与上限逐字照搬）。因子随成员/启用状态变化重算，计入状态哈希。**簇 = 刚体 + 铰接在它上面的件**（现状 5）：装配层把每条轮子铰链登记进规则层，否则「自己一个刚体」的轮子永远看不到底盘的引擎功率。
4. **动力轮门控**：带 `powerConsumption` 的推进件，其每 tick 冲量按 `factor` 缩放（#8）；`factor = 0` 即完全不出力。
   - **最高速已落地（2026-10-04 补丁）**：`m_maximumSpeed = 15 × factor`（`MotorWheel.cs:101-103`，粘轮同一条 override 链），沿轮轴的速度 `|v∥| ≥ max` 时**完全不出力**，以下按 `sqrt(1 - |v∥| / max)` 递减（`:292-299` 的 `num2 < m_maximumSpeed && num2 > -m_maximumSpeed` 与 `Mathf.Pow(f, 0.5f)`）。落地在 `GameplayRules.RunMotors`，常量 `MotorWheelMaximumSpeed = 15f`（原版代码常量，非内容值）。实测/用例：`AMotorWheelTapersToItsTopSpeedAndStopsThere`、`ADrivenWheelTopsOutAtFifteenTimesThePowerFactor`（`15 × 1.2676 ≈ 19.01`）。
     - **注意「手感顶速」还有第二半**：原版每个零件刚体有 `linearDamping 0.2`、机翼/尾翼 `1.0`（`BasePart.cs:1192`、`Wings.cs:99`、`Tail.cs:52`），这才是螺旋桨/火箭这类「无自带上限」推进件的终端速度来源。PigForge 还没有刚体阻尼项 → 见 §7。
   - **驱动轮集合由提取器判定**（后续补丁）：原版「是不是驱动轮」看脚本有没有 override `InitializeEngine()`——`MotorWheel`/`OffRoadWheel`/`StickyWheel` 有（`MotorWheel.cs:99-104`、`OffRoadWheel.cs:172-180`、`StickyWheel.cs:117-122`），只会滚的 `CartWheel` 没有（`CartWheel.cs:129-149`）。`tools/bple-power/extract-power.mjs` 用 `m_Script` guid → 类名 → 基类链推出这个集合，再从 prefab 读 `m_force`，按马达轮的标定锚（`m_force` 50 → 冲量 2.2）比例换算，写进 `motor` + `activation:"toggle"`。因此粘轮（`m_force` 100）得到 4.4，与马达轮共用同一条门控路径。
5. 不改协议：因子是服务端规则态。

## 5. 实施分期与验收

| 阶段 | 内容 | 验收 |
|---|---|---|
| 1 | 提取器 + 内容字段（先跑出全量分布报告） | 报告可复现；内容校验（严格解析）通过 |
| 2 | 簇功率聚合 + `factor` 计算 | 单测：无引擎 → 0；有引擎且消耗 ≤1 → 1；消耗 >1 → 按公式与上限；指数两段都覆盖；启用开关影响消耗 |
| 3 | 引擎停止出力 + 动力轮按 factor 缩放 | 单测：引擎单独不产生位移；动力轮无引擎时不动、有引擎时动；跨运行确定性 |
| 4 | 回归与文档 | 全量 `dotnet test` 绿；ADR 记录；`AGENTS.md` 补工具与规则 |

## 6. 风险

- **手感面**：现有内容里 `motor` 能力同时挂在引擎与动力轮上，改动后「引擎直接推」的旧玩法会消失（这是有意还原）。沙盒里已有的构造物行为会变。
- **数值面**：`factor ≤ 1` 的绝大多数组合会让推进变弱（原版如此），需要试玩确认不是「太弱」。
- **回归面**：`RunMotors` 是热门路径，注意保持无分配与确定性排序。

## 7. 不做

- 电气回路（`ElectricalPart`/`Wire`/`Electrode` 的逻辑电平系统）与 `FuelTube`：那是开关/逻辑子系统，与机械动力无关。
- 旧分支（`DynamicPowerSystem = false`）。
- ~~**原作马达限速**~~（**已落地，2026-10-04**）：`m_maximumSpeed = 15 × factor`、`sqrt(1 - |v∥|/max)` 递减、到顶零出力（`MotorWheel.cs:101-103,292-299`），见 §4 决议 4。
- **刚体线性阻尼**（新的未做项）：原版每个零件刚体 `linearDamping 0.2` / `angularDamping 0.05`（`BasePart.cs:1192-1193`），机翼与尾翼覆盖成 `1.0` / `0.2`（`Wings.cs:99`、`Tail.cs:52`），气球 2.0（`Balloon.cs:130`）、沙袋 1.0（`Sandbag.cs:133`）、金猪 0.5（`GoldenPig.cs:16`），并且有一个 IN 开关 `NoDrag` 会把全场阻尼清零（`INContraption.cs:299-336`）。**PigForge 的 `IPhysicsWorld` 契约里没有阻尼项**，所以火箭/螺旋桨/肚子推力都没有终端速度；要补就是契约 + Bepu/Jolt 两个后端 + 逐件内容值（提取器）+ 簇内按质量聚合。
- ~~**推进件的因子**~~（已实现，2026-10-03）：原作对 `FanPropeller`/`PoweredUmbrella`/`StickyWheel` 同样乘因子（`FanPropeller.cs:85-92`、`PoweredUmbrella.cs:70-89`、`StickyWheel.cs:119`）。粘轮归入 `motor` 驱动路径（决议 4 的补丁）；`fan`/`umbrella` 与 `rotor`（旋翼，现与风扇同为 `FanPropeller` 的 `fan` 推力件，走 `RunFans` 且受 `maxSpeed` 上限约束，见 `docs/specs/fan-propeller.md`）现在统一走 `GameplayRules.TryDriveFactor` 按 `ClusterPowerFactor` 缩放，簇内无引擎即为 0。同一批还落地了 §2 真值 9 的底盘门控（G25）。唯一未建模的是 `PoweredUmbrella.cs:70-89` 里「有功率但该分量没有引擎」时再乘 0.5 的分支——本模型的「分量」就是功率簇，不区分这两档。
- **引擎按钮联动**：原作点引擎会开关同分量内全部耗能件（`Engine.cs:29`、`Contraption.cs:1013-1090 ActivateAllPoweredParts`）。本切片只做供能与门控，引擎的 `activation: toggle` 保留但不联动（各耗能件仍用自己的开关）。
