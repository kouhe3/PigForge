# PigForge

无 Unity 依赖的联机游戏核心与无头服务器项目。

## 当前结构

- `src/PigForge.Core`：游戏状态与领域规则，不依赖 Unity 或具体物理引擎。
- `src/PigForge.Physics.Abstractions`：物理后端契约与跨引擎数据类型。
- `src/PigForge.Protocol`：客户端命令、服务器快照和协议版本。
- `src/PigForge.Server`：可运行的无头服务器宿主。
- `tests/PigForge.Core.Tests`：核心契约测试。

当前阶段只建立稳定边界，不引入 Unity 或具体 PhysX NuGet 包。后续物理实现应放在独立适配器项目中，例如 `PigForge.Physics.MagicPhysX`。

## 运行时边界

- 服务端和核心项目统一目标 `net10.0`，不为 Unity 的托管运行时降级或多目标化。
- Unity 仅作为独立客户端/参考运行时，通过版本化协议和回放文件交换数据，不直接引用 `PigForge.Core` 或服务端程序集。
- Unity 物理后端与 `PigForge.Physics.MagicPhysX` 都实现同一组语义契约；契约的跨运行时形式由协议/回放 schema 定义，而不是由 Unity 的 CLR 版本决定。

## 文档

- `docs/specs/pigforge-headless-spec.md`：项目目标、边界、测试策略和成功标准。
- `docs/specs/physics-replay-v1.md`：回放字段、事件顺序和 canonical state hash 规则。
- `docs/decisions/ADR-001-net10-physics-backend-boundary.md`：`.NET 10`、Unity 隔离和物理后端边界决策。
- `docs/implementation-plan.md`：按依赖关系排列的实施计划和检查点。
