# Spec: 原版零件变体目录补全（皮肤 + 特殊效果变体）

> 状态：Draft。消费 `docs/intent/part-variant-catalog.md`。
> 承接 `docs/decisions/ADR-004-part-variants.md`（扁平 partTypeId + `variantOf` 分组 + 行为差异走能力）。
> 本规格修正 ADR-004 的一处事实错误（见「证据修正」）。

## Capability Map

| Module id | Responsibility | Depends on |
|---|---|---|
| variant-catalog | 从原版注册表生成皮肤条目：`content/parts.json` + `part-map.json` + 形状/贴图解包 | — |
| tnt-effects | TNT 连锁引爆 + AlienTNT 开关专属点火 + BlasterTNT 冲击波 | variant-catalog（内容字段） |
| glue-effect | AlienEgg 超级胶：含胶零件的簇不裂缝 | variant-catalog |
| client-catalog | 调色板变体标签、`PLAY_PARTS` 同步、TS 校验器 | variant-catalog |

Build order: `variant-catalog` → `tnt-effects`、`glue-effect`、`client-catalog`。

## 证据修正（ADR-004）

ADR-004 称 AlienTNT「半径/力度放大」。实测原版数据不支持：

- `Part_TNT_01_SET` 与 `Part_TNT_06_SET` 序列化字段完全相同：`m_explosionRadius: 8`、`m_explosionImpulse: 25`、`m_triggerSpeed: 4`、`m_mass: 1`。
- 运行时倍率（`Assets/TextAsset/INSettingsBExp.json`）`TNTExplosionRadius=1.0 / TNTExplosionForce=2.0`，`AlienTNTExplosionRadius=1.0 / AlienTNTExplosionForce=2.0` —— 基准件与 AlienTNT 同值。
- AlienTNT 的真实差异：`OnCollisionEnter` 为空（撞击不点火，`AlienTNT.cs:48-50`）、爆炸后不销毁自身且 `LateUpdate` 每帧重置 `m_triggered`（可重复引爆，0.1s 冷却）、半径内引爆普通 TNT（`AlienTNT.cs:26-29`）、2 单位内触发 BlasterTNT（`AlienTNT.cs:30-33`）。
- 普通 TNT 也连锁：`TNT.cs:147-151` 对半径内任意 `TNT`（排除 AlienTNT）调用 `Explode()`。PigForge 当前实现**没有连锁**——这是需要补齐的原版语义，不是 AlienTNT 专属。

## Assumptions（写进规格，实现不得另猜）

1. **目录口径 = 原版注册表**：`Assets/MonoBehaviour/GameData.asset` 的 `m_customParts`（44 组 / 241 件）。只收「PigForge 已有基准件」映射到的组；每组取全部 custom part（基准件本身除外）。
2. **皮肤 = 同物理**：皮肤条目的 `mode/mass/material/shapes/capabilities` 复制基准件；例外只有规格列出的参数变体（重沙袋、电动小轮、灯光框架、异形风箱）。皮肤不改变基准件行为。
3. **编号只追加**：`partTypeId` 1–50 与 51/52（AlienTNT/BlasterTNT，贴图已在 `part-map.json`）冻结；新皮肤从 53 起，按「基准 partTypeId 升序 → 原版 customPartIndex 升序」分配，一经提交不再改号。
4. **命名**：`name` = `<基准 name>-v<NN>`（NN = 原版 prefab 后缀两位，冲突时用组内序号）；皮肤 `variantName` 省略，调色板显示「<基准件中文名> #n」；行为变体写英文 `variantName`（如 `Alien TNT`）。
5. **形状/贴图**：由现有解包器从原版 prefab 重新提取（`tools/bple-shapes`、`tools/bple-textures`）。贴图解析失败（警告）的条目必须从目录剔除，不留白。
6. **能力不继承语义**：行为变体在复制基准能力后覆盖字段；禁止用基准件能力冒充行为变体（ADR-004 #5）。
7. **零协议改动**：PGFS/PGFC 布局不变；变体就是普通 partTypeId。
8. **行为语义以原版为准**（ADR-002，无伤害系统）：TNT 连锁、BlasterTNT 冲击波、AlienEgg 超级胶见下节；不引入伤害/血量。

## 变体目录（variant-catalog）

覆盖（基准件 → 原版组 → 新条目数）：

