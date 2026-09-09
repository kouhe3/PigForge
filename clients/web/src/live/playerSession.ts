/**
 * Per-connection player session: the local Editing/Materialized state machine, the set of
 * entities this client owns (learned from PGFA acks), and the pending command bookkeeping
 * that maps an ack sequence back to the command it answers.
 *
 * The server stays authoritative: ownership is granted by the entityId on an accepted Place
 * ack, never guessed from snapshot frames (owner is not on the wire). This module is pure
 * TypeScript with no Vue imports so it can be tested without a component harness.
 */

export type PlayerPhase = "editing" | "materialized";

/** PGFC kinds the sandbox client sends; kind 4 (EnterBuildMode) is not wire-encodable. */
export type CommandKind = 0 | 1 | 2 | 3 | 5;

export const ACK_ACCEPTED = 0;

export interface PlayerAck {
  sequence: number;
  status: number;
  error: number;
  entityId: number;
}

export interface PendingCommand {
  kind: CommandKind;
  /** Removed id for Remove commands, so an accepted ack can drop it locally. */
  entityId?: number;
}

export interface PlayerSession {
  readonly phase: PlayerPhase;
  readonly ownEntityIds: ReadonlySet<number>;
  readonly pending: ReadonlyMap<number, PendingCommand>;
  /**
   * Records a command about to be sent and returns the sequence to put on the wire.
   * Sequences only need to increase per connection (the server rejects duplicates/stale).
   */
  noteSent(kind: CommandKind, entityId?: number): number;
  /** Applies a PGFA ack; returns the rejection message, or null when accepted/unknown. */
  applyAck(ack: PlayerAck): string | null;
  /** Clears all per-connection state on connect/disconnect; sequences stay monotonic. */
  reset(): void;
}

export function createPlayerSession(): PlayerSession {
  let phase: PlayerPhase = "editing";
  let ownEntityIds = new Set<number>();
  let pending = new Map<number, PendingCommand>();
  let sequence = 1;

  return {
    get phase(): PlayerPhase {
      return phase;
    },
    get ownEntityIds(): ReadonlySet<number> {
      return ownEntityIds;
    },
    get pending(): ReadonlyMap<number, PendingCommand> {
      return pending;
    },
    noteSent(kind: CommandKind, entityId?: number): number {
      const value = sequence;
      sequence = value + 1;
      pending.set(value, entityId === undefined ? { kind } : { kind, entityId });
      return value;
    },
    applyAck(ack: PlayerAck): string | null {
      const entry = pending.get(ack.sequence);
      pending.delete(ack.sequence);
      if (ack.status !== ACK_ACCEPTED) {
        return `命令 ${ack.sequence} 被拒绝 status=${ack.status} error=${ack.error}`;
      }
      if (entry === undefined) {
        return null;
      }
      if (entry.kind === 0) {
        if (ack.entityId !== 0) {
          ownEntityIds.add(ack.entityId);
        }
      } else if (entry.kind === 1) {
        if (entry.entityId !== undefined) {
          ownEntityIds.delete(entry.entityId);
        }
      } else if (entry.kind === 3) {
        phase = "materialized";
      } else if (entry.kind === 5) {
        phase = "editing";
        ownEntityIds.clear();
      }
      return null;
    },
    reset(): void {
      phase = "editing";
      ownEntityIds = new Set<number>();
      pending = new Map<number, PendingCommand>();
    },
  };
}
