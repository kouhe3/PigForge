# Spec: 弹簧的原版关节化（G62）

> 状态：Draft（实现前收口；消费 `docs/intent/spring-and-glove.md`）。
> 取代今天的实现：`capabilities.spring: 12.0`（手写数）+ `GameplayRules.RunSprings`（接触即给自己向上冲量的「弹跳垫」）。
> 权威契约仍是协议 v2、`SnapshotWire`/`CommandWire`、ADR-002；本文件只补「弹簧」这一块。

## 0. 结论（一句话）

**弹簧是一条可断的软关节，不是推进件、更不是弹跳垫**：它把两件拉在一起（弹力绳 250 N/m、20 N·s/m，静止长度 0）或给一条单轴的软限位（0.1 m、bounciness 1），
拉过 3 m 就断并由一个端点刚体续接。它**从不给任何刚体加冲量**——所以「越弹越高」在原版物理上不可能发生。

## 1. 原版真值

| 量 | 值 | 出处 |
|---|---|---|
| 限位弹簧刚度 / 阻尼 | `SPRING_LIMIT_SPRING = 250` N/m、`SPRING_DAMPING = 20` N·s/m | `Spring.cs:7,9` |
| 限位 / 弹性 | `SPRING_LIMIT = 0.1`、`SPRING_BOUNCINESS = 1` | `Spring.cs:11,13` |
| 断力 | `SPRING_BREAK_FORCE = 250`；IN `StrongSpringConnection=true` → `m_jointConnectionStrengthHigh × 2 = 1200`（并把自身强度改 High） | `Spring.cs:15,38-39` |
| 关节路径 A（**弹力绳**） | `StableSpringConnection && m_customPartIndex ∈ {0,2}` → `SpringJoint`：`minDistance 0 / maxDistance 0 / spring 250 / damper 20`、`anchor (0,−0.5,0)`、`breakForce = SPRING_BREAK_FORCE`、`enablePreprocessing true`；**`connectedAnchor` 留给 Unity 默认（autoConfigure = true）** | `Spring.cs:104-118`；`JointExtensions.cs:5-12` |
| 关节路径 B（**y 软限位**） | 其余皮肤 → `ConfigurableJoint`：角三轴 Locked、`x/z Locked`、`yMotion Limited`、`configuredInWorldSpace true`、`linearLimitSpring(250,20)`、`linearLimit(0.1, bounciness 1)`、`enablePreprocessing false`、`breakForce = SPRING_BREAK_FORCE`；`connectedAnchor` 同样留默认 | `Spring.cs:119-127` |
| 拉断 | `FixedUpdate`：两锚点距离 > **3 m** 且 `!contraption.HasSuperGlue` → 销毁该件的全部 FixedJoints → `HandleJointBreak()` → `CreateSpringBody(Direction.Down)`（实例化 `SpringEndpoint.prefab` 为**新刚体**并挂上同款关节）；`m_jointBroken` 只挡重复断 | `Spring.cs:78-92,131-176` |
| 质量 | IN `StableSpringConnection` → `rigidbody.mass = 1`；否则 prefab（内容现 0.6） | `Spring.cs:69-75` |
| 锚点/视觉 | `m_localConnectionPoint = (0, 0.5, 0)`（本件系）、`m_remoteConnectionPoint = part.InverseTransformPoint(self.position − 0.5·up)`；`SpringVisualization` 每帧按两锚点距离缩放摆向 | `Spring.cs:47-58,120-122` |
| IN 发布配置 | `StrongSpringConnection = true`、`StableSpringConnection = true`（`INSettingsBExp.json`） | 见 §7 |

**逐皮肤关节路径（`tasks/bple-springs-report.json`，27 条硬断言，直方图由工具守卫）**：

| prefab | partTypeId | `customPartIndex` | prefab `m_mass` | **生效 mass** | 关节路径 |
|---|---|---|---|---|---|
| `Part_Spring_01_SET` | 12 | 0 | 0.3 | **1** | **SpringJoint**（弹力绳） |
| `Part_Spring_02_SET` | 205 | 1 | 0.3 | **1** | ConfigurableJoint（y 软限位） |
| `Part_Spring_03_SET` | 206 | 2 | 0.3 | **1** | **SpringJoint**（弹力绳） |
| `Part_Spring_04_SET` | 207 | 3 | 0.3 | **1** | ConfigurableJoint（y 软限位） |

