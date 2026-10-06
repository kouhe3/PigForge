#!/usr/bin/env node
// Writes the extracted spring values of every mapped part into content/parts.json,
// preserving the file's hand-authored formatting everywhere else. Companion to
// extract-springs.mjs; the report is the ONLY admissible source for these numbers.
//
// Three capabilities are written (each inside the part's `capabilities` object):
//
//   - suspension: the original's linear-limit spring of the wheel's own joint
//     (OffRoadWheel.CustomConnectToPart, OffRoadWheel.cs:202-220): the wheel's local Y is
//     Limited and held at `restOffset` by `stiffness` N/m with `damper` N*s/m. The axis is
//     not content: it is the wheel's own build-frame Y, perpendicular to its axle (the
//     joint's default axes), and the code attaches it to the parent body.
//     A part whose report entry declares no suspension must not carry the key: the
//     extractor's negative evidence (CartWheel/MotorWheel/StickyWheel never override
//     CustomConnectToPart, so Contraption.AddFixedJoint welds them rigidly) is what makes
//     their axles stay rigid.
//
//   - spring: the original `Spring`'s own connection (docs/specs/spring-joint.md §2): the
//     declared class constants and the *declaration-default* `breakForce` (250; profile B's
//     StrongSpringConnection is what doubles it to 1200). No route key and no mass override:
//     the declaration defaults leave StableSpringConnection false, so every skin takes the
//     ConfigurableJoint y-soft-limit branch and keeps its own content mass -- never authored.
//
//   - glove: the interactive `SpringBoxingGlove` (docs/specs/boxing-glove.md §2): the glove
//     body (`mass` + `shapes` from the referenced BoxingGlove*.prefab), the host-glove
//     joint's limit/drives/projection and the shoot/wind moves, with each skin's prefab
//     overrides (`wind.driveSpring = 10 x m_targetDistanceY x BoxingGloveLength`).
//     `activation` is `toggle` when IN `SwitchableBoxingGlove` is on, `trigger` otherwise.
//     A glove skin never carries `spring`: the old misplaced placeholder key is removed.
//
// Usage:
//   node tools/bple-springs/apply-springs.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-springs-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8"));
const parts = report.parts;

/** The suspension renderer (tools/bple-joints style): an integral value keeps one decimal. */
const num = (value) => (Number.isInteger(Number(value)) ? Number(value).toFixed(1) : String(Number(Number(value).toFixed(4))));

/** The spring/glove renderer: the hand-authored content writes these bare (`250`, `0.1`,
 *  `1200`), so the value is printed as itself with the float noise cut at six decimals. */
const plain = (value) => {
  const rounded = Number(Number(value).toFixed(6));
  if (!Number.isFinite(rounded)) throw new Error(`cannot render a non-finite value: ${JSON.stringify(value)}`);
  return String(rounded);
};

/** Numeric comparison of a rendered value against the report's. */
const same = (left, right) => Number(left) === Number(right);

const renderSuspension = (suspension) =>
  `{ "stiffness": ${num(suspension.stiffness)}, "damper": ${num(suspension.damper)}, "restOffset": ${num(suspension.restOffset)} }`;

/**
 * Index of the `}` that closes the object opening at `start`, skipping braces inside JSON
 * strings. A plain `indexOf("}")` stops at the first nested object's brace -- the glove's
 * `shoot`/`wind` are two levels deep, so that would splice at the wrong offset.
 */
function matchingBrace(text, start) {
  let depth = 0;
  let inString = false;
  for (let index = start; index < text.length; index += 1) {
    const char = text[index];
    if (inString) {
      if (char === "\\") index += 1;
      else if (char === '"') inString = false;
      continue;
    }

    if (char === '"') inString = true;
    else if (char === "{") depth += 1;
    else if (char === "}") {
      depth -= 1;
      if (depth === 0) return index;
    }
  }

  return -1;
}

