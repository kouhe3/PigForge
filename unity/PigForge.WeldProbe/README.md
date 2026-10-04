# PigForge.WeldProbe —— 焊点弹性的原版基准（Unity 2021.3.45f2）

这个最小 Unity 工程只有一个用途：**在同一代 PhysX 上量出原版《捣蛋猪》的焊点行为**，
给 `docs/specs/weld-compliance.md` 的 PigForge `Weld` 关节提供拟合目标。

- **编辑器版本 = 原版的**：`BPLE 2022.1.9/ProjectSettings/ProjectVersion.txt` 钉的是
  `2021.3.45f2`，所以基准必须跑在这个版本上（Unity 6 换了 PhysX 后端，数值不可直接比）。
- **不许改原版工程**：`BPLE 2022.1.9`/`BPLE_Unity6` 都是反编译重建产物（资源初始化本就脆弱），
  探针因此**只用图元**复刻几何：原版每个框的碰撞体就是 1×1×1 盒
  （`tasks/bple-jointstrength-report.json`），质量/阻尼/约束/关节旗标逐条来自源码与 prefab。
- **镜像的来源**（脚本头部逐条带引用）：关节＝`Contraption.cs:1507-1546`（六轴 Locked、
  anchor 取相对位置的一半、`enablePreprocessing = a && b`、`breakForce = gs(a)+gs(b)`）；
  刚体＝`BasePart.cs:1184-1196`（drag 0.2 / angularDrag 0.05 / 2.5D 约束 56）；
  物理＝`BPLE 2022.1.9/ProjectSettings/{DynamicsManager,TimeManager}.asset`
  （固定步长 0.02、求解迭代 6/1、最大角速度 7、接触偏移 0.005、重力 −9.81）。
- **量什么**：`replays/weld-compliance-probe.json`（随后抄进 `tasks/`）——每个格子的
  「相对偏移 × 时间」曲线与汇总（最大/稳态偏移、Z 角、断裂步与力）。

## 跑法（headless）

```bash
unity run unity/PigForge.WeldProbe --editor-version 2021.3.45f2 --timeout 1200 \
  -- -executeMethod PigForge.WeldProbe.Probe.WeldComplianceProbe.Run -logFile -
```

需要有效 license（`unity license activate --personal --accept-eula`）。

## 格子矩阵

| 组 | 变量 | 目的 |
|---|---|---|
| A `load_pp{on,off}_c{56,0}_m{0.5,1,4,20}` | `enablePreprocessing` × 2.5D 约束 × 质量 | 软/硬分档 + 木(0.5)/铁(1)/MetalFrame_131(4) 的质量单调性 |
| B `rig_pp{on,off}` | 锚点 50 kg 动态（真实质量比） | 原版整车拖一个框的实际工况 |
| C `break_{1000,2400}` | 力斜坡到断裂 | 校验内容里 `(gs(a)+gs(b))×ConnectionStrength` 的阈值语义 |

`replays/`、`Library/` 等已在 `.gitignore`；只有本文件、`Assets/`、`ProjectSettings/`、`Packages/` 入库。
