/**
 * The dev harness' URL contract (`docs/specs/dev-harness.md`): `?dev=1` turns the drawer on, and
 * exactly one of `scenario` / `level` selects the world. The same query is handed to the websocket
 * and to `GET /level`, so both sides see one world; a selection is a page reload, never a live
 * swap.
 */
export interface DevWorldSelection {
  scenario?: string;
  level?: string;
}

export interface DevPageState extends DevWorldSelection {
  dev: boolean;
}

const SCENARIO = "scenario";
const LEVEL = "level";

/** Reads the drawer flag and the world selection from a query string (`location.search`). */
export function parseDevQuery(search: string): DevPageState {
  const params = new URLSearchParams(search);
  const scenarioValue = params.get(SCENARIO)?.trim() ?? "";
  const levelValue = params.get(LEVEL)?.trim() ?? "";
  const scenario = scenarioValue.length > 0 ? scenarioValue : undefined;
  const level = levelValue.length > 0 ? levelValue : undefined;
  return {
    dev: params.get("dev") === "1",
    // The server rejects both; the client mirrors it by preferring the scenario only when it is
    // the single selection.
    scenario: scenario !== undefined && level === undefined ? scenario : undefined,
    level: level !== undefined && scenario === undefined ? level : undefined,
  };
}

/** `scenario=x` or `level=y`, or an empty string for the default sandbox. */
export function worldQuery(selection: DevWorldSelection): string {
  const params = new URLSearchParams();
  if (selection.scenario !== undefined && selection.level === undefined) {
    params.set(SCENARIO, selection.scenario);
  } else if (selection.level !== undefined && selection.scenario === undefined) {
    params.set(LEVEL, selection.level);
  }

  return params.toString();
}

/**
 * The websocket URL for a world: the base keeps its own query (nothing today) and gains the
 * selection. The client and the server share this spelling, so `GET /level` can parse the same
 * query back out of the live URL.
 */
export function liveUrlFor(base: string, selection: DevWorldSelection): string {
  const query = worldQuery(selection);
  if (query.length === 0) {
    return base;
  }

  return `${base}${base.includes("?") ? "&" : "?"}${query}`;
}

/** The page URL a drawer selection navigates to: keeps `dev=1`, replaces the world. */
export function devPageUrl(search: string, selection: DevWorldSelection): string {
  const params = new URLSearchParams(search);
  params.set("dev", "1");
  params.delete(SCENARIO);
  params.delete(LEVEL);
  if (selection.scenario !== undefined && selection.level === undefined) {
    params.set(SCENARIO, selection.scenario);
  } else if (selection.level !== undefined && selection.scenario === undefined) {
    params.set(LEVEL, selection.level);
  }

  const query = params.toString();
  return query.length > 0 ? `?${query}` : "?dev=1";
}

/** The live URL a fresh page should connect to, from `location.search` (empty outside a browser). */
export function initialLiveUrl(base: string, search: string | undefined): string {
  if (search === undefined) {
    return base;
  }

  const state = parseDevQuery(search);
  return state.dev ? liveUrlFor(base, state) : base;
}
