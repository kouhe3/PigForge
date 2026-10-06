# ADR-028: 按钮与开关（`activation` 的真值）+ 快照的子实体标志（PGFS v5）

## 状态

Accepted

## 日期

2026-10-04

## 背景

2026-10-04 的两条用户实机报告把它们并成一条决策：

1. 「风箱和拳套箱预期是**按钮**而不是**开关**。现在是开关。」
2. 「拳套箱预期会弹出**拳头**，现在弹出的不是拳头。」

### 按钮还是开关：原版怎么定义

原版每个零件自己声明它的控件形态：`BasePart.HasOnOffToggle()`（`BasePart.cs:587`）为 `false` 的件是**按钮**，
为 `true` 的件是**开关**；`GetTriggerButtonInfo()` 把这个值原样塞进 `UIPartTriggerButtonInfo`（`BasePart.cs:1418-1421`），
UI 拿它决定画按钮还是画开关。两条路径的入口都是 `BasePart.OnButtonTriggered` → `ProcessTouch()`（`BasePart.cs:1428-1431`），
而 `ProcessTouch` **只由 UI 到达**（开关条按钮、键盘的 `ActivateOnePartOfType`、鼠标点零件）——碰撞事件从不调用它。

风箱和拳套都声明 `HasOnOffToggle() => false`（`Bellows.cs:55-58`、`SpringBoxingGlove.cs:81-84`），所以它们是**按钮**。

PigForge 的 `capabilities.activation: "toggle" | "trigger"` 一直是这两者的映射，但语义实现上有两处偏离：

- `trigger` 的按下被**门控扣住**：`RunBellows`/`RunRockets`/`RunGrapples` 先判底盘（或方向）门，不通过就 `continue`——
  于是开关条里那个件的 `Active` 永远是 1，**卡在开位、再点也关不掉**（`SetActive` 对同值早退）。用户报的「现在是开关」就是这个症状。
- 风箱被消费一次后 `BoostedRecently` 对「有条目的件」**永久为真**，按第二次不再响应——也不是按钮。
- 拳套的内容选了 `toggle`（因为 IN `SwitchableBoxingGlove = true`，`INSettingsBExp.json:504-508`），于是开关条把它画成一个真正的开关。

### 子实体的美术

`ADR-027` 让一个放置件可以带 N 个刚体/实体，但子实体在 `PGFS` 上**借宿主的 `partTypeId`**（当时 `flags` 的其余位是保留位）。
客户端按 `partTypeId` 取清单里的合成图，所以拳套弹出的手套被画成**第二个拳套零件**（含它的支架与弹簧线）——
原版不是这样：`SpringBoxingGlove.m_BoxingGlovePrefab`（`SpringBoxingGlove.cs:38`）指向 `BoxingGlove*.prefab`，它有自己的
`Visualization` 精灵（5 个皮肤各一张，`Resources/guisystem/sprites.txt` 里都在）。

## 决策

1. **`activation` 的语义 = 原版控件的语义**：
   - `toggle` = 开关：`Active` 是档位，保持到再次切换。
   - `trigger` = 按钮：**按下即动作、且一定被消费**。每条规则先 `TryConsumeButtonPress(entity)`（`GameplayRules.cs`，public，
     `GameRoom` 的拳套状态机也用它），**再**判该件自己的门控；门控拒绝时按下照样消失。
2. **按钮可重复**：按件自己的节流重臂。风箱用原版自己的周期 `0.8 s + 0.3 s inflate`（`Bellows.cs:64-67`，
   `BellowsState.ReadyAtTick`）；火箭/气球/TNT 烧完即止。
3. **物理接触不是按钮**：删除拳套把 `ContactStarted` 当触摸的路径（会「落地就出拳」）。
4. **风箱的推力轴也是零件自己的**：`Bellows.cs:84-87` 的 `transform.TransformDirection(m_direction)`，全部 8 个皮肤
   `m_direction = (1,0,0)`，施力点 `transform.position + vector × 0.5`。与风扇那条修正（`ADR-022` 2026-10-04 补做段）同源。
5. **内容改用按钮分支**：拳套 28（+242–245）写 `activation: "trigger"`。原版 IN `SwitchableBoxingGlove` 的那条档位分支
   **仍然实现**（`toggle`：off→on 出拳、on→off 中断回卷、`Active` 常留），由 `tools/bple-springs/apply-springs.mjs` 的一个常量选择——
   改回 `"toggle"` 即整体回退。这是**有意偏差**，写在 `docs/specs/boxing-glove.md` §2。
