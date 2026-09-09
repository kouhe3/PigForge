import { describe, expect, it } from "vitest";
import { createPlayerSession } from "./playerSession";

describe("playerSession", () => {
  it("starts editing with no owned entities and no pending commands", () => {
    const session = createPlayerSession();
    expect(session.phase).toBe("editing");
    expect(session.ownEntityIds.size).toBe(0);
    expect(session.pending.size).toBe(0);
  });

  it("records sent commands under increasing sequences", () => {
    const session = createPlayerSession();
    expect(session.noteSent(0)).toBe(1);
    expect(session.noteSent(1, 7)).toBe(2);
    expect([...session.pending.entries()]).toEqual([
      [1, { kind: 0 }],
      [2, { kind: 1, entityId: 7 }],
    ]);
  });

  it("adds the acked entity id on an accepted place", () => {
    const session = createPlayerSession();
    const sequence = session.noteSent(0);
    expect(session.applyAck({ sequence, status: 0, error: 0, entityId: 42 })).toBeNull();
    expect(session.ownEntityIds.has(42)).toBe(true);
    expect(session.pending.size).toBe(0);
  });

  it("drops the sent entity id on an accepted remove", () => {
    const session = createPlayerSession();
    const place = session.noteSent(0);
    session.applyAck({ sequence: place, status: 0, error: 0, entityId: 42 });
    const remove = session.noteSent(1, 42);
    // The server echoes entityId 0 for removes; ownership comes from the sent command.
    session.applyAck({ sequence: remove, status: 0, error: 0, entityId: 0 });
    expect(session.ownEntityIds.has(42)).toBe(false);
  });

  it("keeps ownership and phase for accepted move and scale acks", () => {
    const session = createPlayerSession();
    const place = session.noteSent(0);
    session.applyAck({ sequence: place, status: 0, error: 0, entityId: 42 });
    const move = session.noteSent(6, 42);
    session.applyAck({ sequence: move, status: 0, error: 0, entityId: 0 });
    const scale = session.noteSent(7, 42);
    session.applyAck({ sequence: scale, status: 0, error: 0, entityId: 0 });
    expect(session.ownEntityIds.has(42)).toBe(true);
    expect(session.phase).toBe("editing");
  });

  it("materializes on an accepted start", () => {
    const session = createPlayerSession();
    const place = session.noteSent(0);
    session.applyAck({ sequence: place, status: 0, error: 0, entityId: 5 });
    const start = session.noteSent(3);
    session.applyAck({ sequence: start, status: 0, error: 0, entityId: 0 });
    expect(session.phase).toBe("materialized");
    expect(session.ownEntityIds.has(5)).toBe(true);
  });

  it("returns to editing and clears owned entities on an accepted retry", () => {
    const session = createPlayerSession();
    const place = session.noteSent(0);
    session.applyAck({ sequence: place, status: 0, error: 0, entityId: 5 });
    const start = session.noteSent(3);
    session.applyAck({ sequence: start, status: 0, error: 0, entityId: 0 });
    const retry = session.noteSent(5);
    session.applyAck({ sequence: retry, status: 0, error: 0, entityId: 0 });
    expect(session.phase).toBe("editing");
    expect(session.ownEntityIds.size).toBe(0);
  });

  it("surfaces a rejection and changes no state", () => {
    const session = createPlayerSession();
    const place = session.noteSent(0);
    session.applyAck({ sequence: place, status: 0, error: 0, entityId: 5 });
    const remove = session.noteSent(1, 5);
    const message = session.applyAck({ sequence: remove, status: 5, error: 2, entityId: 0 });
    expect(message).toBe("命令 2 被拒绝 status=5 error=2");
    expect(session.phase).toBe("editing");
    expect(session.ownEntityIds.has(5)).toBe(true);
    expect(session.pending.has(remove)).toBe(false);
  });

  it("ignores an accepted ack for an unknown sequence", () => {
    const session = createPlayerSession();
    expect(session.applyAck({ sequence: 99, status: 0, error: 0, entityId: 7 })).toBeNull();
    expect(session.ownEntityIds.size).toBe(0);
  });

  it("reset clears phase, owned entities, and pending commands", () => {
    const session = createPlayerSession();
    const place = session.noteSent(0);
    session.applyAck({ sequence: place, status: 0, error: 0, entityId: 5 });
    const start = session.noteSent(3);
    session.applyAck({ sequence: start, status: 0, error: 0, entityId: 0 });
    session.reset();
    expect(session.phase).toBe("editing");
    expect(session.ownEntityIds.size).toBe(0);
    expect(session.pending.size).toBe(0);
    // Sequences never repeat, so a reconnected player can never replay an old one.
    expect(session.noteSent(0)).toBeGreaterThan(start);
  });
});
