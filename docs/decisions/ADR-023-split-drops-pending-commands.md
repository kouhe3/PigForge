# ADR-023: 复合体拆分时丢弃指向已销毁刚体的待决指令

## 状态

Accepted

## 日期

2026-10-04

## 背景

`GameRoom.Tick` 的相位顺序埋了一个坑：

1. 相位 1 用**上一 tick 的** `_output.Commands` 驱动物理世界，并把这份列表复制进 `_appliedCommands`；
2. 相位 4 `_rules.Tick` 清空并重填 `_output.Commands`（本 tick 的指令）；
3. 相位 5 `SplitFromAppliedCommands` 用 `_appliedCommands`（即刚刚施加的那批冲量）判定接缝断裂，命中就 `_world.DestroyBody(旧 body)` 再为每片 `BindCluster` 新 body。

于是**相位 4 刚发出的那批指令，目标 body 在相位 5 就被销毁了，而它们要等到下一 tick 才被施加**。`BepuPhysicsWorld.ApplyCommands` 对未知 body 直接抛 `KeyNotFoundException`（"Impulse target body N does not exist or is not dynamic"），房间从此停帧——玩家看到的是整台载具冻住。

这不是纯理论路径：任何「每 tick 都出指令」的零件加上一次够强的断裂冲量就能命中。实测复现（`CompoundSplitRoomTests`）：木框 + 尾翼 + 炸药，炸药 25 冲量（半径衰减后 19.4）超过接缝阈值 10 → 拆成两片，而尾翼那一 tick 的指令仍指向旧 body → 下一 tick 抛错。

ADR-019 修的是同一片代码的另一半（拆簇时的成员记账、TNT 不驱动自身 body）；本 ADR 补的是**指令队列**这一半。

## 决策

1. **销毁 body 时同步丢弃指向它的待决指令**：新增 `GameRoom.DropPendingCommands(PhysicsBodyId)`，就地倒序删除 `_output.Commands` 里 `Body` 命中的条目（零闭包分配），在三处调用：
   - `SplitFromAppliedCommands` 拆簇；
   - `DetachFromCompound`（脱钩件把自己拆出去）；
   - `UnbindEntity` 的孤儿 body 销毁分支。
2. **不重发、不重定向**：这些指令是为一个已经不存在的刚体算出来的，丢掉即可；拆出的每一片都会被 `BindCluster` 重新绑定到规则层，下一 tick 各自出新的指令。代价是那一 tick 该零件的推力少一次，属于可接受且确定性的。

## 影响

- `src/PigForge.Server/GameRoom.cs`：4 处（1 个方法 + 3 个调用点）。
- 回归测试 `tests/PigForge.Server.Tests/CompoundSplitRoomTests.cs`（真内容 + 真 Bepu）：**非空验证过**——去掉修复后该测试以完全相同的 `KeyNotFoundException` 失败，加回后通过；测试同时断言拆簇确实发生（两片 body id 不同），避免退化成空断言。
- 现状下没有「持续出力且单次冲量 > 接缝阈值 10」的出厂零件（风扇 0.1167、旋翼 2.0、马达轮 2.2、气球 0.383），所以这条路径需要「一次够强的断裂 + 一个仍在出指令的零件」同时出现；`ADR-022` 期间用被否掉的 10.3 倍标定锚做旋翼时它必然触发（旋翼 16.6 > 10），记载于此以免再次踩坑。
