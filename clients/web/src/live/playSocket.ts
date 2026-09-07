import { decodeAck, encodeCommand } from "@/schema/encodeCommand";
import { decodeSnapshotFrame } from "@/schema/decodeSnapshot";
import { toDrawEntities } from "@/schema/toDrawEntities";
import type { ClientCommand, DrawEntity } from "@/schema/types";

export interface PlaySnapshot {
  tick: number;
  phase: number;
  entities: DrawEntity[];
}

export function connectPlaySocket(
  url: string,
  onSnapshot: (snapshot: PlaySnapshot) => void,
  onAck: (ack: { sequence: number; status: number; error: number; entityId: number }) => void,
  onError: (message: string) => void,
): { send(command: ClientCommand): void; close(): void } {
  const socket = new WebSocket(url);
  socket.binaryType = "arraybuffer";
  socket.addEventListener("message", (event) => {
    if (!(event.data instanceof ArrayBuffer)) {
      onError("Live socket received a non-binary frame; the client ignores authoritative text.");
      return;
    }
    const bytes = new Uint8Array(event.data);
    if (bytes.length >= 4 && bytes[0] === 0x50 && bytes[1] === 0x47 && bytes[2] === 0x46 && bytes[3] === 0x53) {
      const decoded = decodeSnapshotFrame(bytes);
      if (typeof decoded === "string") {
        onError(decoded);
        return;
      }
      onSnapshot({
        tick: decoded.tick,
        phase: decoded.phase,
        entities: toDrawEntities(decoded.entities),
      });
      return;
    }
    const ack = decodeAck(bytes);
    if (typeof ack === "string") {
      onError(ack);
      return;
    }
    onAck(ack);
  });
  socket.addEventListener("error", () => onError("Play socket failed."));
  socket.addEventListener("close", () => onError("Play socket closed."));
  return {
    send(command: ClientCommand): void {
      if (socket.readyState === WebSocket.OPEN) {
        socket.send(encodeCommand(command));
      }
    },
    close(): void {
      socket.close();
    },
  };
}
