import { describe, expect, it } from "vitest";
import {
  decodeSnapshotFrame,
  SNAPSHOT_BUILDING_PHASE,
  SNAPSHOT_CHALLENGE_FLAGS_BYTES,
  SNAPSHOT_COLLECTED_COUNT_BYTES,
  SNAPSHOT_COLLECTED_ENTRY_BYTES,
  SNAPSHOT_ELAPSED_BYTES,
  SNAPSHOT_ENTITY_BYTES,
  SNAPSHOT_HEADER_BYTES,
  SNAPSHOT_RUN_STATE_BYTES,
  SNAPSHOT_VERSION,
} from "./decodeSnapshot";

function writeFrame(flags = 1, collected: readonly number[] = [], elapsedSeconds = 0, challengeFlags = 0): Uint8Array {
  const bytes = new Uint8Array(
    SNAPSHOT_HEADER_BYTES +
      SNAPSHOT_ENTITY_BYTES +
      SNAPSHOT_COLLECTED_COUNT_BYTES +
      collected.length * SNAPSHOT_COLLECTED_ENTRY_BYTES +
      SNAPSHOT_RUN_STATE_BYTES,
  );
  const view = new DataView(bytes.buffer);
  bytes.set([0x50, 0x47, 0x46, 0x53], 0);
  view.setUint16(4, SNAPSHOT_VERSION, true);
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
  view.setFloat32(83, 0.75, true);
  view.setUint8(87, flags);
  // v7's tail: the collected challenge markers, none unless the caller asks for them.
  const tail = SNAPSHOT_HEADER_BYTES + SNAPSHOT_ENTITY_BYTES;
  view.setUint16(tail, collected.length, true);
  collected.forEach((index, position) => {
    view.setUint32(tail + SNAPSHOT_COLLECTED_COUNT_BYTES + position * SNAPSHOT_COLLECTED_ENTRY_BYTES, index, true);
  });
  // v8's tail: the run timer and the challenge bits.
  const runState = tail + SNAPSHOT_COLLECTED_COUNT_BYTES + collected.length * SNAPSHOT_COLLECTED_ENTRY_BYTES;
  view.setFloat32(runState, elapsedSeconds, true);
  view.setUint8(runState + SNAPSHOT_ELAPSED_BYTES, challengeFlags);
  return bytes;
}

describe("decodeSnapshotFrame", () => {
  it("reads the collected markers off the tail", () => {
    const decoded = decodeSnapshotFrame(writeFrame(1, [4, 91]));
    expect(typeof decoded).not.toBe("string");
    if (typeof decoded === "string") {
      return;
    }
    expect(decoded.collected).toEqual([4, 91]);
    // A frame with nothing collected still carries the section's count.
    const empty = decodeSnapshotFrame(writeFrame());
    expect(typeof empty === "string" ? empty : empty.collected).toEqual([]);
  });

  it("reports a truncated collected tail", () => {
    const bytes = writeFrame(1, [4, 91]).slice(0, SNAPSHOT_HEADER_BYTES + SNAPSHOT_ENTITY_BYTES + 4);
    expect(decodeSnapshotFrame(bytes)).toBe("Snapshot collected payload is truncated.");
  });

  it("decodes a v8 PGFS frame", () => {
    const decoded = decodeSnapshotFrame(writeFrame());
    expect(typeof decoded).not.toBe("string");
    if (typeof decoded === "string") {
      return;
    }
    expect(decoded.version).toBe(SNAPSHOT_VERSION);
    expect(decoded.tick).toBe(7);
    expect(decoded.phase).toBe(1);
    expect(decoded.entities).toHaveLength(1);
    expect(decoded.entities[0].entityId).toBe(3);
    expect(decoded.entities[0].position[0]).toBeCloseTo(1.5);
    expect(decoded.entities[0].position[1]).toBeCloseTo(2.25);
    expect(decoded.entities[0].scale).toBeCloseTo(1);
    expect(decoded.entities[0].attachYaw).toBeCloseTo(0.75);
    expect(decoded.entities[0].active).toBe(true);
    expect(decoded.entities[0].subEntity).toBe(false);
    expect(decoded.entities[0].mirrored).toBe(false);
    expect(decoded.elapsedSeconds).toBe(0);
    expect(decoded.challengeFlags).toBe(0);
  });

  it("reads the run state off the tail", () => {
    const decoded = decodeSnapshotFrame(writeFrame(1, [4, 91], 12.5, 0b101));
    expect(typeof decoded).not.toBe("string");
    if (typeof decoded === "string") {
      return;
    }
    expect(decoded.collected).toEqual([4, 91]);
    expect(decoded.elapsedSeconds).toBeCloseTo(12.5);
    // The flags byte keeps every bit, not just the two stars consume.
    expect(decoded.challengeFlags).toBe(0b101);

    const fresh = decodeSnapshotFrame(writeFrame(1, [], 0, 0b11));
    expect(typeof fresh === "string" ? fresh : fresh.challengeFlags).toBe(0b11);
  });

  it("reports a truncated run state tail", () => {
    const bytes = writeFrame(1, [4, 91], 3.5, 0b11);
    // One byte short through all five: the section is read whole or not at all.
    for (let missing = 1; missing <= SNAPSHOT_RUN_STATE_BYTES; missing++) {
      expect(decodeSnapshotFrame(bytes.slice(0, bytes.length - missing))).toBe("Snapshot run state payload is truncated.");
    }
    expect(SNAPSHOT_ELAPSED_BYTES + SNAPSHOT_CHALLENGE_FLAGS_BYTES).toBe(SNAPSHOT_RUN_STATE_BYTES);
  });

  it("reads the sub-entity and mirror flags beside the switch flag", () => {
    const decoded = decodeSnapshotFrame(writeFrame(0b111));
    expect(typeof decoded).not.toBe("string");
    if (typeof decoded === "string") {
      return;
    }
    expect(decoded.entities[0].active).toBe(true);
    expect(decoded.entities[0].subEntity).toBe(true);
    expect(decoded.entities[0].mirrored).toBe(true);

    // Bit2 alone: a mirrored part whose switch is off.
    const mirroredOnly = decodeSnapshotFrame(writeFrame(0b100));
    expect(typeof mirroredOnly).not.toBe("string");
    if (typeof mirroredOnly === "string") {
      return;
    }
    expect(mirroredOnly.entities[0].active).toBe(false);
    expect(mirroredOnly.entities[0].subEntity).toBe(false);
    expect(mirroredOnly.entities[0].mirrored).toBe(true);
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
    const previous = writeFrame();
    new DataView(previous.buffer).setUint16(4, 6, true);
    expect(decodeSnapshotFrame(previous)).toBe("Snapshot version 6 is unsupported.");
    const lastVersion = writeFrame();
    new DataView(lastVersion.buffer).setUint16(4, 7, true);
    expect(decodeSnapshotFrame(lastVersion)).toBe("Snapshot version 7 is unsupported.");
  });
});
