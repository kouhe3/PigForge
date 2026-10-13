# ADR-026: 玩家生命周期等于连接，RESET 回到 START 之前的布局

## 状态

Accepted

## 日期

2026-10-04

## 背景

多玩家沙盒（`docs/specs/multiplayer-sandbox.md`）原来的两条规则是：

1. **玩家断开后零件留在世界，任何其他玩家都不能回收**；重连带上同一个 `?session=<id>` 才恢复该 player id 与归属（`PlaySessions`）。
2. **RESET（PGFC kind 5）销毁该玩家全部实体与布局**，玩家回到「空场地」的编辑态，要玩就得重新摆一遍。

实测下来两条都站不住：

- 规则 1 是**孤儿零件的制造机**。player id 按连接分配、进程内不复用，`?session=` 只在**一次页面加载**的内存里：刷新页面、换标签页、客户端换了 URL 参数，都拿到新 player id，于是上一批零件成了「无主的、谁都删不掉的」实体，永久占着格子。用户实测的「丢失连接重连之后老零件清不掉」就是这条。
- 规则 2 让 RESET（以及出界清理，它走同一条路径）变成惩罚：摆好的车一旦跑飞或被炸掉，玩家要重新摆一遍才能再试一次。
- 用户明确：**零件跨断连留在世界这个玩法目前没有需求**。

## 决策

### 1. 连接即玩家的生命

- `PlayHost` 每接受一条 `/play` 连接分配一个新的进程内唯一 player id，**接受后连接断开即调用 `GameRoom.LeavePlayer(playerId)`**：该玩家的零件（实体 + 刚体 + 关节 + 占格）离开世界，捕获的布局、`SandboxPlayers` 条目与 `CommandValidator` 的序列记录一并清除。重连是**新玩家**、空场地。
- **删掉 `?session=` / `PlaySessions`**：身份不再需要跨连接恢复，保留它只会让「断开后零件是谁的」出现两种解释。客户端 `live/playSession.ts` 与其测试一并删除，`App.vue` 每次连接与每次掉线都 `player.reset()`（归属列表与相位都跟着服务器走）。
- 客户端不再声称「重连保留归属」：`live/playerSession.ts` 的 `reconnect()` 删除（它保留 `ownEntityIds` 的语义已经错误）。
- `PlayHost` 的清理是**无条件**的（不做「同 player id 还有别的活连接就跳过」的竞态守卫）：行为只由「socket 关了」决定，客户端与服务器对此的结论永远一致。

### 2. RESET = 把布局放回 START 之前

- `GameRoom.StartPlayer(playerId)` 在装配前**捕获一份布局**（件、位姿、缩放，实体升序），存进 `_startLayoutByPlayer[playerId]`。
- `ResetPlayer(playerId)` 不再删零件：
  1. 逐个（升序）`_rules.CleanupEntityStores` + `UnbindEntity(destroyBodyIfOrphan: true)` —— 刚体与运行期规则状态（开关、引信、动力）清掉，**建造实体留在 `ConstructionRules` 里**，占格保留；
  2. 该玩家**在运行中被摧毁的件**（爆炸、断缝、被 `Forget`）按捕获布局重建（新实体 id）；
  3. 幸存件按内容重新注册角色（`RegisterPlacedRole`，开关回到关、引信复位），再 `SyncEngineEnclosure`/`SyncChassisAnchors` 重发门控；
  4. `MarkEditing` + `DropOrphanedCommands`。
- 因此 RESET 之后玩家看到的是**START 之前的预览布局**（同一批实体 id、同一批位姿），可以直接再按 Start，也可以继续编辑。
- **编辑态下的 RESET 不复活被删掉的件**：判据是 `_sandboxPlayers.IsMaterialized(playerId)` —— 运行中玩家不能 Remove（`WrongMode`），所以「少了的件」只有两种来源：运行中被摧毁（要恢复）与编辑时自己删的（不能恢复）。
- **出界清理复用同一条 RESET**（`ResetPlayersOutOfBounds`）：开飞出界的玩家拿回自己的布局，而不是被清空。
- **`ForgetWeldsForEntities`**：RESET 保住了框的实体 id，`PruneDeadWelds` 因此不会丢掉这些焊缝定义，而 `BindWeldJoints` 的幂等键 `_weldKeys` 会把下一次 Start 重新注册的同一对吞掉 → 车架会被重建但**不再焊接**。所以 RESET 必须显式丢弃该玩家涉及的焊缝定义，让下一次装配重新登记并绑定（`FrameWeldRoomTests.APlayerResetAndRebuildGetsItsWeldsAgain` 守住这条）。

