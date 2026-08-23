# PigForge

无 Unity 依赖的联机游戏核心与无头服务器项目。

## 当前结构

- `src/PigForge.Core`：游戏状态与领域规则，不依赖 Unity、协议或具体物理引擎。
- `src/PigForge.Physics.Abstractions`：物理后端契约与跨引擎数据类型。
- `src/PigForge.Physics.Bepu`：基于纯 C# BepuPhysics v2 的无头物理适配器。
- `src/PigForge.Protocol`：客户端命令、服务器快照、回放契约和协议版本。
- `src/PigForge.Replay`：固定 Tick 回放执行器、canonical state hash 和物理回放适配器。
- `src/PigForge.Server`：可运行的无头服务器宿主。
- `tests/`：Core、Protocol、Replay 和 Physics contract 分层测试。

当前物理主后端采用 BepuPhysics v2，避免 Unity 和 native runtime 依赖。JoltPhysicsSharp 保留为第二后端，用于后续性能和行为差异比较。

## 运行时边界

- 服务端和核心项目统一目标 `net10.0`，不为 Unity 的托管运行时降级或多目标化。
- Unity 仅作为独立客户端/参考运行时，通过版本化协议和回放文件交换数据，不直接引用 `PigForge.Core` 或服务端程序集。
- Unity、BepuPhysics 和后续 Jolt 后端都实现同一组语义契约；契约的跨运行时形式由协议/回放 schema 定义，而不是由 Unity 的 CLR 版本决定。

## 文档

- `docs/specs/pigforge-headless-spec.md`：项目目标、边界、测试策略和成功标准。
- `docs/specs/physics-replay-v1.md`：回放字段、事件顺序和 canonical state hash 规则。
- `docs/decisions/ADR-001-net10-physics-backend-boundary.md`：`.NET 10`、Unity 隔离和物理后端边界决策。
- `docs/implementation-plan.md`：按依赖关系排列的实施计划和检查点。
