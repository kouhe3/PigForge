# Spec: 单人开发测试台（dev harness）

> 2026-10-19。**状态：已实现**（交付读数与测试见 §11）。解决的不是玩法，而是**验证**：现在客户端只连一个由服务端启动时选定的世界，换关卡/换场景要重启进程，
> 玩家/开发者每验一个功能都得从头摆一遍。原版关卡是单机内容（部分机关还是一次性的），不适合当联机玩法；
> 联机要做什么等社群输入。本片先做**一个人、一个 URL、一个功能**的测试台。

## 1. 目标

1. 打开客户端的一个 URL 就能进入**指定场景或指定关卡**，**不需要**摆零件、不需要重启服务端。
2. 场景文件可以**预先摆好零件**（并可选择自动 Start），让一次点击就能看到某个功能（开关、TNT、风扇、以后的绳）。
3. 同一个服务端进程可以同时服务多个测试会话，每个会话是**自己的房间**，互不干扰。
4. 不改动现有 `--play` 沙盒/联机行为；测试台是**本地开发模式**。

## 2. 非目标

- 玩家侧的关卡选择/进度/存档（`G74`/`G83`）——联机内容未定，先不做。
- 多人联机内容设计（原版关卡单机、单次触发机关不适合联机）；等社群回答「联机玩什么」。
- 把测试台暴露到公网；它只用于 `127.0.0.1` 的开发验证。

## 3. 现状与约束

| 事实 | 出处 |
|---|---|
| 一个 `PlayHost` 只建一个房间；`--level` 在启动时选定 | `src/PigForge.Server/PlayHost.cs:54-75` |
| 所有 `/play` socket 共享该房间，tick 循环只有一个 | 同上 `AcceptAsync`/`TickAsync` |
| `GET /level` 固定返回启动时解析的那份文档 | 同上 |
| 客户端从 URL 之外没有世界切换入口；连接后只发命令 | `clients/web/src/App.vue`、`live/playerSession.ts` |
| 同进程可创建多个 `GameRoom`（测试已这么做），每个自带物理世界与内容库 | `tests/PigForge.Server.Tests` |

## 4. 设计

### 4.1 模式

新增 `--dev`（可与 `--play` 同用）：

```
dotnet run --project src/PigForge.Server -c Release -- --play --dev
```

- `--dev` 时，**每个 `/play` 连接**按 URL 查询创建一个**私有房间**（一个连接一个房间，无共享）。
- 不带 `--dev` 时行为完全不变（共享沙盒/关卡房间）。

URL 形式（vite 代理会把查询串原样转给 `:5088`）：

```
ws://127.0.0.1:5088/play?scenario=<id>
ws://127.0.0.1:5088/play?level=<path under content/levels>
```

- `scenario` 与 `level` 互斥；都不给 → 默认场景 `sandbox`（= `terrain-v1.json`，沙盒模式）。
- `level` 允许的路径：`content/levels/` 下的相对路径，且解析后必须仍在该目录内（拒绝 `..`/绝对路径）。
- 未知 scenario/level → 以 400 拒绝 WebSocket 升级，控制台打印原因。

### 4.2 场景文件（`content/scenarios/*.json`）

```json
{
  "format": "pigforge.scenario",
  "schemaVersion": 1,
  "id": "switch-bench",
  "name": "开关 / 风箱 / 火箭",
  "description": "一次点击验证按钮与开关；零件已摆好",
  "level": "terrain-v1.json",
  "build": [
    { "partTypeId": 1, "x": 0, "y": 1, "angle": 0, "scale": 1, "mirrored": false },
    { "partTypeId": 40, "x": 0, "y": 2 }
  ],
  "start": false
}
```

- `x`/`y` 是**世界坐标**（与 `PlacePart` 命令一致；放格由关卡网格裁决）。
- `build` 由服务器在 `SetupFromLevel` 之后用 `ConstructionRules.Place(..., owner: 0)` 逐条放置；
  任何一条失败 → 房间构建失败（400/控制台错误），不静默跳过。
