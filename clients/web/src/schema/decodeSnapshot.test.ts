import { describe, expect, it } from "vitest";
import { decodeSnapshotFrame, SNAPSHOT_BUILDING_PHASE, SNAPSHOT_ENTITY_BYTES, SNAPSHOT_HEADER_BYTES } from "./decodeSnapshot";

function writeFrame(): Uint8Array {
  const bytes = new Uint8Array(SNAPSHOT_HEADER_BYTES + SNAPSHOT_ENTITY_BYTES);
  const view = new DataView(bytes.buffer);
  bytes.set([0x50, 0x47, 0x46, 0x53], 0);
  view.setUint16(4, 2, true);
  view.setUint32(6, 7, true);
  view.setUint8(10, 1);
  view.setUint32(11, 1, true);
  view.setUint32(15, 3, true);
  view.setUint32(19, 4, true);
  view.setUint32(23, 1, true);
  view.setFloat32(27, 1.5, true);
  view.setFloat32(31, 2.25, true);
  view.setFloat32(35, 0, true);
  view.setFloat32(39, 0, true);
  view.setFloat32(43, 0, true);
  view.setFloat32(47, 0, true);
  view.setFloat32(51, 1, true);
  view.setFloat32(55, 0.1, true);
  view.setFloat32(59, -0.2, true);
  view.setFloat32(63, 0, true);
  view.setFloat32(67, 0, true);
  view.setFloat32(71, 0, true);
  view.setFloat32(75, 0, true);
  view.setFloat32(79, 1, true);
  return bytes;
}

describe("decodeSnapshotFrame", () => {
  it("decodes a v2 PGFS frame", () => {
    const decoded = decodeSnapshotFrame(writeFrame());
    expect(typeof decoded).not.toBe("string");
    if (typeof decoded === "string") {
      return;
    }
    expect(decoded.tick).toBe(7);
    expect(decoded.phase).toBe(1);
    expect(decoded.entities).toHaveLength(1);
    expect(decoded.entities[0].entityId).toBe(3);
    expect(decoded.entities[0].position[0]).toBeCloseTo(1.5);
    expect(decoded.entities[0].position[1]).toBeCloseTo(2.25);
    expect(decoded.entities[0].scale).toBeCloseTo(1);
  });

  it("exposes building phase 0x10", () => {
    expect(SNAPSHOT_BUILDING_PHASE).toBe(0x10);
  });

  it("rejects v1 magic or version", () => {
    const bytes = writeFrame();
    bytes[0] = 0x58;
    expect(decodeSnapshotFrame(bytes)).toBe("Snapshot magic must be PGFS.");
    const versioned = writeFrame();
    new DataView(versioned.buffer).setUint16(4, 1, true);
    expect(decodeSnapshotFrame(versioned)).toBe("Snapshot version 1 is unsupported.");
  });
});
