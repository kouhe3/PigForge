# ADR-021: 连接状态决定物理体 —— 条件支架实心/隐藏，滑翔翼 body 两态

## 状态

Accepted

## 日期

2026-10-03

## 背景

ADR-014 让条件精灵按连接状态显示、ADR-017 让条件支架参与建造贴合与连接，但两者都把条件支架**排除在物理体之外**，滑翔翼的 body collider 也冻结成 prefab 里的一个固定盒（两 ADR 都记为「未做」）。原版不是这样：

- 每个 `ChangeVisualConnections` 覆写不只开关节点，还让**节点上的 collider 跟随状态**。`Rocket.cs:162-165` 明写 `m_*Attachment.GetComponent<BoxCollider>().isTrigger = !m_*Attachment.activeInHierarchy`——**显示的那个是实心碰撞体，隐藏的那个是 trigger**。`TNT.cs:86-108`（`BlasterTNT : TNT`）、`SpotLight.cs:70-105`、`GrapplingHook.cs:218-255` 只 `SetActive`，而 GameObject 失活即 collider 停用，净效果相同（两处 `isTrigger` 的 grep 只在 `Rocket.cs:157-160` 命中，其余族靠 `SetActive`）。
- 滑翔翼 `Wings.cs:62-71` 改的是**自己的根 BoxCollider**：

  ```
  if (!flag3) { center.y = 0.05f;  size.y = 0.3f; }   // 只有上支架
  else        { center.y = -0.15f; size.y = 0.6f; }   // 下支架（默认/无连接）
  ```

  即「只有上支架时是薄的、偏上；否则是厚的、偏下」，`size.x` 不变。prefab 序列化的 `center.y -0.15`、`size.y 0.6122213`（`Part_WoodenWings_01_SET`，`tools/bple-shapes` 导入为 `halfExtents [0.95, 0.3061, 0.75]`）**正是脚本运行前的值**——脚本每次都会覆盖它，所以运行中的游戏是 `0.3`/`0.6` 与 `0.05`/`-0.15`。

判定输入仍是部件自身帧里的侧别，与 ADR-014 的精灵门控同源：客户端 `renderer/connectionVisuals.ts` 已经算好 `connectableSides` + `conditionalSpriteVisible`，服务端要做的是同一件事的镜像。

## 决策

1. **规则进内容：part-level `connectionVisual`。** 哪个脚本挂在该 prefab 上决定它的条件 collider 服从哪条公式，这个事实此前只存在于 sprite manifest（`tools/bple-textures/extract.mjs` 从 prefab 的 `m_Script` 推导）。新增 `tools/bple-connections/apply-connections.mjs` 把它**原样复制**进 `content/parts.json`（43 件：rocket 系 18 → `attachmentFallback`、TNT 系 6 → `attachmentPlain`、spotlight/grapple 系 10 → `attachmentEight`、glider 系 9 → `frame`），幂等且可 `--dry-run`；schema、`PartContentParser`（字符串→`ConnectionVisualKind`）、`PartContentDocument`、客户端 `types.ts`/`validateContent.ts` 同步。**没有**从形状种类猜规则：一个只有 `condition.kind === "attachment"` 的零件必须声明 `connectionVisual`，否则解析即失败（交叉校验），免得不带规则的条件件在启动时静默丢掉全部支架。真值来源仍是 prefab 上的脚本，manifest 与内容都是它的下游产物（与 `frame` 形状的 sprite-bbox 复制同一模式）。
2. **服务端镜像 `ConnectionShapes`**（`src/PigForge.Core/Construction/ConnectionShapes.cs`），逐条对齐客户端的 `connectionVisuals.ts`：
   - `ConnectableSides`：对本件与邻件的**对齐盒**做接触判定（沿用客户端 `snapBoxOf` 的语义：全部形状的并集 AABB，带 `frame` 的件改用支架盒；容差 `ContactTolerance = 0.06`），可焊判据取原版 `Contraption.cs:690`（两端非 `none`、至少一端 `source`），贴合轴决定世界侧、再按 yaw 反旋转回零件自身坐标系（`LOCAL_ORDER` 与四分之一圈取整照抄）。
   - `ConditionVisible`：四族公式照抄客户端——`attachmentFallback`（`Rocket.cs:153-156`，bottom 兼作「无其它侧」幽灵）、`attachmentPlain`（`TNT.cs:94-106`，各侧独立无回退）、`attachmentEight`（对角仅在 45° 朝向，正交反之，另有 bottom 兜底）、`frame`（支架是精灵对不是 collider，恒 false）。
   - `SpawnShapes`：非条件形状（滑翔翼按状态改写根盒）+ 可见的 `attachment` 形状；`frame` 支架（ADR-018 的 art 派生对齐盒）**永不**进物理体。
