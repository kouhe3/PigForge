# 规格：原版装配模型（关节能力 + 嵌套）与「猪是货物」

状态：待实施（模型已由原版源码与数据核实，无待拍板项）。
来源：全部规则从原版反编译脚本 `BPLE_Unity6/Assets/Scripts/Assembly-CSharp/` 与 343 个零件 prefab 取证，非推测。

## 1. 要解决的问题（用户观察到的四处偏差）

1. **猪和猪会建立关节** —— PigForge 里两只相邻的猪会被焊成一个刚体；原版绝不会。
2. **零件不可嵌套** —— 常见玩法是把猪（或 TNT、引擎）放进木框/铁框里；PigForge 把这种放置当冲突拒绝。
3. **引擎不必在框里就能工作** —— 原版引擎只有被框包裹时才算有效零件。
4. **动力轮不受门控** —— 原版推进件必须相邻底盘才工作。

## 2. 原版真值（逐条出处）

### 2.1 关键修正：关节能力是**每件一个三值属性**，不是「框 vs 非框」

原版 `BasePart.m_jointConnectionType`（序列化在零件 prefab 里，`BasePart.cs:209`）取值
`JointConnectionType { None = 0, Source = 1, Target = 2 }`（`BasePart.cs:111-116`），
**判据只有一处**（`Contraption.cs:690`）：

```csharp
if (part1.m_jointConnectionType != JointConnectionType.None
    && part2.m_jointConnectionType != JointConnectionType.None
    && (part2.m_jointConnectionType == JointConnectionType.Source
        || part1.m_jointConnectionType == JointConnectionType.Source))
```

即 **两端都不得为 `None`，且至少一端为 `Source`**，才建立关节。另见 `Contraption.cs:1991`（`Source` 件对外发关节）、`PartGeneratorManager.cs:473`、`Glue.cs:139`。

原版全量分布（343 个 `Part_*.prefab`）：**`target` 196 / `none` 109 / `source` 38**。
PigForge 内容里（`tools/bple-joints/extract-joints.mjs` 报告）：**`none` 83 / `source` 31 / `target` 150**。

| 零件 | partTypeId | `m_jointConnectionType` | 出处 prefab |
|---|---|---|---|
| 猪 | 4 | **none** | `Part_Pig_01_SET` |
| 猪王 | 24 | **none** | `Part_KingPig_01_SET` |
| 鸟蛋 | 27 | **none** | `Part_Egg_01_SET` |
| 引擎 | 8 | **none** | `Part_Engine_01_SET` |
| TNT | 9 | **target** | `Part_TNT_01_SET` |
| 木框（`wooden-block`） | 1 | **source** | `Part_WoodenFrame_01_SET` |
| 铁框（`metal-box`） | 18 | **source** | `Part_MetalFrame_01_SET` |
| 弹簧 | 12 | **source** | `Part_Spring_01_SET` |
| 沙袋 / 气球 | 21 / 10 | none | `Part_Sandbag_01_SET` / `Part_Balloon_01_SET` |
| 轮子 / 动力轮 / 扇 | 7 / 17 / 11 | target | `Part_NormalWheel_01_SET` 等 |

→ **「猪/猪王/鸟蛋/引擎不可关节」不需要任何特判**：它们的数据就是 `none`，按 §2.1 的判据自然焊不上任何东西。TNT 是 `target`，因此**既可关节（对着 source 件）也可入框**。

### 2.2 `none` 的另一半：气球/沙袋在 Start 后建**运行时**关节

`none` 只表示**设计期不建关节**，不代表零件永远不连接。气球与沙袋的数据就是 `none`，但它们在
装配完成的瞬间（`Initialize`）自己找邻居建一个 `SpringJoint`：