/** Same as `matchingBrace`, for an array opening at `start`. */
function matchingBracket(text, start) {
  let depth = 0;
  let inString = false;
  for (let index = start; index < text.length; index += 1) {
    const char = text[index];
    if (inString) {
      if (char === "\\") index += 1;
      else if (char === '"') inString = false;
      continue;
    }

    if (char === '"') inString = true;
    else if (char === "[") depth += 1;
    else if (char === "]") {
      depth -= 1;
      if (depth === 0) return index;
    }
  }

  return -1;
}

/** End index (exclusive) of the JSON value starting at `start` inside a single-line object. */
function valueEnd(text, start) {
  const char = text[start];
  if (char === "{") return matchingBrace(text, start) + 1;
  if (char === "[") return matchingBracket(text, start) + 1;
  if (char === '"') return text.indexOf('"', start + 1) + 1;
  let index = start;
  while (index < text.length && text[index] !== "," && text[index] !== "}") index += 1;
  return index;
}

/** `{ ... }` with exactly one space inside each brace. */
const tidy = (capabilities) => capabilities.replace(/^\{\s*/, "{ ").replace(/\s*\}$/, " }");

/** The `"key":` at the capabilities object's TOP level, or null. The glove's drives also carry
 *  a `spring` key one level down, so a bare `/"spring":/` search would rewrite the wrong one. */
function topLevelKeyIndex(capabilities, key) {
  const wanted = `"${key}"`;
  let depth = 0;
  let inString = false;
  let stringStart = -1;
  for (let index = 0; index < capabilities.length; index += 1) {
    const char = capabilities[index];
    if (inString) {
      if (char === "\\") index += 1;
      else if (char === '"') {
        inString = false;
        if (depth === 1 && capabilities.slice(stringStart, index + 1) === wanted) {
          let next = index + 1;
          while (next < capabilities.length && /\s/.test(capabilities[next])) next += 1;
          if (capabilities[next] === ":") return { start: stringStart, valueStart: next + 1 };
        }
      }

      continue;
    }

    if (char === '"') {
      stringStart = index;
      inString = true;
    } else if (char === "{" || char === "[") depth += 1;
    else if (char === "}" || char === "]") depth -= 1;
  }

  return null;
}

/** Replace `key`'s value in the inline capabilities text, or append `key: rendered` before the
 *  closing brace (the hand-authored caps order is preserved: present keys stay in place). */
function upsert(capabilities, key, rendered) {
  const found = topLevelKeyIndex(capabilities, key);
  if (!found) {
    const body = capabilities.replace(/^\{\s*/, "").replace(/\s*\}$/, "");
    return body.length === 0 ? `{ "${key}": ${rendered} }` : `{ ${body}, "${key}": ${rendered} }`;
  }

  let valueStart = found.valueStart;
  while (/\s/.test(capabilities[valueStart])) valueStart += 1;
  return capabilities.slice(0, found.start) + `"${key}": ${rendered}` + capabilities.slice(valueEnd(capabilities, valueStart));
}

/** Remove a property and one separating comma from the inline capabilities text. */
function removeProperty(capabilities, key) {
  const found = topLevelKeyIndex(capabilities, key);
  if (!found) return capabilities;
  const start = found.start;
  let valueStart = found.valueStart;
  while (/\s/.test(capabilities[valueStart])) valueStart += 1;
  const end = valueEnd(capabilities, valueStart);
  if (capabilities[end] === ",") {
    return `${capabilities.slice(0, start).replace(/\s+$/, "")}${capabilities.slice(end + 1).replace(/^\s+/, " ")}`;
  }

  return `${capabilities.slice(0, start).replace(/,\s*$/, "")}${capabilities.slice(end)}`;
}

