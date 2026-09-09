# PigForge Web 2D 客户端规格

> 状态：已冻结（v1）。本规格覆盖 `clients/web/` 下的浏览器 2D 客户端。定位与 `unity/` 参考工程同级：只消费版本化 Schema 的显示端，不进任何服务端编译依赖。

## 1. 定位与边界

客户端是**纯显示与预测端**，遵守既有 ADR 与无头规格的约束：

- 永不提交权威位置、断裂、胜负；只提交协议命令（PlacePart / RotatePart / RemovePart 等）。
- 不引用 .NET 程序集；与服务器共享的只有 `schemas/` 下的版本化 JSON Schema 与未来的快照二进制格式。
- 渲染为纯 2D：正交视角取快照 x/y，z 恒为 0，无视深度与 3D 相机。
- 无复杂着色器需求：渲染按 partTypeId 摆放形状/贴图即可，不引入 WebGL 引擎框架。

生命周期分两个形态：

| 形态 | 输入 | 依赖 | 用途 |
|---|---|---|---|
| A：回放查看器 | `physics-replay-v2` JSON + `part-content` JSON（本地文件） | 无，纯前端 | 调试回放、后端差异对比、内容格式验证；同时是开发工具 |
| B：实时客户端 | 服务器快照流 + 命令回执（Phase 4/5 交付后） | WebSocket 连接 | 玩家建造、启动、观战；显示 + 本地预测 |

形态 A 与形态 B 共享渲染层、UI 层；仅数据源与连接层不同。

## 2. 技术栈（已决策）

| 层 | 选择 | 理由 |
|---|---|---|
| 框架 | Vue 3 + TypeScript + Vite | `v-model` 原生满足表单数据绑定需求；SFC 适合"一画布多面板"结构 |
| 渲染 | 原生 Canvas 2D | 贴图按 x/y 摆放、无着色器；渲染器收敛在单模块，实测不足时可替换为 PixiJS |
| UI 组件库 | 形态 A 暂不引入；表单形态引入 Naive UI（按需） | 查看器仅播放控制与数字输入；避免提前背上组件库样式约定 |
| Schema 类型 | `json-schema-to-typescript` 从 `schemas/*.schema.json` 生成 | 单一事实来源：C# DTO、JSON Schema、TS 类型三者由 Schema 链接 |
| 状态管理 | Pinia，按域拆 store | 每个面板/领域一个 store，禁止大一统 EditorState |
| 文本查看/编辑 | CodeMirror（M2 引入） | 禁止自研文本编辑器 |
| 包管理 | pnpm | 仓库级安装速度与磁盘占用最优 |

## 3. 模块划分

```text
clients/web/
├── src/
│   ├── schema/          # 生成的 TS 类型 + 加载/校验（语义对齐 PartContentParser 与 ReplayDocumentValidator）
│   ├── gesture/         # 唯一监听 canvas 指针事件的模块：平移/缩放/拖拽/框选 → 语义化消息（TS 判别联合）
│   ├── renderer/        # 唯一碰 canvas 2d context 的模块：相机（平移/缩放/跟随）、按 partTypeId 画形状/贴图
│   ├── playback/        # tick 时钟、播放/暂停/逐帧/进度拖动、速度倍率
│   ├── diff/            # M2：双回放叠加渲染 + 差异 tick 高亮（可视化 ReplayComparer 语义）
│   ├── stores/          # Pinia store：playback、camera、selection、content、replay 文档
│   └── app/             # Vue 组件：时间轴、实体检查器、内容库面板、菜单/命令面板
```

### 3.1 硬性架构约束

