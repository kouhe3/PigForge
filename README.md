# PigForge

无 Unity 依赖的联机游戏核心与无头服务器项目。

## 当前结构

- `src/PigForge.Core`：游戏状态与领域规则，不依赖 Unity 或具体物理引擎。
- `src/PigForge.Physics.Abstractions`：物理后端契约与跨引擎数据类型。
- `src/PigForge.Protocol`：客户端命令、服务器快照和协议版本。
- `src/PigForge.Server`：可运行的无头服务器宿主。
- `tests/PigForge.Core.Tests`：核心契约测试。

当前阶段只建立稳定边界，不引入 Unity 或具体 PhysX NuGet 包。后续物理实现应放在独立适配器项目中，例如 `PigForge.Physics.MagicPhysX`。
