import { describe, expect, it } from "vitest";
import { decodeAck, encodeCommand } from "./encodeCommand";

describe("encodeCommand", () => {
  it("encodes PlacePart matching PGFC layout", () => {
    const bytes = encodeCommand({
      kind: 0,
      sequence: 1,
      playerId: 1,
      tick: 0,
      partTypeId: 4,
      x: -5.25,
      y: 4.5,
      angle: -0.35,
      scale: 1.5,
    });
    const view = new DataView(bytes.buffer);
    expect(String.fromCharCode(...bytes.slice(0, 4))).toBe("PGFC");
    expect(view.getUint16(4, true)).toBe(2);
    expect(view.getUint8(6)).toBe(0);
    expect(view.getUint32(7, true)).toBe(1);
    expect(view.getUint32(19, true)).toBe(4);
    expect(view.getFloat32(23, true)).toBeCloseTo(-5.25);
    expect(view.getFloat32(35, true)).toBeCloseTo(1.5);
  });

  it("decodes PGFA ack", () => {
    const bytes = new Uint8Array(16);
    const view = new DataView(bytes.buffer);
    bytes.set([0x50, 0x47, 0x46, 0x41], 0);
    view.setUint16(4, 2, true);
    view.setUint32(6, 3, true);
    view.setUint8(10, 0);
    view.setUint8(11, 0);
    view.setUint32(12, 9, true);
    const ack = decodeAck(bytes);
    expect(typeof ack).not.toBe("string");
    if (typeof ack === "string") {
      return;
    }
    expect(ack.sequence).toBe(3);
    expect(ack.entityId).toBe(9);
  });
});
