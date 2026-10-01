# ADR-011: 装配模型对齐原版 —— 关节能力、嵌套、运行时连接

## 状态

Accepted。取代 ADR-008 决策 1 里「除轮子外相邻即焊」的隐含默认；轮子铰接本身不变。

## 日期

2026-10-01

## 背景

用户报告四处与原版不符的玩法：猪和猪会互相建立关节；零件无法嵌套（不能把猪放进木框）；引擎不必在框里就能工作；动力轮不受门控。

调查发现根因是 **PigForge 的装配模型比原版简单**：`CompoundAssembler.CanMerge` 只排除轮子，其余相邻的动态零件一律并成一个刚体；`ConstructionRules.Place` 要求零几何重叠。原版的判据不是「框 vs 非框」，而是每件一个三值属性。

## 原版真值（逐条出处）

1. **关节能力**是每个零件 prefab 里序列化的 `m_jointConnectionType`：`None = 0 / Source = 1 / Target = 2`（`BasePart.cs:111-116,209`）。
2. **判据只有一处**（`Contraption.cs:690`）：两端都不得为 `None`，且至少一端为 `Source`，才建立关节。
3. **猪 / 猪王 / 鸟蛋 / 引擎 = `none`**；**TNT、轮子、扇 = `target`**；**木框、铁框、弹簧 = `source`**。全量 343 个 prefab 分布：`target` 196 / `none` 109 / `source` 38。
4. **嵌套**：框可包裹零件（`Frame.cs:32`、`BasePart.cs:1143`），除框外皆可被包裹（`BasePart.cs:1148-1166`）；放置到框所在格即设 `enclosedPart`，一框一个且禁止同类型（`Contraption.cs:1729-1749`）；被包裹件与框加 `FixedJoint` 并 `Physics.IgnoreCollision`（`Frame.cs:44-50`）。
5. **`none` 只表示设计期不建关节**：气球与沙袋在 `Initialize` 里沿网格方向（半径 10 格，`INSettingsBExp.json`）找第一个合法锚点 —— **框或猪**（`Sandbag.cs:96-102`、`Balloon.cs:104-107`）—— 然后建 `SpringJoint`（`minDistance 0`、`spring 100`、`damper 10`；沙袋 `maxDistance` 按袋数 0.5/0.55/0.65，气球为 `Random.Range(0.8,1.2) × (距离−0.5) + (锚点是猪 ? 0.3 : 0)`）。
6. **引擎仅在被包裹时有效**（`Engine.cs:62`）；**推进件需相邻底盘**（`BasePropulsion.cs:13-19`）。

## 决策

1. **内容新增三段真值**，唯一来源是 `tools/bple-joints/extract-joints.mjs`（扫描全部 `Part_*.prefab`），**禁止手写**；写入由 `tools/bple-joints/apply-joints.mjs` 从报告驱动、幂等：
   - `capabilities.jointConnectionType`（`none`/`source`/`target`）
   - `capabilities.canEnclose`（仅框：`wooden-block`/`metal-box` 族，共 22 条）；`canBeEnclosed` 由 `!canEnclose` **推导**，不落数据
   - `capabilities.attachment`（方向 + 绳长 + 挂点；气球族 `down`、沙袋族 `up`，共 38 条）
2. **装配判据逐字落地** `Contraption.cs:690`：`CanMergePair(a,b) = CanMerge(a) && CanMerge(b) && 两端非 none && 至少一端 source`。于是猪无需任何特判 —— 它的数据就是 `none`，焊不上任何东西。
3. **嵌套关系是独立于关节能力的一条边**：被包裹件与其框**无论 `jointConnectionType` 为何都合并**（对应原版 `Frame.cs:44-49` 直接加 FixedJoint、绕过第 2 条判据）。同体成员天然互不碰撞，因此不需要 `IgnoreCollision` 的等价物。
4. **运行时连接**新增物理关节种类 `PhysicsJointKind.Distance`（最小/最大距离 + 弹簧频率/阻尼比），Bepu 侧用 `DistanceLimit` 实现，Jolt 按既有约定抛 `NotSupportedException`。原版 `spring/damper` 按 `f = sqrt(k/m_r)/2π`、`ζ = c/(2·sqrt(k·m_r))` 换算成 Bepu 的 `SpringSettings`（教科书标准关系，且因此正确地保留了原版弹簧对质量的依赖）。

## 记录在案的偏差

1. **气球绳长的随机因子取均值**：原版 `Random.Range(0.8, 1.2)` 与 PigForge 的确定性要求冲突（双跑哈希测试），故取 `1.0 × (距离 − 0.5) + (锚点是猪 ? 0.3 : 0)`；代码注释里标注了差异与原版出处。
2. **锚点重定位未实现**：原版 `Initialize` 会把气球/沙袋**瞬移**到锚点附近的固定偏移（`transform.position = anchor + vector`），PigForge 只建关节、不搬位置，让绳子的最小/最大距离去约束。静止垂度 `mg/k = 0.29 m` 与原版一致；气球 9g 级拉升下软限位会拉伸到 1.9 m 左右，原版同样是软弹簧，未视为缺陷。
3. **气球机身参数未采纳**：原版运行时把气球改写为 `mass 0.1`、`linearDamping 2`、`angularDamping 0.5`、`constraints 48`、半径 0.5 的球碰撞体（`Balloon.cs:124-131`），而内容里是 0.3/0.6/0.9。本轮**不动内容质量**，留待手感验收。
4. **`BalloonBalancer` 未实现**：原版给气球的锚点挂的摇摆抑制组件，属稳定性辅助件，视试玩决定是否补。
5. **缺省即 `none`**：内容未给 `jointConnectionType` 的零件（仅 3 个 PigForge 自造的静态关卡件 2/5/6）不能建关节。这是有意的严格默认，避免静默焊接。

## 验证

- **308 个测试通过 / 0 失败**（基线 274；新增 34：`JointAndEnclosureTests` 14、`AttachmentTests` 4、内容与物理契约扩充）。
- 既存测试中只有一条断言按新行为**有意反转**：`SandboxRoomTests` 的「猪与木块同体」改为「不同体」（该文件是新模型的核心断言，已带 §2 出处注释）；其余改动均为夹具补 `jointConnectionType`。
- **真实 socket 端到端**（真实 `--play` 房间）：
  - 8 次放置全部接受，含**把猪放进框所在同一格**（嵌套在真实协议上生效）；
  - 框里的猪与框**同 body（10）**并一起落地；
  - 两猪相邻 → body 11 / 12 **分开**（用户头号抱怨已修）；
  - 气球绑定后带框升空；沙袋被绳**带着升到 29.7**（对照：无锚沙袋留在地面）。
- 内容分布与提取器报告**逐条一致（0 处漂移）**：`none` 83 / `source` 31 / `target` 150。

## 未做

- 引擎与推进门控（原版真值见 §背景 6，已单列待办）。
- 客户端放置交互（规格阶段 4）。
