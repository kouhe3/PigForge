# Spec: PigForge 无 Unity 联机核心与无头服务器

## 状态

Draft。当前基础解决方案已经初始化；本规格约束下一阶段的物理后端、数据导向核心、回放和服务器实现。

## Objective

PigForge 是从现有反编译 Unity 项目提取行为和内容后，重新实现的联机游戏核心。生产服务器必须运行在 .NET 10，无 Unity、无渲染、无场景生命周期依赖，并由服务器权威推进游戏状态。

Unity 继续作为离线参考运行时或客户端适配器，但不进入服务端和核心项目的编译依赖。旧 BPLE 项目用于生成行为回放，新实现通过版本化命令、快照、事件和回放 Schema 与其比较。

### 用户和场景

- 玩家：通过客户端建造、启动和运行载具。
- 服务器：验证命令、推进固定 Tick、运行物理、判定事件和胜负、广播快照。
- 内容工具：把现有 Unity/关卡数据转换为与引擎无关的 PigForge 内容定义。
- 维护者：可以替换 Unity、BepuPhysics、JoltPhysicsSharp 或其他物理后端，而不重写核心规则。

## Tech Stack

- C# / .NET 10 LTS
- `PigForge.Core`：数据导向游戏状态与规则；不引用 Unity 或具体物理后端
- `PigForge.Physics.Abstractions`：PigForge 语义物理契约；不暴露引擎对象或 native 指针
- `PigForge.Physics.Bepu`：当前主后端，基于纯 C# BepuPhysics v2
- `PigForge.Physics.Jolt`：计划中的第二后端，基于 JoltPhysicsSharp 和锁定的 native RID 包
- Unity 6：只作为参考运行时或客户端适配器
- 测试：xUnit；固定 Tick 回放和后端差异测试
- 文本文件：UTF-8、LF；由 `.editorconfig` 和 `.gitattributes` 约束

## Commands

```powershell
dotnet restore PigForge.slnx
dotnet test PigForge.slnx
dotnet build PigForge.slnx -c Release
dotnet run --project src/PigForge.Server/PigForge.Server.csproj -c Release
```

当前服务器入口在未配置物理后端时必须明确退出或报告未就绪，不得伪装成可接受联机连接的生产服务。

## Project Structure

```text
src/
├── PigForge.Core/                    # WorldState、ECS-like stores和游戏规则
├── PigForge.Physics.Abstractions/    # 物理语义契约、句柄、快照和事件
├── PigForge.Physics.Bepu/            # 纯 C# BepuPhysics v2 适配器
├── PigForge.Protocol/                # 版本化命令、快照和回放 DTO/schema
├── PigForge.Replay/                  # 回放执行、Tick 调度和 canonical hash
├── PigForge.Server/                  # 固定 Tick、房间、权威服务端
└── PigForge.Physics.Jolt/            # 计划中的 Jolt native 适配器

schemas/                              # 跨运行时 wire/replay schema
content/                              # 引擎无关的烘焙后内容
replays/                              # 本地回放样本，不提交生成输出
unity/                                # 独立 Unity 参考/客户端适配器

tests/
├── PigForge.Core.Tests/              # 纯规则和状态测试
├── PigForge.Protocol.Tests/          # 协议和 Schema 边界测试
├── PigForge.Replay.Tests/            # ReplayRunner 和后端差异测试
└── PigForge.Physics.Tests/           # 后端契约与生命周期测试

docs/
├── specs/                            # 产品和技术规格
├── decisions/                         # ADR
└── implementation-plan.md             # 可跟踪实施计划和检查点

tasks/                                # 本地 agent 工作清单，按仓库策略忽略
```

## Architecture Contract

### 依赖方向

```text
PigForge.Core
    ↓
PigForge.Physics.Abstractions

PigForge.Replay
    ├── Protocol
    └── Physics.Abstractions

PigForge.Server
    ├── Core
    ├── Replay
    └── PigForge.Physics.Bepu

PigForge.Physics.Jolt
    └── Physics.Abstractions
Unity Reference Adapter
    └── versioned schema/replay files
```

Unity 不引用 `PigForge.Core`、`PigForge.Server` 或 net10 服务端程序集。Unity 和服务端共享的是版本化协议/回放 Schema，而非 Unity 兼容的 CLR 程序集。

### 物理边界

物理后端必须提供批量边界：

```text
ApplyCommands(batch)
    → Step(fixedDeltaTime)
    → CopySnapshots(batch)
    → DrainEvents(batch)
```

禁止核心层访问 `Rigidbody`、`GameObject`、`PxRigidDynamic*`、`IntPtr` 或 native 资源。

### ECS 形态

第一版采用稳定句柄和分离组件存储，不预先实现完整 archetype ECS：

```text
EntityId = slot index + generation
TransformStore
PhysicsBodyStore
PartStore
MotorStore
DamageStore
JointStore
```

只有 profiler 证明 GC 或布局成为瓶颈时，才引入针对性池化或非托管存储。禁止未经测量把所有状态改为裸指针。

## Code Style

