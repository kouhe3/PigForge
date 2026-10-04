#!/usr/bin/env node
// Rewrites the `shapes` block of every mapped part in content/parts.json from the
// BPLE collider report, preserving the file's hand-authored formatting everywhere
// else. Companion to extract-shapes.mjs; see ADR-005 for the mapping rules.
//
// Usage:
//   node tools/bple-shapes/extract-shapes.mjs --out artifacts/bple-part-shapes.json
//   node tools/bple-shapes/apply-shapes.mjs [--report <file>] [--content <file>] [--dry-run]
//
// Mapping rules (ADR-005, revised by ADR-007):
//   - Every non-trigger body collider of the original becomes one PigForge shape with
//     its local offset: a wheel is the tire sphere plus the support box that mounts it,
//     exactly as BPLE builds it.
//   - A capsule becomes a CHAIN OF SPHERES of its own radius along its own axis (the physics
//     contract has no capsule shape): the original's capsules are round, and their envelope box
//     gave the king pig a square collision. Sphere centres run from -reach to +reach along the
//     capsule's axis, `reach = max(height/2 - radius, 0)`, spaced at most `radius / 3` apart
//     (so the union's surface dips at most `radius - sqrt(radius^2 - (radius/6)^2)`, 1.4% of the
//     radius, between spheres) and capped at 12 spheres. The node's Z rotation is applied to the
//     chain, so a tilted capsule (the egg's collider sits at -45 degrees) keeps its axis.
//   - Z half-extent of a box comes from the collider's Z size when positive, otherwise the
//     previous shape's Z (parts are one unit deep).
//   - Parts with no body collider keep their authored shape.
//   - This tool REPLACES a part's whole `shapes` array, so it also drops the conditional
//     `condition.kind === "frame"` bracket that tools/bple-brackets/apply-brackets.mjs appends
//     (ADR-018). Always run apply-brackets.mjs afterwards: extract-shapes -> apply-shapes ->
//     apply-brackets is the canonical order.

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "artifacts", "bple-part-shapes.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8")).parts;

const round4 = (value) => Number(value.toFixed(4));
const num = (value) => (Number.isInteger(value) ? value.toFixed(1) : String(round4(value)));

/** PigForge shapes for a part: every original body collider with its local offset. */
function shapesFor(partTypeId, current) {
  const entry = report[String(partTypeId)];
  if (!entry || entry.shapes.length === 0) return null;
  // A prefab whose only colliders are attachment markers (spotlight) has no body geometry to
  // extract: keep the authored shape instead of replacing the part's body with nothing.
  if (entry.shapes.every((shape) => shape.condition)) return null;
  const previousZ = current[0].kind === "box" ? current[0].halfExtents[2] : current[0].radius;
  const body = [];
  for (const shape of entry.shapes) {
    const offset = [round4(shape.offset[0]), round4(shape.offset[1]), round4(shape.offset[2])];
    const place = (value) => (offset.some((component) => component !== 0) ? { ...value, offset } : value);
    const values = [];
    if (shape.kind === "box") {
      const zHalf = shape.size[2] > 0 ? shape.size[2] / 2 : previousZ;
      values.push({ kind: "box", halfExtents: [round4(shape.size[0] / 2), round4(shape.size[1] / 2), round4(zHalf)] });
    } else if (shape.kind === "sphere") {
      values.push({ kind: "sphere", radius: round4(shape.radius) });
    } else {
      // Capsule: `radius` around a segment along `m_Direction` (0 = X, 1 = Y, 2 = Z), rotated by
      // the node's own Z angle.
      const reach = Math.max(shape.height / 2 - shape.radius, 0);
      const radius = round4(shape.radius);
      const count = reach > 0
        // The epsilon keeps float noise out of the count: `2 * 0.15` is 0.30000000000000004, which
        // would otherwise round the king pig's chain up from two spheres to three.
        ? Math.min(Math.max(Math.ceil((2 * reach) / (shape.radius / 3) - 1e-9) + 1, 2), 12)
        : 1;
      const axis = shape.direction === 0 ? [1, 0, 0] : shape.direction === 1 ? [0, 1, 0] : [0, 0, 1];
      const cos = Math.cos(shape.angle ?? 0);
      const sin = Math.sin(shape.angle ?? 0);
      const rotated = [cos * axis[0] - sin * axis[1], sin * axis[0] + cos * axis[1], axis[2]];
      for (let index = 0; index < count; index++) {
        const along = count === 1 ? 0 : -reach + (2 * reach * index) / (count - 1);
        values.push({
          kind: "sphere",
          radius,
          offset: [offset[0] + rotated[0] * along, offset[1] + rotated[1] * along, offset[2] + rotated[2] * along].map(round4),
        });
      }
    }

    for (const value of values) {
      // `place` only adds an offset when the collider had one; a chain sphere always carries its
      // own computed offset, so it is already placed.
      const placed = value.offset ? value : place(value);
      body.push(shape.condition ? { ...placed, condition: shape.condition } : placed);
    }
  }
  // This rewrite replaces the whole `shapes` array, so it must carry over the frame shape that
  // `tools/bple-brackets/apply-brackets.mjs` adds (ADR-018) — otherwise running this tool after
  // that one silently drops every part's build bracket.
  return [...body, ...current.filter((shape) => shape.condition?.kind === "frame")];
}

