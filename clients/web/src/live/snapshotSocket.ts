import { decodeSnapshotFrame } from "@/schema/decodeSnapshot";
import { toDrawEntities } from "@/schema/toDrawEntities";
import type { DrawEntity } from "@/schema/types";

export interface LiveSnapshot {
  tick: number;
  phase: number;
  entities: DrawEntity[];
  /**
   * v7: the level props a challenge has collected, as indices into the level document's own `props`
   * (`docs/specs/level-challenges.md` §5).
   */
  collected: number[];
  /**
   * v8: the run timer's reading in seconds, frozen when the pig entered the goal area
   * (`docs/specs/level-stars.md` §5).
   */
  elapsedSeconds: number;
  /** v8: bit i is `challenges[i]` completed; 0 until the run is won. */
  challengeFlags: number;
}

/** Client never sends pose, contact, break, or outcome — snapshots inbound only. */
export function connectSnapshotSocket(
  url: string,
  onSnapshot: (snapshot: LiveSnapshot) => void,
  onError: (message: string) => void,
): () => void {
  const socket = new WebSocket(url);
  socket.binaryType = "arraybuffer";
  socket.addEventListener("message", (event) => {
    if (!(event.data instanceof ArrayBuffer)) {
      onError("Live socket received a non-binary frame; the client ignores authoritative text.");
      return;
    }
    const decoded = decodeSnapshotFrame(new Uint8Array(event.data));
    if (typeof decoded === "string") {
      onError(decoded);
      return;
    }
    onSnapshot({
      tick: decoded.tick,
      phase: decoded.phase,
      entities: toDrawEntities(decoded.entities),
      collected: decoded.collected,
      elapsedSeconds: decoded.elapsedSeconds,
      challengeFlags: decoded.challengeFlags,
    });
  });
  socket.addEventListener("error", () => onError("Live snapshot socket failed."));
  socket.addEventListener("close", () => onError("Live snapshot socket closed."));
  return () => socket.close();
}