/** Numeric/structural equality, so a re-parsed capabilities object is compared by value. */
function deepSame(left, right) {
  if (typeof left === "number" && typeof right === "number") return left === right;
  if (Array.isArray(left) && Array.isArray(right)) {
    return left.length === right.length && left.every((value, index) => deepSame(value, right[index]));
  }
  if (left && right && typeof left === "object" && typeof right === "object") {
    const leftKeys = Object.keys(left);
    const rightKeys = Object.keys(right);
    return leftKeys.length === rightKeys.length && leftKeys.every((key) => key in right && deepSame(left[key], right[key]));
  }

  return left === right;
}

/** The `spring` the report derives for one Spring-family prefab (docs/specs/spring-joint.md §2). */
function renderSpring(entry) {
  const route = entry.jointPath?.route;
  // The declaration defaults (StableSpringConnection false) put every skin on the
  // ConfigurableJoint y-soft-limit branch, so the capability carries no route discriminator and
  // it carries no mass override either -- the part keeps its own content mass (gaps G107).
  if (route !== "ConfigurableJointYLimit") {
    throw new Error(`${entry.prefab}: the spring joint path changed to ${JSON.stringify(route)}; the declaration defaults only ever take the y-limit branch`);
  }
  const constants = report.classConstants.Spring;
  const runtime = report.spring.runtime;
  const fields = [
    `"stiffness": ${plain(constants.SPRING_LIMIT_SPRING.value)}`,
    `"damper": ${plain(constants.SPRING_DAMPING.value)}`,
    `"limit": ${plain(constants.SPRING_LIMIT.value)}`,
    `"bounciness": ${plain(constants.SPRING_BOUNCINESS.value)}`,
    `"breakForce": ${plain(runtime.breakForce.value)}`,
  ];
  return `{ ${fields.join(", ")} }`;
}

const renderVector = (vector) => [vector.x, vector.y, vector.z].map(plain).join(", ");

/** One glove-body collider in the content shapeDefinition shape: `kind` plus the prefab's own
 *  radius/half-extents, and an `offset` only when the prefab's `m_Center` is not the origin. */
function renderShape(shape) {
  const fields = [`"kind": "${shape.kind}"`];
  if (shape.radius !== null && shape.radius !== undefined) fields.push(`"radius": ${plain(shape.radius)}`);
  if (shape.halfExtents) fields.push(`"halfExtents": [${renderVector(shape.halfExtents)}]`);
  const center = shape.center ?? shape.offset;
  if (center && (center.x !== 0 || center.y !== 0 || center.z !== 0)) fields.push(`"offset": [${renderVector(center)}]`);
  return `{ ${fields.join(", ")} }`;
}

