#!/usr/bin/env node
// Rewrites the `shapes` block of every mapped part in content/parts.json from the
// BPLE collider report, preserving the file's hand-authored formatting everywhere
// else. Companion to extract-shapes.mjs; see ADR-005 for the mapping rules.
//
// Usage:
//   node tools/bple-shapes/extract-shapes.mjs --out artifacts/bple-part-shapes.json
//   node tools/bple-shapes/apply-shapes.mjs [--report <file>] [--content <file>] [--dry-run]
//
// Mapping rules (ADR-005):
//   - A body whose only collider is a sphere (pig, sandbag) stays a sphere.
//   - A part whose current shape is a sphere and which has a sphere collider (wheels)
//     takes that sphere: the tire is the collision, the support box is a stacking helper.
//   - Everything else becomes one box: the X/Y envelope of the body colliders, centred
//     on the part origin (shape offsets are not expressible in part-content-v1 physics).
//   - Capsules become their X/Y envelope box; Z half-extent comes from the collider's
//     Z size when positive, otherwise the previous shape's Z.
//   - Parts with no body collider keep their authored shape.

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

/** PigForge shapes for a part, or null when the original has no body collider. */
function shapesFor(partTypeId, current) {
  const entry = report[String(partTypeId)];
  if (!entry || entry.shapes.length === 0) return null;
  const spheres = entry.shapes.filter((shape) => shape.kind === "sphere");
  if (entry.shapes.length === 1 && spheres.length === 1) {
    return [{ kind: "sphere", radius: round4(spheres[0].radius) }];
  }
  if (current[0].kind === "sphere" && spheres.length > 0) {
    const tire = spheres.reduce((a, b) => (b.radius > a.radius ? b : a));
    return [{ kind: "sphere", radius: round4(tire.radius) }];
  }

  const extents = { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity };
  let zHalf = null;
  for (const shape of entry.shapes) {
    const radii = shape.kind === "box" ? [shape.size[0] / 2, shape.size[1] / 2] : [shape.radius, shape.radius];
    // Capsules: radius around a segment along m_Direction (0 = X, 1 = Y, 2 = Z).
    const reach = shape.kind === "capsule" ? Math.max(shape.height / 2 - shape.radius, 0) : 0;
    const halfX = radii[0] + (shape.direction === 0 ? reach : 0);
    const halfY = radii[1] + (shape.direction === 1 ? reach : 0);
    extents.minX = Math.min(extents.minX, shape.offset[0] - halfX);
    extents.maxX = Math.max(extents.maxX, shape.offset[0] + halfX);
    extents.minY = Math.min(extents.minY, shape.offset[1] - halfY);
    extents.maxY = Math.max(extents.maxY, shape.offset[1] + halfY);
    if (shape.kind === "box" && shape.size[2] > 0) zHalf = Math.max(zHalf ?? 0, shape.size[2] / 2);
  }
  const previousZ = current[0].kind === "box" ? current[0].halfExtents[2] : current[0].radius;
  return [{
    kind: "box",
    halfExtents: [
      round4((extents.maxX - extents.minX) / 2),
      round4((extents.maxY - extents.minY) / 2),
      round4(zHalf ?? previousZ),
    ],
  }];
}

function renderShapes(shapes, indent) {
  const lines = [`${indent}"shapes": [`];
  shapes.forEach((shape, index) => {
    lines.push(`${indent}  {`);
    lines.push(`${indent}    "kind": "${shape.kind}",`);
    lines.push(shape.kind === "box"
      ? `${indent}    "halfExtents": [${shape.halfExtents.map(num).join(", ")}]`
      : `${indent}    "radius": ${num(shape.radius)}`);
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