### 3. 已知偏差（写下来但不再排队，接受）

- 运行中被摧毁的件是**用新的实体 id 重建**的：PGFS 不含 owner，客户端学不到新 id，因此那个件不会出现在该玩家的开关栏里（仍可被该玩家在服务器侧 Start/Remove —— 归属在服务器）。复活旧句柄需要给 `EntityStore` 加「按 id 重建」，超出本切片。

## 影响

- `src/PigForge.Server/GameRoom.cs`（`CapturedPart`、`_startLayoutByPlayer`、`CaptureLayout`、`StartPlayer`、`ResetPlayer`、`LeavePlayer`、`SortedPlacedEntitiesOf`、`ForgetWeldsForEntities`）、`PlayHost.cs`（断连清理，去掉会话）、`SandboxPlayers.cs`（`Forget`）、`CommandValidator.cs`（`Forget`）、删除 `PlaySessions.cs`。
- 客户端：`clients/web/src/App.vue`（不再附加 `?session=`、连接与掉线都 `reset()`）、`live/playerSession.ts`（RETRY 保留 `ownEntityIds`、删 `reconnect()`）、删除 `live/playSession.ts|.test.ts`。
- 测试：Server 112（`SandboxRoomTests` 的 RESET/出界三条改为新语义 + 三条新用例：重建被摧毁件、编辑态 RESET 不复活、`LeavePlayer` 清场；`PlayHostIdentityTests` 的 4 条 session 用例换成 3 条玩家生命周期用例；`FrameWeldRoomTests` 的 RESET 用例不再重新摆放），web 221（删 4 条 playSession 用例）。
- 规格与意图同步：`docs/specs/multiplayer-sandbox.md`（Assumption 1/4、身份节、状态机、命令表、Web 客户端、测试策略、验收）、`docs/intent/multiplayer-sandbox.md`（Outcome/Success 两条）。

### 4. 2026-10-13 修正：关卡房间的 `Retry` 用同一条规则（差距 `G117`）

沙盒的 `ResetPlayer`（§2）与关卡房间的 `Retry` 本来是两套写法：`Retry` 先 `_construction.ResetAll()`、销毁**全部**零件，
再按捕获布局重新摆放 —— 于是玩家放置的每一件在每次「回到建造态」都拿到**新的实体 id**。沙盒之外没人看见过这条
（关卡房间此前只有测试与 demo 用），但 `--play --level` 把官方关卡变成正式玩法后它就是硬伤：**客户端只从自己的 `Place` ack 学 id**
（owner 不上线，见 §3），用新 id 重建的布局会让页面握着一批服务器不再认识的 id —— 猪出界、房间自动回建造态之后，
本地仍是「运行中」且无法再建造。

修正后两个房间共用一条规则：**房间回到建造态时，能活的件保持身份**。

- `GameRoom.Retry` 不再 `ResetAll` + 全部重建：幸存的件**留在 `ConstructionRules` 里**（同 id、同格子、回到捕获的建造位姿，
  角色按内容重新注册），只有运行中被摧毁的件用新 id 重建（与 §3 的已知偏差同一条）。
- 逐实体清理**先于**全局 `ResetAll`：`UnlinkBodyMember` 是清掉 `ResetAll` 不管的那几张按实体索引的表（件的局部偏移、
  局部旋转、镜像位、风扇生成方向）的唯一路径，而它需要 `ResetAll` 随后要丢掉的 body link。
- 关卡自带的 actor（`LevelSpawnDefinition`，不属于任何玩家）照旧销毁重建（新 id，客户端从不认识它们）。
- 客户端配套：`live/playerSession.noteRoomPhase` 让本地相位跟着**房间自己的帧相位**回落到 `editing` —— 房间自己回建造态
  （`ADR-036`）不走任何 ack，只有快照说得出这件事；服务器先发 ack 再广播，所以在途的旧建造帧不会撤销一次 Start。