- `start: true` → 放置完立即 `room.Start()`，适合「打开就能看物理」的验证。
- 场景的关卡可以是没有 `build` 的 v1 靶场（`terrain-v1.json`）；也可以指向任意原版关卡。
- 场景文件进版本库；`tools/dev/scenarios.mjs --check` 校验（id 唯一、level 存在、partTypeId 存在、
  坐标有限、schema 版本）。

### 4.3 服务端接线（`PlayHost`，仅 `--dev`）

- `RunAsync` 增加 `dev` 参数；`AcceptAsync` 解析查询并创建 `DevSession`：
  `{ Guid Id, PlayClient Client, GameRoom Room, string LevelJson, Task Tick }`。
- 每个会话一个 tick 循环（60 Hz，`PeriodicTimer`），只把该房间的快照发给自己；房间/客户端互不共享。
- `GET /level` 读取同一条查询，返回该会话场景/关卡解析出的文档（CORS 同现状）。
- `GET /dev/worlds`（仅 `--dev`）：返回
  `{ scenarios: [{id,name,description,level}], levels: [{path, area, name}] }`，用于客户端下拉。
  levels 列表从 `content/levels/**` 枚举（277 + 2 靶场）。
- socket 关闭：`room.LeavePlayer`、`room.Dispose()`、停 tick；**一个连接的生命周期 = 房间的生命周期**。
- 并发：会话字典 `ConcurrentDictionary<Guid, DevSession>`；tick 只锁自己的房间；无跨房间锁。

### 4.4 客户端（`?dev=1`）

- 页面检测 `dev=1` → **直接切到「建造」并自动连接**（一个 URL 一个功能，不再点「建造 → 连接房间」），
  并显示一个开发抽屉（右下角，不影响正常 UI）：
  - 下拉：场景列表 + 关卡列表（从 `GET /dev/worlds` 拉）；
  - 选中 → 写 URL `?dev=1&scenario=<id>`（或 `&level=<path>`）并 `location.reload()`；
  - 「重开本场景」= 同样的 reload；「返回沙盒」= 去掉查询。
- WS 连接与 `GET /level` 都带当前查询串（`App.vue` 一处拼 URL）。
- 页面加载顺序不变：先 `GET /level`，再开 WS；两者必须看到**同一个**世界（查询串一致）。
- **dev 世界里的归属**：场景零件由服务器预置，客户端永远收不到它们的 `Place` ack。`?dev=1` 时把
  构建帧里的**所有实体**视为本玩家（可选中/旋转/开关），`Start` 也由「帧里存在动态零件」放行；
  非 dev 页面仍用 ack 归属。服务器侧的归属/权限不变（关卡房间 owner 0）。
- **已经被场景启动的房间**：`start: true` 的场景在客户端连接前就进了 Running，没有 Start ack 可学，
  所以**带 `scenario=` 的 dev 页面**收到非建造帧时把本地相位提升为 `materialized`；裸沙盒（无选择）不提升，
  否则会失去放置能力（沙盒房间本来就是 Running，本地相位仍应是 `editing`）。非 dev 只接受降级（G117）。
- 非 `dev=1` 时客户端行为不变。

### 4.5 场景与关卡的隔离

- 每个会话一个 `GameRoom` + 一个 `BepuPhysicsWorld`；内容库可共享（只读）。
- 场景里的零件 owner 0，与关卡房间一致（单机原版语义）。
- `--dev` 下不广播给其它连接；一个浏览器标签一个世界。

## 5. 需要新增/修改