| 基准 | 原版组 | + | 基准 | 原版组 | + |
|---|---|---|---|---|---|
| 1 木块 | WoodenFrame | 9 | 27 蛋 | Egg | 5 |
| 4 猪 | Pig | 19 | 28 拳套 | SpringBoxingGlove | 4 |
| 7 轮子 | NormalWheel | 6 | 30 红烟花 | RedRocket | 3 |
| 8 发动机 | Engine | 6 | 31 木滑翔翼 | Wings | 4 |
| 9 TNT | TNT | 2（51/52，行为） | 32 金属滑翔翼 | MetalWing | 3 |
| 10 气球 | Balloon | 7 | 33 木尾翼 | Tailplane | 3 |
| 11 风扇 | Fan | 5 | 34 金属尾翼 | MetalTail | 3 |
| 12 弹簧 | Spring | 3 | 35 黑伞 | Umbrella | 6 |
| 13 火箭 | Rocket | 3 | 36 电伞 | PoweredUmbrella | 3 |
| 14 小轮 | SmallWheel | 7 | 37 旋翼 | Rotor | 9 |
| 15 大轮 | CartWheel | 10 | 38 螺旋桨 | Propeller | 9 |
| 16 粘性轮 | StickyWheel | 3 | 39 齿轮杆 | Gearbox | 5 |
| 17 马达轮 | MotorWheel | 6 | 40 风箱 | Bellows | 7 |
| 18 金属箱 | MetalFrame | 11 | 41 绳索 | Rope | 3 |
| 19 双气球 | Balloons2 | 7 | 43 拆卸器 | Kicker | 4 |
| 20 三气球 | Balloons3 | 7 | 44 手电筒 | PointLight | 4 |
| 21 沙袋 | Sandbag | 5 | 45 探照灯 | SpotLight | 3 |
| 22 双沙袋 | Sandbag2 | 3 | 46 抓钩 | GrapplingHook | 5 |
| 23 三沙袋 | Sandbag3 | 3 | 24 猪王 | KingPig | 6 |
| 25 黑汽水 | CokeBottle | 4 | 26 绿汽水 | SodaBottle | 4 |

合计 217 件 + TNT 两件（51/52）= **219** 条新内容；导入时基准 50 条 → 269，后来删除 PigForge 自制件 3/29 → 现 **267** 条（44 基准 + 223 变体）。

**参数变体**（复制基准件后覆盖，均为原版实测值）：

| 原版 prefab | 覆盖 | 依据 |
|---|---|---|
| `Part_Sandbag_05_SET` | `mass = 基准 × (5 / 1.1)` | 兄弟件 mass 1.1，仅 05 为 5（脚本逐字段相同） |
| `Part_SmallWheel_08_SET` | 加 `motor { thrustPerTick: 2.2, directionX: 1 }` + `activation: toggle` | 脚本是 `MotorWheel`（force 50/power 100/mass 1），同组其余是 `CartWheel` |
| `Part_MetalFrame_11_SET` | `light: 2.14` | prefab 内置 `PointLightSource` size 5；PigForge 手电筒 size 7 → 3.0，按比例 5/7 |
| `Part_Bellows_07_SET` | `bellows: 32.0` | prefab `m_alienBellow=1, m_boostForce=120`，兄弟件 30；PigForge 风箱 8.0，按 120/30 |

**明确不建模（皮肤处理 + 文档记录）**：`Balloon_08` 不可碰破（PigForge 气球本就不破）、`Rope_03/04` 绳段可碰撞、`Kicker_2..5` 自动/弹性/标记连接器、`PointLight` 家族的闪烁/夜视/常亮、`SpringBoxingGlove_05` 出拳距离 5 vs 2.5、`MetalFrame_09` 随机贴图、瓶子 `Cork` 装饰。

**排除（原版基准件 PigForge 没有）**：Basket、EngineSmall、EngineBig、JetEngine、Pumpkin、GoldenPig、ColoredFrame、CustomPart、电路/机械 IN 扩展件、`Part_GrapplingHook_06`（异形枪：发射弹体，需要投射物子系统）、特殊蛋 `Egg_02..05`（引力/反重力/幽灵：需要逐刚体重力与碰撞过滤，`IPhysicsWorld` 无此能力）。

## 能力扩展（part-content-v1，全部可选、向后兼容）

```jsonc
"tnt": {
  "fuseTicks": 5,
  "chainDetonate": true,     // 爆炸时点燃半径内其它 TNT（原版 TNT.cs:147-151）
  "igniteOnImpact": false    // false = 只能由开关点火（原版 AlienTNT.cs:48-50）
},
"blaster": {                 // 一次性冲击波（BlasterTNT）
  "radius": 6.0,             // 冲量作用半径（世界单位）
  "impulse": 40.0,           // 中心冲量，线性衰减
  "chainRadius": 8.0         // 半径内的其它 blaster 同刻起爆（原版 dist²<64）
},
"glue": true                 // 含此零件的簇不做裂缝拆分（原版 AlienEgg 超级胶）
```