（生效 mass 1 而不是 prefab 的 0.3：`StableSpringConnection = true` → `EnsureRigidbody` 强制 `mass = 1`，`Spring.cs:69-75`。）
四个 prefab 的 `m_endPointPrefab` 都指向 **`SpringEndpoint.prefab`**（自带 `Rigidbody` mass 1 + `BoxCollider`），即 §4 的端点刚体。

### 1.1 原版实测（2026-10-04，`tasks/spring-probe.json`；`unity run unity/PigForge.WeldProbe -- -executeMethod PigForge.WeldProbe.Probe.SpringProbe.Run`，钉 2021.3.45f2 + 原版物理设置）

**探针推翻了两条凭源码想当然的读法**，以实测为准：

1. **`minDistance = maxDistance = 0` 不是「恒定拉向重合」**。`SpringJoint` 的 `connectedAnchor` 在原版里是 **Unity 默认的 autoConfigure**，
   于是关节的静止长度 = **装配时的实际间距**：`min = max = 0` 意味着「**不允许松动的距离链接**」，而不是把人拉成一点。
   实测（质量 1 kg 吊在固定端下）：关节力 **9.68 N = 载荷**，静止间距从 `1.0` 变到 `1.0645`（挠度 **0.0645 m**）；
   质量 2 kg：载荷 19.65 N、挠度 0.1031 m ⇒ 等效刚度 **≈150 N/m（1 kg）/ 190 N/m（2 kg）**，即**载荷相关**，不是声明的 250 N/m。
2. **它不弹**。给 4 m/s 的初始速度后，挠度**只有半个周期就单调收敛**（400 步 = 8 s 内零交叉 1 次，实测 ζ ≈ 0.54；理论 `c/(2√(km))` = 0.63）——
   即这个「弹簧」实际是**强阻尼的柔性链接**，不是蹦床。这解释了为什么原版里它从来不会把车弹起来。
3. **断点 = 声明值**：`breakForce 250` → **245 N** 断；`breakForce 1200` → **1190 / 1200 / 1205 N** 断（阈值比标称低 ~2%）。
4. **路径 B（y 软限位）**同载荷下挠度 **0.1385 m**（≈ `limit 0.1` + 柔度），同样**不振荡**、同样按声明断力断。

⇒ PigForge 的建模目标因此**更简单也更准**：两条路径都是「**在装配间距上的柔性距离关节**」（ADR-011 已有的 `Distance` 机器），
差别只是允许的偏移量（A ≈ 0.065 m/9.8 N、B ≈ 0.14 m/9.8 N）与软度标定；**都不要做成会还给车能量的弹性件**。

## 2. 内容

```jsonc
// 12 spring（+ 205/206/207 皮肤）
"capabilities": {
  "jointConnectionDirection": "upAndDown", "jointConnectionStrength": "normal",
  "jointConnectionType": "source", "powerConsumption": 0, "enginePower": 0,
  "spring": {
    "joint": "bungee",          // "bungee" | "limit"，逐皮肤，由提取器决定
    "stiffness": 250, "damper": 20,
    "limit": 0.1, "bounciness": 1,   // 仅 limit 路径使用
    "breakForce": 1200,              // StrongSpringConnection=true 的发布值
    "mass": 1                        // StableSpringConnection=true 的发布值
  }
}
```

- 拳套**不再**共用这个键（见 `docs/specs/boxing-glove.md`）。
- 五处同步：`schemas/part-content-v1.schema.json`、`PartContentParser`、`PartContentDocument`/`PartCapabilities`、
  `clients/web/src/schema/{types,validateContent}.ts`，并重生成 `clients/web/src/builder/playParts.generated.json`。
- 提取器：`tools/bple-springs/extract-springs.mjs`（报告）+ `apply-springs.mjs`（报告驱动、幂等、`--dry-run`）。

## 3. 装配（`CompoundAssembler` + `GameRoom`）

1. **弹簧缝不并簇**：union 循环里加一档——seam 的任一端 `capabilities.spring != null` → **不 union**（与 ADR-024 的 frame↔frame 同形），
   登记 `CompoundSpring(Left, Right, AnchorInLeft, AnchorInRight, Joint, Stiffness, Damper, Limit, Bounciness, BreakImpulse)`。
   否则弹簧会被并进同一个刚体，「弹性」直接消失（今天就是这样：两端都是 `source` → union）。