/** The `glove` the report derives for one SpringBoxingGlove skin (docs/specs/boxing-glove.md §2). */
function renderGlove(entry) {
  const declaration = report.boxingGlove.declaration;
  const defaults = report.classConstants.SpringBoxingGlove;
  const glove = entry.glove;
  if (!glove) throw new Error(`${entry.prefab}: the report carries no glove body`);
  const overrides = entry.overrides ?? {};
  const override = (name, constant) => (overrides[name] === null || overrides[name] === undefined ? constant.value : overrides[name]);
  const length = report.inFeatures?.BoxingGloveLength?.value;
  if (length === null || length === undefined) throw new Error(`${REPORT} carries no IN BoxingGloveLength`);

  const distanceY = override("m_targetDistanceY", defaults.m_targetDistanceY);
  const deviationX = override("m_targetDeviationX", defaults.m_targetDeviationX);
  const shootTime = override("m_ShootTime", defaults.m_ShootTime);
  const windTime = override("m_WindingTime", defaults.m_WindingTime);
  const yDriveSpring = override("m_SpringYDrive", defaults.m_SpringYDrive);
  const yDriveDamper = override("m_SpringYDriveDamper", defaults.m_SpringYDriveDamper);
  const mass = glove.serializedMass ?? declaration.gloveMass.value;
  const shapes = glove.shapes ?? [];
  if (shapes.length === 0) throw new Error(`${entry.prefab}: the glove prefab declares no solid collider`);
  // SpringBoxingGlove.cs:385-395: positionSpring = 10 * m_targetDistanceY * BoxingGloveLength,
  // positionDamper = 10 * 0.25. The damper is distance-independent, hence constant across skins.
  const driveSpring = declaration.wind.springFactor * distanceY * length;
  const driveDamper = declaration.wind.springFactor * declaration.wind.damperFactor;

  const fields = [
    `"mass": ${plain(mass)}`,
    `"shapes": [ ${shapes.map(renderShape).join(", ")} ]`,
    `"limit": ${plain(declaration.gloveJoint.linearLimit.limit)}`,
    `"yDrive": { "spring": ${plain(yDriveSpring)}, "damper": ${plain(yDriveDamper)} }`,
    `"xDrive": { "spring": ${plain(declaration.gloveJoint.xDrive.spring)}, "damper": ${plain(declaration.gloveJoint.xDrive.damper)} }`,
    `"projectionDistance": ${plain(declaration.gloveJoint.projectionDistance.value)}`,
    `"shoot": { "distanceY": ${plain(distanceY)}, "deviationX": ${plain(deviationX)}, "time": ${plain(shootTime)}, "limitSpring": ${plain(declaration.shoot.linearLimitSpring.value)} }`,
    `"wind": { "time": ${plain(windTime)}, "mass": ${plain(declaration.windingGloveMass.value)}, "driveSpring": ${plain(driveSpring)}, "driveDamper": ${plain(driveDamper)} }`,
    `"solverIterationScale": ${plain(declaration.solverIterationsFactor.value)}`,
  ];
  return `{ ${fields.join(", ")} }`;
}

/** The suspension the report derives for a mapped part, or null when the part declares none. */
function expectedSuspension(partTypeId) {
  const entry = parts[String(partTypeId)];
  if (!entry) return undefined; // unmapped partTypeId (PigForge-only invention): nothing extracted
  if (entry.suspension === null) return null;
  const suspension = entry.suspension;
  if (!(suspension.stiffness > 0) || !(suspension.damper >= 0) || !Number.isFinite(suspension.restOffset)) {
    throw new Error(`part ${partTypeId}: report suspension is not a usable spring: ${JSON.stringify(suspension)}`);
  }

  return suspension;
}

/** Report family prefabs -> content partTypeId. A family prefab that reaches no content part is
 *  a hole between the extractor and the catalog, and a silent skip would leave the content
 *  without a value the runtime reads -- so it is a hard error, never a warning. */
function familyMap(entries, family) {
  if (!Array.isArray(entries) || entries.length === 0) throw new Error(`${REPORT} carries no ${family} prefabs`);
  const map = new Map();
  for (const entry of entries) {
    if (entry.partTypeId === null || entry.partTypeId === undefined) {
      throw new Error(`report ${family} prefab ${entry.prefab} maps to no content part`);
    }
    map.set(entry.partTypeId, entry);
  }
  return map;
}

const springByPart = familyMap(report.spring?.prefabs, "spring");
const gloveByPart = familyMap(report.boxingGlove?.prefabs, "glove");
// PigForge content puts the glove on the original's *momentary* branch. The original's own bar
// widget for a SpringBoxingGlove is a button, not an on/off switch (`HasOnOffToggle() => false`,
// SpringBoxingGlove.cs:81-84), and `BasePart.OnButtonTriggered` -> `ProcessTouch()` is what that
// button calls, so a press punches and the machine winds the fist home on its own shoot time --
// the flow the BoxingGloveProbe measured (docs/specs/boxing-glove.md §1.1). The latching branch the
// IN switch `SwitchableBoxingGlove` selects is still implemented in the room; a skin that wants it
// writes "toggle" here (spec §2).
const activation = "trigger";
const switchableBoxingGlove = report.inFeatures?.SwitchableBoxingGlove?.value === true;