| 文件 | 改动 |
|---|---|
| `src/PigForge.Server/PlayHost.cs` | `dev` 参数、查询解析、`DevSession`、每会话 tick/广播、`/dev/worlds`、`/level` 查询 |
| `src/PigForge.Server/Program.cs` | 解析 `--dev`，传给 `RunAsync` |
| `src/PigForge.Server/GameRoom.cs` | 可选：`SeedScenario(parts)`（或复用 `Submit(PlacePart)`；建议加一个显式方法，避免伪造命令序列影响 validator） |
| `content/scenarios/*.json` | 新目录；首批 3 个场景（见 §7） |
| `tools/dev/scenarios.mjs` | 校验场景（`--check`），可选 `--list` |
| `clients/web/src/dev/DevPanel.vue`（或 `App.vue` 内联） | 抽屉、列表、URL 切换 |
| `clients/web/src/App.vue` | WS/level URL 带上查询；`dev=1` 挂载面板 |
| `clients/web/src/live/playerSession.ts` | 不变（reload 后重建） |
| `docs/specs/level-content-v1` 等 | 无改动（场景不是关卡内容） |

## 6. 测试

### 服务端

1. **场景解析**：合法场景 → 房间构建成功、零件数正确；`start:true` → `Mode == Running`；坏 `partTypeId`/
   越界坐标 → 构建失败并报错。
2. **`/dev/worlds`**：列出全部 `content/scenarios/*.json` 与 `content/levels/**/*.json`；没有 `--dev` 时 404。
3. **路径安全**：`?level=../../etc/passwd`、绝对路径、`content/parts.json` → 拒绝。
4. **会话隔离**：两个 WS 同时连 `?scenario=a` 与 `?scenario=b` → 各自的 `GET /level` 不同、快照互不串。
5. **清理**：关闭 socket → 房间 dispose、tick 停止、无残留实体。

### 客户端

1. `?dev=1` 显示抽屉；`?scenario=x` 时 WS 与 `/level` 的 URL 都带 `scenario=x`。
2. 选择场景 → URL 更新 + reload（单测可用纯函数 `devUrl(current, selection)`）。
3. 非 dev 模式不显示抽屉、URL 不带查询。

### 实机

1. `--play --dev` + vite；浏览器打开 `?dev=1`，选 `switch-bench` → 零件已摆好 → Start → 开关可点。
2. 选 `tnt-bench` → Start → 炸；选一个原版关卡 → 地形/道具/建造栏正确。
3. 两个标签页分别选不同场景 → 互不影响；关闭一个 → 服务端日志显示房间清理。

## 7. 首批场景（验证现有功能，也作为模板）

| id | 关卡 | 内容 | start |
|---|---|---|---|
| `switch-bench` | `bench-v1.json` | 木框 + 发动机 + 风箱 + 火箭（都已放置，验证开关条/按钮） | false |
| `tnt-bench` | `bench-v1.json` | 木框 + TNT（验证点火/爆炸） | false |
| `sky-bench` | `bench-v1.json` | 木框 + 风扇 + 气球（验证升力/推力） | true |
| `rope-bench` | `bench-v1.json` | 左框 + 绳 + 右框（按钮剪中间 / 点击剪最近段，`docs/specs/rope.md`） | false |
| `rope-bench`（待 G63） | `terrain-v1.json` | 框 + 绳 + 框 + 重物（绳实现后加） | false |

坐标按 `terrain-v1` 的地板（y≈0）与 1 格网格写；构建失败即场景 bug。

## 8. 偏差 / 限制

- 一个连接一个房间：两个标签页不会在同一个世界联机（联机验证仍用 `--play` 共享房间）。
- 开发抽屉只读列表 + reload；不做热切换（热切换会引入房间替换/在途命令的竞态，reload 是确定性的）。
- `GET /dev/worlds` 与 `?level=` 只在 `--dev` 生效；生产/普通 `--play` 不暴露。
- 场景坐标是作者手写的世界坐标；不做「吸附到网格」的编辑器（场景校验会拒绝不存在的 partTypeId）。
- dev 页面会自动切到建造并连接；普通页面仍然是「回放查看器 → 建造 → 连接房间」的手动流程。
- 一个浏览器标签一个房间；切世界是整页重载（不做运行期房间替换）。

