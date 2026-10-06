# ADR-032：关卡地形 = 静态三角网格形状

- 状态：**已接受**（2026-10-06）
- 相关：`ADR-005`（形状来自 prefab collider）、`ADR-007`（多碰撞体）、`ADR-031`（接触法线是表面几何）、
  `docs/specs/original-level-pack.md`、`docs/specs/physics-replay-v2.md`
- 差距：`G76`（地形是 primitive 拼装，无网格地形）、`G73`（关卡格式）、`G74`（关卡数量/进度）

## 背景

原版每一关的地形都是**网格**：`e2dTerrain` 的 fill 多边形由 `LevelLoader.ReadTerrain` 读入
（`LevelLoader.cs:218-262`），`hasCollider` 为真时 `LevelLoader.CreateCollider`
（`LevelLoader.cs:339-380`）把多边形的**轮廓顶点**沿 z 挤成三角带（深度
`e2dConstants.COLLISION_MESH_Z_DEPTH = 10`，即 z ∈ [-5, +5]，子节点名 `_collider`），于是碰撞体是
一圈**围绕地形轮廓的墙**，而车在 z ≈ 0 的平面里与这圈墙的内/外面碰撞（原版整个游戏是 2.5D：
`BasePart.EnsureRigidbody` 的 `(RigidbodyConstraints)56` 冻结 z 位移，见 `G133`）。

PigForge 的地形此前是若干 primitive（`ground-slab` box、`terrain-box` box、`ramp-plank` 斜 box，
`content/levels/{slope-v1,terrain-v1}.json`），搬运 277 关原版关卡必须先有网格碰撞。用户 2026-10-06
拍板：**真三角网格形状**（另两个选项是「离线把多边形拆成静态凸体/盒子」与「只做视觉网格」）。
契约里 `PhysicsShapeKind` 早就有 `TriangleMesh` 这个值，`PartContentParser` 也认得
`triangleMesh` 的 `vertices`/`triangles` 键，但**两个后端都拒绝**（`BepuPhysicsWorld.cs:153`
当时是「exactly one box, sphere, or compound shape per body」）。

## 决策

### 1. 契约：`TriangleMeshShapeDefinition`，静态专用

```csharp
public sealed record TriangleMeshShapeDefinition : ShapeDefinition
{
    public TriangleMeshShapeDefinition(IReadOnlyList<PhysicsVector3> vertices, IReadOnlyList<int> triangles);
    public IReadOnlyList<PhysicsVector3> Vertices { get; }
    public IReadOnlyList<int> Triangles { get; }
}
```

- 顶点在**刚体本地系**，三角形是三个一组的顶点下标（源文件里索引是 `int16`，契约里是 `int`）。
- 构造期校验：≥3 个顶点、索引数量非零且是 3 的倍数、每个下标落在顶点数内、顶点有限。
- **只允许静态体**：两个后端都不给网格体算惯量、也不移动它，动态体在
  `IPhysicsWorld.CreateBody` 处抛 `NotSupportedException`（沿用「未实现的能力显式抛错」的约定）。
- **不进 compound**：`AddCompoundChild` 仍只认 box/sphere，`ShapeMetrics.Volume` 对网格抛
  `NotSupportedException`（compound 要按体积分质量）。

### 2. 语义：网格是**双面曲面**，不是实心体

原版的碰撞体是 Unity 的**非凸** `MeshCollider`（PhysX 三角网格）。PhysX 的
`PxMeshGeometryFlag::eDOUBLE_SIDED` 只作用于 **raycast 与 sweep**（"it is ignored for overlap
queries"，PhysX 3.4 API 参考）⇒ 网格**接触**本来就不分正反面。

实测（2026-10-06）两个后端**都是单面**，而且**绕向相反**：

| 三角形 (A,B,C) 的正面向 | Bepu 2.4 | Jolt 2.22 |
|---|---|---|
| `cross(B-A, C-A)`（Unity/标准约定） | 反面（`TriangleWide.BackfaceNormalDotRejectionThreshold = -0.01` 把它判定为背面→丢掉接触） | 正面（碰撞） |
| `cross(C-A, B-A)` | 正面（碰撞） | 反面（穿过去） |

所以**每个后端把每个三角形按两种绕向各发一份**，把「双面」这一层语义收在各自的后端里，
契约不需要声明绕向，转换器也不必为不同后端生成不同数据。代价是网格三角形数与 BVH 内存各翻倍
（原版一关的 fill 轮廓通常几千个三角形，可接受）。

### 3. 各后端实现

- **Bepu**：`BepuPhysics.Collidables.Mesh(Buffer<Triangle>, in Vector3 scale, BufferPool)`。Bepu 的
  `Triangle` 内联三个顶点（没有索引缓冲），所以转换时把索引三元组展开；缓冲从世界自己的
  `_bufferPool` 取，`Mesh` 持有它、`Shapes.RemoveAndDispose` → `Mesh.Dispose(pool)` 归还。缩放恒为
  `Vector3.One`（关卡的局部缩放走刚体位姿）。`Triangle.Id` 是 Bepu 的常量形状类型 id，不是三角形序号。
- **Jolt**：`MeshShapeSettings(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<IndexedTriangle> triangles)`
  → `MeshShape(settings)`（`JoltPhysicsSharp` 2.22）。Jolt 的 `MeshShape` 官方就是静态专用形状，
  与决策 1 一致。

## 影响与偏差

- **地形碰撞体形态**：搬运时按原版 `CreateCollider` 的办法生成——轮廓多边形沿 z 挤压成墙（深度 10），
  不是「地形表面的一块实心地板」。这是原版的语义，不是近似。
- **2.5D 的墙 vs 完整 3D**（延续 `G133` 的已知偏差）：原版冻结 z 位移，所以零件永远待在墙体厚度内；
  PigForge 是完整 3D，动态件可以走出 z ∈ [-5, +5]，从墙的侧面离开地形。这是既有的有意偏差，
  网格地形不新增它，但会让它更容易被看到。
- **Jolt 后端**：只支持静态网格 + 单 box 两种形状（compound、运行期质量/碰撞开关仍是
  `NotSupportedException`），因此 Jolt 继续只作验证后端。
- **契约没有新增 `IPhysicsWorld` 方法**：`CreateBody` 已经足够表达静态网格体。

## 备选方案（未采用）

1. **离线把 fill 多边形拆成静态凸体/盒子拼接**：零后端改动，但地表与原版不一致，而地形正是关卡手感的主体。
2. **只做视觉网格、碰撞用包围体**：最省，但车会浮在地形上方或穿进坡里。
3. **单面网格 + 在后端里翻绕向**：能跑，但把「哪一侧算正面」变成每个后端各自的隐式约定，
   一旦地形几何换成实心体就会在另一侧漏接触；双面把这条不确定性整个删掉。

## 验收

- `tests/PigForge.Physics.Tests/TriangleMeshTests.cs`：Bepu 6 条（落到网格地面上静置、沿 30° 斜坡下滑、
  双运行确定性、动态体被拒、构造期校验、**网格下方的物体停在下表面**）+ Jolt 3 条（与 Bepu 落在同一高度、
  下表面、动态体被拒）。
- **非空验证**（实测两次）：把绕向换成单面/相反绕向时，同一个夹具在 Bepu 上直接穿过去（240 tick 后
  y = −76.8，等于自由落体），在 Jolt 上则稳稳落住；换回来则相反——两个后端确实各认一种绕向，
  双面实现不是文书。
- 全量：Physics **96**（75 非 Jolt + 21 Jolt，本 ADR 新增 9），Release 零告警零错误。
