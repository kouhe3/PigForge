#!/usr/bin/env node
// Writes the extracted charge values of the 7 TNT-family parts into content/parts.json, preserving
// the file's hand-authored formatting everywhere else. Companion to extract-tnt.mjs; the report is
// the ONLY admissible source for these numbers.
//
// Per part, `capabilities.tnt` becomes exactly this object, in this key order:
//   { "fuseTicks", "radius", "impulse", "triggerSpeed", "chainDetonate"?, "igniteOnImpact"?,
//     "repeatable"?, "chainTarget"? }
// where
//   fuseTicks    kept from the content (the hand-authored ignition-to-blast delay)
//   radius       `m_explosionRadius` x the class's IN radius (vanilla 1.0) -- `TNT.cs:129`
//   impulse      `m_explosionImpulse` x the class's IN force (vanilla 1.0) -- `TNT.cs:222`
//   triggerSpeed the impact speed that arms it (`OnCollisionEnter`, `TNT.cs:78-83`)
//   chainDetonate written only when false (every charge here chains)
//   igniteOnImpact written only when false (the alien charge has an empty handler)
//   repeatable   written only when true (the alien charge stays and re-arms, `AlienTNT.cs:8-52`)
//   chainTarget  written only when false (only a non-alien charge may be chained, `TNT.cs:152`)
//
// Usage: node tools/bple-tnt/apply-tnt.mjs [--report <file>] [--content <file>] [--dry-run]
import { readFileSync } from "node:fs";
import { applyReport, contentFile } from "../lib/paths.mjs";
import { applyContent, canonicalCapabilities, renderCapabilities, rewriteCapabilitiesLines } from "../lib/parts.mjs";

const REPORT = applyReport("tnt");
const REPORT_FORMAT = "pigforge.bple-tnt.report";

/** The charge families this report is allowed to speak for. */
const EXPECTED_PARTS = 7;

const report = JSON.parse(readFileSync(REPORT, "utf8"));
if (report.format !== REPORT_FORMAT) {
  throw new Error(`${REPORT}: format '${report.format}', expected '${REPORT_FORMAT}' -- regenerate with extract-tnt.mjs`);
}

const parts = report.parts ?? {};
if (Object.keys(parts).length !== EXPECTED_PARTS) {
  throw new Error(`${REPORT}: ${Object.keys(parts).length} parts, expected ${EXPECTED_PARTS}`);
}

/** The tnt object in the document's own inline style (`{ "fuseTicks": 5, ... }`), keys in the
 * contract's order; every value is a plain number or a boolean. */
const renderTnt = (tnt) =>
  `{ ${Object.entries(tnt)
    .map(([key, value]) => `"${key}": ${String(value)}`)
    .join(", ")} }`;

const render = renderCapabilities({ tnt: renderTnt });
const canonical = canonicalCapabilities({});

/** The tnt object the report declares for one part; a missing or malformed entry is a hard error. */
function tntOf(partTypeId) {
  const entry = parts[partTypeId];
  if (!entry) {
    return null;
  }

  for (const key of ["fuseTicks", "radius", "impulse", "triggerSpeed", "repeatable", "chainTarget", "igniteOnImpact"]) {
    if (entry[key] === undefined) {
      throw new Error(`report part ${partTypeId}: missing '${key}'`);
    }
  }

  if (!Number.isInteger(entry.fuseTicks) || entry.fuseTicks < 0) {
    throw new Error(`report part ${partTypeId}: fuseTicks ${entry.fuseTicks}`);
  }

  for (const key of ["radius", "impulse", "triggerSpeed"]) {
    if (!Number.isFinite(entry[key]) || entry[key] <= 0) {
      throw new Error(`report part ${partTypeId}: ${key} ${entry[key]}`);
    }
  }

  const tnt = {
    fuseTicks: entry.fuseTicks,
    radius: entry.radius,
    impulse: entry.impulse,
    triggerSpeed: entry.triggerSpeed,
  };
  if (entry.igniteOnImpact !== true) {
    tnt.igniteOnImpact = false;
  }

  if (entry.repeatable === true) {
    tnt.repeatable = true;
  }

  if (entry.chainTarget !== true) {
    tnt.chainTarget = false;
  }

  return tnt;
}

applyContent({
  rewrite(text) {
    return rewriteCapabilitiesLines(text, {
      canonical,
      render,
      rewrite(partTypeId, capabilities) {
        if (capabilities.tnt === undefined) {
          return null;
        }

        const tnt = tntOf(partTypeId);
        if (tnt === null) {
          throw new Error(`part ${partTypeId} carries tnt but the report has no entry for it`);
        }

        return { ...capabilities, tnt };
      },
    });
  },
  verify(result) {
    // Re-derive the rewritten document: every part the report speaks for must now carry its blast.
    // (`result.text`, not the file: a dry run never writes one.)
    const written = JSON.parse(result.text);
    const writtenParts = written.parts.filter((part) => part.capabilities?.tnt?.radius !== undefined);
    if (writtenParts.length !== EXPECTED_PARTS) {
      throw new Error(`content carries ${writtenParts.length} parts with a tnt radius, expected ${EXPECTED_PARTS}`);
    }

    return { written: writtenParts.length };
  },
  report(result, verified, { dryRun, content }) {
    console.log(`${dryRun ? "dry-run" : "wrote"} ${content}: ${result.updated} updated, ${result.unchanged} unchanged`);
    console.log(`parts in the report: ${verified.written}`);
  },
});