3. **启动时解析一次，装配两处同源。** `PartContentLibrary` 新增 `PlaceShapes(partTypeId, scale, shapes)` 与 `DescribeWheel(..., shapes)` 重载（形状→物理的映射只有一份），`CompoundAssembler.BuildCluster` 用它算体积质心、`CompoundCluster.CreateBodyDefinition(content, construction, ...)` 用它建子形状——**两者必须取同一套形状**，否则 Bepu 的 compound recentring 会把整个装配体挪偏。连接布局在房间运行期不变（原版网格邻接同样不变），seam 断开后重建 body 得到相同形状，所以每次现算即可。
4. **滑翔翼两态取脚本值**：`thick` → `halfExtents.y 0.3`、`offset.y -0.15`；`thin` → `0.15`、`+0.05`；x/z 与半宽不变。prefab 的 `0.3061`（→`size.y 0.6122213`）是脚本覆盖前的序列化值，只保留在**内容**里供占格/连接几何使用；物理体用脚本值。`flag3 = flag2 || !flag` 在自身帧里等于「下/左/右任一可连 **或** 上/左/右都不可连」。
5. **不动围格与连接几何**：`PartFootprint._body` 仍是 ADR-020 的声明格盒，`_all`（含全部条件形状）仍是连接接近/客户端贴合的依据。本轮只改**物理体形状**。

## 影响

- `content/parts.json` 43 件多一行 `connectionVisual`；`schemas/part-content-v1.schema.json` 新增 `$defs.connectionVisual`（并改写 `shapeDefinition.condition` 的说明——「physics ignores both」已不成立）；`PartContentParser` 白名单 + 交叉校验；`clients/web/src/schema/{types,validateContent}.ts` 同步；`playParts.generated.json` 重新生成（`parts: 267`，`--check` 绿）。
- 物理体随连接状态变化：孤立火箭只多出 bottom 幽灵支架（0.25×0.14×0.5，位于 −0.37）；有邻居时变成对应侧的支架。滑翔翼的根盒高度在 0.6↔0.3 间切换，体积质心随之变化（body 位姿由同一解析结果算出）。
- 回归：Core 218（新增 `ConditionalShapeTests` 9 例；`ConstructionRulesTests.AConditionalBracketConnectsWithoutOccupying` 的合成内容补上规则）、Server 99、Protocol 39、Replay 11、Physics 32（非 Jolt）+ 6（Jolt）、客户端 224；确定性双跑哈希用例不变仍绿（只改了形状集合，没有改 tick 语义或迭代顺序）。新测试在改动前**失败 6/9**（把 `ConnectionShapes.Resolve` 临时退回「只取非条件形状」复现）。
- **实机**（`dotnet run --project src/PigForge.Server -c Release -- --play` + 裸 WS 客户端，用客户端自己的 `encodeCommand`/`decodeAck`/`decodeSnapshotFrame`；每轮重启服务）：
  - 木块(1) `@(-11,10)` + 火箭(13) `@(-10,10)`：`PlacePart`×2 与 `StartSimulation` 全部 `status 0 / error 0`，实体数 11（9 关卡 + 2 放置）恒定；tick 180→360 从 y 9.25 落到 −0.66 并在斜坡上滑动（未穿地，地面顶 −3.0）。
  - 木框(1) `@(1,0)` + 木滑翔翼(31) `@1.7404`（ADR-018 吸附位）：三条 ack 全 `0/0`，实体数 11，tick 210 起静止于 y −2.5（地面顶上 0.5，即 1×1 件的正常停放高度）。

## 未做

- **spotlight/grappling hook/TNT 的条件 collider 不在内容里**：`tools/bple-shapes/apply-shapes.mjs` 对「所有 collider 都是支架」（spotlight 的 8 个）的 prefab 退回已写形状，TNT 的支架节点也没有非 trigger collider，所以 `attachmentEight`/`attachmentPlain` 规则在今天的目录里**没有可作用的形状**（规则本身已实现并被单测覆盖）。补形状需要改 `tools/bple-shapes`（本切片未授权），届时规则已就位。
- **条件精灵的 8 向兜底行**仍是 ADR-014 的语义实现（`SpotLight.cs:101-104` 的编译器痕迹按「八向都检查」写）。
- **连接方向（`m_jointConnectionDirection`）仍未进焊接判据**（ADR-014/ADR-020 的既有缺口），本轮的侧别判定沿用客户端的几何接触，与 `CanMergePair` 一样只看 joint type。

## 参考

- 原版依据：`Rocket.cs:139-165`、`TNT.cs:86-108`、`SpotLight.cs:70-105`、`GrapplingHook.cs:218-255`、`Wings.cs:41-75`、`Contraption.cs:690`、`BasePart.cs:735-776`
- 代码：`tools/bple-connections/apply-connections.mjs`、`src/PigForge.Core/Construction/ConnectionShapes.cs`、`src/PigForge.Core/Content/PartContentLibrary.cs`、`src/PigForge.Core/Construction/CompoundAssembler.cs`、`src/PigForge.Server/GameRoom.cs`、`clients/web/src/renderer/connectionVisuals.ts`
- 测试：`tests/PigForge.Core.Tests/ConditionalShapeTests.cs`、`ConstructionRulesTests.cs`、`CompoundAssemblerTests.cs`
- 相关：ADR-014（条件精灵，本 ADR 补上它的决策 3 遗留）、ADR-017（条件支架参与建造，本 ADR 补上它的决策 5 遗留）、ADR-018（`frame` 支架的由来）、ADR-020（占格取声明格盒，本 ADR 不改）