2. **关节**（两条路径按 §1.1 的实测**共用一个模型**）：
   - 共同形状 = **`min == max` 的距离关节（间距 = 装配时的两件间距，不允许松动）+ 弹簧频率/阻尼比 + 可断**。
     契约优先**复用 ADR-011 的 `Distance`**（只补「`min == max` + 断力」这一档），换算**只能用** `GameRoom.TrySpringResponse`
     （`omega = sqrt(k/m)`、`zeta = c/(2·sqrt(k·m))`；质量取**两端刚体**的质量，ADR-012 决策 5 的口径）。
   - 路径差别**只用弹簧标定表达**：A（bungee）≈ 9.8 N 载荷 0.065 m 挠度；B（limit）≈ 9.8 N 载荷 0.14 m 挠度 —— 两者都**强阻尼**（实测半个周期就收敛，ζ ≈ 0.54）。
   - **标定必须用实测的有效刚度，不是声明的 250**：Bepu 的 `Distance(min == max)` 是**精确**弹簧（实测 sag = 载荷/k 到小数点后 5 位：1 kg → **0.03924 m** = 9.81/250），
     而原版同样的载荷下是 **0.0645 m**（等效 **150 N/m**，2 kg 时 190 N/m）⇒ 照抄 250 会比原版**硬 39%**，超出 §6 的 ±25%。
     做法照 `ADR-024` 的先例：把**实测有效刚度**当标定输入（一个 PigForge 标定常量 + 注释写明「声明的 250 不是 PhysX 实际交付的」），并在 §7 记偏差。
   - **不为路径 B 新开「单轴线性限位」能力**（ADR-012 的 `LinearAxisServo` 留在轮子悬挂里）：0.14 vs 0.065 的差别用标定近似，记进 §7。
3. 绑定与重建沿用 `_weldJoints` 的纪律：`ForgetJointsForBody` 先忘关节再 `DestroyBody`；拆簇后 `Rebind…`（ADR-023/024 的先例）。

## 4. 断（> 3 m）

- 每 tick 检查两锚点距离（弹簧件数很少，直接算）：> **3 m** 且该簇无 SuperGlue → 销毁这条关节 → `HandleJointBreak()` →
  host 注册一个**运行时子实体**「端点刚体」（`SpringEndpoint.prefab` 的几何/质量由提取器给出；`ADR-027`）并用同一条关节把它接上。
- 子实体归属 host 的 owner：host 被 RESET / 离开 / 拆簇摧毁 → 子实体同处销毁。
- 关节的断力 = `breakForce`（250 / 1200）→ 换算成规则层可复算的冲量阈值（与 G15 同一换算口径；本切片先按 §7 记录口径）。

## 5. 要删掉的东西（干净切换，不留旧路径）

`GameplayRules.RunSprings`、`SpringState`/`SpringStore`（若只剩它一个用处）、`_touchedBodies` 的弹跳分支、`ReArmBounces`（若空）、
`AddSpring` 及其调用点、`GameplayRulesTests.SpringLaunchesOncePerTouchdown`（它钉的就是错语义）、内容里的 `"spring": 12.0`、`tools/bple-power` 里给弹簧手写值的分支（若有）。

## 6. 验收

- **原版编辑器探针**：`unity/PigForge.WeldProbe/Assets/PigForge/Probe/Editor/SpringProbe.cs`，入口
  `unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 -- -executeMethod PigForge.WeldProbe.Probe.SpringProbe.Run`
  → `unity/PigForge.WeldProbe/replays/spring-probe.json`（抄进 `tasks/spring-probe.json`）。**已跑，读数（2026-10-04）**：

  | 单元 | 载荷 | 挠度 | 关节力 | 等效刚度 | 振荡 | 断点 |
  |---|---|---|---|---|---|---|
  | A（auto 锚，1 kg，break 1200） | 9.81 N | 0.0645 m | 9.68 N | 150 N/m | 半周期收敛（零交叉 1 次） | 1200 N |
  | A（auto 锚，2 kg，break 1200） | 19.62 N | 0.1031 m | 19.65 N | 190 N/m | 半周期收敛 | 1190 N |
  | A（auto 锚，1 kg，break 250） | 9.81 N | 0.0645 m | 9.68 N | 150 N/m | — | **245 N** |
  | B（y 限位，1 kg，break 1200） | 9.81 N | 0.1385 m | 9.76 N | 70 N/m | 半周期收敛 | 1200 N |

  待补（探针已支持、本轮未跑全）：拉到 > 3 m 的断点与端点刚体接管（`CreateSpringBody`）——**实现前必须补量**。