| # | 原版规则 | 出处 |
|---|---|---|
| 1 | 沿网格方向搜索 `FindPartAt(coordX + i*dx, coordY + i*dy)`，半径是**各自家族的**连接距离：沙袋 **1 格**、气球 **5 格**（声明默认档 `INDeclarationSettingsExp.json` 的 `SandbagConnectionDistance = 1` / `BalloonConnectionDistance = 5`；B 档把两者都写成 10，2026-10-06 起已按 vanilla 落地）；取第一个**合法锚点**并停止 | `Sandbag.cs:63,71,96-102`、`Balloon.cs:87,93,104-107` |
| 2 | 合法锚点 = `IsPartOfChassis()`（＝我们的 `source` 件／框）**或猪**（气球另含 Kicker）。判空条件写作「既不是底盘**也不是猪**才丢弃」，所以**猪是合法锚点**，其他零件（轮子、TNT…）跳过继续向外找 | `Sandbag.cs:96-102`、`Balloon.cs:104-107`、气球为猪特判 `Balloon.cs:139-147` |
| 3 | 沙袋挂到锚点**下方**，`SpringJoint`：`minDistance 0`、`spring 100`、`damper 10`、`anchor = up*0.5`；`maxDistance` 按这一摞的袋数取值：1 袋 **0.5**、2 袋 **0.55**、3 袋 **0.65**（含各自挂点偏移，见 `Sandbag.cs:144-160`） | `Sandbag.cs:136-164` |
| 4 | 气球镜像：**向下**搜索、浮在锚点上方、`anchor = up*-0.5`；`maxDistance = Random.Range(0.8,1.2) * (两点距离 - 0.5) + (锚点是猪 ? 0.3 : 0)`，并给锚点挂 `BalloonBalancer` | `Balloon.cs:136-163` |
| 5 | **随机项不可照搬**：PigForge 以确定性为一等公民（双跑哈希测试），故取该随机因子的**均值 1.0**，并在代码注释里标注与原版的差异 | 本规格，冲突点见 §2.2-4 |

→ 因此 §4 的装配改动**必须同时**给出这个运行时连接，否则气球/沙袋会从载具上脱落。
PigForge 现状是「靠相邻焊接顺带粘住」，没有对应机制（见 §3 第 7 行）。

### 2.3 嵌套（入框）

| # | 原版规则 | 出处 |
|---|---|---|
| 1 | 只有框能包裹零件：`CanEncloseParts() => true`（默认 `false`） | `Frame.cs:32`、`BasePart.cs:1143` |
| 2 | 除框以外都能被包裹：`CanBeEnclosed()` 对非 WoodenFrame/MetalFrame 返回 true | `BasePart.cs:1148-1166` |
| 3 | 猪可被包裹；**只有被包裹时**才算「整体零件」 | `Pig.cs:170`、`Pig.cs:167` |
| 4 | 未被包裹的猪用**连续碰撞**（自由刚体＝货物） | `Pig.cs:183` |
| 5 | 放置到框所在格 = 设 `enclosedPart`；一框仅一个，且不能放同类型零件 | `Contraption.cs:1729-1749`、`ConstructionUI.cs:1212` |
| 6 | 被包裹件与框加 **FixedJoint** 焊死（不再走 §2.1 的判据） | `Frame.cs:44-49` |
| 7 | 焊后 **`Physics.IgnoreCollision`** 关掉两者碰撞（框碰撞体是实心 1×1×1） | `Frame.cs:50`、木框 prefab `m_Size {x:1,y:1,z:1}` |
| 8 | 框↔框相邻（距离 < 4 或 5.6）才互焊，带断裂力 | `FrameJointManager.cs:141-183` |

### 2.4 动力门控

| # | 原版规则 | 出处 |
|---|---|---|
| 1 | 引擎仅在被包裹时有效：`ValidatePart() => m_enclosedInto != null` | `Engine.cs:62` |
| 2 | 推进件必须至少相邻一个底盘零件 | `BasePropulsion.cs:13-19` |

## 3. PigForge 现状（逐条出处）

| # | 现状 | 出处 |
|---|---|---|
| 1 | 放置要求**零几何重叠**，重叠即 `CellsOccupied` 失败 | `ConstructionRules.cs:190` |
| 2 | 通过后把邻近零件**全部** `Link` 成连接 | `ConstructionRules.cs:199-230` |
| 3 | 合并谓词：动态 + 非轮 + 形状全为 box/sphere → 并入同一刚体。**没有任何「能否关节」的概念** | `CompoundAssembler.cs:547-576` |
| 4 | 并查集按连接合并；轮子走 revolute 铰接不焊 | `CompoundAssembler.cs:305-330`、`:586` |
| 5 | 内容 `capabilities` 现有键里**无关节能力、无包裹概念** | `content/parts.json` |
| 6 | 同一复合体成员天然互不碰撞 | ADR-008/009 |
| 7 | **气球/沙袋没有任何运行时连接机制**：气球只有 `RunBalloons` 施加升力，沙袋全仓无代码；它们现在「粘在车上」只是相邻焊接的副作用 | `GameplayRules.cs:777`（`RunBalloons`）、`content/parts.json`（沙袋只有 mass） |

## 4. 决议

1. **内容加 `jointConnectionType`**（`none` / `source` / `target`），真值唯一来源是新增的
   `tools/bple-joints/extract-joints.mjs`（扫描全部 `Part_*.prefab` 的 `m_jointConnectionType`），
   与 `tools/bple-materials` 同等地位：手写值一律不接受。
