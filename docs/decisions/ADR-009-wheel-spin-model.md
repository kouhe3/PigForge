# ADR-009: 车轮铰链的旋转模型（轮体绕轮胎中心自转、安装件挂父体、关节对不碰撞）

## 状态

Accepted。取代 ADR-008 决策 1 的「锚点 = 轮子零件原点」与隐含的「轮体携带全部碰撞体」。

## 日期

2026-09-12

## 背景

ADR-008 让轮子成为独立刚体 + revolute 关节后，实测仍是坏的。用户报告：移动木轮时木轮的长边对不齐木框、木轮不滚动、木框 + 木轮的车在斜面上静止、几秒后穿墙。

实测根因（`CompoundAssembler` 组装结果 + 真实 Bepu 复现）：

1. **关节锚点取在零件原点，不是轮胎中心**。木轮的轮胎球 `r = 0.33 @ (0.0106, -0.2057)`，球心在零件原点下方 0.2057。绕零件原点转动 = 让轮胎绕着关节点做凸轮运动，接触点速度不为零，轮子只能打滑/抖动，不能滚动。
2. **轮体携带全部碰撞体**（轮胎球 + 支撑盒）。轮体要自转，就必须只带「自转下形状不变」的碰撞体：只有球体绕自身球心旋转不变；支撑盒随轮体转会整圈扫过上层木框。
3. **把支撑盒挂到父体后，它与轮体的轮胎球自相碰撞**。两者中心只差 0.038，球半径 0.33 —— 深穿透的持续对撞把整台车锁死（实测：车 1 秒内速度归零并停住，轮子 `ω = 0`，地面摩擦再把车钉在坡上）。这正是「车在斜面上静止」的直接原因；旧实现下车体还会以 ~0.04–0.07 m 的深度持续嵌进坡面。

最小复现（真实 Bepu，: `tests/PigForge.Physics.Tests` 的临时夹具，已删除）：静态坡 + 两轮（球 r=0.33）+ 底盘（盒，且带上两轮的支撑盒）。锚点取轮胎中心且两对刚体互不碰撞 → 车以 `ω = v/r`（比值 1.00）连续下坡；把支撑盒挂上父体但不做碰撞过滤 → 车 5 秒只挪 0.01 m。

原作语义（`Assets/Scripts/Assembly-CSharp/CartWheel.cs`）：轮胎球挂在 `WheelPivot` 上、支撑盒挂在不旋转的兄弟节点 `SupportCollider` 上，两者属于**同一个刚体**，因此同零件的碰撞体之间永不碰撞；`Update` 里旋转 `WheelPivot`（球体旋转几何无副作用）并靠 `SpeedInDirection` 走视觉/音效。

## 决策

