# Spec: 建造多选（框选 + 批量操作）与 PGFA 错误语义

> 状态：Draft。消费 `docs/intent/multi-select.md`。
> 纯 Web 切片：无 PGFC/PGFS/内容/服务端改动；服务器权威与逐条校验不变。
> 取代：`docs/specs/advanced-building.md` 的「单选 v1」（Assumption 1）与 Open Questions 中「多选/框选另开切片」的多选部分；数值面板仍在范围外。

## Capability Map

| Module id | Responsibility | Depends on |
|---|---|---|
| marquee-select | 选择工具左键拖拽框选、Shift 加选、多选高亮、主选 | — |
| multi-actions | `Delete` / 方向键 / `R` 作用于全部选中自有可编辑零件 | marquee-select |
| ack-messages | PGFA `status`/`error` → 中文语义 | — |

Build order: `ack-messages` 独立可先落地；`marquee-select` → `multi-actions`。

## Assumptions（写进规格，实现不得另猜）

1. **平移改绑**：中键拖拽 = 相机平移（所有工具）；**选择工具**左键空白拖拽 = 框选；其他工具左键空白拖拽仍是平移。框选期间不移动相机。
2. **命中规则**：框选命中 = 实体**中心点**落在世界坐标矩形内（不含 footprint 精确相交，后续另开）。
3. **选择范围**：与单击一致——可选任意玩家或关卡实体（只读检查）；批量操作按 owner + Editing 门控，服务器仍逐条校验。
4. **修饰键**：`Shift`+点击零件 = 切换该零件选中状态；`Shift`+框选 = 并入现有选择（不取消）；普通点击/框选 = 替换；空白点击 = 清空。
5. **主选** = 选择序列的第一个；检查器显示「已选中 N 个」+ 主选详情。**指针拖拽变换仍只作用于抓取到的单个零件**（按下即把选择收敛为该零件）——批量指针拖拽不在本切片。
6. **批量键盘操作**：`Delete`/`Backspace` 删除全部选中自有可编辑零件；方向键（移动工具）与 `R`（旋转）对每个选中自有可编辑零件各发一条命令。
7. **非原子**：批量 = 每零件一条 PGFC（kind 1/6/2，实体升序），服务器逐条校验；部分失败**不回滚**，失败项进错误列表，成功项已生效。
8. **零协议改动**：不新增 PGFC kind、不改 PGFS 布局、不改内容 schema。

## 交互（唯一事实来源）

| 工具 | 空白左键按下 | 空白左键拖拽 | 空白左键松手 | 中键拖拽 |
|---|---|---|---|---|
| 放置 | 记录起点 | 相机平移 | 未移动 → `PlaceRequested` | 相机平移 |
| 选择 | 记录框选起点（**不**清空选择） | 画框（本地预览） | 未移动 → 清空；移动 → 框选 | 相机平移 |
| 移动 / 旋转 / 缩放 | 记录起点 | 相机平移 | — | 相机平移 |

命中零件时（左键）：单击 = 选择该零件（`Shift` 切换）；变换工具按下 = 开始该零件的变换拖拽（选择收敛为它）。框选进行中（选择工具）不触发相机平移。

## 状态与消息

```ts
// viewState.ts
selectedIds: number[];          // 有序，[0] = 主选；替换 selectedId
marquee: { minX: number; minY: number; maxX: number; maxY: number } | null;

// types.ts（GestureMessage）
| { kind: "SelectEntities"; entityIds: number[] }   // 替换 SelectEntity
| { kind: "Marquee"; rect: { minX; minY; maxX; maxY } | null }
```

- `editor/tools.ts` 新增纯函数 `entitiesInBox(entities, minX, minY, maxX, maxY): number[]`（中心点判定，实体升序）。
- 框选松手：`Shift` → 并集（保持既有顺序，新增按实体升序）；否则替换；空框 → 清空。
- 中键平移只改相机，不改选择。

