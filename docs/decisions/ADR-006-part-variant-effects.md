# ADR-006: 零件行为变体——按件能力参数，语义取自原版

## 状态

Accepted

## 日期

2026-09-09

## 背景

ADR-004 决定「变体 = 扁平 partTypeId + 内容层分组，行为差异走能力」，并把行为变体（AlienTNT/BlasterTNT）推迟到能力字段落地。现在落地时发现两处原版事实需要修正与建模：

- **TNT 连锁是全部 TNT 的行为**：`TNT.cs:130-182` 对半径内任意 `TNT`（排除 `AlienTNT`）调用 `Explode()`；AlienTNT 同样连锁普通 TNT（`AlienTNT.cs:26-29`）。PigForge 此前不连锁。
- **AlienTNT 的差异不是半径/力度**：`Part_TNT_01_SET` 与 `Part_TNT_06_SET` 的 `m_explosionRadius: 8`/`m_explosionImpulse: 25` 完全相同，`INSettingsBExp.json` 里 `TNTExplosion*` 与 `AlienTNTExplosion*` 也都是 `1.0/2.0`。AlienTNT 的真实差异是 `OnCollisionEnter` 为空（`AlienTNT.cs:48-50`：撞击不点火，只能由开关/触碰触发）。
- **BlasterTNT 是一次性冲击波**：`BlasterTNT.cs:123-157` 起爆后以 `radiusVelocity 160`、`radiusDrag 0.2`、`amplitude 80000` 的扩张波推刚体 2 秒（`159-212`），自身不销毁（灰化保留），8 单位内连锁其它 BlasterTNT；质量 2、`m_triggerSpeed 32`。它是 IN 扩展件，不在 `GameData.m_customParts` 里。
- **AlienEgg 是超级胶**：`AlienEgg.cs:12-19,49-66` 把同连通分量的木/铁框体粘住并把关节 `breakForce` 设为 ∞（`NewAlienEgg` 下可开关）。

## 决策

1. **连锁进 `tnt` 能力，默认开**：`tnt.chainDetonate`（默认 `true`，原版所有 TNT 都连锁）+ `tnt.igniteOnImpact`（默认 `true`；AlienTNT 置 `false`）。连锁在同一 tick 内只把半径内的其它引信置为点燃，不递归爆炸；顺序按实体 id 升序，回放确定。爆炸参数仍取关卡 `GameplayConfig`（原版 prefab 值与 PigForge 关卡参数同量级）。
2. **BlasterTNT 用新 `blaster` 能力**：`{ radius, impulse, chainRadius? }`，必须配 `activation: "trigger"`（解析器强制）。触发后对本 tick 快照内所有动态体施加一次径向冲量（与 TNT 爆炸同一几何：线性衰减、按刚体中心施力），零件**不销毁**但标记 `Spent`，`ResetForRebuild` 重新武装；`chainRadius` 内的其它 blaster 同 tick 起爆（body id 升序去重）。
3. **AlienEgg 用新 `glue` 能力**：`glue: true` 的零件所在 compound 在 `GameRoom.SplitFromAppliedCommands` 中跳过裂缝拆分，冲量再大也不散；`glue` 是常驻能力（不建模原版 `NewAlienEgg` 的开关）。
4. **数值进内容，偏差写清楚**：BlasterTNT 的 `radius 3.5 / impulse 30 / chainRadius 8`（chainRadius 取自原版 `dist² < 64`）是按原版近场包络（1 单位处约 36 冲量）标定的一次性近似；**偏差**：原版 2 秒扩张波的时间与远场衰减不建模，原版「先解锁再击发」两段式合并为一次触发，AlienTNT 原版爆炸后可重复引爆而 PigForge 爆炸即销毁（既有模型），AlienTNT 原版不会被其它 TNT 连锁而 PigForge 会。
5. **皮肤照旧**：其余 217 件是纯皮肤（含 4 个参数变体：重沙袋 `mass × 5/1.1`、电动小轮加 `motor`、灯光框架 `light 2.14`、异形风箱 `bellows 32`），由 `tools/bple-variants/import-variants.mjs` 从原版注册表生成。
6. **不做的**：`Part_GrapplingHook_06`（异形枪）需要投射物子系统；特殊蛋 `Egg_02..05`（引力/反重力/幽灵）需要逐刚体重力与碰撞过滤——`IPhysicsWorld` 两者都没有，另开切片。

## 影响

- `content/parts.json` 269 条：46 基准 + 223 变体；`tnt` 家族 9/42/47–52 全部 `chainDetonate`（默认），51 `igniteOnImpact: false`，52 `blaster` + 质量 2，241 `egg + glue`。
- 新能力字段：schema/`PartContentParser`/`PartContentDocument`/`types.ts`/`validateContent.ts` 五处同步；`GameplayRules` 新增 `BlasterStore`/`GlueStore`、`RunBlasters`、`IgniteChargesInRadius`，并在 `RunTntFuses` 里补上规格早就要求、但此前缺失的「TNT 开关点火」（`docs/specs/play-part-switches.md:228`）。
- `GameRoom.RegisterPlacedRole` 把三个能力接到规则；`SplitFromAppliedCommands` 通过 `IsGluedCompound` 跳过含胶簇。
- 回放/快照格式不变：新能力只是内容，`partTypeId` 仍是唯一身份。

## 参考

- 原版：`Assets/Scripts/Assembly-CSharp/{TNT,AlienTNT,BlasterTNT,AlienEgg,PointLight,Mushroom,Lantern,AlienPointLight,SmallWheel,MotorWheel,Sandbag,Bellows}.cs`、`Assets/MonoBehaviour/GameData.asset`、`Assets/TextAsset/INSettingsBExp.json`、`Assets/GameObject/Part_TNT_0{1,6,7}_SET.prefab`
- PigForge：`src/PigForge.Core/Runtime/{GameplayRules,GameplayStores}.cs`、`src/PigForge.Server/GameRoom.cs`、`schemas/part-content-v1.schema.json`、`tools/bple-variants/{import-variants.mjs,variant-overrides.json}`、`docs/specs/part-variant-catalog.md`
- `ADR-002`（无伤害、语义取自原版）、`ADR-004`（变体模型）、`ADR-005`（形状来源）