1. **轮体位姿 = 轮胎中心，铰链锚点 = 同一点**。`PartContentLibrary.DescribeWheel(partTypeId, scale)` 给出轮子的自转集合与轴心：轴心 = 轮胎形状的体积加权中心；`CompoundHinge.LocalAxle` 携带该轴心（零件局部、已按 scale 缩放），`BuildCluster` 用它作轮体体位姿，`GameRoom.BindWheelHinges` 用它换算两侧锚点。
2. **轮体只携带自转集合**：零件的球体（`IsTireShape`）。因此 `CreateBodyDefinition` 对铰链簇产出「一个球」的轮体，`ω_z` 就是真实滚动角（ADR-008 决策 4 继续成立）。
3. **非球形状（支撑盒）由父体承载**：`CompoundAttachment` 记录「哪个零件的哪些形状挂在哪」，父体 compound 加入这些形状、并按体积计入父体质心；轮体不再重复它们。父体必须是动态件（物理契约没有静态 compound，轮子挂在静态件上时保留自己的安装件）。
4. **没有球的 wheel 件不被拆**：螺旋桨（part 38，只有一个盒）没有可滚动的轮胎，`DescribeWheel` 让它的全部形状随轮体自转（原作螺旋桨正是整体旋转），轴心 = 全部形状的体积中心。
5. **关节两端刚体之间不生成接触**：Bepu 后端维护 `_jointedPairs`，在 `AllowContactGeneration` 里拒绝该对的接触；关节建立/销毁/刚体销毁/清空时同步维护。语义与「同零件碰撞体不自碰」一致。
6. **贴图按「是否挂在会转的节点上」分流**（显示 BUG 修复）：`extract.mjs` 用 prefab 的父子链判定每个精灵是否位于原作运行时会转动的节点下（`WheelPivot`/`FakeWheelPivot`/`FanVisualization`），输出**每精灵布尔字段 `rotates`**（附加在 `schemaVersion` 2 上，向后兼容；v3 预留的 `spin` 动画描述符不动）。渲染时 `rotates: true` 的精灵用实体 `yaw`（含滚动角），`rotates: false` 的（如木轮/马达轮/大轮挂在 prefab 根上的车轴条）用**建造朝向**（缺省与轴心的来源见决策 8、9）。
7. **建造朝向由客户端按帧追踪**：`clients/web/src/live/restYaw.ts` 记录每个实体的建造角——任何仍是布局的帧（建造期 `phase = 0x10`、或沙盒预览 `bodyId = 0`）都会刷新它，首次出现的位姿作兜底（对回放与「开始前就连上」的客户端精确，只有中途加入的客户端会拿到偏旧的角，但仍优于让车轴跟着转）。回放视图与沙盒两条路径共用同一追踪器（`stores/session.ts`）。
8. **旋转轴来自零件内容（清单只是兜底）**（显示 BUG 修复）：`draw.ts` 的 `wheelAxle(part, texture)` 取零件**轮胎球**的体积加权中心（与 `DescribeWheel` 同一算法；无球但只有一个形状时取该形状的偏移——原作螺旋桨；再退到清单 `pivot`）。清单的 `pivot` 仍是**图集帧**里的轴心（调色板缩略图用），但网格渲染不再依赖它：`tools/bple-textures` 的产物是 gitignored 的生成文件，旧的/缺失的清单会让木轮失去轴心、整个复合体绕零件原点摆动而不是绕轮胎自转（用户报的显示 BUG）。渲染时**自转精灵钉在轴上**（脱离原作 prefab 里那个偏心偏移——木轮的轮盘精灵比轴高 0.179、马达轮高 0.075），**安装件保持建造坐标系里相对轴的位置**。判定「在滚动」的条件：零件带 `wheel` 能力、拿到轴心、且实体已实体化（`bodyId !== 0`）；建造期预览不适用（见决策 9）。
9. **哪些精灵跟着转：清单 `rotates` 优先，缺省按轮胎尺寸推断**。`rotates` 是唯一知道原作驱动哪个美术节点的信息（只有 prefab 父子链能给出，几何推断无法替代：实测 47 个 wheel 件里几何规则有 16 件与 `rotates` 不一致，例如马达轮有 2 个自转精灵），因此它是权威；只有整个清单都没有任何 `rotates` 标记（旧生成文件）时，才退回「画轮胎的那个精灵」——两轴尺寸都接近轮胎直径（2×最大球半径，误差 ≤15%）的那个。
10. **建造期预览不抵消滚动**：只有物理已接管（`bodyId !== 0`）时才用建造角画安装件；预览（`bodyId === 0`，含拖拽中的旋转预览）整体跟随 `yaw`，否则拖拽旋转零件时车轴会停在旧角度（用户报的显示 BUG）。
11. **每个精灵的 `cx`/`cy` 是「美术中心」，不是节点位置**（显示 BUG 修复）：`extract.mjs` 复刻 `Sprite.SelectSprite`/`CreateMesh` 的运行时网格偏移——`(选择框中心 − 打包矩形中心 + pivot + 组件 pivot)` 源像素——再按 `scale`、`UNITS_PER_PIXEL` 折算成世界单位，从节点局部位置里减掉。忽略它会把木轮的轮胎叠在支架上、把小轮的支架压到轮胎下面（实测：木轮两个精灵的间距从原始 0.03 恢复为 0.30、小轮 0.149，均与 `Icon_*` prefab 逐位吻合）。`UnmanagedSprite`/`INSerializedSprite` 的网格以节点为中心且没有 pivot，只有 `Sprite` 路径做这个修正。
12. **安装件相对「轮胎精灵」摆放，不用内容轴心换算**（显示 BUG 修复）：`draw.ts` 在滚动时把 `rotates` 精灵钉在内容轴心上，其余精灵用**清单内**的相对量 `placement − tire` 旋转到建造角。清单的 `cx`/`cy` 是相对复合体质心锚点的，内容轴心是相对零件原点的；两者混用会引入锚点差（木轮 0.02 m 可忽略，小轮 0.23 m 直接把支架甩到轮胎下面，正是用户报的「小轮没对齐」）。
13. **快照发布安装参考坐标系（PGFS v4）**（显示 BUG 修复，用户选定的方案）：客户端在数学上无法从车轮实体自身分离「底盘朝向」与「自转角」（`yaw − ∫ω` 恒等于建造角，已实测），所以服务端在 `BindWheelHinges` 时记录 `_attachByEntity[轮实体] = (父刚体, 父体装配朝向⁻¹ ∘ 轮建造朝向)`，发布时算 `attachYaw = yaw(父体当前朝向 ∘ 该局部旋转)`；非铰链零件 = 自身 `rotation` 的 yaw；关节被销毁/断开时移除条目（该轮重新变成「刚性于自身」）。实体由 69B 变 73B（`scale` 与 `flags` 之间插入 `attachYaw:f`），`CurrentVersion = 4`；`docs/specs/play-part-switches.md` 的 PGFS 一节同步。客户端 `draw.ts` 用 `attachYaw ?? restYaw ?? yaw` 作为安装件朝向（回放文档没有该字段，退回追踪到的建造角）。
14. **精灵按 z 降序即绘制顺序（由远及近）**（显示 BUG 修复）：原作相机在 `z = -15` 朝 +z（`IngameCamera.cs:440,1038`），z 越大越远，Unity 透明队列先画远的；`extract.mjs` 原先按 **z 升序** 排列，正好反了。判据取自原作自身数据：`Part_MetalFrame_12_SET` 中名为 `Background` 的精灵 z 最大（最远），`Part_Pig_02_SET`/`Part_KingPig_02_SET` 的 z 顺序是 Body(0) → Face/Eyes(-0.10) → Glasses(-0.12/-0.20) → Hat(-0.131)，与「帽子压眼镜、眼镜压脸」一致；四个轮的 `Icon_*` prefab 与零件 prefab 排出同样的相对次序。顺序反了的症状：马达轮的轮胎盖住透过轮圈孔应该可见的轮辐、且马达块被压在轮子后面（用户报的回归）。