```csharp
public readonly record struct EntityId(uint Value)
{
    public bool IsValid => Value != 0;
}

public interface IPhysicsWorld : IDisposable
{
    PhysicsBodyId CreateBody(BodyDefinition definition);
    void ApplyCommands(ReadOnlySpan<PhysicsCommand> commands);
    void Step(FixedTimeStep timeStep);
    int CopySnapshots(Span<PhysicsBodySnapshot> destination);
    int DrainEvents(Span<PhysicsEvent> destination);
}
```

规则：

- nullable enabled；新代码不以 null 表示生命周期状态
- native 资源拥有者显式实现 `IDisposable`
- 公共输入在边界验证；核心内部使用已经验证的数据
- 热路径避免 LINQ、反射、字符串日志、临时集合和逐实体 native 调用
- Entity/Body/Joint 使用稳定 opaque ID，不使用引擎实例 ID
- 错误使用结构化错误或启动失败，不使用静默 fallback

## Testing Strategy

### Core

覆盖：

- EntityId generation 和旧句柄失效
- 重复实体拒绝
- 非法命令拒绝
- 建造/删除/旋转/连接规则
- Tick 边界和固定时间步
- 关卡完成、死亡和重置

### Physics adapter

每个后端必须通过同一契约测试：

- Foundation/Scene/Body/Joint 创建和释放
- Box、Mesh、Material 定义转换
- 固定 Tick
- 批量命令在 Step 前应用
- 快照读取
- 接触、断裂和 Body 生命周期事件
- 后端能力缺失时启动失败

### Replay

相同：

```text
初始状态
内容版本
行为版本
随机种子
命令序列
Tick 数
```

比较：

```text
事件序列
最终状态哈希
最终胜负
位置/旋转误差
```

不默认要求不同物理后端逐浮点一致；先保证可观察事件和玩法结果兼容。

### Performance

真实典型和压力场景测量：

- Tick p50/p95/p99
- 每 Tick 托管分配
- Gen0/Gen1/Gen2 和暂停时间
- native 内存峰值
- 快照字节数
- 并发房间数

无 profiler 证据，不引入全局手动内存管理。

## Boundaries

### Always

- 服务端和核心保持 net10.0
- Unity 只通过协议/回放 Schema 接入
- 固定 Tick 和单一物理权威
- 外部内容和网络命令先验证
- 物理后端生命周期显式释放
- 文本文件使用 LF
- 新行为添加测试和回放样本

### Ask first

- 修改物理行为版本或旧 BUG 兼容行为
- 更换 PhysX/native 版本
- 添加网络传输依赖
- 改变协议或回放格式
- 引入非托管 ECS 存储
- 修改 Unity 参考基线

### Never

- Core 引用 UnityEngine、任何具体物理后端或 native 指针
- Server 启动 Unity 场景、UI、MainMenu 或 MonoBehaviour 链
- 客户端声明位置、接触、断裂或胜负结果
- 通过静默 fallback 隐藏后端能力缺失
- 在物理 Tick 热路径使用 JSON、反射或无限增长集合
- 在没有基准数据时声称手动内存一定更快

## Success Criteria

1. `dotnet build PigForge.slnx -c Release` 零警告、零错误。
2. Core 和 Server 的依赖图没有 Unity 或 Unity Mono 程序集。
3. 一个物理后端可以在 .NET 10 无头进程内创建、推进和销毁物理世界。
4. Unity 参考运行时可以导出同 Schema 的回放，但不需要引用 Server/Core 程序集。
5. 同一回放可在两个后端运行并比较事件、最终结果和误差。
6. 服务器权威固定 Tick 能处理重复、乱序和非法命令。
7. 典型场景性能预算由实测数据确定，并能检测 GC、native 内存和 Tick 回归。

## Open Questions

- 第一版物理 Tick 采用 30 Hz 还是 60 Hz；默认先以 60 Hz 建立基线。
- 第一版网络传输选择何种可靠/不可靠组合；在回放和本地服务器闭环前不决定。
- 兼容基线是 BPLE 2022 原始行为还是当前 Unity 6 修复后的行为；默认先把两者分成不同 `PhysicsBehaviorVersion`。

## References

- Unity Physics/Havok 共享输入输出但行为不完全一致：<https://docs.unity3d.com/Packages/com.havok.physics@1.3/manual/index.html>
- Godot Server/RID 服务器边界：<https://godotengine.org/article/why-does-godot-use-servers-and-rids/>
- NVIDIA PhysX 模拟生命周期：<https://nvidia-omniverse.github.io/PhysX/physx/5.4.1/docs/Simulation.html>
- NVIDIA PhysX 最佳实践：<https://nvidia-omniverse.github.io/PhysX/physx/5.4.1/docs/BestPractices.html>
- .NET 10 性能与分配建议：<https://learn.microsoft.com/en-us/aspnet/core/fundamentals/best-practices?view=aspnetcore-10.0>
- .NET unsafe 代码实践：<https://learn.microsoft.com/en-us/dotnet/standard/unsafe-code/best-practices>