1. **renderer 是唯一调用 canvas 2D context 的模块**；输入只有"某一帧的实体数组 + 相机状态"普通对象。形态 B 换数据源时 renderer 零改动。
2. **gesture 是唯一监听 canvas 指针事件的模块**，产出类型化语义消息（`MoveObjects` / `RotateObjects` / `CameraChanged` …），禁止把高频指针事件直接接入 Vue 响应式系统。渲染帧数据与相机内部状态存放于普通对象，处于响应式系统之外，由 playback 手动推送。
3. **回放与内容在加载期校验**：format/协议版本/内容版本/帧数对齐/逐 kind 形状字段，语义与 Core 侧解析器一致；坏文件在渲染前拒绝并给出全部错误列表（"启动前拒绝"原则的 Web 版）。
4. **命令与菜单用 TS 判别联合类型分发**，禁止字符串路由。
5. **禁止自研通用原语**：文本编辑（CodeMirror）、组件库（Naive UI）、差异比较等使用现成生态；自研仅限领域逻辑（渲染器、手势桥、回放时钟）。
6. **渲染形状而非贴图起步**：第一版按 content 的 box halfExtents 画矩形、sphere 画圆（与物理形状一致）；贴图映射做成可选资产清单（partTypeId → 图像路径），属于内容层扩展，不阻塞。

## 4. 数据契约

- 回放文档：`schemas/physics-replay-v2.schema.json`（TS 类型生成来源）。
- 内容文档：`schemas/part-content-v1.schema.json`。
- 示例数据：`content/parts.json`、`unity/PigForge.UnityReference/replays/unity-reference-replay.json`。
- 差异报告（M2 可选输入）：`ReplayComparer` 输出的差异序列（后续如有需要可版本化为独立 Schema，M2 先用内存结构）。

## 5. 里程碑

### M1 回放查看器（形态 A 最小闭环）

- 文件加载（回放 + 内容，拖拽与文件选择器）→ 加载期校验。
- Canvas 渲染：地面/动态体按形状绘制，相机平移/缩放，实体检查器（选中实体的位姿/速度）。
- 播放控制：播放/暂停/逐帧/进度拖动/倍速；事件时间轴标记（ContactStarted / EntityDestroyed 等）。
- 验收：加载 Unity 参考回放与 Bepu/Jolt 导出回放均可正确播放与检查；非法回放被拒绝并显示错误列表。

### M2 差异视图 + 贴图映射

- 双回放同屏叠加渲染，差异 tick 高亮，事件流对比列表。
- 贴图资产清单与 sprite 渲染路径（已实现：`tools/bple-textures/extract.mjs` 生成 `part-textures.json` + 图集，`renderer/atlas.ts` 加载，`renderer/draw.ts` 按 partTypeId 绘制；资产不入库、缺失时回退形状渲染，见 `docs/decisions/ADR-003-original-texture-assets.md`）。
- 验收：Unity/Bepu/Jolt 三方同一场景回放的可视化对比可用。

### M3 实时客户端（依赖 Phase 4/5）

- 最小能玩切片见 `docs/specs/minimal-playable.md`（斜坡关、PGFC 命令、无预测、无 keep）。
- 本里程碑全文（Naive UI、预测回滚、完整建造表单）仍有效，但不阻塞最小能玩。

## 6. 测试与质量

- schema/ 加载校验：用 Core 测试同款的非法样本集（GUID 字段、坏版本、帧数不对齐）做 vitest 单测。
- renderer：快照像素无关的纯函数几何计算（世界坐标 → 屏幕坐标）单测；渲染快照用 Playwright 截图做冒烟（可选）。
- gesture/playback：合成指针事件序列驱动，断言语义消息序列。
- CI 可运行 `pnpm build && pnpm test`；与 .NET 测试相互独立。

## 7. 与仓库结构的关系

```text
clients/web/            # 本规格的全部产出（不进入 PigForge.slnx）
schemas/                # 既有 Schema 是唯一共享契约
content/                # 既有内容样本直接作为 M1 测试数据
unity/                  # 保留参考工程；Web 客户端不依赖它，仅共享其导出的回放样本
```

依赖方向：

```text
clients/web ──读取──> schemas/ + content/ + replays 样本
clients/web ──(M3)──> PigForge.Server 快照流（版本化二进制协议）
clients/web ──X────> PigForge.Core / Server / Physics.* 程序集（禁止）
```

## 8. 设计溯源

- 分层纪律与手势桥模式参照 `C:\tmp\badpiggies-editor`（Dioxus 版编辑器）验证有效的部分：UI-free core、canvas 交互与框架状态解耦、重计算出 UI 线程。
- 该项目证明无效而本规格明确规避的部分：自研文本编辑器、无组件库手写全部交互原语、大一统编辑器状态对象、字符串路由命令、超配 GPU 渲染管线。
