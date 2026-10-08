// Report and assertion conventions shared by every tools/bple-* tool: how a number is rendered
// into content/parts.json, how a histogram is counted and printed, how hard assertions are
// collected, and how the JSON/Markdown report artifacts are written.

import { mkdirSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";

/// A tool's exit-1 path for a condition it cannot continue past.
export const fail = (message) => {
  console.error(message);
  process.exit(1);
};

// ------------------------------------------------------------------ number rendering
// Content is hand-authored, so a rendered value has to look like the text around it: an integral
// float keeps a decimal (`4.0`, never `4`), a tick count never looks like a float. The four
// spellings below are the ones the hand-authored document already used where each tool first ran.

/// At most six decimals, with an explicit `.0` for an integral value.
export const num6 = (value) => {
  const rounded = Number(value.toFixed(6));
  return Number.isInteger(rounded) ? `${rounded}.0` : String(rounded);
};

/// At most four decimals; an integral value stays bare (`4`).
export const num4 = (value) => (Number.isInteger(value) ? String(value) : String(Number(value.toFixed(4))));

/// An integral value as its single decimal (`4.0`, `0.5`), anything else at four decimals.
export const numInt1 = (value) => (Number.isInteger(value) ? value.toFixed(1) : String(Number(value.toFixed(4))));

/// At most six decimals without a forced `.0`.
export const numPlain6 = (value) => String(Number(Number(value).toFixed(6)));

/// A number rounded to six decimals, for the report's own numbers (already validated as finite).
export const round6 = (value) => Number(value.toFixed(6));

/// A number rounded to six decimals, or null for anything else -- so a value of the wrong shape
/// never compares equal to an extracted one.
export const numberOrNull6 = (value) => (typeof value === "number" && Number.isFinite(value) ? Number(value.toFixed(6)) : null);

/// `f(value, digits)`: a number rounded for a Markdown table, anything else verbatim.
export const fixed = (value, digits = 6) => (typeof value === "number" ? Number(value.toFixed(digits)) : value);

// ------------------------------------------------------------------ histograms
/** Bump one histogram bucket. The histograms are Maps, not objects: a JSON object reorders an
 * integer-like key (`"1"`) to the front, and a Map keeps the counting order explicit. */
export function count(histogram, value) {
  const key = String(value);
  histogram.set(key, (histogram.get(key) ?? 0) + 1);
}

const entriesOf = (histogram) => (histogram instanceof Map ? [...histogram] : Object.entries(histogram));

/** A histogram's entries, ascending numerically where the keys are numbers (9 before 18, which a
 * plain string sort gets wrong) and lexically otherwise (`"1,0,0"`, a direction triple). */
export const sortedEntries = (histogram) =>
  entriesOf(histogram).sort(([left], [right]) =>
    Number.isFinite(Number(left)) && Number.isFinite(Number(right)) ? Number(left) - Number(right) : left.localeCompare(right));

/** The histogram as the report prints it, in counting order. */
export const histogramLine = (histogram) =>
  `{ ${entriesOf(histogram).map(([key, value]) => `"${key}": ${value}`).join(", ")} }`;

/// The histogram as the report prints it, keys sorted.
export const sortedHistogramLine = (histogram) =>
  `{ ${sortedEntries(histogram).map(([key, value]) => `"${key}": ${value}`).join(", ")} }`;

/// The histogram as a JSON object, keys sorted.
export const numericHistogram = (histogram) => Object.fromEntries(sortedEntries(histogram));

// ------------------------------------------------------------------ report artifacts
/** Writes a report artifact, creating its directory. */
export function writeArtifact(path, text) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, text);
}

/// The JSON report convention: `JSON.stringify(value, null, 2)` and a final newline.
export const writeJsonArtifact = (path, value) => writeArtifact(path, `${JSON.stringify(value, null, 2)}\n`);

/// The Markdown report convention: the lines joined, with a final newline.
export const writeMarkdownArtifact = (path, lines) => writeArtifact(path, `${lines.join("\n")}\n`);

// ------------------------------------------------------------------ hard assertions
/**
 * A tool's hard-assertion collector. Every `expect*` records a failure instead of throwing, so
 * one run reports every drift at once, and `verify` is the single exit-1 site. A tool whose
 * failure text differs from the shapes here pushes its own message with `push`.
 */
export function checks() {
  const failures = [];
  const record = (condition, message) => {
    if (!condition) {
      failures.push(message);
    }
  };

  return {
    failures,

    /// `label`: the actual value, expected `wanted` (identity).
    equal: (label, actual, wanted) => record(actual === wanted, `${label}: ${actual}, expected ${wanted}`),

    /// The same, within 1e-9, for a value computed by two different orders of operation.
    close: (label, actual, wanted) =>
      record(typeof actual === "number" && Math.abs(actual - wanted) <= 1e-9, `${label}: ${actual}, expected ${wanted}`),

    /// A histogram (a Map, or a plain object) against its expected `[key, count]` pairs.
    histogram: (label, histogram, wanted) => {
      const actual = JSON.stringify(numericHistogram(histogram));
      const expected = JSON.stringify(Object.fromEntries(wanted));
      record(actual === expected, `${label}: ${actual}, expected ${expected}`);
    },

    /// A histogram (a Map) against an expected `[key, count]` array in counting order.
    histogramOrdered: (label, histogram, wanted) => {
      const actual = JSON.stringify(entriesOf(histogram));
      const expected = JSON.stringify(wanted);
      record(actual === expected, `${label}: ${actual}, expected ${expected}`);
    },

    /// Any JSON-serialisable value against its expectation.
    matches: (label, actual, wanted) => {
      const expected = JSON.stringify(wanted);
      const actualText = JSON.stringify(actual);
      record(actualText === expected, `${label}: expected ${expected}, got ${actualText}`);
    },

    /// Free-form failure text, for the checks that build their own message.
    push: (message) => {
      failures.push(message);
    },

    /// Prints every failure and exits 1; a clean run prints nothing. `trailingBlankLine` is for
    /// the tools whose failure block has always ended with one.
    verify: (header, { trailingBlankLine = false } = {}) => {
      if (failures.length === 0) {
        return;
      }

      console.error(header);
      for (const failure of failures) {
        console.error(`  - ${failure}`);
      }

      if (trailingBlankLine) {
        console.error("");
      }

      process.exit(1);
    },
  };
}
