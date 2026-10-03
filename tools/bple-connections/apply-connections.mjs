#!/usr/bin/env node
// Writes each part's connection-visual rule into content/parts.json as a part-level
// `connectionVisual` string. The rule is the script the original prefab mounts
// (Rocket -> attachmentFallback, TNT/BlasterTNT -> attachmentPlain, SpotLight/GrapplingHook ->
// attachmentEight, Wings -> frame) and decides, per connection side, which of the part's
// conditional (`condition.kind === "attachment"`) colliders the original leaves solid
// (`Rocket.cs:139-165`) and which body form a glider's root collider takes
// (`Wings.cs:41-75`). The server reads it to mirror that: see
// src/PigForge.Core/Construction/ConnectionShapes.cs.
//
// The client renderer keeps reading the same value from the sprite manifest; this tool only
// copies the manifest's already-derived rule into the content document so the server has one
// authored source instead of a heuristic over shape kinds.
//
// Usage: node tools/bple-connections/apply-connections.mjs [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");
const MANIFEST = join(REPO, "clients", "web", "public", "assets", "original", "part-textures.json");
const CONTENT = join(REPO, "content", "parts.json");
const DRY_RUN = process.argv.includes("--dry-run");

const RULES = new Set(["attachmentFallback", "attachmentPlain", "attachmentEight", "frame"]);

const textures = JSON.parse(readFileSync(MANIFEST, "utf8")).parts;
const visualByPart = new Map();
for (const [partTypeId, texture] of Object.entries(textures)) {
  const visual = texture.connectionVisual;
  if (visual === undefined) continue;
  if (!RULES.has(visual)) throw new Error(`manifest part ${partTypeId}: unknown connectionVisual ${visual}`);
  visualByPart.set(Number(partTypeId), visual);
}

let text = readFileSync(CONTENT, "utf8");
const document = JSON.parse(text);
let updated = 0;

for (const part of document.parts) {
  const visual = visualByPart.get(part.partTypeId);
  if (visual === undefined) {
    if (part.connectionVisual !== undefined) {
      throw new Error(`part ${part.partTypeId} carries a connectionVisual the manifest does not declare`);
    }
    continue;
  }

  const anchorIndex = text.indexOf(`"partTypeId": ${part.partTypeId},`);
  if (anchorIndex < 0) throw new Error(`anchor missing for part ${part.partTypeId}`);
  const nameLine = `      "name": ${JSON.stringify(part.name)},`;
  const nameIndex = text.indexOf(nameLine, anchorIndex);
  if (nameIndex < 0) throw new Error(`name line missing for part ${part.partTypeId}`);
  const lineStart = nameIndex + nameLine.length;
  const line = `      "connectionVisual": ${JSON.stringify(visual)},`;
  const nextLine = text.slice(lineStart + 1, text.indexOf("\n", lineStart + 1));

  if (nextLine === line) {
    continue;
  }

  if (nextLine.startsWith('      "connectionVisual"')) {
    text = `${text.slice(0, lineStart + 1)}${line}${text.slice(lineStart + 1 + nextLine.length)}`;
  } else {
    text = `${text.slice(0, lineStart)}${"\n"}${line}${text.slice(lineStart)}`;
  }

  updated += 1;
  console.log(`${String(part.partTypeId).padStart(3)} ${part.name.padEnd(24)} ${visual}`);
}

// Re-parse and re-derive: the rewrite must be valid JSON, carry exactly the manifest's rules,
// and be idempotent (a second run must find every line already in place).
const check = JSON.parse(text);
const seen = new Set();
for (const part of check.parts) {
  const visual = visualByPart.get(part.partTypeId);
  if (visual === undefined) continue;
  if (part.connectionVisual !== visual) {
    throw new Error(`connectionVisual mismatch for part ${part.partTypeId}: ${part.connectionVisual} != ${visual}`);
  }
  seen.add(part.partTypeId);
}

for (const partTypeId of visualByPart.keys()) {
  if (!seen.has(partTypeId)) throw new Error(`manifest part ${partTypeId} is missing from content`);
}

if (!DRY_RUN) {
  writeFileSync(CONTENT, text);
}

console.log(`${DRY_RUN ? "would update" : "updated"} ${updated} parts in ${CONTENT}`);
