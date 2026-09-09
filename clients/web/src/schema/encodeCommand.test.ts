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

  it("encodes MovePart and ScalePart matching the PGFC v2 layout", () => {
    const move = encodeCommand({ kind: 6, sequence: 2, playerId: 1, tick: 0, entityId: 7, x: 1.5, y: -2.25 });
    expect(move.byteLength).toBe(31);
    const moveView = new DataView(move.buffer);
    expect(moveView.getUint8(6)).toBe(6);
    expect(moveView.getUint32(19, true)).toBe(7);
    expect(moveView.getFloat32(23, true)).toBeCloseTo(1.5);
    expect(moveView.getFloat32(27, true)).toBeCloseTo(-2.25);

    const scale = encodeCommand({ kind: 7, sequence: 3, playerId: 1, tick: 0, entityId: 7, scale: 2.5 });
    expect(scale.byteLength).toBe(27);
    const scaleView = new DataView(scale.buffer);
    expect(scaleView.getUint8(6)).toBe(7);
    expect(scaleView.getUint32(19, true)).toBe(7);
    expect(scaleView.getFloat32(23, true)).toBeCloseTo(2.5);
  });

  it("encodes SetPartActive and SetPartTypeActive matching the PGFC layout", () => {
    const setPart = encodeCommand({ kind: 8, sequence: 4, playerId: 1, tick: 0, entityId: 7, active: true });
    expect(setPart.byteLength).toBe(24);
    const partView = new DataView(setPart.buffer);
    expect(partView.getUint8(6)).toBe(8);
    expect(partView.getUint32(19, true)).toBe(7);
    expect(partView.getUint8(23)).toBe(1);

    const setType = encodeCommand({ kind: 9, sequence: 5, playerId: 1, tick: 0, partTypeId: 11, active: false });
    expect(setType.byteLength).toBe(24);
    const typeView = new DataView(setType.buffer);
    expect(typeView.getUint8(6)).toBe(9);
    expect(typeView.getUint32(19, true)).toBe(11);
    expect(typeView.getUint8(23)).toBe(0);
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