// A prefab that declares the wheel spring but has no content part is the state the catalog is in:
// the OffRoadWheel (Part_MotorWheel_08_SET) is an IN extension part GameData.m_customParts never
// listed, so PigForge never imported it. Nothing can be written for it, and this is reported
// instead of silently applying nothing: the moment the part is added to
// tools/bple-variants/variant-overrides.json extras, this script writes its suspension.
if (report.unmappedSpringPrefabs.length > 0) {
  console.warn(
    `no content part for the prefab(s) that declare the wheel suspension: ${report.unmappedSpringPrefabs.join(", ")}`);
  console.warn("  -> they are not in the catalog (IN extension parts); add them to tools/bple-variants/variant-overrides.json extras");
}

let text = readFileSync(CONTENT, "utf8");
const document = JSON.parse(text);
let updated = 0;
let removed = 0;
const changed = [];

for (const part of document.parts) {
  const springEntry = springByPart.get(part.partTypeId) ?? null;
  const gloveEntry = gloveByPart.get(part.partTypeId) ?? null;
  const suspension = expectedSuspension(part.partTypeId);
  if (!springEntry && !gloveEntry && suspension === undefined) continue;

  const anchor = `"partTypeId": ${part.partTypeId},`;
  const anchorIndex = text.indexOf(anchor);
  if (anchorIndex < 0) throw new Error(`anchor missing for part ${part.partTypeId}`);
  const shapesIndex = text.indexOf('"shapes":', anchorIndex);
  if (shapesIndex < 0) throw new Error(`shapes missing for part ${part.partTypeId}`);

  const capabilitiesIndex = text.indexOf('"capabilities":', anchorIndex);
  const hasCapabilities = capabilitiesIndex >= 0 && capabilitiesIndex < shapesIndex;
  let open = -1;
  let close = -1;
  let capabilities = "{ }";
  if (hasCapabilities) {
    open = text.indexOf("{", capabilitiesIndex);
    close = matchingBrace(text, open);
    if (open < 0 || close < 0 || text.slice(open, close).includes("\n")) {
      throw new Error(`part ${part.partTypeId}: expected a single-line capabilities object`);
    }

    capabilities = text.slice(open, close + 1);
  }

  const before = hasCapabilities ? capabilities : null;
  let after = capabilities;
  if (springEntry) after = upsert(after, "spring", renderSpring(springEntry));
  if (gloveEntry) {
    after = upsert(after, "glove", renderGlove(gloveEntry));
    after = upsert(after, "activation", `"${activation}"`);
    // Pre-G95 content carried the boxing glove as `"spring": 25.0`; the glove skin owns `glove`.
    after = removeProperty(after, "spring");
  }

  if (suspension !== undefined) {
    if (suspension === null) {
      const stripped = removeProperty(after, "suspension");
      if (stripped !== after) {
        after = stripped;
        removed += 1;
      }
    } else {
      after = upsert(after, "suspension", renderSuspension(suspension));
    }
  }

  after = tidy(after);
  if (after === "{ }" || after === before) continue;

  updated += 1;
  if (hasCapabilities) {
    text = text.slice(0, open) + after + text.slice(close + 1);
  } else {
    const lineStart = text.lastIndexOf("\n", shapesIndex) + 1;
    text = `${text.slice(0, lineStart)}      "capabilities": ${after},\n${text.slice(lineStart)}`;
  }

  changed.push({ partTypeId: part.partTypeId, name: part.name, before, after });
}

