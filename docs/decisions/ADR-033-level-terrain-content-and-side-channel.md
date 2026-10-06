# ADR-033：关卡地形进内容，客户端用 HTTP 拿关卡文档

- 状态：**已接受**（2026-10-06）
- 相关：`ADR-032`（网格形状本身）、`ADR-002`（关卡只有位置触发）、`docs/specs/original-level-pack.md`
- 差距：`G73`（关卡格式）、`G76`（地形）、`G74`（关卡数量/进度）

## 背景

`ADR-032` 给了两个后端静态三角网格形状，但关卡侧还没有任何东西能**产生**一个网格体，客户端也从来看不到
关卡：`PGFS` 的实体记录只有 `partTypeId`（73 B），客户端画地面的方式是「按 `partTypeId` 查自带的
`content/parts.json` 再画它的 `shapes[]`」，而 `goalZone`/`bounds` 是客户端**手写复制**的一份
（`clients/web/src/builder/slope.ts` 的 `GOAL_ZONE`/`MAP_BOUNDS` 就是 `terrain-v1.json` 的数字）。

搬运原版关卡要同时解决三件事：**关卡的几何怎么进内容**、**它怎么进世界（body 归属谁）**、
**客户端怎么知道它**。

## 决策

### 1. 关卡内容 v2 携带 `terrain`：位置 + 深度 + **边界环**

```json
"terrain": [ { "position": [-2.79, 9.02, 0], "depth": 10, "loops": [ [[x, y], ...] ] } ]
```

- 一条对应原版一个带碰撞体的 `e2dTerrain` 对象（2146 个里 1648 个）；`position` 是它自己的变换
  （实测 2146/2146 旋转为 0、缩放为 1），`depth` 是 `e2dConstants.COLLISION_MESH_Z_DEPTH = 10`。
- `loops` 是**边界环**，不是 fill 网格的顶点表顺序：实测 1648 个地形里 1643 个顶点表就是轮廓，4 个在一点相接
  （某顶点 4 条边界边 = 两环共点），1 个有一个顶点根本不在边界上。不变量是**每个边界顶点的边界边数为偶数**，
  所以轮廓总能分解成闭环；转换器按环走、按环挤出。
- 挤出由 `LevelTerrainMesh.Build` 完成，逐字复刻 `LevelLoader.CreateCollider` 的索引构造
  （`array2[6i..6i+5] = {2i, 2i+1, 2i+2, 2i+2, 2i+1, 2i+3}`，下标对 `2 × 点数` 取模）。

### 2. 地形刚体**没有实体**：只碰撞、不进快照、不属于任何玩家

`GameRoom.EnsureTerrainBodies` 在 `SetupFromLevel` 里建一次（**每房间一次**：`SetupFromLevel` 还会被
`EnterBuildMode(Clear)` 与 `Retry` 再次调用，而关卡的 terrain 不变）：

- 不注册 `EntityId`，因此不进 `PGFS` 快照、不属于任何玩家、不参与 `Start`/RESET/`LeavePlayer`/出界清理；
- 规则层对「没有实体的 body」的接触事件本来就跳过（`GameRoom.cs` 的 `_entitiesByBody` 查找），不需要特例；
- 但 `IPhysicsWorld.CopySnapshots` 报告世界里的**每一个** body，所以 `EnsureBuffers` 的容量必须把
  `_terrainBodies.Count` 算进去（这个 bug 被 `TerrainRoomTests` 当场抓到，见验收）。

### 3. 客户端用 `GET /level` 拿关卡文档，而不是把它塞进协议

`PlayHost` 在 `/play`（WebSocket）之外新增 `GET /level`，返回**服务器解析的那份 JSON 原文**
（`application/json`，带 `Access-Control-Allow-Origin: *`，让 vite 的 :5173 能跨源取）。客户端因此：

- 画出地形多边形与世界边界（不需要协议改动、不需要给 1648 个网格体编 part id）；
- 用关卡自带的 `goalZone`/`bounds` 取代 `builder/slope.ts` 的手写副本（那份副本只剩「还没有关卡时」的回退）。

### 4. `--play --level <content/levels 下的相对路径>` 直接把某一关当目标局来跑

不给 `--level` 时行为不变（沙盒房间用 `terrain-v1.json`）；给了就建一个**有目标**的房间
（`sandboxMode: false`，猪进终点区即 `Won`），于是「任意官方关卡能加载能玩」有了一个可执行的入口。

## 影响与偏差

- **地形不可见时是「隐形地面」**：客户端没取到关卡（服务器没起、跨源被拦）时会回退到旧的手写 `bounds`/`GOAL_ZONE`，
  地面画不出来，但物理照旧 —— 这是「先能玩、后好看」的取舍；地形**视觉**目前只填轮廓多边形，原版的
  fill/curve 贴图与控制贴图（2146 张内嵌 PNG）仍未使用。
- **地形没有实体 id**：客户端无法选中/点击地形本身（原版也不会选中地形）。
- **原版的 2.5D 墙**（`G133`）：碰撞是轮廓沿 z 挤出的 10 单位厚壳，PigForge 是完整 3D，动态件可以整体
  滑出这个 z 区间。已知偏差，不新增。
- **`PrefabOverrides` 仍未实现**（`docs/specs/original-level-pack.md` §6）：关卡级组件数据（挑战目标、
  相机限制等）暂时丢弃，所以终点区用的是 `Goal*` prefab 自身的碰撞盒，而不是关卡覆盖值。

## 备选方案（未采用）

1. **把地形做成一种 `partTypeId`**：给每个关卡的地形编内容件 —— 每关几何都不同，等于把 277 份几何
   塞进零件目录，且与 `ADR-004` 的「内容件 = 可建造件」冲突。
2. **PGFS 升 v7，加一条地形记录**：客户端就不需要 HTTP 了，但要动协议、动快照尺寸（73 B/实体）与
   所有解码器，且地形是静态的、每关只传一次 —— HTTP 一次取全文更省。
3. **把 `goalZone`/`bounds` 塞进现有快照头**：只解决"目标在哪"，解决不了地形。

## 验收

- `tests/PigForge.Core.Tests/LevelContentTests.cs`：v2 解析出 terrain（位置/深度/环）、v1 仍可解析、
  五种畸形 terrain 被拒、未知 `schemaVersion` 被拒；`LevelTerrainMesh` 的索引构造逐项等于原版公式。
- `tests/PigForge.Server.Tests/TerrainRoomTests.cs`：40 × 4 的地形壳上猪停在地面高度（0.2…1.2）；
  **同一关卡去掉 terrain 后猪必须掉出世界**（非空验证）；terrain 只建一次；没有 terrain 的关卡不建。
- 实机：`dotnet run --project src/PigForge.Server -c Release -- --play --level <转换后的关卡>`，
  `curl http://127.0.0.1:5088/level` 取到同一份 JSON，客户端连上后能看到地形与终点区。
