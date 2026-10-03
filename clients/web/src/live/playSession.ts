/**
 * The client half of the play host's session handshake.
 *
 * The host hands out one player id per connection (`PlayHost.NextPlayerId`), so a bare reconnect
 * is a *new* player: the parts this client already placed stay in the world but belong to the
 * previous id and can no longer be removed -- which is exactly what clicking 连接房间 used to do
 * mid-build. A session id names the identity instead of the connection: the host resumes the same
 * player id for every socket that carries it (`PlaySessions`).
 *
 * The id lives in memory for one page load, so a reload starts a new player and so does a second
 * browser tab. That keeps the multiplayer-sandbox rule intact -- a disconnected player's parts
 * stay in the world and nobody reclaims them -- while the same page keeps its own.
 *
 * Pure TypeScript, no Vue and no socket: App.vue owns the connection, this owns the name.
 */

/** Query parameter the host reads the session id from. */
export const SESSION_PARAM = "session";

/** 16 random bytes as hex: 32 characters, well inside the host's 16..64 bound. */
export function createSessionId(): string {
  const bytes = new Uint8Array(16);
  const cryptoApi = globalThis.crypto;
  if (cryptoApi?.getRandomValues !== undefined) {
    cryptoApi.getRandomValues(bytes);
  } else {
    for (let index = 0; index < bytes.length; index += 1) {
      bytes[index] = Math.floor(Math.random() * 256);
    }
  }

  let hex = "";
  for (const byte of bytes) {
    hex += byte.toString(16).padStart(2, "0");
  }

  return hex;
}

/**
 * Returns `url` with the session parameter set, replacing any value already there so a hand-typed
 * URL cannot pin a stale identity. A URL the platform cannot parse (a relative one, say) is
 * returned unchanged: that connection is simply a new player, as it was before sessions existed.
 */
export function withSessionParam(url: string, sessionId: string): string {
  try {
    const parsed = new URL(url);
    parsed.searchParams.set(SESSION_PARAM, sessionId);
    return parsed.toString();
  } catch {
    return url;
  }
}