## 影响

- **图层顺序修正**（实测，真实清单 + 真实 `drawFrame` + 真实沙盒）：马达轮现在按 轮辐(`1514,44`) → 轮胎(`808,596`) → 马达块(`1127,690`) 绘制，实测马达块压在轮胎上方、轮辐透过轮圈透明孔可见（改动前正相反）；木轮/大轮/小轮的轮胎与支架次序同步翻正（三种次序的像素差分别为 6034/3471/6454 px，说明顺序确实可见）。回归测试 `draw.test.ts` 的「blits the composite in the manifest's own order」（把渲染器改成逆序 blit 即失败：期望 `[10, 200]`，实测 `[200, 10]`）。调色板缩略图共用同一清单顺序。

- **车轴跟随底盘**（实测，真实沙盒 + 真实渲染器）：改动前把车平放在斜坡上（角 0）启动，底盘俯仰 0 → 0.25 rad 而安装件精灵 300 帧角度恒为 0.000（复现用户报的「车轴不会旋转」）；现在客户端用快照的 `attachYaw`，同一场景安装件随底盘转。服务端回归测试 `SandboxRoomTests.SandboxWheelAttachFrameFollowsTheChassis`（从高处落到平地、底盘翻滚：断言 `attachYaw == 轮建造角 + 底盘转角` 且车轮自身 `rotation` 已离开该坐标系；把服务端改回「发布自身朝向」即失败：期望 `0.40`，实测 `0.88`）；客户端回归测试 `draw.test.ts` 的「orients the mount by the attach frame the snapshot publishes」；`decodeSnapshot.test.ts` 断言 v4 帧的 `attachYaw` 与版本 3 被拒。

