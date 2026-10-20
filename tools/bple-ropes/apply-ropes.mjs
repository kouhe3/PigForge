#!/usr/bin/env node
// Writes the rope capability (`docs/specs/rope.md`) into content/parts.json from
// `tasks/bple-ropes-report.json`: the four Rope skins get
//   "rope": { segments, segmentMass, segmentRadius, segmentFriction }
// plus the button activation (`activation: "trigger"`, the original's trigger button cuts the
// middle), and a one-cell build box. Idempotent; the report is the only source of the numbers.
//
// Usage:
//   node tools/bple-ropes/apply-ropes.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync } from "node:fs";
import { flag } from "../lib/args.mjs";
import { applyContent, matchingBracket, partSpan, renderValue, rewriteCapabilitiesLines, canonicalCapabilities } from "../lib/parts.mjs";
import { applyReport, contentFile } from "../lib/paths.mjs";
import { readPartMap } from "../lib/unity.mjs";

const REPORT = applyReport("ropes");
const CONTENT = contentFile();
const DRY_RUN = flag("dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8"));
const ropePrefabs = new Set((report.prefabs ?? []).filter((prefab) => prefab.class === "Rope").map((prefab) => prefab.name));
if (ropePrefabs.size !== 4) {
  throw new Error(`${REPORT}: expected the four Rope prefabs, got ${[...ropePrefabs].join(", ")}`);
}

const map = readPartMap();
const ropePartIds = Object.entries({ ...map.parts, ...map.variants })
  .filter(([, prefab]) => ropePrefabs.has(prefab))
  .map(([id]) => Number(id))
  .sort((left, right) => left - right);
if (ropePartIds.length !== 4) {
  throw new Error(`part-map.json does not name the four Rope prefabs (found ids ${ropePartIds.join(", ")})`);
}

const capability = {
  segments: report.settings.segments,
  segmentMass: report.segment.mass,
  segmentRadius: report.segment.radius,
  segmentFriction: report.segment.friction,
};
const ids = new Set(ropePartIds);

/** The one-cell build box: the original declares no collider (`docs/specs/rope.md` §5). */
const SHAPES = `      "shapes": [
        {
          "kind": "box",
          "halfExtents": [0.5, 0.5, 0.5]
        }
      ]`;

function rewriteShapes(text) {
  let changed = 0;
  for (const partTypeId of ropePartIds) {
    const { shapesIndex } = partSpan(text, partTypeId);
    const lineStart = text.lastIndexOf("\n", shapesIndex) + 1;
    const end = matchingBracket(text, text.indexOf("[", shapesIndex));
    const current = text.slice(lineStart, end + 1);
    if (current === SHAPES) {
      continue;
    }

    text = `${text.slice(0, lineStart)}${SHAPES}${text.slice(end + 1)}`;
    changed++;
  }

  return { text, changed };
}

applyContent({
  rewrite(text) {
    const capabilities = rewriteCapabilitiesLines(text, {
      rewrite: (partTypeId, current) => {
        if (!ids.has(partTypeId)) {
          return null;
        }

        return { ...current, activation: "trigger", rope: capability };
      },
      canonical: canonicalCapabilities(),
      render: (current) => `{ ${Object.entries(current).map(([key, value]) => `"${key}": ${renderValue(value)}`).join(", ")} }`,
    });
    const shapes = rewriteShapes(capabilities.text);
    return {
      text: shapes.text,
      capabilities: { updated: capabilities.updated, unchanged: capabilities.unchanged },
      shapes: { updated: shapes.changed },
    };
  },
  verify(result) {
    const document = JSON.parse(result.text);
    for (const partTypeId of ropePartIds) {
      const part = document.parts.find((candidate) => candidate.partTypeId === partTypeId);
      if (!part) {
        throw new Error(`part ${partTypeId} disappeared`);
      }

      const rope = part.capabilities?.rope;
      if (JSON.stringify(rope) !== JSON.stringify(capability)) {
        throw new Error(`part ${partTypeId}: rope capability mismatch ${JSON.stringify(rope)}`);
      }

      if (part.capabilities?.activation !== "trigger") {
        throw new Error(`part ${partTypeId}: activation must be "trigger"`);
      }

      if (part.shapes?.length !== 1 || JSON.stringify(part.shapes[0].halfExtents) !== JSON.stringify([0.5, 0.5, 0.5])) {
        throw new Error(`part ${partTypeId}: expected the one-cell build box, got ${JSON.stringify(part.shapes)}`);
      }
    }

    return { ropeParts: ropePartIds };
  },
  report(result, verified, { dryRun, content }) {
    console.log(`${dryRun ? "would update" : "updated"}: capabilities ${result.capabilities.updated}, shapes ${result.shapes.updated}`);
    console.log(`unchanged: capabilities ${result.capabilities.unchanged}`);
    console.log(`rope parts: ${verified.ropeParts.join(", ")}`);
    console.log(`content: ${content}`);
  },
});
