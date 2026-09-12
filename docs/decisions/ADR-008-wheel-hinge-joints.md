# ADR-008: 轮子用铰链关节（真滚动），复合体以形状体积质心为体位姿

## 状态

Accepted

## 日期

2026-09-09

## 背景

ADR-007 把车轮恢复成「轮胎球 + 支撑盒」并允许 sphere 焊接后，出现了两个问题：

1. **轮子不会滚动**：车轮被焊进车体复合体，刚体内部不能相对转动，车只能整体滑动（斜坡上整车滑下去、轮子角速度恒为 0）。原作的轮子是**独立刚体 + 关节**，靠地面摩擦真滚动；`IPhysicsWorld` 也早已声明 `CreateJoint`/`DestroyJoint`，只是 Bepu 后端一直抛 `NotSupportedException`。
2. **带偏移形状的零件视觉与碰撞错位**：Bepu 的 `CompoundBuilder.BuildDynamicCompound` 会把 compound 子形状**重新居中到体积质心**，而 `BodyDefinition` 的位姿被放在零件原点上，于是带偏移的零件（149 个）碰撞体整体偏移了「体积质心」的距离（实测木轮下沉 0.15、火箭下沉 0.03）。

## 决策

1. **轮子保留自己的刚体，用 revolute 关节挂在相邻车体上**：`CompoundAssembler.CanMerge` 对 `capabilities.wheel` 的零件返回 false；新增 `CompoundAssembler.CollectHinges` 为每个轮子选一个父件（优先最小 entity id 的非轮子邻居），`GameRoom.BindWheelHinges` 在绑定完簇之后创建关节，轴 = 局部 Z。**锚点与轮体形状的细节已由 ADR-009 取代**（锚点 = 轮胎中心，轮体只带轮胎形状，安装件挂父体，关节对不碰撞）；本条的「独立刚体 + revolute 关节」结论不变。
2. **扩展 `JointDefinition`**：追加 `LocalAnchorA/B`、`LocalAxisA/B`（局部坐标系，Revolute 必须是非零轴）；Bepu 后端用 `Hinge` 约束实现 `Revolute`，`PhysicsCapabilities.SupportedJointKinds = { Revolute }`，其它种类仍显式抛 `NotSupportedException`。
3. **复合体位姿取形状体积质心**：`CompoundAssembler.BuildCluster` 用 `PartContentLibrary.EnumerateShapePlacements` + `ShapeMetrics.Volume` 计算所有 leaf shape 的体积加权中心作为刚体位姿，成员局部偏移按该中心换算；`GameRoom.BindCluster` 对局部偏移非零的单件簇也记录 `_compoundLocalByEntity`，快照/渲染据此还原零件原点。这样 Bepu 的居中就是恒等变换，碰撞几何与内容逐字一致。
4. **轮子的滚动角由物理给出**：轮子刚体的 Z 旋转就是真实滚动角，快照 `rotation` 直接可用——贴图动画不再需要按地面速度推算轮子转速（见 `docs/specs/part-texture-animation.md`）。

## 影响

- 车轮在斜坡上真滚动（实测：木轮+木框车在 terrain-v1 斜坡上 ω_z ≈ 1 rad/s 并下行）；马达轮把车推走的同时自身旋转（`SandboxRoomTests.SandboxCartHingesWheelsAndDrives` 断言 |ω_z| > 0.5）。
- 关节生命周期：`GameRoom` 跟踪 `(joint, wheelBody, parentBody)`；实体/刚体销毁时先销毁相关关节（Bepu 的 `DestroyBody` 也会清理引用它的约束）；复合体裂缝拆分暂不重建轮子关节（拆分只影响焊接簇，轮子本来就是独立刚体）。
- 关节断裂阈值（`JointDefinition.BreakForce/BreakTorque`）本轮只写入定义、未接断链判定；轮子不会被撞飞（与 ADR-002 的「冲量超阈值断链」在后续切片补齐）。
- 螺旋桨（part 38）内容里带 `wheel: true`，因此也走铰链；它的推进门控仍沿用 `RunMotors` 的「轮子触地才推进」规则——这是既有内容语义，不在本切片修。
- 生成链与内容不变；本轮只改物理契约、Core 装配、Bepu 后端与 GameRoom。

## 参考

- `src/PigForge.Physics.Abstractions/PhysicsContracts.cs`（`JointDefinition`、`ShapeMetrics`）、`src/PigForge.Physics.Bepu/BepuPhysicsWorld.cs`（`Hinge`）
- `src/PigForge.Core/Construction/CompoundAssembler.cs`（`CanMerge`、`CollectHinges`、`BuildCluster`）、`src/PigForge.Server/GameRoom.cs`（`BindWheelHinges`、`ForgetJointsForBody`）
- `docs/decisions/ADR-007-part-multi-colliders.md`、`docs/specs/part-texture-animation.md`