// Re-parse and re-derive: the rewrite must be valid JSON, every value must match the report
// exactly, and it must be idempotent (a second run changes nothing). A family prefab with no
// content part is a hard error, never a silent skip.
const check = JSON.parse(text);
const seen = { spring: new Set(), glove: new Set() };
for (const part of check.parts) {
  const springEntry = springByPart.get(part.partTypeId);
  if (springEntry) {
    seen.spring.add(part.partTypeId);
    const expected = JSON.parse(renderSpring(springEntry));
    if (!deepSame(part.capabilities?.spring, expected)) {
      throw new Error(`part ${part.partTypeId}: spring mismatch ${JSON.stringify(part.capabilities?.spring)} != ${JSON.stringify(expected)}`);
    }
  }

  const gloveEntry = gloveByPart.get(part.partTypeId);
  if (gloveEntry) {
    seen.glove.add(part.partTypeId);
    const expected = JSON.parse(renderGlove(gloveEntry));
    if (!deepSame(part.capabilities?.glove, expected)) {
      throw new Error(`part ${part.partTypeId}: glove mismatch ${JSON.stringify(part.capabilities?.glove)} != ${JSON.stringify(expected)}`);
    }

    if (part.capabilities?.activation !== activation) {
      throw new Error(`part ${part.partTypeId}: activation ${JSON.stringify(part.capabilities?.activation)} != ${JSON.stringify(activation)}`);
    }

    if (part.capabilities?.spring !== undefined) {
      throw new Error(`part ${part.partTypeId}: a glove skin must not carry a spring capability`);
    }
  }

  const suspension = expectedSuspension(part.partTypeId);
  if (suspension === undefined) continue;
  const actual = part.capabilities?.suspension ?? null;
  if (suspension === null) {
    if (actual !== null) throw new Error(`part ${part.partTypeId}: unexpected suspension`);
    continue;
  }

  if (actual === null
    || !same(actual.stiffness, suspension.stiffness)
    || !same(actual.damper, suspension.damper)
    || !same(actual.restOffset, suspension.restOffset)) {
    throw new Error(`part ${part.partTypeId}: suspension mismatch ${JSON.stringify(actual)} != ${JSON.stringify(suspension)}`);
  }
}

for (const [partTypeId, entry] of springByPart) {
  if (!seen.spring.has(partTypeId)) throw new Error(`content has no part ${partTypeId} for spring prefab ${entry.prefab}`);
}

for (const [partTypeId, entry] of gloveByPart) {
  if (!seen.glove.has(partTypeId)) throw new Error(`content has no part ${partTypeId} for glove prefab ${entry.prefab}`);
}

if (!DRY_RUN) writeFileSync(CONTENT, text);
console.log(`${DRY_RUN ? "would update" : "updated"} ${updated} parts in ${CONTENT}${removed > 0 ? ` (dropped ${removed} stale suspensions)` : ""}`);
for (const entry of changed) {
  console.log(`  ~ ${entry.partTypeId} ${entry.name}`);
  console.log(`    - ${entry.before ?? "(no capabilities)"}`);
  console.log(`    + ${entry.after}`);
}

for (const part of check.parts) {
  const springEntry = springByPart.get(part.partTypeId);
  if (springEntry) {
    console.log(`  - ${part.partTypeId} ${part.name}: spring ${springEntry.jointPath.route === "SpringJoint" ? "bungee" : "limit"}, breakForce ${report.spring.runtime.breakForce.value}, mass ${report.spring.runtime.mass.value}`);
  }

  const gloveEntry = gloveByPart.get(part.partTypeId);
  if (gloveEntry) {
    console.log(`  - ${part.partTypeId} ${part.name}: glove mass ${gloveEntry.glove.serializedMass}, activation ${activation} (IN SwitchableBoxingGlove ${switchableBoxingGlove} -- the momentary branch is chosen on purpose), distanceY ${part.capabilities.glove.shoot.distanceY}, driveSpring ${part.capabilities.glove.wind.driveSpring}`);
  }

  const suspension = expectedSuspension(part.partTypeId);
  if (suspension) {
    console.log(`  - ${part.partTypeId} ${part.name}: stiffness ${suspension.stiffness}, damper ${suspension.damper}, restOffset ${suspension.restOffset}`);
  }
}
