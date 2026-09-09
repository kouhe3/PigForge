# Intent: 建造编辑工具（Advanced Building，本机）

> 2026-09-09 访谈确认（协议方案与选择范围）。后续 spec/计划消费本文件，不重新猜范围。

- **Outcome:** Web 建造模式有一组显式工具——**选择 / 移动 / 旋转 / 缩放**——作用于已放置的**预览零件**（Editing 状态），交互参照 Besiege 的 Advanced Building（选中后用工具精确变换）；服务端仍是唯一权威，变换走新的 PGFC 命令而不是位姿上传。
- **User:** 仓库维护者本人 + 本机多开的玩家（多标签/多窗口），用于把载具搭得比"点一下就摆"更精确。
- **Why now:** 多玩家沙盒闭环已完成，建造体验的下一瓶颈是放置后无法调整：目前只有 `R` 旋转 15°、`Delete` 删除，摆歪了只能删掉重摆。
- **Success:** 选中一个自己摆的零件，用移动/旋转/缩放工具把它调到想要的位置和大小，另一个标签实时看到同样的预览位姿；动别人的零件被服务器拒绝并提示；Start 后位姿与编辑时一致。
- **Constraint:** 权威只在服务器；拖拽期间不连续上传位姿，松手发一条构建命令；沿用 PGFC 19B 头与 v2 版本（只追加 kind 6/7）；沿用确定性构建规则与 per-player 归属校验；`ConstructionRules` 仍是唯一占位/连接真相。
- **Spec:** `docs/specs/advanced-building.md`
- **Out of scope:** 多选/框选、镜像/对称/复制、数值输入面板（Transform Mapper）、撤销/重做、把 `badpiggies-editor`（地形编辑器）的任何工具搬进来。
