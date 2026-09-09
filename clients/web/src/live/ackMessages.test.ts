import { describe, expect, it } from "vitest";
import { describeRejection } from "./ackMessages";

describe("describeRejection", () => {
  it("names the command and the rule error", () => {
    expect(describeRejection(0, 5, 6, 29)).toBe("命令 29（放置零件）被拒绝：位置已被其他零件占用");
    expect(describeRejection(2, 5, 11, 7)).toBe("命令 7（旋转零件）被拒绝：目标位姿被其他零件挡住");
    expect(describeRejection(8, 5, 14, 3)).toBe("命令 3（开关零件）被拒绝：不是你的零件");
    expect(describeRejection(9, 5, 15, 4)).toBe("命令 4（开关类型）被拒绝：该零件没有开关");
  });

  it("explains protocol and mode rejections", () => {
    expect(describeRejection(0, 4, 0, 12)).toBe("命令 12（放置零件）被拒绝：当前阶段不允许该操作");
    expect(describeRejection(3, 2, 0, 5)).toBe("命令 5（Start）被拒绝：命令序列过期");
    expect(describeRejection(1, 1, 0, 6)).toBe("命令 6（删除零件）被拒绝：重复命令");
  });

  it("falls back to raw codes when the kind or code is unknown", () => {
    expect(describeRejection(undefined, 4, 0, 1)).toBe("命令 1（命令）被拒绝：当前阶段不允许该操作");
    expect(describeRejection(0, 5, 99, 2)).toBe("命令 2（放置零件）被拒绝：status=5 error=99");
    expect(describeRejection(0, 9, 0, 2)).toBe("命令 2（放置零件）被拒绝：status=9 error=0");
  });
});