- 三个字段全部可选：旧内容零改动。`tnt.chainDetonate` 默认 `false`、`igniteOnImpact` 默认 `true`。
- `blaster` 需要 `activation: "trigger"`；触发后对本 tick 快照内所有动态体施加一次径向冲量（与 TNT 爆炸同一几何：线性衰减、按刚体中心施力），零件自身**不销毁**、开关一次性消耗。
- **已记录的偏差**：原版 BlasterTNT 是 2 秒扩张波（`BlasterTNT.cs:159-212`，振幅 80000/半径²），PigForge 用一次性径向冲量近似；原版「先解锁再击发」两段式合并为一次触发。数值按近场等效标定并写进内容。
- **已记录的偏差**：原版 AlienTNT 爆炸后不销毁、可重复引爆；PigForge 的 TNT 爆炸即销毁（既有模型），AlienTNT 因此只保留「撞击不点火 + 连锁」。

## 行为实现

**TNT 连锁**（`GameplayRules.Explode`）：爆炸时按 body id 升序遍历，半径内其它 TNT 立即置 `Ignited`（下一 tick 起走各自引信）；同一 tick 内不递归二次爆炸，避免链式爆炸顺序依赖。`igniteOnImpact=false` 时 `IgniteTntOnBody` 跳过该零件。

**Blaster**（`GameplayRules.RunBlasters`）：新增 `BlasterStore`；触发消费开关 → 记录一次「起爆」事件 → 同 tick 内对快照动态体施加径向冲量；`chainRadius` 内的其它 blaster 一并起爆（body id 升序，去重）；起爆后的 blaster 标记为已消耗，不再响应。

**AlienEgg 胶**（`GameRoom.SplitFromAppliedCommands`）：拆分前检查该 compound 的成员实体是否存在 `glue` 能力；存在则跳过裂缝拆分（关节阈值仍然生效于其它簇）。

## 客户端

- `PLAY_PARTS`（`clients/web/src/builder/slope.ts`）是内联内容副本，`slope.test.ts` 已有漂移守卫。新增 `tools/web-parts/generate-play-parts.mjs` 从 `content/parts.json` 生成 `clients/web/src/builder/playParts.generated.ts`，`slope.ts` 改为引用；测试改为断言生成文件与 `content/parts.json` 全量一致（双向）。
- 调色板变体标签：`variantName ?? "<基准件中文名> #n"`（n = 组内序号，1 起）。派生逻辑放在纯函数 `variantLabel()` 并有单测。
- `schema/types.ts` 与 `schema/validateContent.ts` 补齐 `blaster` / `glue` / 两个 `tnt` 布尔字段的校验。

## Testing Strategy

- .NET：`PartContentTests` 数量断言 267 + 新能力解析；`GameplayRulesTests` 新增：连锁（半径内 TNT 被点燃、半径外不）、`igniteOnImpact=false` 撞击不点火、blaster 冲量只作用于半径内动态体且自身存活、chainRadius 连锁；`GameRoomTests` 新增：含胶簇不裂缝、无胶簇照常裂缝。
- Web：`validateContent` 新字段（合法/非法各一）、`variantLabel` 纯函数、`slope.test.ts` 全量一致性。
- 手验：`--play` 启动，调色板每个基准件展开皮肤（贴图正确）、AlienTNT 开关引爆并连锁、BlasterTNT 推飞、异形蛋簇撞击不散。

## Boundaries

**Always**：皮肤不改变基准件物理；变体编号只追加；行为数值必须带原版证据（file:line 或 prefab 字段）；零协议改动。

**Ask first**：为特殊蛋/异形枪引入逐刚体重力或碰撞过滤（改 `IPhysicsWorld` 与两个后端）；把 TNT 爆炸半径/冲量从关卡配置迁到内容。

**Never**：用基准件能力冒充行为变体；给皮肤写隐式继承；静默丢弃解包失败的皮肤；改 PGFS/PGFC 布局。

## Success Criteria

1. `dotnet test PigForge.slnx`、`pnpm test`、`pnpm build` 全绿。
2. `content/parts.json` 267 条；每个有原版变体的基准件在调色板里都能展开皮肤，顺序与原版 `customPartIndex` 一致。
3. `tools/bple-textures/extract.mjs` 对全部 267 条映射输出 0 警告（`parts: 264/267`，3 个 null 为 ground-slab/terrain-box/ramp-plank）。
4. AlienTNT 撞击不点火、开关点火后连锁半径内普通 TNT；BlasterTNT 触发后推飞周围动态体且自身存活；含 AlienEgg 的簇在超阈冲量下不裂缝。
5. `docs/decisions/ADR-004-part-variants.md` 的 AlienTNT 描述修正为实测结论。

## Open Questions

- 特殊蛋（引力/反重力/幽灵）需要逐刚体重力与碰撞过滤：是否扩展 `IPhysicsWorld`（Bepu/Jolt 双后端）另开切片。
- 异形枪（`Part_GrapplingHook_06`）需要投射物子系统；是否做「弹体实体 + TTL + 连爆」另开切片。
- BlasterTNT 的扩张波是否需要逐 tick 复刻（当前一次性冲量近似）。
