// The half every applier shares: finding a part in `content/parts.json` by its `partTypeId` and
// rewriting what it owns, without reformatting the hand-authored document around it. The document
// is edited as text (one line per field), never re-serialised, so a rewrite touches exactly the
// bytes it owns and the diff stays readable.

import { readFileSync, writeFileSync } from "node:fs";
import { flag } from "./args.mjs";
import { contentFile } from "./paths.mjs";

/// The content document's text.
export const readContentText = () => readFileSync(contentFile(), "utf8");

/// The document's `parts` array.
export const parseParts = (text) => JSON.parse(text).parts;

/** The offsets of one part: its `"partTypeId"` field and the `"shapes"` field that closes the
 * region the part's own fields live in. Missing either is a hard error, never a guess. */
export function partSpan(text, partTypeId) {
  const anchorIndex = text.indexOf(`"partTypeId": ${partTypeId},`);
  if (anchorIndex < 0) {
    throw new Error(`anchor missing for part ${partTypeId}`);
  }

  const shapesIndex = text.indexOf('"shapes":', anchorIndex);
  if (shapesIndex < 0) {
    throw new Error(`shapes missing for part ${partTypeId}`);
  }

  return { anchorIndex, shapesIndex };
}

/** The offsets of one part's inline `capabilities` object, or null when the part has none. */
export function capabilitiesSpan(text, partTypeId) {
  const { anchorIndex, shapesIndex } = partSpan(text, partTypeId);
  const fieldsIndex = text.indexOf('"capabilities":', anchorIndex);
  if (fieldsIndex < 0 || fieldsIndex > shapesIndex) {
    return null;
  }

  const open = text.indexOf("{", fieldsIndex);
  if (open < 0) {
    return null;
  }

  return { anchorIndex, shapesIndex, open, close: matchingBrace(text, open) };
}

/** Index of the `}` (or `]`) that closes the object (or array) opening at `start`, skipping
 * braces inside JSON strings. A plain `indexOf("}")` stops at the first nested object's brace --
 * a part that already carries `attachment` or a glove's `shoot`/`wind` would then be spliced at
 * the wrong offset. */
function matchingDelimiter(text, start, open, close) {
  let depth = 0;
  let inString = false;
  for (let index = start; index < text.length; index += 1) {
    const char = text[index];
    if (inString) {
      if (char === "\\") {
        index += 1;
      } else if (char === '"') {
        inString = false;
      }

      continue;
    }

    if (char === '"') {
      inString = true;
    } else if (char === open) {
      depth += 1;
    } else if (char === close) {
      depth -= 1;
      if (depth === 0) {
        return index;
      }
    }
  }

  return -1;
}

/// `matchingDelimiter` for an object.
export const matchingBrace = (text, start) => matchingDelimiter(text, start, "{", "}");

/// `matchingDelimiter` for an array.
export const matchingBracket = (text, start) => matchingDelimiter(text, start, "[", "]");

/** End index (exclusive) of the JSON value starting at `start` inside a single-line object. */
export function valueEnd(text, start) {
  const char = text[start];
  if (char === "{") return matchingBrace(text, start) + 1;
  if (char === "[") return matchingBracket(text, start) + 1;
  if (char === '"') return text.indexOf('"', start + 1) + 1;
  let index = start;
  while (index < text.length && text[index] !== "," && text[index] !== "}") index += 1;
  return index;
}

/** The `"key":` at an inline object's TOP level (`{ start, valueStart }`), or null. A nested key
 * of the same name -- a glove's `shoot.spring` -- is not matched. */
export function topLevelKeyIndex(object, key) {
  const wanted = `"${key}"`;
  let depth = 0;
  let inString = false;
  let stringStart = -1;
  for (let index = 0; index < object.length; index += 1) {
    const char = object[index];
    if (inString) {
      if (char === "\\") {
        index += 1;
      } else if (char === '"') {
        inString = false;
        if (depth === 1 && object.slice(stringStart, index + 1) === wanted) {
          let next = index + 1;
          while (next < object.length && /\s/.test(object[next])) next += 1;
          if (object[next] === ":") {
            return { start: stringStart, valueStart: next + 1 };
          }
        }
      }

      continue;
    }

    if (char === '"') {
      stringStart = index;
      inString = true;
    } else if (char === "{" || char === "[") {
      depth += 1;
    } else if (char === "}" || char === "]") {
      depth -= 1;
    }
  }

  return null;
}

/// `{ ... }` with exactly one space inside each brace.
export const tidy = (object) => object.replace(/^\{\s*/, "{ ").replace(/\s*\}$/, " }");

/** Matches one property's value in an inline object's text (values nest at most one level). */
export const propertyPattern = (key) =>
  new RegExp(`"${key}":\\s*(?:"[^"]*"|true|false|-?[0-9.]+|\\{(?:[^{}]|\\{[^{}]*\\})*\\})`);

/** Applies `[key, rendered, append]` fields to an inline capabilities object: a missing field is
 * inserted (at the front by default, at the end when `append` is set, so the drive fields land
 * after `wheel` the way the hand-authored motor-wheel line does), an existing one replaced. */
export function upsert(capabilities, fields) {
  let object = capabilities;
  const missingHead = fields.filter(([key, , append]) => !append && !propertyPattern(key).test(object));
  const missingTail = fields.filter(([key, , append]) => append && !propertyPattern(key).test(object));
  if (missingHead.length > 0) {
    object = `{ ${missingHead.map(([, rendered]) => rendered).join(", ")},${object.slice(1)}`;
  }

  if (missingTail.length > 0) {
    object = `${object.slice(0, -1).replace(/\s+$/, "")}, ${missingTail.map(([, rendered]) => rendered).join(", ")} }`;
  }

  for (const [key, rendered] of fields) {
    object = object.replace(propertyPattern(key), rendered);
  }

  return object;
}

