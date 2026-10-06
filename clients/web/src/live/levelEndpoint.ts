import { decodeLevelContent, type LevelContentDocument } from "@/schema/levelContent";

const LEVEL_PATH = "/level";

/**
 * The HTTP endpoint that serves the level document, derived from the live socket rather than
 * hardcoded: `ws://host:port/play` (or `/snapshots`) becomes `http://host:port/level`. A relative
 * socket URL (the dev server proxy) keeps the same origin.
 */
export function levelUrlFrom(liveUrl: string): string {
  const trimmed = liveUrl.trim();
  const scheme = /^([a-z][a-z0-9+.-]*):\/\//i.exec(trimmed);
  if (scheme === null) {
    return LEVEL_PATH;
  }

  const authority = trimmed.slice(scheme[0].length).split(/[/?#]/)[0];
  const protocol = scheme[1].toLowerCase();
  const http = protocol === "wss" ? "https" : protocol === "ws" ? "http" : protocol;
  return `${http}://${authority}${LEVEL_PATH}`;
}

/**
 * Fetches and decodes the level next to the socket. A missing, unreadable or malformed document
 * yields null instead of throwing, so the caller can keep its fallback (no terrain, the builder's
 * own goal zone and bounds).
 */
export async function fetchLevel(liveUrl: string, fetcher: typeof fetch = fetch): Promise<LevelContentDocument | null> {
  try {
    const response = await fetcher(levelUrlFrom(liveUrl), { headers: { accept: "application/json" } });
    if (!response.ok) {
      return null;
    }
    return decodeLevelContent(await response.json()).level;
  } catch {
    return null;
  }
}
