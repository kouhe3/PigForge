#!/usr/bin/env node
// Adds each part's build-mode bracket to content/parts.json as a conditional shape with
// `condition.kind === "frame"`. A bracket is what a part is placed and occupancy-checked by:
// a glider wing's collider (1.9 m wide) is its wing, which overhangs its neighbours, while the
// frame it welds with is a single cell (ADR-018). Reads the original sprite manifest — the same
// `frame` art the build overlay draws — and is idempotent: it replaces the frame shape it
// previously wrote, and leaves every hand-authored shape alone.
//
// Usage: node tools/bple-brackets/apply-brackets.mjs [--dry-run]

import { readFileSync } from "node:fs";
import { MANIFEST } from "../lib/paths.mjs";
import { applyContent, matchingBracket, partSpan } from "../lib/parts.mjs";
import { numInt1 } from "../lib/report.mjs";

const round4 = (value) => Number(value.toFixed(4));

/** Bracket bounds of one texture, in world metres relative to the part origin; null without one. */
function bracketFor(texture) {
  const frames = (texture.sprites ?? []).filter((sprite) => sprite.condition?.kind === "frame");
  if (frames.length === 0) return null;
  let minX = Number.POSITIVE_INFINITY;
  let maxX = Number.NEGATIVE_INFINITY;
  let minY = Number.POSITIVE_INFINITY;
  let maxY = Number.NEGATIVE_INFINITY;
  for (const frame of frames) {
    minX = Math.min(minX, frame.cx - frame.sx / 2);
    maxX = Math.max(maxX, frame.cx + frame.sx / 2);
    minY = Math.min(minY, frame.cy - frame.sy / 2);
    maxY = Math.max(maxY, frame.cy + frame.sy / 2);
  }

  return {
    halfX: round4((maxX - minX) / 2),
    halfY: round4((maxY - minY) / 2),
    offsetX: round4((minX + maxX) / 2),
    offsetY: round4((minY + maxY) / 2),
  };
}

const isFrame = (shape) => shape.condition?.kind === "frame";

/** A part's shapes with its bracket appended as the last, conditional entry. */
function withBracket(shapes, bracket) {
  const body = shapes.filter((shape) => !isFrame(shape));
  const depth = body.find((shape) => shape.kind === "box")?.halfExtents[2] ?? 0.5;
  return [
    ...body,
    {
      kind: "box",
      halfExtents: [bracket.halfX, bracket.halfY, depth],
      offset: [bracket.offsetX, bracket.offsetY, 0],
      condition: { kind: "frame" },
    },
  ];
}

function renderShapes(shapes, indent) {
  const lines = [`${indent}"shapes": [`];
  shapes.forEach((shape, index) => {
    const properties = [`${indent}    "kind": "${shape.kind}"`];
    properties.push(`${indent}    "halfExtents": [${shape.halfExtents.map(numInt1).join(", ")}]`);
    properties.push(`${indent}    "offset": [${shape.offset.map(numInt1).join(", ")}]`);
    if (shape.condition) {
      properties.push(`${indent}    "condition": { "kind": "${shape.condition.kind}" }`);
    }

    lines.push(`${indent}  {`);
    lines.push(properties.join(",\n"));
    lines.push(`${indent}  }${index === shapes.length - 1 ? "" : ","}`);
  });
  lines.push(`${indent}]`);
  return lines;
}

const textures = JSON.parse(readFileSync(MANIFEST, "utf8")).parts;
const brackets = new Map();
for (const [partTypeId, texture] of Object.entries(textures)) {
  const bracket = bracketFor(texture);
  if (bracket !== null) {
    brackets.set(Number(partTypeId), bracket);
  }
}

applyContent({
  rewrite: (text) => {
    const document = JSON.parse(text);
    let updated = 0;

    for (const part of document.parts) {
      const bracket = brackets.get(part.partTypeId);
      if (bracket === undefined) {
        continue;
      }

      const shapes = withBracket(part.shapes, bracket);
      const { shapesIndex } = partSpan(text, part.partTypeId);
      const lineStart = text.lastIndexOf("\n", shapesIndex) + 1;
      const open = text.indexOf("[", shapesIndex);
      text = `${text.slice(0, lineStart)}${renderShapes(shapes, "      ").join("\n")}${text.slice(matchingBracket(text, open) + 1)}`;
      updated += 1;
      console.log(`${String(part.partTypeId).padStart(3)} ${part.name.padEnd(22)} bracket x[${round4(bracket.offsetX - bracket.halfX)}, ${round4(bracket.offsetX + bracket.halfX)}]`);
    }

    return { text, updated };
  },
  // Re-parse and re-derive: the rewrite must be valid JSON and idempotent.
  verify: (result) => {
    const check = JSON.parse(result.text);
    for (const part of check.parts) {
      const bracket = brackets.get(part.partTypeId);
      if (bracket === undefined) continue;
      const expected = withBracket(part.shapes, bracket);
      if (JSON.stringify(part.shapes) !== JSON.stringify(expected)) {
        throw new Error(`bracket mismatch for part ${part.partTypeId}`);
      }
    }
  },
  report: ({ updated }, _verified, { dryRun, content }) => {
    console.log(`${dryRun ? "would update" : "updated"} ${updated} parts in ${content}`);
  },
});
