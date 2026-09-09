import type { CommandKind } from "./playerSession";

/** PGFC kind → the action the player tried to perform. */
const COMMAND_LABELS: Record<CommandKind, string> = {
  0: "放置零件",
  1: "删除零件",
  2: "旋转零件",
  3: "Start",
  5: "RESET",
  6: "移动零件",
  7: "缩放零件",
  8: "开关零件",
  9: "开关类型",
};

/** PGFA status (CommandStatus) → reason, except RuleRejected (5), which reads the error. */
const STATUS_LABELS: Record<number, string> = {
  1: "重复命令",
  2: "命令序列过期",
  3: "tick 过期",
  4: "当前阶段不允许该操作",
  6: "未知命令",
};

/** PGFA error (ConstructionError) names for a rule rejection. */
const ERROR_LABELS: Record<number, string> = {
  1: "未知零件类型",
  2: "角度非法",
  3: "缩放非法",
  4: "位置非法",
  5: "占位面积超出上限",
  6: "位置已被其他零件占用",
  7: "零件数量已达上限",
  8: "连接数已达上限",
  9: "零件不存在",
  10: "不是建造实体",
  11: "目标位姿被其他零件挡住",
  12: "零件已冻结",
  13: "不支持的形状",
  14: "不是你的零件",
  15: "该零件没有开关",
};

/**
 * Human-readable reason for a rejected PGFA ack. Known codes become Chinese text;
 * unknown kinds/statuses/errors fall back to the raw codes so nothing is hidden.
 */
export function describeRejection(kind: CommandKind | undefined, status: number, error: number, sequence: number): string {
  const command = kind === undefined ? "命令" : (COMMAND_LABELS[kind] ?? "命令");
  const reason = status === 5
    ? ERROR_LABELS[error] ?? `status=${status} error=${error}`
    : STATUS_LABELS[status] ?? `status=${status} error=${error}`;
  return `命令 ${sequence}（${command}）被拒绝：${reason}`;
}