## 渲染

- 所有选中零件画现有 `SELECT_STROKE` 方框（`drawFrame` 的 `selectedId` 参数改为 `selectedIds: readonly number[]`）。
- 框选进行中画半透明虚线矩形（`drawFrame` 新增末尾可选参数 `marquee`）。
- 渲染器仍是唯一碰 canvas 2D 上下文的模块。

## 错误语义（ack-messages）

`clients/web/src/live/ackMessages.ts`：

```ts
export function describeRejection(kind: CommandKind | undefined, status: number, error: number, sequence: number): string;
```

- 命令名：0 放置零件 / 1 删除零件 / 2 旋转零件 / 3 Start / 5 RESET / 6 移动零件 / 7 缩放零件 / 8 开关零件 / 9 开关类型。
- `status`：1 重复命令 / 2 序列过期 / 3 tick 过期 / 4 当前阶段不允许 / 5 规则拒绝（用 `error` 名）/ 6 未知命令。
- `error`（`ConstructionError`）：1 未知零件类型 / 2 角度非法 / 3 缩放非法 / 4 位置非法 / 5 占位超出上限 / 6 位置已被其他零件占用 / 7 零件数量已达上限 / 8 连接数已达上限 / 9 零件不存在 / 10 不是建造实体 / 11 目标位姿被其他零件挡住 / 12 零件已冻结 / 13 不支持的形状 / 14 不是你的零件 / 15 该零件没有开关。
- 未知码回落 `status=N error=M`；`playerSession.applyAck` 用 `pending` 里的 kind 生成消息。

## Testing Strategy

- `editor/tools.test.ts`：`entitiesInBox` 边界（边界上的中心点算命中、旋转/缩放不影响中心判定、空集）。
- `gesture/canvasGestures.test.ts`：框选发出 `SelectEntities`（按实体升序）；`Shift` 并入；空框清空；中键拖拽只发 `CameraChanged`；选择工具拖拽期间无 `CameraChanged`。
- `renderer/draw.test.ts`：多选高亮（每个选中一个方框）、框选矩形描边、中键平移不改选中（由 gesture 覆盖）。
- `live/ackMessages.test.ts`：每条映射 + 未知码回落 + 无 kind 的命令。
- 手验：浏览器双标签，框选→高亮→`Delete`/`R`；重叠放置看错误文案。

## Boundaries

**Always**

- 服务器权威、逐条校验；客户端零预测（框选只改本地选择状态，不发命令）。
- 批量命令按实体升序，便于抓包/日志对照。

**Ask first**

- 批量**指针拖拽**（需要多零件预览与 N 命令的失败语义，或新 kind 原子批处理）。
- footprint 精确框选、数值面板、复制粘贴。

**Never**

- 新增协议 kind / 改 PGFS 布局 / 改内容 schema（本切片零协议改动）。
- 让框选或批量操作绕过 owner + Editing 门控。
- 静默吞掉部分失败。

## Success Criteria

1. `pnpm test`、`pnpm build` 全绿；`dotnet test PigForge.slnx` 不受影响（无 .NET 改动）。
2. 选择工具左键拖空白画框 → 松手后框内零件全部高亮；`Shift` 再框 → 并入；空白点击 → 清空。
3. 中键拖拽在所有工具下都能平移相机，且不清空选择。
4. 框选 3 个自有零件 → `Delete` 一次删完（3 条 kind 1 命令）；框选后按 `R` 全部旋转（3 条 kind 2）。
5. 重叠放置/移动被拒时，错误列表显示中文语义（含命令名），未知码回落原始码。

## Open Questions

- 批量指针拖拽：多零件本地预览 + 松手 N 条命令（非原子）或新 kind 原子批处理——另开切片。
- 框选命中是否改为 footprint 相交（含旋转/缩放）：本切片按中心点。
- 框选是否只选自有零件（当前与单击一致，可选任意实体只读检查）。