6. **PGFS v5：`flags` bit1 = 运行期子实体**（`SnapshotFrame.CurrentVersion = 5`；`GameRoom.SnapshotFlags`，三条发布路径共用）。
   客户端 `decodeSnapshot.ts` 读到它，`atlas.ts` 的 `subEntityTexture` 换成清单里的**子实体美术**；
   清单加 `subSprites`（`tools/bple-textures/extract.mjs` 顺着 `m_BoxingGlovePrefab` 的 guid 抽，schemaVersion 5）。
   没有 `subSprites` 的子实体（拉断弹簧的端点）继续借宿主的图。
6b. **停用的子实体不进快照**：原版拳套的 `InitilizeBoxingGlove()` 最后一步是 `m_BoxingGlove.SetActive(false)`（`SpringBoxingGlove.cs:215-222`），
   出拳才 `SetActive(true)`——被停用的 GameObject 既不渲染也不参与模拟。所以 `WindedUp` 时子实体**不出现在已发布的帧里**
   （`GameRoom._stowedSubEntityIds` + `FillConstructionOrder`/`FillEntityOrder` 过滤），客户端于是画盒子本身；出拳/回卷时才出现。
   刚体仍留在世界里（出拳的 yDrive 要从贴身位置把它推出去），所以这只是「不发布」，不是销毁。
   **只有 bit1 还不够**：子实体借宿主的合成图，静止时拳头恰好贴在盒子原点上，于是被画在盒子上面（用户实机报告「未触发时我看到的是拳套，原版是盒子」）——
   把停用的子实体从帧里去掉，客户端自然只画盒子。
6c. **子实体画在宿主之下**（用户实机报告「拳头收回后突然消失」）：原版同 sorting order 的精灵按到相机的距离排序，拳套的拳头挂 `z = 0.15`、
   盒子自己的美术在 `z = 0.1`（相机在 `z = -15` 看向 +z），所以**盒子盖住拳头**，收回时拳头是滑进盒子背后而不是凭空消失。PigForge 的渲染器跨实体
   只按快照顺序画（子实体 id 更大 → 画在上面），所以 `draw.ts` 新增 `drawOrder(entities)`：每个子实体紧挨着它所属的件（同 `partTypeId` 里最近的非子实体）
   之前画，其余顺序与实体数不变。两条用户报告合起来才是完整的：**静止不画（6b）+ 收尾画在盒子底下（6c）**。
7. **开关条把按钮画成按钮**：`App.vue` 只对 `toggle` 组加 `on` 高亮，`trigger` 组用虚线边框、不做常亮，
   `aria-pressed` 也只给 `toggle`。

## 取舍与后果

- **换来的**：按一下响一下；门控拒绝不再把开关卡在开位；拳头画成拳头；两端的「按钮/开关」语义与原版的
  `HasOnOffToggle()` 一一对应，内容一眼可查。
- **代价**：PGFS 版本从 4 升到 5——客户端与服务器必须同批升级（仓库内同批改，`SNAPSHOT_VERSION` 与 `CurrentVersion` 两个常量）。
  旧客户端会以「version is unsupported」拒绝整帧，这是有意的（宁可拒绝也不画错）。
- **仍然存在的偏差**：风箱的**力**仍是「一次 puff = 一个总冲量」（内容值 8.0 / 32.0），而原版是 0.5 s 内按
  `(1 - (1 - t/0.5)²) × m_boostForce` 逐帧施加（`m_boostForce` prefab 里是 30，alien 皮肤 120）；数值来源与逐帧斜坡都记在
  `tasks/original-vs-implemented.md`（G98）。拳套的弹簧线视觉仍未做（`docs/specs/boxing-glove.md` §7）。

## 参考

- 规格：`docs/specs/play-part-switches.md`（Assumption 2/6/9）、`docs/specs/boxing-glove.md`（§2/§3/§5/§6.1/§7）
- 原版：`BasePart.cs:587,1418-1431`、`Bellows.cs:50-118`、`SpringBoxingGlove.cs:38,81-84,263-278`、`INSettingsBExp.json:504-508`
- 代码：`src/PigForge.Core/Runtime/GameplayRules.cs`（`TryConsumeButtonPress`、`RunBellows`）、
  `src/PigForge.Server/GameRoom.cs`（`RunGloves`、`SnapshotFlags`）、`src/PigForge.Protocol/SnapshotWire.cs`（v5）、
  `clients/web/src/schema/decodeSnapshot.ts`、`clients/web/src/renderer/atlas.ts`、`clients/web/src/App.vue`、
  `tools/bple-springs/apply-springs.mjs`、`tools/bple-textures/extract.mjs`