## 9. 分片

| 片 | 内容 | 验收 |
|---|---|---|
| **D1** | 场景格式 + `tools/dev/scenarios.mjs` + 三个场景 + 合法/非法校验测试 | 工具 `--check` 绿；坏场景被拒 |
| **D2** | `PlayHost --dev`：查询解析、每连接房间、`/level` 查询、`/dev/worlds`、清理 | 服务端测试 1–5 |
| **D3** | 客户端 `?dev=1` 抽屉 + URL 切换 + 非 dev 不变 | 客户端测试 1–3 |
| **D4** | 实机三段（switch/tnt/原版关卡）+ 两标签隔离 | §6 实机 1–3 |
| **D5**（随 G63） | `rope-bench` 场景 + 点击剪切验证 | 绳切片验收时用 |

## 10. 开放问题

1. `--dev` 是否默认关闭端口共享（同一端口同时服务共享/私有模式）？本规格：是，`--dev` 下全部连接私有；
   联机测试仍用不带 `--dev` 的进程。
2. 场景是否也要支持「建造模式下的预览」与「自动 RESET」？本规格只做 `start` 布尔；需要时加 `resetAfter`。
3. 是否把 `GET /dev/worlds` 的关卡列表分页/按 episode 分组？客户端先全部列出（280 条），可后加过滤。


## 11. 交付记录（2026-10-19）

**实现**：`ScenarioLoader`/`DevWorlds`/`PlayHost --dev`/`GameRoom.SeedPart`、`content/scenarios/` 三个场景、
`tools/dev/scenarios.mjs`、客户端 `live/devWorld.ts` + `dev/DevPanel.vue` + `?dev=1` 自动连接/归属规则、
`levelEndpoint` 保留世界查询、vite `/dev` 代理。

**读数**（真服务器 `--play --dev` + 真 Bepu + 真 Chromium）：

| 步骤 | 读数 |
|---|---|
| `GET /dev/worlds` | 200，**4** 场景、**280** 关卡文档、0 错误 |
| `GET /level?scenario=sky-bench` | 200，`contentVersion = bench-v1` |
| `GET /level?level=../parts.json` / `?bogus=1` | **400**（路径逃逸/未知查询） |
| WS `?scenario=sky-bench`（start:true） | phase 0；实体 = 4 场景件 + 1 关卡 actor（`bench-v1` 的地板） |
| WS `?scenario=tnt-bench` / `switch-bench` | phase 0x10；实体 = 各自场景件 + 1 关卡 actor；`Start` 后 switch 变 phase 0 |
| 浏览器 `?dev=1&scenario=switch-bench` | 自动进「建造」并连接；Start 可用；开关条 `1风扇 / 2火箭 / 3风箱 / 4小发动机` |
| 抽屉切 `sky-bench` | URL 变 `?dev=1&scenario=sky-bench`，自动连接即 **运行中**；开关条 `气球×2 / 风扇` |
| 抽屉切 `Level_05` | 地形/装饰/挑战 HUD 正常，phase 编辑中；控制台 0 错误 |
| `?dev=1&scenario=rope-bench` | Start 后 host `bodyId 0`、8 段、开关条 `1绳索`；红折线连接两框；按钮/点击剪切均无错误 |

**测试**：Server **184**（+6：场景校验/播种/拒绝/查询/清单/坏文件）Core 432 / Protocol 45 / Replay 11 /
Physics 96（75+21）/ web **432**（+12：`devWorld` 11、`playerSession` 2，`levelEndpoint` 改 2 条）；
`pnpm build` 绿；Release 0 警告 0 错误；`scenarios.mjs --check` 3 ok。

**发现的客户端规则**（已写进 §4.4）：dev 页面自动切建造并连接；dev 世界把全部实体视为本玩家；
`Start` 由帧里的动态零件放行；`start:true` 的房间由帧相位提升本地相位。