2. **装配实现 `Contraption.cs:690` 的判据**：新增 `CanMergePair(a, b)`，在现有 `CanMerge` 之上要求
   「两端都非 `none` 且至少一端 `source`」。于是：
   - 猪 + 猪 → 不关节（修掉观察 1）
   - 猪落在木块上但没进框 → 不关节，自由刚体（原版 §2.2-4）
   - TNT + 木框 → 关节（`target` + `source`）✓
   - TNT + TNT → 不关节（两个 `target`，无 `source`）✓
3. **嵌套**：内容加 `canEnclose`（`source` 件里的框：`wooden-block`/`metal-box` 及其变体）；`canBeEnclosed` 由 `!canEnclose` 推导（对应原版 §2.2-2 的推导式）。放置时 footprint 与框重叠不再直接拒绝，改为记录包裹关系（新增 `EnclosedBy`/`EnclosedPart` 存储），一框一个、禁止同类型（§2.2-5）。被包裹件与其框合并进同一刚体 → 天然互不碰撞（§2.2-7 的等价实现，无需 `IgnoreCollision`）。
4. **本轮不改**轮子铰接、不改协议（包裹关系是服务端构造态）。
5. **动力门控（§2.3）另起一片**，不与装配改动同批。
6. **气球/沙袋的运行时连接必须与第 2 条同时落地**（§2.2）：内容标记 attach 方向；物理层新增「距离限位/绳索」关节种类（原版是 `SpringJoint`，Bepu 侧用距离限位实现，Jolt 同现有约定抛 `NotSupportedException`）；规则层在开始模拟的绑定阶段（与轮子铰接同处）沿网格方向找到第一个 `source` 件并绑定，找不到就自由落体。数值沿用原版：最小距离 0、`spring 100`、`damper 10`、最大距离按零件取值。

## 5. 实施分期与验收

| 阶段 | 内容 | 验收 |
|---|---|---|
| 1 | 关节能力提取器 + 内容字段（本阶段已完成工具与报告） | 报告可复现；内容校验通过 |
| 2 | `CanMergePair` 落地（`CompoundAssembler`） | 单测：两猪不合并；猪+木块相邻不合并；TNT+木框合并；TNT+TNT 不合并；轮子行为不变；跨运行确定性 |
| 3 | `ConstructionRules` 包裹存储与放置判定 | 单测：框内可放置；同类型二次包裹被拒；普通重叠仍被拒；移动/删除/断线清理会释放包裹位 |
| 4 | 客户端放置交互不拦包裹 | 真实浏览器：猪拖到木框格能放下，PGFA 无错误 |
| 4b | 气球/沙袋运行时连接（§2.2） | 单测：沙袋向上找到框并挂住不坠落；气球向下绑定并升力生效；方向无框时保持自由；绑定跨运行确定性 |
| 5 | 回归与文档 | 全量 `dotnet test` + `pnpm test` 绿；`AGENTS.md` 记录新工具与装配规则；ADR 记录 |

## 6. 风险

- **回归面**：内容新增字段会触及 `PartContentParser` 的严格校验（未知键即报错）与 `schemas/part-content-v1.schema.json`。
- **行为面**：沙袋/气球从「相邻即焊」变成 `none`（焊不上），是**有意的**还原，但会改变现有沙盒手感。
- **确定性**：新增存储迭代必须按 `EntityId` 稳定排序。
- **顺序依赖**：若先合入装配判据再补运行时连接，气球/沙袋会脱落 —— 两者必须同一批合入（见 §4 第 6 条）。

## 7. 不做

- 不引入 `Physics.IgnoreCollision` 等价机制（同体天然不碰撞）。
- 不改快照协议。

## 8. 收口记录

- ~~A1：非框零件之间是否停用相邻焊接？~~ **前提错误，已撤回**。原版不是「框 vs 非框」二分，而是每件三值的 `m_jointConnectionType`（§2.1）。用户指正：不可关节件为猪、猪王、鸟蛋、引擎；TNT 既可关节也可入框 —— 与 `Part_*.prefab` 实测完全一致。
- **B1**：`canBeEnclosed` 用推导式（`!canEnclose`），对应原版 `BasePart.CanBeEnclosed()` 的实现方式。
- **B2**：包裹限一层（§2.2-5 原版即一框一个）。
- **B3**：被包裹件取框中心位置（原版 `SetPartPos(x, y, basePart)`）。