- **木框 + 木轮的车真滚动下坡**（实测 terrain-v1 长坡，真实内容）：`ω_z = v/r` 比值 0.95–1.00，`t=15` 0.51 m/s → `t=240` 8.07 m/s，x 从 -8.64 滑到 -23.94；跨三段坡板接缝（x ≈ -12.2、-19.2）时比值 0.95–1.00，无穿透。旧实现同一场景 1 秒内速度归零。
- 马达轮照常驱动（`SandboxRoomTests.SandboxCartHingesWheelsAndDrives`）；回归测试：`SandboxRoomTests.SandboxWoodenCartRollsDownTheLongSlope`（无动力车必须下坡且按 v/r 自转）、`PhysicsContractTests.JointedBodiesOverlapWithoutBeingPushedApart`（关节对含真实重叠时不被推开；无关节的对照组必须被推开）、`CompoundAssemblerTests.WoodenWheelSupportColliderConnectsToFrameDirectlyAbove`（轮体 = 居中的轮胎球、轴心、支撑盒挂父体）。
- 碰撞过滤只作用于**该对刚体**：它们与地形、其它零件的接触不受影响；`RunMotors` 的「轮子触地才推进」仍依赖地面接触。
- 关节生命周期不变（ADR-008）：裂缝拆分仍不重建轮子关节；拆分后该轮成为自由刚体，重建的父簇也不再携带它的安装件（重建路径只从焊接成员生成形状）。
- 后端范围：只有 Bepu 实现 revolute 关节与这对过滤；Jolt 后端仍未实现关节，因此轮子在 Jolt 下不会成为独立轮体，行为与 ADR-008 之前一致。
- 快照/渲染：轮子实体的世界位姿仍还原到**零件原点**（`_compoundLocalByEntity` 记录 `-LocalAxle`），旋转取轮体旋转。渲染时自转精灵钉在轴上、不转精灵保持建造朝向——快照位置随刚体绕轴心摆动，这两条合起来让贴图在世界里稳定（否则车轴会跟着绕轴心公转）。
- 显示回归（实测，浏览器 + 真实清单 + 真实渲染器 `drawFrame`，回放视图逐帧采样）：滚动中车轴精灵（图集矩形 `1127,844`，`rotates: false`）角度恒定、位置逐帧完全相同，轮盘精灵（`1127,551`，`rotates: true`）角度随滚动 `-0.25 → -1.25 → -2.25` 且位置不变；同一数据置为预览（`bodyId: 0`）后**两个**精灵都随拖动角旋转（建造期旋转零件时车轴跟着转）。调色板图标（`partThumbnailDataUrl` 输出）实测轮盘悬在轮轴条下方并水平居中。单测：`draw.test.ts`（轮盘钉在轴、车轴不绕轴、预览跟随 yaw、无建造角时退回全 `yaw`）、`thumbnails.test.ts`（轮盘落在 `pivot`）、`atlas.test.ts`（`rotates` 解析与 `schemaVersion` 门）、`live/restYaw.test.ts`（预览刷新 / 首次兜底 / 实体消失清理）。
- **精灵堆叠与对齐修好**（实测，真实清单 + 真实 `drawFrame` + 真实沙盒）：三个轮子的精灵中心（清单值） 木轮 支架 `+0.1148` / 轮胎 `-0.1877`、小轮 `+0.0641` / `-0.0849`、马达轮 支架 `+0.1875` / 胎+辐条 `-0.0625`——轮胎一律在支架下方 0.15–0.30 m，与 `Icon_NormalWheel`/`Icon_SmallWheel`/`Icon_MotorWheel` prefab 的相对关系一致（独立按 `Sprite.SelectSprite` 复算核对）。浏览器实测（真实沙盒 + 真实内容，`--play`）：幽灵态与滚动态各三个轮子（铁轮/小轮/马达轮）全部「轮胎悬在支架下方且居中对齐」。回归测试：`draw.test.ts` 的「keeps a mount above its tire when the content axle is far from the art anchor」（把安装件改回内容轴心换算即失败：期望支架在轮胎上方 `-2.03 px`，实测落在下方 `+0.75 px`）。
- **轴心不再依赖生成文件**（实测，真实捕捉的沙盒滚动帧 + 真实 `drawFrame`）：同一批帧（木轮 `yaw` 0.25 → 1.285，`restYaw` 0.25）在「带 `pivot`/`rotates` 的清单」与「剥掉 `pivot`/`rotates` 的旧清单」两种输入下渲染结果**逐字段相同**：轮胎精灵角度随 `yaw` 变（-0.25 → -1.285）、安装件恒为 -0.25、轮胎位置钉在轴上。改动前，旧清单下的轮胎与安装件都退化成 `yaw`（整个复合体刚性旋转），回归测试 `rotations` 期望 `[-1.5, -0.25]` 而实测 `[-1.5, -1.5]` 即失败。回归测试：`draw.test.ts` 的「takes the axle from the part content, not the manifest」「spins the tire about the axle even when the manifest never described one」「has no axle for a part that cannot roll」。

## 参考

- `src/PigForge.Core/Content/PartContentLibrary.cs`（`IsTireShape`、`DescribeWheel`、`WheelShape`）
- `src/PigForge.Core/Construction/CompoundAssembler.cs`（`CompoundHinge`、`CompoundAttachment`、`BuildCluster`、`CreateBodyDefinition`）
- `src/PigForge.Physics.Bepu/BepuPhysicsWorld.cs`（`_jointedPairs`、`AreJointed`、`AllowContactGeneration`）
- `src/PigForge.Server/GameRoom.cs`（`BindWheelHinges`）
- `docs/decisions/ADR-008-wheel-hinge-joints.md`、`docs/decisions/ADR-007-part-multi-colliders.md`
- BPLE 依据：`Assets/Scripts/Assembly-CSharp/CartWheel.cs`、`Assets/GameObject/Part_CartWheel_01_SET.prefab`（`WheelPivot` 球 + `SupportCollider` 盒）
- 安装参考坐标系（PGFS v4）：`src/PigForge.Protocol/SnapshotWire.cs`（`AttachYaw`、`EntityByteCount = 73`）、`src/PigForge.Server/GameRoom.cs`（`_attachByEntity`、`YawOf`、`AttachYawOf`）、`clients/web/src/schema/decodeSnapshot.ts`、`clients/web/src/schema/toDrawEntities.ts`
- 显示分流与旋转轴：`tools/bple-textures/extract.mjs`（`SPINNING_NODES`、`rotates`、`pivot`）、`clients/web/src/renderer/draw.ts`（`wheelAxle`、`turningSprites`）、`clients/web/src/renderer/{atlas,thumbnails}.ts`、`clients/web/src/live/restYaw.ts`、`clients/web/src/stores/session.ts`
