# ADR-004: 零件变体——扁平 partTypeId + 内容层分组，行为差异走能力

## 状态

Accepted

## 日期

2026-09-09

## 背景

原版零件身份是 `(PartType, customPartIndex)`：`BasePart.TypeInfo`（`BasePart.cs:355-377`），变体注册表是 `GameData.m_customParts: List<CustomPartInfo>`（`CustomPartInfo` = 类型 + 该类型的变体列表）。皮肤不只是换图：

- TNT 有 7 个变体（`Part_TNT_01..07_SET`，索引 0..6）。02–05 是纯外观（`TNT.cs`）；06 是 `AlienTNT`（撞击不点火、只能由开关触发；半径/冲量与基准件相同——`INSettingsBExp.json` 的 `TNTExplosionRadius/Force` 与 `AlienTNTExplosionRadius/Force` 都是 1.0/2.0，prefab 的 `m_explosionRadius: 8`/`m_explosionImpulse: 25` 逐字段相同）；07 是 `BlasterTNT`（IN 扩展件，不在 `m_customParts` 里；一次性扩张冲击波，质量 2）。证据：`TNT.cs:130-182`（连锁引爆半径内任意 TNT）、`AlienTNT.cs:8-50`、`BlasterTNT.cs:123-212`，prefab 的 `m_partType`/`customPartIndex`/`m_mass`。
> 2026-09-09 修正：本 ADR 初版称 AlienTNT「半径/力度放大」不成立（见上）；`chainDetonate` 是全部 TNT 的原版行为，不是 AlienTNT 专属。实现见 ADR-006。
- 气球（`Balloon.cs:229`）、木箱/金属箱（`BasePart.cs:1152/1156`）、电容（`CapacitorPart.cs:17`）、彩色框（`ColoredFrame.cs:61-92`）等也按 `customPartIndex` 分支。

而 PigForge 的线格式没有变体位：PGFS 实体固定 68 字节、只有 `partTypeId:u32`（`src/PigForge.Protocol/SnapshotWire.cs:5-13,32`），PGFC PlacePart 只有 `partTypeId` + 位姿/角度/缩放（`CommandWire.cs:15,54-60`）。身份必须由 `partTypeId` 单独承载。

## 决策

1. **变体 = 独立的 partTypeId（扁平编号）**。不引入协议 v3、不改回放格式、不改 PGFS/PGFC 布局。新增变体按追加分配（当前基准 1–46，TNT 皮肤 47–50）。
2. **内容层可选分组字段**：`variantOf`（该变体所属的基准 partTypeId）+ `variantName`（显示名，可选）。二者只用于 UI 分组/显示，物理与规则仍按 `partTypeId` 查内容，服务器对变体无特殊逻辑。
3. **能力不继承**：变体条目自带完整定义（mode/mass/material/shapes/capabilities），不写隐式继承。变体与其基准的关系是"皮肤归属"，不是"派生"。
4. **引用约束三处强制**（JSON Schema、C# 解析器、TS 校验器）：`variantOf` 必须指向本文档中已声明的、且自身不是变体的零件；禁止自引用与链式变体（客户端调色板只需两层）。
5. **行为变体先解包贴图、后接能力**：`AlienTNT`/`BlasterTNT` 的贴图先进清单（id 51/52），能力字段与规则后补（ADR-006 已补齐）。**禁止**用基础 TNT 的能力冒充行为变体。
6. **贴图清单按 partTypeId 索引**，因此变体自动获得贴图，客户端渲染路径无需改动。解包器覆盖原版三种精灵系统：`Sprite.cs`（`spritemapping.txt` UV）、`UnmanagedSprite`（prefab 网格 UV）、`INSerializedSprite`（`Assets/TextAsset/<Atlas>_TextAsset.txt` 命名图集，左上原点）。

## 影响

- 每个皮肤在 `content/parts.json` 中占一条。当前：46 个基准件 + 223 个变体 = 269 条（TNT 家族 47–52；其余 53–269 覆盖原版 `m_customParts` 中属于 PigForge 基准件的全部 217 件）。
- 调色板按 `variantOf` 在基准零件下方展开皮肤按钮（`App.vue` 的 `.variants`）；`PALETTE` 仍只列基准零件，无 `variantName` 的皮肤显示「<基准件> #n」。
- `partTypeId` 空间随皮肤增长，追加分配即可；回放/快照中的皮肤就是普通零件。
- 行为变体（AlienTNT/BlasterTNT/AlienEgg 与参数变体）的能力语义与实现见 `ADR-006-part-variant-effects.md`。

## 参考

- 原版：`Assets/Scripts/Assembly-CSharp/{BasePart,CustomPartInfo,CustomizationManager,TNT,AlienTNT,BlasterTNT,Balloon}.cs`、`Assets/MonoBehaviour/GameData.asset`（`m_parts`/`m_customParts`）、`Assets/GameObject/Part_TNT_0*_SET.prefab`
- PigForge：`schemas/part-content-v1.schema.json`、`src/PigForge.Core/Content/{PartContentDocument,PartContentParser}.cs`、`clients/web/src/schema/{types,validateContent}.ts`、`clients/web/src/App.vue`
- `docs/decisions/ADR-002-runtime-rules-semantics.md`（行为语义来源）、`ADR-003-original-texture-assets.md`（贴图管线）
