# ADR-001: 以 net10 为核心运行时并隔离物理后端

## 状态

Accepted

## 日期

2026-08-22

## 背景

PigForge 需要从反编译 Unity 项目中重建可联机、可无头运行的游戏。现有 BPLE_Unity6 使用 Unity 内置 3D 物理；Unity 的内置 3D 物理是 PhysX 集成，但 Unity 的托管运行时和项目生命周期不应成为无头服务端的基础依赖。

项目还需要保留替换物理后端的能力。当前选择纯 C# 的 BepuPhysics v2 作为无头服务器主后端；JoltPhysicsSharp 作为第二后端候选，Unity 继续作为离线参考运行时。

## 决策

1. `PigForge.Core`、`PigForge.Protocol`、`PigForge.Physics.Abstractions`、`PigForge.Server` 统一以 `net10.0` 为目标框架。
2. 不为 Unity 的 Mono/托管兼容性把核心项目降级到 `netstandard`，也不让 Unity 作为核心程序集的编译目标。
3. 物理后端通过端口/适配器隔离：
   - `PigForge.Physics.Abstractions` 只定义 PigForge 所需的语义数据、稳定句柄、能力声明、快照和事件。
   - `PigForge.Physics.Bepu` 实现当前主后端，不引入 native runtime。
   - `PigForge.Physics.Jolt` 作为计划中的 native 第二后端，必须锁定 JoltPhysicsSharp 及其 RID 资源。
   - Unity 物理只作为独立 Unity 参考运行时或客户端适配器存在，不进入 `PigForge.Core` 和 `PigForge.Server` 的依赖图。
4. Unity 与 .NET 10 进程之间通过版本化协议或回放文件交换数据，而不是共享依赖 Unity Mono 的 C# 程序集。
5. 物理后端在每个固定 Tick 内按统一阶段运行：

   ```text
   应用命令 → 后端写入 → 固定步长模拟 → 等待结果 → 读取快照/事件 → 规则判定
   ```

6. 后端句柄只能是 PigForge 自己的 opaque ID，例如 `PhysicsBodyId`、`PhysicsJointId`；禁止向核心泄漏 `Rigidbody`、`GameObject`、`PxRigidDynamic*` 或其他 native 指针。
7. “可替换”定义为代码边界和数据契约稳定，不承诺不同物理后端逐位一致。后端差异通过 `PhysicsBehaviorVersion`、能力检查和回放测试显式管理。

## 未采用的方案

### 共享 netstandard 程序集给 Unity

拒绝。它会把 .NET API 面和编译约束降低到 Unity 可接受的交集，并使 Unity 的托管运行时成为服务端架构的隐式约束。Unity 只需要消费协议/回放数据，不需要引用 Core 程序集。

### 在 Core 中直接使用 Unity 或 PhysX 类型

拒绝。这样会把游戏规则绑定到某个引擎的对象模型和生命周期，替换后端时必须迁移所有调用点。

### 用“最低公分母”接口隐藏所有物理能力

拒绝。BPLE 需要关节、断裂、碰撞事件、查询、CCD、惯性和约束自由度。接口应表达游戏实际需要的语义能力，并通过能力声明拒绝不支持的后端，而不是静默近似。

### 让 Unity、Bepu 和 Jolt 在线同时作为权威

拒绝。多套求解器的时间步、solver、接触参数、关节创建顺序和 native 版本可能不同；在线多权威会产生状态分叉。线上服务器只有一个配置好的权威后端，其他后端只用于离线回放比较。

## 后果

### 优点

- 服务端可以使用 .NET 10 和独立 native 物理绑定，不受 Unity Mono 版本牵制。
- Core、协议、房间和规则可以在不修改的情况下替换物理后端。
- Unity 旧项目可继续作为行为参考机，帮助定位迁移差异。
- 物理后端的 native 生命周期、线程限制和资源释放集中在适配器中。
- 回放文件可以跨语言、跨运行时验证行为。

### 成本

- 需要维护版本化协议/回放 schema。
- Unity 参考运行时、Bepu 主后端和 Jolt 第二后端需要各自的资源转换与参数映射。
- 换物理后端仍然需要重新烘焙碰撞形状、映射关节、调参并运行回归回放。
- 不同后端的行为不能默认逐 Tick、逐浮点值一致；首先保证事件和最终玩法结果兼容。

## 验证策略

1. 后端契约测试：创建/销毁刚体、固定 Tick、快照读取、事件排空、非法定义拒绝。
2. 回放测试：相同初始数据和命令序列重复运行，比较事件序列和最终状态哈希。
3. 兼容性测试：旧 Unity 参考运行时与新后端比较接触、断裂、死亡和关卡完成等可观察结果。
4. 启动能力检查：后端缺少所需 Joint、CCD 或惯性能力时显式失败。
5. PhysX 后端使用 Checked/Debug 构建和错误回调验证场景配置，再使用 Release 构建进行性能测试。

## 参考资料

- .NET 官方依赖注入与接口实现：<https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/usage>
- Unity 内置 3D 物理是 NVIDIA PhysX 集成：<https://docs.unity3d.com/6000.0/Documentation/Manual/PhysicsOverview.html>
- Unity Physics / Havok 共享输入输出并可切换后端；官方同时说明行为相似但不完全相同：<https://docs.unity3d.com/Packages/com.havok.physics@1.3/manual/index.html>
- Godot Server/RID 的命令边界与不暴露底层对象思路：<https://godotengine.org/article/why-does-godot-use-servers-and-rids/>
- NVIDIA PhysX 固定时间步与 `simulate`/`fetchResults` 生命周期：<https://nvidia-omniverse.github.io/PhysX/physx/5.4.1/docs/Simulation.html>
- NVIDIA PhysX 调试构建、能力和关节稳定性最佳实践：<https://nvidia-omniverse.github.io/PhysX/physx/5.4.1/docs/BestPractices.html>
