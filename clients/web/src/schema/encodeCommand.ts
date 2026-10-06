import type { ClientCommand } from "./types";

export const COMMAND_VERSION = 3;
export const COMMAND_HEADER_BYTES = 19;

// v3 gives the two placement commands a trailing `mirrored:u8` (ADR-030): a build pose's
// handedness is not expressible as a rotation, so it travels beside the absolute angle.
const PAYLOAD_BYTES: Record<ClientCommand["kind"], number> = { 0: 21, 1: 4, 2: 9, 3: 0, 5: 0, 6: 12, 7: 8, 8: 5, 9: 5 };

export function encodeCommand(command: ClientCommand): Uint8Array {
  const bytes = new Uint8Array(COMMAND_HEADER_BYTES + PAYLOAD_BYTES[command.kind]);
  const view = new DataView(bytes.buffer);
  bytes.set([0x50, 0x47, 0x46, 0x43], 0);
  view.setUint16(4, COMMAND_VERSION, true);
  view.setUint8(6, command.kind);
  view.setUint32(7, command.sequence, true);
  view.setUint32(11, command.playerId, true);
  view.setUint32(15, command.tick, true);
  if (command.kind === 0) {
    view.setUint32(19, command.partTypeId, true);
    view.setFloat32(23, command.x, true);
    view.setFloat32(27, command.y, true);
    view.setFloat32(31, command.angle, true);
    view.setFloat32(35, command.scale, true);
    view.setUint8(39, command.mirrored ? 1 : 0);
  } else if (command.kind === 1) {
    view.setUint32(19, command.entityId, true);
  } else if (command.kind === 2) {
    view.setUint32(19, command.entityId, true);
    view.setFloat32(23, command.angle, true);
    view.setUint8(27, command.mirrored ? 1 : 0);
  } else if (command.kind === 6) {
    view.setUint32(19, command.entityId, true);
    view.setFloat32(23, command.x, true);
    view.setFloat32(27, command.y, true);
  } else if (command.kind === 7) {
    view.setUint32(19, command.entityId, true);
    view.setFloat32(23, command.scale, true);
  } else if (command.kind === 8) {
    view.setUint32(19, command.entityId, true);
    view.setUint8(23, command.active ? 1 : 0);
  } else if (command.kind === 9) {
    view.setUint32(19, command.partTypeId, true);
    view.setUint8(23, command.active ? 1 : 0);
  }
  return bytes;
}

export function decodeAck(source: Uint8Array): { sequence: number; status: number; error: number; entityId: number } | string {
  if (source.length < 16) {
    return "Ack frame is truncated.";
  }
  if (source[0] !== 0x50 || source[1] !== 0x47 || source[2] !== 0x46 || source[3] !== 0x41) {
    return "Ack magic must be PGFA.";
  }
  const view = new DataView(source.buffer, source.byteOffset, source.byteLength);
  if (view.getUint16(4, true) !== COMMAND_VERSION) {
    return "Ack version is unsupported.";
  }
  return {
    sequence: view.getUint32(6, true),
    status: view.getUint8(10),
    error: view.getUint8(11),
    entityId: view.getUint32(12, true),
  };
}
