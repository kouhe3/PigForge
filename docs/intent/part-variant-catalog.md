# Intent: 原版零件皮肤与特殊效果变体补全（本机）

> 2026-09-09 用户口述：「现在PigForge只实现了部分TNT变体。缺少更多的原作的零件皮肤和零件特殊效果变体。」
> 目录口径、编号规则、能力语义见规格 Assumptions；实现不得另猜。

- **Outcome:** 把原版变体注册表（`GameData.m_customParts`）里属于 PigForge 已有基准件的变体全部落进内容层——约 217 个纯皮肤（木框/金属框/猪/轮子/气球/发动机/尾翼/伞/抓钩……）加行为变体（AlienTNT 连锁爆炸、BlasterTNT 冲击波、AlienEgg、爆炸抓钩、蘑菇/灯笼/Alien 灯、内置灯金属框、电动小轮、重沙袋）。皮肤自动获得原版贴图与原版碰撞体，调色板在基准件下方按原版顺序展开。
- **User:** 仓库维护者本人 + 本机 `--play` 的玩家。
- **Why now:** ADR-004 只落了 4 个 TNT 皮肤（47–50），51/52 的贴图已解包但内容未接；其余基准件在调色板里没有任何皮肤，与「零件身份 = (PartType, customPartIndex)」的原版结构不对齐。
- **Success:** 调色板选中任意基准件都能看到它全部的原版皮肤并按原版顺序排列；AlienTNT 爆炸会连锁引爆半径内的其它 TNT、BlasterTNT 释放一次性冲击波、爆炸抓钩触地爆炸——都能在 `--play` 里观察并留痕；`dotnet test PigForge.slnx`、`pnpm test`、`pnpm build` 全绿。
- **Constraint:** 零协议改动（变体 = 扁平 `partTypeId`，PGFS/PGFC 布局不变）；皮肤不改变基准件的物理；行为变体的数值与语义必须有原版脚本/prefab 证据（ADR-002，无伤害系统）。
- **Spec:** `docs/specs/part-variant-catalog.md`
- **Out of scope:** PigForge 没有基准件的原版类型（Basket、EngineSmall、EngineBig、JetEngine、Pumpkin、GoldenPig、ColoredFrame、电路/机械 IN 扩展件）；解锁/战利品/商店系统；皮肤选择 UI 重做（沿用现有变体按钮行）；Unity 参考工程改动。