function renderShapes(shapes, indent) {
  const lines = [`${indent}"shapes": [`];
  shapes.forEach((shape, index) => {
    const properties = [`${indent}    "kind": "${shape.kind}"`];
    properties.push(shape.kind === "box"
      ? `${indent}    "halfExtents": [${shape.halfExtents.map(num).join(", ")}]`
      : `${indent}    "radius": ${num(shape.radius)}`);
    if (shape.offset) {
      properties.push(`${indent}    "offset": [${shape.offset.map(num).join(", ")}]`);
    }

    if (shape.condition) {
      // A frame bracket has no side; only an attachment marker carries one.
      const side = shape.condition.side === undefined ? "" : `, "side": "${shape.condition.side}"`;
      properties.push(`${indent}    "condition": { "kind": "${shape.condition.kind}"${side} }`);
    }

    lines.push(`${indent}  {`);
    lines.push(properties.join(",\n"));
    lines.push(`${indent}  }${index === shapes.length - 1 ? "" : ","}`);
  });
  lines.push(`${indent}]`);
  return lines;
}

let text = readFileSync(CONTENT, "utf8");
const document = JSON.parse(text);
let updated = 0;
for (const part of document.parts) {
  const shapes = shapesFor(part.partTypeId, part.shapes);
  if (!shapes) continue;
  const anchorIndex = text.indexOf(`"partTypeId": ${part.partTypeId},`);
  if (anchorIndex < 0) throw new Error(`anchor missing for part ${part.partTypeId}`);
  const keyIndex = text.indexOf('"shapes": [', anchorIndex);
  if (keyIndex < 0) throw new Error(`shapes missing for part ${part.partTypeId}`);
  const lineStart = text.lastIndexOf("\n", keyIndex) + 1;
  let end = text.indexOf("[", keyIndex);
  for (let depth = 0; end < text.length; end += 1) {
    if (text[end] === "[") depth += 1;
    else if (text[end] === "]") {
      depth -= 1;
      if (depth === 0) break;
    }
  }
  text = `${text.slice(0, lineStart)}${renderShapes(shapes, "      ").join("\n")}${text.slice(end + 1)}`;
  updated += 1;
  console.log(`${String(part.partTypeId).padStart(3)} ${part.name.padEnd(20)} ${JSON.stringify(shapes)}`);
}

// Re-parse and re-derive: the rewrite must be valid JSON and idempotent.
const check = JSON.parse(text);
for (const part of check.parts) {
  const shapes = shapesFor(part.partTypeId, part.shapes);
  if (!shapes) continue;
  if (JSON.stringify(part.shapes) !== JSON.stringify(shapes)) {
    throw new Error(`shape mismatch for part ${part.partTypeId}: ${JSON.stringify(part.shapes)} != ${JSON.stringify(shapes)}`);
  }
}
if (!DRY_RUN) writeFileSync(CONTENT, text);
console.log(`${DRY_RUN ? "would update" : "updated"} ${updated} parts in ${CONTENT}`);