- **Bepu 对照**：同工况 ±25% 口径照 `docs/specs/weld-compliance.md` §4.1 的写法写清。**已量到的第一版**（`SpringDistanceJointTests`，契约子代理，physics **75** 通过 = 59 非 Jolt + 16 Jolt）：
  1 kg 载荷下 Bepu 的 sag = **0.03924 m**（= 载荷/声明的 250，精确），而原版 **0.0645 m** ⇒ **−39%，必须按上面的标定改**（这就是 §7 那条偏差的由来）；
  断力两档已可复现（`breakForce 250` 在 294 N 载荷下第 26 tick 断、`1200` 在 1373 N 下第 62 tick 断、`breakImpulse 2 N·s` 也可断，断后两 body 都活着、`JointBroken` 事件照发）。
- **单测**：Core（装配不并簇 → 两条 body + 一条关节；断力两条；`ForgetJointsForBody` 纪律）；Physics（真 Bepu：bungee 与 limit 各一条）。
- **实机**（真服务器 + 真内容 + 真 Bepu，读数写进本节）：弹簧 + 木框的车从坡上下来**不被自己的弹簧撕开**、**不自己弹起**；
  两件用弹簧连起来能感觉到「拉得住、会回弹」；`SeamBreakImpulse` 不再被弹簧触碰。

## 7. 已知偏差（写下来就要排队，否则等于永久保留）

- **IN 面**：只取 `StrongSpringConnection` / `StableSpringConnection`（原版发布 = `true`）；不实现运行时切 IN 开关。逐皮肤路径因此固定。
- **声明的 250/20 不是 PhysX 实际交付的行为**：实测有效刚度 150–190 N/m 且**载荷相关**、且**不振荡**。PigForge 按**实测**标定（一个标定常量，照 ADR-024 的先例），不复刻「声明值 vs 实际值」这条物理差异。
- **路径 B 的近似**：不新开单轴线性限位能力，用「`min == max` 距离关节 + 更软的标定」表达 0.14 m 的挠度（与 A 的 0.065 m 区别只在标定）。
- **视觉**：`SpringVisualization`（弹簧线随两锚点拉伸）不做 → P1 客户端项（与绳的样条渲染同一批）。
- **`enablePreprocessing`**：Bepu 没有对应开关；软/硬一律按 ADR-024 的连续弹簧口径拟合，**不要照 Unity 文档猜**（ADR-011 的教训）。
- **Jolt 后端**：`limit`/`bungee` 都不声明能力（`NotSupportedException`），与 ADR-011/012 的既有约定一致。
- **锚点与静止长度**（2026-10-04 实现落地）：`CompoundSpring` 的两端锚都取原版的 `(0, -0.5, 0)`，各自换算到该件的局部系；原版的 `m_remoteConnectionPoint`（`Spring.cs:120-122`）会让两端锚在装配时**重合**，而契约的距离带（ADR-011 的 `Distance`）要求正的静止长度，因此 PigForge 取「两端锚在装配时的实际间距」当静止长度。挠度 = 载荷/k 与静止长度无关，所以实测口径不变；「拉过 3 m」判定的就是这两个锚的世界距离，与原版同口径。**未做**：原版 `connectedAnchor` 的 Unity autoConfigure 最近点语义。
- **断力**（2026-10-04 实现落地）：关节直接带内容声明的 `breakForce`（发布值 1200 N），由 Bepu 后端在 `Distance` 关节上按累积冲量/时间执行（physics 侧已有测试）。后端按力断 与 3 m 拉断走同一条「销毁关节 → 生成端点刚体」路径；上面 §4 里「本切片先按 §7 记录口径」的 force → impulse 换算因此不必要的：**后端直接执行声明断力**，规则层不再复算冲量阈值。端点刚体续接的那条关节不设断力（与 `CreateSpringBody` 一致，`Spring.cs:139-176`）。
