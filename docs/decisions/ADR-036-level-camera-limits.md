# ADR-036：关卡内容 v5 —— 相机界（`m_cameraLimits`）是猪的边界，出界回建造态

- 状态：**已接受**（2026-10-08）
- 相关：`ADR-033`（关卡 v2 的 `terrain` + `GET /level` 侧通道）、`ADR-034`（fill 进 v3）、
  `ADR-035`（边缘条带进 v4）、`docs/specs/original-level-pack.md`（格式、分解与开放问题）、
  `docs/specs/level-terrain-visuals.md`、`docs/specs/multiplayer-sandbox.md`
- 差距：`G78`（失败条件）、`G111`（出界的判定源与后果）

## 背景

第二十八轮查原版源码时把「出界」这条查清了（`G78`）：原版**有**出界机制，矩形来自**关卡自己**的
`LevelManager.m_cameraLimits`（`topLeft` + `size`，写在关卡文件的 `PrefabOverrides` 文本块里），
检查的是**猪自己的 transform**，后果是**回建造态**：

```csharp
// Pig.cs:396-403（CheckStopped，只在 gameState == Running 时跑）
if (!INSettings.GetBool(INFeature.CancelPigBoundsDetection) && m_checkCameraLimits) {
    LevelManager.CameraLimits currentCameraLimits = WPFMonoBehaviour.levelManager.CurrentCameraLimits;
    if (position.y < currentCameraLimits.topLeft.y - currentCameraLimits.size.y
     || position.x > currentCameraLimits.topLeft.x + currentCameraLimits.size.x * 1.1f
     || position.x < currentCameraLimits.topLeft.x - currentCameraLimits.size.x * 0.1f) {
        EventManager.Send(new PigOutOfBounds());
    }
}
```
```csharp
// GameMode.cs:384-387
protected virtual void OnPigOutOfBounds(Pig.PigOutOfBounds data) {
    levelManager.SetGameState(LevelManager.GameState.Building);
}
```

矩形是「左上角 + 尺寸」并且**向右向下**铺（`IngameCamera.cs:599` 就是这么建 `Rect` 的），x 两侧有
**不对称的 10% 余量**（右 `×1.1`、左 `×0.1`），而且**没有上界**；`CancelPigBoundsDetection` 在声明默认档
是 `false`（检测开），A/B 档才关掉。`SetGameState(Building)` 从 `Running` 走的是
`StopRunningContraption()` + `ContraptionProto.SetVisible(true)`（`BaseGameMode.cs:204-266`），即
**同一个载具回到建造位、可以再 Start**。

PigForge 当时的做法完全不同：`MapBounds` = 地形 AABB + 25 m 余量（自造），检查**承载猪的刚体**
（框里装猪时那是簇体中心），后果是 `Phase = Failed`（靶场房间还会顺带 `RestartRequested`），而
`PlayHost` 只在 `Phase == Playing` 时 tick ⇒ 房间**冻结**在一个既不是失败页也不是建造态的相位上。

`PrefabOverrides` 是关卡文件里**每实例一段的文本**（`ObjectDeserializer` 格式，`LevelLoader.cs:198-208`），
277 关**全部**带（1795 条 / 2.08 MB），实测其中 **277/277** 关的 `LevelManager` 块带
`m_cameraLimits`，且 277 个矩形互不相同。

## 决策

### 1. 内容 v5：`cameraLimits` 从 `PrefabOverrides` 读出来，不许手写

`tools/bple-levels/lib/overrides.mjs` 解析那段文本（制表符缩进的 `Type name [= value]` 树），
按 `ObjectDeserializer.ReadFile` 的规则先核对根 `GameObject <name>` 与本实例标签一致，再取
`Component LevelManager` 的 `m_cameraLimits`；数值按 `float.Parse` → `float` 的语义取
`Math.fround`，所以内容里的每个数都是原版运行期那个 float32。

```json
"cameraLimits": { "topLeft": [-10.46, 13.45], "size": [56.3, 24.7] }
```

v5 的文档**必须**有它，v1–v4 出现即硬错误（服务端解析器与客户端校验器同一条规则，与 v3 的
`fill`/`collider`、v4 的 `curve` 门控一致）。277 关已全部重写（第二次运行 0 changed）；
`extract-levels` / `build-levels` 都断言「有相机界的关卡数 == 关卡数」。

保留原版的 `topLeft` + `size` 形态而不是折成 min/max：那条判定本身是不对称的（`×1.1`/`×0.1`、
没有上界），折成矩形会把这两个事实丢掉。

### 2. 猪的边界 = 关卡自己的相机界，判定在**猪自己**的 transform 上

`CameraLimits`（`PigForge.Core`，与 `GameplayZone` 并列）把三条比较逐字实现为
`Contains(PhysicsVector3)`；`GameplayRules.CheckObjectives` 在猪离开它时把该猪记进
`GameplayTickOutput.PigsOutOfBounds`，位置用 `ResolvePartFrame`（刚体位姿 × 成员局部偏移）取，
而不是刚体中心 —— 猪可以坐在载具的任意位置，两侧都可能先越界（有测试钉住两个方向）。

没有相机界的文档（PigForge 自造的 v1 `slope-v1`/`terrain-v1` 靶场房间）保留旧的
`MapBounds` → `Failed`：那是 PigForge 自己的房间，语义由 PigForge 定，靶场的用例也继续有意义。

### 3. 后果 = 回建造态，即房间的 `Retry`

非沙盒关卡房间是**单车循环**：`Mode == Running` 时收到「有猪出界」，房间调 `Retry()`
（Running → Building，把 `Start` 时捕获的布局按建造位放回去、重新 spawn 关卡演员），房间随后
**停在建造态等下一次 Start** —— 与原版 `SetGameState(Building)` 的观感一致，而不是停在 `Failed`。

沙盒房间不受影响：`ObjectivesEnabled = false`，它自己的 `ResetPlayersOutOfBounds`（用 `bounds`
逐个玩家回收）继续跑。

## 影响

- **客户端**：`levelContent.ts` 认 v5、校验 `cameraLimits`；**取景改用关卡自己的相机矩形**
  （`cameraLimitsRect`，即原版相机被限制的那块），`bounds` 仍是客户端自己的地形盒子
  （画出来的矩形 + 没有相机界时的兜底）。实测（真服务器 + 真 vite + 真 Chromium，`Level_05`）：
  地形渲染跨度 **883 px**，相机界取景预测 **883.3 px**（bounds 取景会是 471.8 px）。
- **`bounds` 的语义收窄**：它从「猪的边界」变成「客户端的盒子 + 沙盒回收的盒子」，文档里改口径。
- **已知偏差**：`CheckStopped` 只在 `gameState == Running` 时检测（我们只在房间 tick 时检测，等价）；
  `m_checkCameraLimits`/`CancelPigBoundsDetection` 按声明默认档（开）；原版 `retries++` 与教程/广告
  门控（`BaseGameMode.cs:214-243`）不实现。
- **证据**：详见 `docs/specs/original-level-pack.md` §7 与本文的实测段（`PigsOutOfBounds` 的
  非空验证、真实房间的实机读数）。