/** Replaces one top-level key's value in an inline object, or appends it before the closing brace
 * (the hand-authored key order is preserved: a present key stays in place). Unlike `upsert` this
 * respects nesting, so it can patch a key whose name also occurs a level down. */
export function upsertKey(object, key, rendered) {
  const found = topLevelKeyIndex(object, key);
  if (!found) {
    const body = object.replace(/^\{\s*/, "").replace(/\s*\}$/, "");
    return body.length === 0 ? `{ "${key}": ${rendered} }` : `{ ${body}, "${key}": ${rendered} }`;
  }

  let valueStart = found.valueStart;
  while (/\s/.test(object[valueStart])) valueStart += 1;
  return `${object.slice(0, found.start)}"${key}": ${rendered}${object.slice(valueEnd(object, valueStart))}`;
}

/** Removes one property (and the separator that joins it to its neighbour) from an inline object,
 * leaving every other byte alone. The leading separator is preferred, so a trailing comma can
 * never be left behind; a property that is absent is left absent. */
export function removeProperty(object, key) {
  const value = propertyPattern(key).source;
  const leading = new RegExp(`\\s*,\\s*(?:${value})`);
  if (leading.test(object)) {
    return object.replace(leading, "");
  }

  return object.replace(new RegExp(`(?:${value})\\s*,\\s*`), "");
}

/** Removes a top-level key and one separating comma from an inline object (nesting-aware; the
 * counterpart of `upsertKey`). */
export function removeKey(object, key) {
  const found = topLevelKeyIndex(object, key);
  if (!found) {
    return object;
  }

  const { start } = found;
  let valueStart = found.valueStart;
  while (/\s/.test(object[valueStart])) valueStart += 1;
  const end = valueEnd(object, valueStart);
  if (object[end] === ",") {
    return `${object.slice(0, start).replace(/\s+$/, "")}${object.slice(end + 1).replace(/^\s+/, " ")}`;
  }

  return `${object.slice(0, start).replace(/,\s*$/, "")}${object.slice(end)}`;
}

// ------------------------------------------------------------------ rendering
/** One scalar of an inline object: a string quoted, an object as JSON, anything else verbatim. */
export const renderValue = (value) =>
  typeof value === "string" ? `"${value}"` : typeof value === "object" && value !== null ? JSON.stringify(value) : String(value);

/** The inline `{ "key": value, ... }` serializer of a capabilities object. `owned` maps the keys
 * the tool owns to their own renderer, which replaces the generic one. */
export const renderCapabilities =
  (owned = {}) =>
  (object) =>
    `{ ${Object.entries(object)
      .map(([key, value]) => `"${key}": ${owned[key] ? owned[key](value) : renderValue(value)}`)
      .join(", ")} }`;

/** The semantic form of a capabilities object, so "already applied" is a value comparison and not
 * a text one. `owned` reduces the keys a tool owns; every other key is compared verbatim. */
export const canonicalCapabilities =
  (owned = {}) =>
  (capabilities) =>
    JSON.stringify(Object.entries(capabilities).map(([key, value]) => [key, owned[key] ? owned[key](value) : value]));

/// The comparison `rewriteCapabilitiesLines` uses to tell "already applied" from "needs a write".
export const isSameCapabilities = (canonical, left, right) => canonical(left) === canonical(right);

/** Rewrites each part's single-line `capabilities` object in place, preserving the rest of the
 * document byte for byte. `rewrite(partTypeId, capabilities)` returns the next capabilities
 * object, or null for a part this tool does not own; `render` serialises it back to one line. */
export function rewriteCapabilitiesLines(text, { rewrite, canonical, render }) {
  const lines = text.split("\n");
  let updated = 0;
  let unchanged = 0;
  let partTypeId = null;

  for (let index = 0; index < lines.length; index++) {
    const partMatch = /^\s*"partTypeId":\s*(\d+),\s*$/.exec(lines[index]);
    if (partMatch) {
      partTypeId = Number(partMatch[1]);
      continue;
    }

    const capabilitiesMatch = /^(\s*)"capabilities":\s*(\{.*\}),\s*$/.exec(lines[index]);
    if (!capabilitiesMatch || partTypeId === null) {
      continue;
    }

    const current = JSON.parse(capabilitiesMatch[2]);
    const next = rewrite(partTypeId, current);
    partTypeId = null;
    if (next === null) {
      continue;
    }

    if (isSameCapabilities(canonical, current, next)) {
      unchanged++;
      continue;
    }

    lines[index] = `${capabilitiesMatch[1]}"capabilities": ${render(next)},`;
    updated++;
  }

  return { text: lines.join("\n"), updated, unchanged };
}

/** The tail every applier shares: rewrite the content document, re-derive it (the tool's own
 * verification), and write it unless `--dry-run`. `rewrite(text)` returns the tool's own result
 * object, which carries the rewritten `text`; `verify(result)` returns the counters the tool
 * reports on top of the rewrite's own. */
export function applyContent({ rewrite, verify, report }) {
  const content = contentFile();
  const result = rewrite(readFileSync(content, "utf8"));
  const verified = verify ? verify(result) : {};
  const dryRun = flag("dry-run");
  if (!dryRun) {
    writeFileSync(content, result.text);
  }

  report(result, verified, { dryRun, content });
}
