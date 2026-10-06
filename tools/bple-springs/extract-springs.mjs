// Spring extractor for three families, all read from the original's scripts + prefabs:
//
//   1. Wheels (`*Wheel` : BasePart) -- the wheel's own joint. Only `OffRoadWheel`
//      overrides `CustomConnectToPart`, producing the original's one soft wheel
//      attachment. This is the ONLY admissible source for `capabilities.suspension`
//      in content/parts.json -- the same rule tools/bple-joints, tools/bple-materials
//      and tools/bple-power established for their values.
//   2. `Spring` (`Part_Spring_01..04_SET`) -- the part's connection to its neighbour is
//      itself elastic. Under `StableSpringConnection` a part whose `customPartIndex` is
//      0 or 2 gets a `SpringJoint` (a rope), every other index a `ConfigurableJoint`
//      whose local Y is Limited at 0.1 m. Both routes break past 3 m of separation and
//      respawn a `SpringEndpoint.prefab` body (`Spring.cs:98-176`, `Spring.cs:78-92`).
//   3. `SpringBoxingGlove` (`Part_SpringBoxingGlove_01..05_SET`) -- a *different* class:
//      its own connection is the ordinary weld, but `Initialize` hangs an extra rigidbody
//      (the glove, `BoxingGlove*.prefab`) off a ConfigurableJoint whose local Y is a
//      yDrive spring, and `Shoot` retargets it by `m_targetDistanceY * BoxingGloveLength`
//      (`SpringBoxingGlove.cs:222-277`, `:327-347`).
//
// Why it matters: a part is welded to its neighbour by `Contraption.AddFixedJoint`
// (Contraption.cs:1504-1533, a ConfigurableJoint with every motion mode Locked) unless
// the part overrides `BasePart.CustomConnectToPart` (BasePart.cs:1214 returns null) or
// `PartGeneratorManager.CreateJoints` picks the part as the joint's owner
// (PartGeneratorManager.cs:472-546, driven by `m_customJointConnectionDirection`).
// Among the wheel family only `OffRoadWheel` overrides it:
//
//   configurableJoint.angularX/Y/ZMotion = Locked
//   configurableJoint.xMotion = Locked; yMotion = Limited; zMotion = Locked
//   linearLimitSpring.spring = m_springStiffness; linearLimitSpring.damper = 5f
//   linearLimit.limit = 0f; linearLimit.bounciness = 0f            (OffRoadWheel.cs:202-220)
//
// i.e. the wheel's local Y is a spring holding it at zero offset from the chassis.
//
// Every number this tool reports carries a `file:line` (scripts/settings) or the prefab
// field name it was read from. Class constants are parsed from the `.cs` text and hard-
// asserted, so a changed original cannot silently produce a wrong report.
//
// Usage: node tools/bple-springs/extract-springs.mjs [--bple <path>] [--json <path>] [--md <path>]
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { loadVanillaSettings, VANILLA_SETTINGS_NAME } from "../in-settings/vanilla-settings.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  const value = index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
  return value;
}

/** `BPLE 2022.1.9` is the pristine original (the same editor the game shipped with,
 * 2021.3.45f2) and the copy whose `.cs` text this extractor asserts against; `BPLE_Unity6`
 * is the migrated copy some sibling tools default to and carries Unity 6 API names
 * (`linearVelocity`). The three families live in scripts/prefabs that are byte-identical
 * in both copies, but the pristine one is the source of record. */
const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE 2022.1.9")));
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-springs-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-springs-report.md")));
const ASSETS = join(BPLE, "Assets");
const SCRIPTS = join(ASSETS, "Scripts", "Assembly-CSharp");
const GAMEOBJECT = join(ASSETS, "GameObject");
const TEXTEXT = join(ASSETS, "TextAsset");
const CONTENT_PARTS = join(REPO, "content", "parts.json");
const TEXTURE_MAP = join(REPO, "tools", "bple-textures", "part-map.json");

if (!existsSync(SCRIPTS) || !existsSync(GAMEOBJECT)) {
  console.error(`BPLE project not found: ${SCRIPTS}\nPass --bple <path to the BPLE project>.`);
  process.exit(1);
}

const warnings = [];
const fail = (message) => {
  console.error(message);
  process.exit(1);
};

// ------------------------------------------------------------------ script index
/** guid -> script class name, so a prefab's `m_Script` guids become class names. */
function buildGuidIndex() {
  const byGuid = new Map();
  for (const entry of readdirSync(SCRIPTS)) {
    if (!entry.endsWith(".cs.meta")) continue;
    const text = readFileSync(join(SCRIPTS, entry), "utf8");
    const match = /^guid:\s*([0-9a-f]{32})/m.exec(text);
    if (match) byGuid.set(match[1], basename(entry, ".cs.meta"));
  }
  return byGuid;
}

/** class name -> base class name, for every script in the assembly. */
function buildClassBases() {
  const bases = new Map();
  for (const entry of readdirSync(SCRIPTS)) {
    if (!entry.endsWith(".cs")) continue;
    const text = readFileSync(join(SCRIPTS, entry), "utf8");
    const match = /^\s*public class (\w+)\s*:\s*([\w<>]+)/m.exec(text);
    if (match) bases.set(match[1], match[2]);
  }
  return bases;
}

const guidIndex = buildGuidIndex();
const classBases = buildClassBases();

/** prefab name -> guid, so a serialized `m_BoxingGlovePrefab: {guid: …}` reference
 *  resolves to the referenced prefab's name instead of an opaque guid. */
function buildPrefabGuidIndex() {
  const byGuid = new Map();
  for (const entry of readdirSync(GAMEOBJECT)) {
    if (!entry.endsWith(".prefab.meta")) continue;
    const text = readFileSync(join(GAMEOBJECT, entry), "utf8");
    const match = /^guid:\s*([0-9a-f]{32})/m.exec(text);
    if (match) byGuid.set(match[1], basename(entry, ".meta"));
  }
  return byGuid;
}

const prefabGuidIndex = buildPrefabGuidIndex();

const derivesFromBasePart = (name) => {
  const seen = new Set();
  let current = name;
  while (current && !seen.has(current)) {
    seen.add(current);
    if (current === "BasePart") return true;
    current = classBases.get(current);
  }
  return false;
};

// The wheel family is derived, not listed: every class whose name ends in `Wheel` and
// that is a BasePart (CartWheel, MotorWheel, OffRoadWheel, StickyWheel on 2.4.0 BPLE).
const wheelClasses = [...classBases.keys()].filter((name) => name.endsWith("Wheel") && derivesFromBasePart(name)).sort();

// ------------------------------------------------------------------ method bodies
/** Body of `public override Joint CustomConnectToPart(...)` inside a class, or null. */
function readCustomConnectBody(className) {
  const text = readFileSync(join(SCRIPTS, `${className}.cs`), "utf8");
  const signature = /public override Joint CustomConnectToPart\([^)]*\)\s*\r?\n\s*\{/.exec(text);
  if (!signature) return null;
  const start = text.indexOf("{", signature.index);
  let depth = 0;
  for (let index = start; index < text.length; index++) {
    if (text[index] === "{") depth++;
    else if (text[index] === "}") {
      depth--;
      if (depth === 0) return text.slice(start, index + 1);
    }
  }
  fail(`${className}.cs: CustomConnectToPart body is unbalanced`);
}

/** `field = 123f;` declared on the class itself (the serialized default). */
function readFieldDefault(className, field) {
  const text = readFileSync(join(SCRIPTS, `${className}.cs`), "utf8");
  const match = new RegExp(`^\\s*public float ${field} = (-?[0-9.]+)f\\s*;`, "m").exec(text);
  return match ? Number(match[1]) : null;
}

const sourceCache = new Map();
/** A class's `.cs` text plus its file name, cached so each file is read once. */
function source(className) {
  if (!sourceCache.has(className)) {
    sourceCache.set(className, { file: `${className}.cs`, text: readFileSync(join(SCRIPTS, `${className}.cs`), "utf8") });
  }
  return sourceCache.get(className);
}

/** 1-based line of a match offset inside a file's text. */
const lineOfIndex = (text, index) => text.slice(0, index).split("\n").length;

/**
 * The first number a regex captures in a class, with its `file:line`. Fails loudly: a
 * renamed or removed literal means the extractor's premise changed, not that the value is
 * absent, so a report is never written from a shape this tool no longer understands.
 */
function readLiteral(className, regex, label) {
  const { file, text } = source(className);
  const match = regex.exec(text);
  if (!match) fail(`${file}: could not find ${label}`);
  return { value: Number(match[1]), file, line: lineOfIndex(text, match.index) };
}

/** A serialized float's literal default (`public float x = 1f;`, also `private static`). */
function readSerializedNumber(className, fieldName) {
  const { file, text } = source(className);
  const pattern = new RegExp(`^[ \\t]*(?:public|private|protected|internal)[ \\t]+(?:static[ \\t]+)?float[ \\t]+${fieldName}[ \\t]*=[ \\t]*(-?[0-9.]+)f[ \\t]*;`, "m");
  const match = pattern.exec(text);
  if (!match) fail(`${file}: no serialized float default for ${fieldName}`);
  return { value: Number(match[1]), file, line: lineOfIndex(text, match.index) };
}

/** The value of a `field = true|false;` assignment, with its `file:line`. */
function readBoolAssignment(className, regex, label) {
  const { file, text } = source(className);
  const match = regex.exec(text);
  if (!match) fail(`${file}: could not find ${label}`);
  return { value: match[1] === "true", file, line: lineOfIndex(text, match.index) };
}

/** Like `readLiteral`, but the *last* match -- for a literal that is written twice (init 0, shoot 0.1). */
function readLastLiteral(className, regex, label) {
  const { file, text } = source(className);
  const matches = [...text.matchAll(new RegExp(regex.source, regex.flags.includes("g") ? regex.flags : `${regex.flags}g`))];
  if (matches.length === 0) fail(`${file}: could not find ${label}`);
  const match = matches[matches.length - 1];
  return { value: Number(match[1]), file, line: lineOfIndex(text, match.index) };
}

const MOTION = /ConfigurableJointMotion\.(\w+)/;

/** The `ConfigurableJointMotion` token a `field = ConfigurableJointMotion.X;` assignment uses. */
function motionOf(body, className, field) {
  const assignment = new RegExp(`(?<![A-Za-z])${field} = ConfigurableJointMotion\\.\\w+`).exec(body);
  if (!assignment) fail(`${className}: CustomConnectToPart does not set ${field}`);
  return MOTION.exec(assignment[0])[1];
}

/**
 * The suspension a wheel class declares in `CustomConnectToPart`, or null when the class
 * does not override it (BasePart.cs:1206 returns null -> Contraption.AddFixedJoint welds
 * the part rigidly). Throws when an override stops looking like the known shape, so a
 * changed original cannot silently produce wrong content.
 */
function readSuspensionDeclaration(className, body) {
  const angular = ["angularXMotion", "angularYMotion", "angularZMotion"].map((name) => motionOf(body, className, name));
  const linear = ["xMotion", "yMotion", "zMotion"].map((name) => motionOf(body, className, name));

  const springField = /linearLimitSpring\.spring = (\w+)\s*;/.exec(body);
  const damper = /linearLimitSpring\.damper = (-?[0-9.]+)f\s*;/.exec(body);
  const limit = /linearLimit\.limit = (-?[0-9.]+)f\s*;/.exec(body);
  const bounciness = /linearLimit\.bounciness = (-?[0-9.]+)f\s*;/.exec(body);
  if (!springField || !damper || !limit || !bounciness) {
    fail(`${className}: CustomConnectToPart does not look like the known linear-limit spring declaration`);
  }

  const defaultStiffness = readFieldDefault(className, springField[1]);
  if (defaultStiffness === null) {
    fail(`${className}: serialized field ${springField[1]} has no literal default`);
  }

  return {
    stiffnessField: springField[1],
    defaultStiffness,
    damper: Number(damper[1]),
    restOffset: Number(limit[1]),
    bounciness: Number(bounciness[1]),
    angularLocked: angular.every((mode) => mode === "Locked"),
    linearLocked: linear[0] === "Locked" && linear[2] === "Locked",
    linearLimited: linear[1] === "Limited",
    angular,
    linear,
  };
}

// Only the class that declares the suspension needs its declaration parsed; the rest are
// reported as rigid by absence of an override.
const declarations = new Map();
for (const className of wheelClasses) {
  const body = readCustomConnectBody(className);
  declarations.set(className, body === null ? null : readSuspensionDeclaration(className, body));
}

const springClasses = wheelClasses.filter((name) => declarations.get(name) !== null);
if (springClasses.length === 0) {
  fail("no wheel class overrides CustomConnectToPart: the extractor's premise changed");
}

// -------------------------------------------------- spring / boxing-glove scripts
// Two *different* classes. `Spring` overrides CustomConnectToPart and picks its joint from
// `customPartIndex`; `SpringBoxingGlove` never overrides it (its own connection stays the
// ordinary weld) and instead suspends a second rigidbody -- the glove -- from a
// ConfigurableJoint. Constants are parsed from the `.cs` text, never hand-copied.
const SPRING_CLASS = "Spring";
const BOXING_GLOVE_CLASS = "SpringBoxingGlove";

/** `{ file, line }` of a regex's first match in a class, or null. */
function cite(className, regex) {
  const { file, text } = source(className);
  const match = regex.exec(text);
  return match ? { file, line: lineOfIndex(text, match.index) } : null;
}

// ------------------------------------------------------------- IN feature settings
// PigForge's baseline is the *declaration* defaults (`INDeclarationSettingsExp.json`, read
// through `tools/in-settings/vanilla-settings.mjs`): `INSettingsBExp.json` is the "everything
// on" mod profile (130 overrides) and it is what turns `StableSpringConnection` and
// `StrongSpringConnection` on -- which would make every spring skin a 1 kg `SpringJoint`
// bungee with a 1200 N break force instead of the declaration default's own prefab mass, y
// soft limit and 250 N. No original part's numbers may come from a mod profile, so nothing
// here reads the experiment file any more (docs/specs/in-settings-profiles.md, gaps G105/G107).
const vanilla = loadVanillaSettings(BPLE);
const declarationText = readFileSync(join(TEXTEXT, VANILLA_SETTINGS_NAME), "utf8");
const feature = (name) => {
  const offset = declarationText.indexOf(`"name": "${name}"`);
  return {
    value: vanilla.get(name),
    file: VANILLA_SETTINGS_NAME,
    line: offset < 0 ? null : lineOfIndex(declarationText, offset),
  };
};
const inFeatures = {
  StrongSpringConnection: feature("StrongSpringConnection"),
  StableSpringConnection: feature("StableSpringConnection"),
  BoxingGloveLength: feature("BoxingGloveLength"),
  SwitchableBoxingGlove: feature("SwitchableBoxingGlove"),
  OffRoadWheel: feature("OffRoadWheel"),
};

const springBody = readCustomConnectBody(SPRING_CLASS);
if (springBody === null) fail(`${SPRING_CLASS}.cs no longer overrides CustomConnectToPart`);
if (readCustomConnectBody(BOXING_GLOVE_CLASS) !== null) {
  fail(`${BOXING_GLOVE_CLASS}.cs now overrides CustomConnectToPart: its joint path changed`);
}

const springDeclaration = (() => {
  const constants = Object.fromEntries(
    ["SPRING_LIMIT_SPRING", "SPRING_DAMPING", "SPRING_LIMIT", "SPRING_BOUNCINESS", "SPRING_BREAK_FORCE"]
      .map((name) => [name, readSerializedNumber(SPRING_CLASS, name)]),
  );
  const guard = /INSettings\.GetBool\(INFeature\.StableSpringConnection\)\s*&&\s*\(customPartIndex == 0 \|\| customPartIndex == 2\)/.exec(springBody);
  if (!guard) fail(`${SPRING_CLASS}.cs: the StableSpringConnection / customPartIndex==0|2 guard changed`);
  const configure = /springJoint\.ConfigureSpringJoint\((-?[0-9.]+)f, (-?[0-9.]+)f, (\w+), (\w+)\)/.exec(springBody);
  if (!configure) fail(`${SPRING_CLASS}.cs: the SpringJoint ConfigureSpringJoint call changed`);
  const anchor = /springJoint\.anchor = new Vector3\((-?[0-9.]+)f, (-?[0-9.]+)f, (-?[0-9.]+)f\)/.exec(springBody);
  if (!anchor) fail(`${SPRING_CLASS}.cs: the SpringJoint anchor changed`);
  if (!/springJoint\.breakForce = (\w+)/.test(springBody) || !/configurableJoint\.breakForce = (\w+)/.test(springBody)) {
    fail(`${SPRING_CLASS}.cs: a breakForce assignment changed`);
  }
  if (!/linearLimitSpring\.spring = (\w+)/.test(springBody) || !/linearLimit\.limit = (\w+)/.test(springBody)) {
    fail(`${SPRING_CLASS}.cs: the ConfigurableJoint linear-limit fields changed`);
  }
  const { file, text } = source(SPRING_CLASS);
  const bodyStart = text.indexOf(springBody);
  const bodyLine = (regex) => {
    const match = regex.exec(springBody);
    return match ? lineOfIndex(text, bodyStart + match.index) : null;
  };
  const strongOverride = /SPRING_BREAK_FORCE = WPFMonoBehaviour\.gameData\.(\w+) \* (-?[0-9.]+)f/.exec(text);
  if (!strongOverride) fail(`${file}: the StrongSpringConnection breakForce override disappeared`);
  const strengthSet = /m_jointConnectionStrength = JointConnectionStrength\.(\w+)/.exec(text);

  return {
    class: SPRING_CLASS,
    constants,
    strongSpringOverride: {
      ...cite(SPRING_CLASS, /SPRING_BREAK_FORCE = WPFMonoBehaviour\.gameData\.(\w+) \* (-?[0-9.]+)f/),
      guard: "StrongSpringConnection",
      strengthField: strongOverride[1],
      factor: Number(strongOverride[2]),
      setsStrengthTo: strengthSet ? strengthSet[1] : null,
    },
    stableGuard: { requires: "StableSpringConnection", indexes: [0, 2], file, line: bodyLine(/INSettings\.GetBool\(INFeature\.StableSpringConnection\)/) },
    springJoint: {
      file,
      line: bodyLine(/springJoint\.ConfigureSpringJoint/),
      configure: { minDistance: Number(configure[1]), maxDistance: Number(configure[2]), spring: configure[3], damper: configure[4] },
      anchor: { x: Number(anchor[1]), y: Number(anchor[2]), z: Number(anchor[3]) },
      breakForce: "SPRING_BREAK_FORCE",
      enablePreprocessing: readBoolAssignment(SPRING_CLASS, /springJoint\.enablePreprocessing = (true|false)/, "springJoint.enablePreprocessing").value,
      extension: "JointExtensions.cs:5-12 (ConfigureSpringJoint sets minDistance/maxDistance/spring/damper)",
    },
    configurableJoint: {
      file,
      line: bodyLine(/configurableJoint\.yMotion = ConfigurableJointMotion\.Limited/),
      angular: ["angularXMotion", "angularYMotion", "angularZMotion"].map((name) => motionOf(springBody, SPRING_CLASS, name)),
      linear: ["xMotion", "yMotion", "zMotion"].map((name) => motionOf(springBody, SPRING_CLASS, name)),
      enablePreprocessing: readBoolAssignment(SPRING_CLASS, /configurableJoint\.enablePreprocessing = (true|false)/, "configurableJoint.enablePreprocessing").value,
      configuredInWorldSpace: readBoolAssignment(SPRING_CLASS, /configurableJoint\.configuredInWorldSpace = (true|false)/, "configurableJoint.configuredInWorldSpace").value,
      breakForce: "SPRING_BREAK_FORCE",
      linearLimitSpring: { spring: "SPRING_LIMIT_SPRING", damper: "SPRING_DAMPING" },
      linearLimit: { limit: "SPRING_LIMIT", bounciness: "SPRING_BOUNCINESS" },
    },
    breakDistance: readLiteral(SPRING_CLASS, /Vector3\.Distance\(a, b\) > (-?[0-9.]+)f/, "the 3 m break distance"),
    superGlueGuard: /!base\.contraption\.HasSuperGlue/.test(springBody),
    ensureRigidbodyMass: {
      ...readLiteral(SPRING_CLASS, /base\.rigidbody\.mass = (-?[0-9.]+)f/, "the EnsureRigidbody mass"),
      requires: "StableSpringConnection",
    },
    endPointPrefabField: /public GameObject m_endPointPrefab;/.test(text) ? "m_endPointPrefab" : fail(`${file}: m_endPointPrefab disappeared`),
  };
})();

const boxingGloveDeclaration = (() => {
  const defaults = Object.fromEntries(
    ["m_targetDistanceY", "m_targetDeviationX", "m_WindingTime", "m_ShootTime", "m_SpringYDrive", "m_SpringYDriveDamper"]
      .map((name) => [name, readSerializedNumber(BOXING_GLOVE_CLASS, name)]),
  );
  const { file, text } = source(BOXING_GLOVE_CLASS);
  const gloveMasses = [...text.matchAll(/component\d\.mass = (-?[0-9.]+)f;/g)]
    .map((match) => ({ value: Number(match[1]), file, line: lineOfIndex(text, match.index) }));
  if (gloveMasses.length !== 2) fail(`${file}: expected 2 glove mass assignments (initialize + wind), found ${gloveMasses.length}`);
  const [gloveMass, windingGloveMass] = gloveMasses;
  if (!/positionSpring = m_SpringYDrive/.test(text) || !/positionDamper = m_SpringYDriveDamper/.test(text)) {
    fail(`${file}: the yDrive no longer reads m_SpringYDrive/m_SpringYDriveDamper`);
  }
  const xDrive = /positionSpring = (-?[0-9.]+)f,\s*\r?\n\s*positionDamper = (-?[0-9.]+)f,/.exec(text);
  if (!xDrive) fail(`${file}: the literal JointDrive (xDrive) changed`);
  if (!/m_targetDistanceY \* INSettings\.GetFloat\(INFeature\.BoxingGloveLength\)/.test(text)) {
    fail(`${file}: the shoot target no longer scales by BoxingGloveLength`);
  }
  if (!/public GameObject m_BoxingGlovePrefab;/.test(text)) fail(`${file}: m_BoxingGlovePrefab disappeared`);

  return {
    class: BOXING_GLOVE_CLASS,
    overridesCustomConnectToPart: false,
    defaults,
    gloveMass,
    windingGloveMass,
    solverIterationsFactor: readLiteral(BOXING_GLOVE_CLASS, /solverIterations \* (-?[0-9.]+)f/, "the solverIterations factor"),
    gloveJoint: {
      file,
      line: cite(BOXING_GLOVE_CLASS, /configurableJoint\.yMotion = ConfigurableJointMotion\.Limited/).line,
      angular: ["angularXMotion", "angularYMotion", "angularZMotion"].map((name) => motionOf(text, BOXING_GLOVE_CLASS, name)),
      linear: ["xMotion", "yMotion", "zMotion"].map((name) => motionOf(text, BOXING_GLOVE_CLASS, name)),
      yDrive: { springField: "m_SpringYDrive", damperField: "m_SpringYDriveDamper", maximumForce: "float.MaxValue" },
      xDrive: { spring: Number(xDrive[1]), damper: Number(xDrive[2]), maximumForce: "float.MaxValue" },
      linearLimit: {
        limit: readLiteral(BOXING_GLOVE_CLASS, /linearLimit\.limit = (-?[0-9.]+)f/, "the glove linearLimit.limit").value,
        bounciness: readLiteral(BOXING_GLOVE_CLASS, /linearLimit\.bounciness = (-?[0-9.]+)f/, "the glove linearLimit.bounciness").value,
      },
      linearLimitSpring: {
        spring: readLiteral(BOXING_GLOVE_CLASS, /linearLimitSpring\.spring = (-?[0-9.]+)f/, "the glove linearLimitSpring.spring").value,
        damper: readLiteral(BOXING_GLOVE_CLASS, /linearLimitSpring\.damper = (-?[0-9.]+)f/, "the glove linearLimitSpring.damper").value,
      },
      projectionMode: /projectionMode = JointProjectionMode\.PositionAndRotation/.test(text) ? "PositionAndRotation" : fail(`${file}: joint projectionMode changed`),
      projectionDistance: readLiteral(BOXING_GLOVE_CLASS, /projectionDistance = (-?[0-9.]+)f/, "projectionDistance"),
      enablePreprocessing: readBoolAssignment(BOXING_GLOVE_CLASS, /configurableJoint\.enablePreprocessing = (true|false)/, "configurableJoint.enablePreprocessing").value,
    },
    shoot: {
      file,
      line: cite(BOXING_GLOVE_CLASS, /component2\.targetPosition = new Vector3/).line,
      linearLimitSpring: readLastLiteral(BOXING_GLOVE_CLASS, /linearLimitSpring\.spring = (-?[0-9.]+)f;/, "the shoot linearLimitSpring.spring"),
      target: "(m_targetDeviationX * +/-1, m_targetDistanceY * BoxingGloveLength, 0)",
    },
    wind: {
      file,
      line: cite(BOXING_GLOVE_CLASS, /yDrive\.positionSpring = num \* m_targetDistanceY/).line,
      springFactor: readLiteral(BOXING_GLOVE_CLASS, /float num = (-?[0-9.]+)f/, "the wind spring factor").value,
      damperFactor: readLiteral(BOXING_GLOVE_CLASS, /positionDamper = num \* (-?[0-9.]+)f/, "the wind damper factor").value,
    },
  };
})();

// ------------------------------------------------------------------ prefab scan
const CONTENT = JSON.parse(readFileSync(CONTENT_PARTS, "utf8"));
const MAP = JSON.parse(readFileSync(TEXTURE_MAP, "utf8"));
const nameByPrefab = new Map();
const partByPrefab = new Map();
for (const [section, ids] of [["parts", MAP.parts], ["variants", MAP.variants]]) {
  for (const [id, prefab] of Object.entries(ids ?? {})) {
    if (typeof prefab === "string" && prefab.length > 0) {
      nameByPrefab.set(prefab, Number(id));
      partByPrefab.set(prefab, CONTENT.parts.find((part) => part.partTypeId === Number(id)) ?? null);
    }
  }
}

const readNumber = (text, field) => {
  const match = new RegExp(`^\\s*${field}:\\s*(-?[0-9.]+)\\s*$`, "m").exec(text);
  return match ? Number(match[1]) : null;
};

const prefabFiles = readdirSync(GAMEOBJECT).filter((entry) => entry.startsWith("Part_") && entry.endsWith(".prefab"));
const prefabs = {};
for (const file of prefabFiles.sort()) {
  const name = basename(file, ".prefab");
  const text = readFileSync(join(GAMEOBJECT, file), "utf8");
  const classes = [...new Set([...text.matchAll(/m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})/g)]
    .map((match) => guidIndex.get(match[1]))
    .filter((value) => value !== undefined))];
  const wheelClass = classes.find((value) => wheelClasses.includes(value)) ?? null;
  const declaration = wheelClass ? declarations.get(wheelClass) : null;

  /** `m_springStiffness: 50` present in the prefab text means the prefab-authored override. */
  const serialized = declaration ? readNumber(text, declaration.stiffnessField) : null;
  prefabs[name] = {
    classes,
    wheelClass,
    springClass: declaration !== null,
    stiffnessSource: declaration === null ? null : (serialized === null ? "class default" : "prefab override"),
    stiffness: declaration === null ? null : (serialized ?? declaration.defaultStiffness),
    damper: declaration?.damper ?? null,
    restOffset: declaration?.restOffset ?? null,
    bounciness: declaration?.bounciness ?? null,
    motion: declaration === null ? null : {
      angular: declaration.angular,
      linear: declaration.linear,
      angularLocked: declaration.angularLocked,
      linearLocked: declaration.linearLocked,
      linearLimited: declaration.linearLimited,
    },
    jointType: readNumber(text, "m_jointType"),
    jointConnectionType: readNumber(text, "m_jointConnectionType"),
    jointConnectionDirection: readNumber(text, "m_jointConnectionDirection"),
    customJointConnectionDirection: readNumber(text, "m_customJointConnectionDirection"),
    jointConnectionStrength: readNumber(text, "m_jointConnectionStrength"),
    jointPreprocessing: readNumber(text, "m_jointPreprocessing"),
    partTypeId: nameByPrefab.get(name) ?? null,
  };
}

// ------------------------------------------------------- content-part projection
const parts = {};
const unmappedSpringPrefabs = [];
for (const [name, entry] of Object.entries(prefabs)) {
  if (entry.partTypeId === null) {
    if (entry.springClass) unmappedSpringPrefabs.push(name);
    continue;
  }

  const part = partByPrefab.get(name);
  parts[entry.partTypeId] = {
    prefab: name,
    name: part?.name ?? "",
    wheelClass: entry.wheelClass,
    suspension: entry.springClass
      ? { stiffness: entry.stiffness, damper: entry.damper, restOffset: entry.restOffset, bounciness: entry.bounciness }
      : null,
    jointConnectionType: entry.jointConnectionType,
    customJointConnectionDirection: entry.customJointConnectionDirection,
    jointConnectionStrength: entry.jointConnectionStrength,
  };
}

// Every wheel-family prefab (mapped or not), so the report states the whole truth about
// "which wheel prefabs reach a wheel connection and what they declare".
const wheelPrefabs = Object.entries(prefabs)
  .filter(([, entry]) => entry.wheelClass !== null)
  .sort((left, right) => left[0].localeCompare(right[0]))
  .map(([name, entry]) => ({
    prefab: name,
    partTypeId: entry.partTypeId,
    wheelClass: entry.wheelClass,
    stiffness: entry.springClass ? entry.stiffness : null,
    stiffnessSource: entry.stiffnessSource,
  }));

// -------------------------------------------------------------- frame welds (context)
// Report-only: what the original's *weld* path declares. The wheels above are the only
// parts whose joint value is a spring; everything else is a FixedJoint/ConfigurableJoint
// whose stiffness comes from Unity's `enablePreprocessing`, which is per-part.
const frameFacts = (() => {
  const preprocessing = Object.entries(prefabs)
    .filter(([, entry]) => entry.jointPreprocessing === 1)
    .map(([name]) => name)
    .sort();
  const strengthDistribution = {};
  for (const entry of Object.values(prefabs)) {
    const key = entry.jointConnectionStrength === null ? "absent" : String(entry.jointConnectionStrength);
    strengthDistribution[key] = (strengthDistribution[key] ?? 0) + 1;
  }

  const contraption = readFileSync(join(SCRIPTS, "Contraption.cs"), "utf8");
  const addFixedJoint = /public void AddFixedJoint\(BasePart part, BasePart other\)/.test(contraption);
  const preprocessingExpression = /joint\.enablePreprocessing = ([^;]+);/.exec(contraption)?.[1]?.trim() ?? null;
  const strengths = {};
  const strengthBlock = /public float GetJointConnectionStrength\(BasePart\.JointConnectionStrength strength\)\s*\{([\s\S]*?)\n\t\}/.exec(contraption);
  if (strengthBlock) {
    for (const match of strengthBlock[1].matchAll(/(\w+) => WPFMonoBehaviour\.gameData\.(m_jointConnectionStrength\w*)/g)) {
      strengths[match[1]] = match[2];
    }
  }

  const frameJointManager = readFileSync(join(SCRIPTS, "FrameJointManager.cs"), "utf8");
  const frameBreak = /breakForce = ([^;]+);/.exec(frameJointManager)?.[1]?.trim() ?? null;

  const gameData = readFileSync(join(ASSETS, "MonoBehaviour", "GameData.asset"), "utf8");
  const strengthValues = {};
  for (const field of new Set(Object.values(strengths))) {
    const match = new RegExp(`^\\s*${field}:\\s*(-?[0-9.]+)\\s*$`, "m").exec(gameData);
    if (match) strengthValues[field] = Number(match[1]);
  }

  return {
    addFixedJoint,
    enablePreprocessing: preprocessingExpression,
    jointPreprocessingTrue: preprocessing.length,
    jointPreprocessingPrefabs: preprocessing,
    jointConnectionStrengthDistribution: strengthDistribution,
    jointConnectionStrengthFields: strengths,
    jointConnectionStrengthValues: strengthValues,
    frameJointManagerBreakForce: frameBreak,
    springFeatureValues: {
      StrongSpringConnection: inFeatures.StrongSpringConnection.value,
      StableSpringConnection: inFeatures.StableSpringConnection.value,
      OffRoadWheel: inFeatures.OffRoadWheel.value,
    },
  };
})();

// ------------------------------------------------- spring / boxing-glove prefabs
// Read per prefab document, so a value inside a child object (a sprite, the glove reference)
// can never be mistaken for the part's own serialized field.
const SPRING_FAMILY_PATTERN = /^Part_Spring_(\d+)_SET$/;
const BOXING_GLOVE_FAMILY_PATTERN = /^Part_SpringBoxingGlove_(\d+)_SET$/;

/** Unity YAML documents of one prefab: `{ type, line, body }` per `--- !u!` block. */
function readPrefab(name) {
  const file = name.endsWith(".prefab") ? name : `${name}.prefab`;
  const text = readFileSync(join(GAMEOBJECT, file), "utf8");
  const documents = [];
  let current = null;
  for (const [index, line] of text.split(/\r?\n/).entries()) {
    if (line.startsWith("--- !u!")) {
      if (current) documents.push(current);
      current = { id: /&(-?\d+)/.exec(line)?.[1] ?? null, type: "", line: index + 1, body: "" };
      continue;
    }
    if (!current) continue;
    if (current.type === "" && line.trim().length > 0) current.type = line.trim().replace(/:$/, "");
    current.body += `${line}\n`;
  }
  if (current) documents.push(current);
  const byId = new Map(documents.filter((document) => document.id !== null).map((document) => [document.id, document]));
  return { text, documents, byId };
}

/** The document whose `m_Script` is the given class. */
const scriptDocument = (documents, className) => documents.find((document) => {
  const match = /m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})/.exec(document.body);
  return match ? guidIndex.get(match[1]) === className : false;
}) ?? null;

/** A `field: {fileID: …, guid: …}` reference resolved to the referenced prefab's name. */
function prefabReference(body, field) {
  const match = new RegExp(`${field}:\\s*\\{fileID:\\s*-?\\d+,\\s*guid:\\s*([0-9a-f]{32})`).exec(body);
  if (!match) return null;
  return { guid: match[1], prefab: prefabGuidIndex.get(match[1]) ?? null };
}

const vectorOf = (text, field) => {
  const match = new RegExp(`^\\s*${field}:\\s*\\{x:\\s*(-?[0-9.]+),\\s*y:\\s*(-?[0-9.]+),\\s*z:\\s*(-?[0-9.]+)\\s*\\}`, "m").exec(text);
  return match ? { x: Number(match[1]), y: Number(match[2]), z: Number(match[3]) } : null;
};

/** Unity collider type -> the content `shapes[].kind` token (schemas/part-content-v1.schema.json). */
const COLLIDER_KIND = { BoxCollider: "box", SphereCollider: "sphere", CapsuleCollider: "capsule", MeshCollider: "convexMesh" };

/** Every collider document of a prefab, structured as content can consume it: the shape `kind`,
 *  the prefab's own `m_Radius`/`m_Center`/`m_Size`, its owner GameObject's layer and trigger
 *  flag. `inContent` marks the solids -- the glove body's shapes are exactly the non-trigger
 *  colliders (a trigger collider is the original's hit/effect marker, never the physical body). */
const collidersOf = (documents, byId) => documents
  .filter((document) => /^(Box|Sphere|Capsule|Mesh)Collider$/.test(document.type))
  .map((document) => {
    const gameObject = /m_GameObject:\s*\{fileID:\s*(-?\d+)\}/.exec(document.body);
    const owner = gameObject && byId ? byId.get(gameObject[1]) : null;
    const isTrigger = readNumber(document.body, "m_IsTrigger");
    const size = vectorOf(document.body, "m_Size");
    return {
      kind: COLLIDER_KIND[document.type] ?? document.type,
      type: document.type,
      line: document.line,
      layer: owner ? readNumber(owner.body, "m_Layer") : null,
      isTrigger,
      inContent: isTrigger === 0,
      radius: readNumber(document.body, "m_Radius"),
      center: vectorOf(document.body, "m_Center"),
      size,
      halfExtents: size ? { x: size.x / 2, y: size.y / 2, z: size.z / 2 } : null,
    };
  });

/** The `Spring` joint a part builds for its own connection, chosen by `customPartIndex`. */
const springJointPath = (customPartIndex) => {
  const stable = inFeatures.StableSpringConnection.value === true;
  const usesSpringJoint = stable && (customPartIndex === 0 || customPartIndex === 2);
  const springJoint = springDeclaration.springJoint;
  const configurableJoint = springDeclaration.configurableJoint;
  const constants = springDeclaration.constants;
  if (usesSpringJoint) {
    return {
      route: "SpringJoint",
      reason: `StableSpringConnection = true and customPartIndex ${customPartIndex} is 0 or 2 (Spring.cs:100)`,
      source: `${springJoint.file}:${springJoint.line}`,
      spring: constants.SPRING_LIMIT_SPRING.value,
      damper: constants.SPRING_DAMPING.value,
      minDistance: springJoint.configure.minDistance,
      maxDistance: springJoint.configure.maxDistance,
      anchor: springJoint.anchor,
      enablePreprocessing: springJoint.enablePreprocessing,
      breakForceField: springJoint.breakForce,
      breakForceDeclared: constants.SPRING_BREAK_FORCE.value,
    };
  }
  return {
    route: "ConfigurableJointYLimit",
    reason: `customPartIndex ${customPartIndex} is not 0 or 2, so the ConfigurableJoint branch (Spring.cs:114-134) is taken`,
    source: `${configurableJoint.file}:${configurableJoint.line}`,
    angular: configurableJoint.angular,
    linear: configurableJoint.linear,
    spring: constants.SPRING_LIMIT_SPRING.value,
    damper: constants.SPRING_DAMPING.value,
    limit: constants.SPRING_LIMIT.value,
    bounciness: constants.SPRING_BOUNCINESS.value,
    configuredInWorldSpace: configurableJoint.configuredInWorldSpace,
    enablePreprocessing: configurableJoint.enablePreprocessing,
    breakForceField: configurableJoint.breakForce,
    breakForceDeclared: constants.SPRING_BREAK_FORCE.value,
  };
};

/** The shared part fields of one family prefab, read from its own MonoBehaviour document. */
function familyEntries(pattern, className) {
  return prefabFiles
    .map((file) => basename(file, ".prefab"))
    .filter((name) => pattern.test(name))
    .sort()
    .map((name) => {
      const { documents, byId } = readPrefab(name);
      const script = scriptDocument(documents, className);
      if (!script) fail(`${name}: no ${className} MonoBehaviour`);
      const body = script.body;
      const customPartIndex = readNumber(body, "customPartIndex");
      if (customPartIndex === null) fail(`${name}: customPartIndex is absent`);
      return {
        prefab: name,
        partTypeId: nameByPrefab.get(name) ?? null,
        scriptLine: script.line,
        customPartIndex,
        mass: readNumber(body, "m_mass"),
        jointType: readNumber(body, "m_jointType"),
        jointConnectionType: readNumber(body, "m_jointConnectionType"),
        jointConnectionStrength: readNumber(body, "m_jointConnectionStrength"),
        jointConnectionDirection: readNumber(body, "m_jointConnectionDirection"),
        customJointConnectionDirection: readNumber(body, "m_customJointConnectionDirection"),
        jointPreprocessing: readNumber(body, "m_jointPreprocessing"),
        colliders: collidersOf(documents, byId),
        references: {
          endPointPrefab: prefabReference(body, "m_endPointPrefab"),
          boxingGlovePrefab: prefabReference(body, "m_BoxingGlovePrefab"),
        },
        overrides: {
          m_targetDistanceY: readNumber(body, "m_targetDistanceY"),
          m_targetDeviationX: readNumber(body, "m_targetDeviationX"),
          m_WindingTime: readNumber(body, "m_WindingTime"),
          m_ShootTime: readNumber(body, "m_ShootTime"),
          m_SpringYDrive: readNumber(body, "m_SpringYDrive"),
          m_SpringYDriveDamper: readNumber(body, "m_SpringYDriveDamper"),
          m_checkRotation: readNumber(body, "m_checkRotation"),
        },
      };
    });
}

const springFamily = familyEntries(SPRING_FAMILY_PATTERN, SPRING_CLASS)
  .map((entry) => ({ ...entry, jointPath: springJointPath(entry.customPartIndex) }));

const boxingGloveFamily = familyEntries(BOXING_GLOVE_FAMILY_PATTERN, BOXING_GLOVE_CLASS)
  .map((entry) => {
    const glovePrefab = entry.references.boxingGlovePrefab?.prefab ?? null;
    const glove = glovePrefab ? readPrefab(glovePrefab) : null;
    const rigidbody = glove?.documents.find((document) => document.type === "Rigidbody")?.body ?? "";
    const colliders = glove ? collidersOf(glove.documents, glove.byId) : [];
    // The glove body's shapes are its solid colliders, in the content shapeDefinition shape
    // (`kind` + `radius`/`halfExtents` + `center`); any other collider is reported and excluded.
    const shapes = colliders.filter((collider) => collider.inContent)
      .map((collider) => ({ kind: collider.kind, radius: collider.radius, halfExtents: collider.halfExtents, center: collider.center }));
    return {
      ...entry,
      jointPath: {
        route: "Weld",
        reason: `${BOXING_GLOVE_CLASS} does not override CustomConnectToPart (BasePart.cs:1214 returns null), so Contraption.AddFixedJoint (Contraption.cs:1504) welds it`,
        source: "BasePart.cs:1214, Contraption.cs:1504",
      },
      glove: glove ? {
        prefab: glovePrefab,
        serializedMass: readNumber(rigidbody, "m_Mass"),
        hasBoxingGloveScript: Boolean(scriptDocument(glove.documents, "BoxingGlove")),
        colliders,
        shapes,
        excluded: colliders.filter((collider) => !collider.inContent)
          .map((collider) => ({ kind: collider.kind, type: collider.type, line: collider.line,
            reason: "m_IsTrigger != 0: a hit/effect marker, not the glove body" })),
      } : null,
    };
  });

// -------------------------------------------------------------- hard assertions
// Everything above is what the original does; the checks below turn a silent drift (a renamed
// field, a retuned constant, a flipped IN switch, a different prefab family) into a failure
// instead of a wrong report. Every failure prints the expected/actual diff and exits 1.
const checks = [];
const check = (label, actual, expected) => {
  checks.push({ label, actual, expected, pass: JSON.stringify(actual) === JSON.stringify(expected) });
};
const histogram = (values) => values.reduce((counts, value) => ({ ...counts, [value]: (counts[value] ?? 0) + 1 }), {});

const wheelDeclaringSprings = Object.entries(prefabs).filter(([, entry]) => entry.springClass).map(([name]) => name).sort();
check("wheel prefabs scanned", wheelPrefabs.length, 39);
check("wheel prefabs declaring a spring", wheelDeclaringSprings.length, 1);
check("Spring family prefab count", springFamily.length, 4);
check("SpringBoxingGlove family prefab count", boxingGloveFamily.length, 5);
check("Spring family customPartIndex", springFamily.map((entry) => entry.customPartIndex), [0, 1, 2, 3]);
check("SpringBoxingGlove family customPartIndex", boxingGloveFamily.map((entry) => entry.customPartIndex), [0, 1, 2, 3, 4]);

const springPathHistogram = histogram(springFamily.map((entry) => entry.jointPath.route));
const boxingGlovePathHistogram = histogram(boxingGloveFamily.map((entry) => entry.jointPath.route));
// Declaration defaults: StableSpringConnection is false, so the SpringJoint (bungee) arm of
// CustomConnectToPart never runs and every skin takes the ConfigurableJoint y-soft-limit branch.
check("Spring joint-path histogram", springPathHistogram, { ConfigurableJointYLimit: 4 });
check("SpringBoxingGlove joint-path histogram", boxingGlovePathHistogram, { Weld: 5 });
check("SpringBoxingGlove glove prefab family", boxingGloveFamily.map((entry) => entry.glove?.prefab),
  ["BoxingGlove.prefab", "BoxingGlove_2.prefab", "BoxingGlove_3.prefab", "BoxingGlove_4.prefab", "BoxingGlove_5.prefab"]);
check("Spring endpoint prefab", [...new Set(springFamily.map((entry) => entry.references.endPointPrefab?.prefab))], ["SpringEndpoint.prefab"]);
check(`INDeclarationSettingsExp StrongSpringConnection (${inFeatures.StrongSpringConnection.file})`, inFeatures.StrongSpringConnection.value, false);
check(`INDeclarationSettingsExp StableSpringConnection (${inFeatures.StableSpringConnection.file})`, inFeatures.StableSpringConnection.value, false);

const springConstants = springDeclaration.constants;
check("Spring.SPRING_LIMIT_SPRING", springConstants.SPRING_LIMIT_SPRING.value, 250);
check("Spring.SPRING_DAMPING", springConstants.SPRING_DAMPING.value, 20);
check("Spring.SPRING_LIMIT", springConstants.SPRING_LIMIT.value, 0.1);
check("Spring.SPRING_BOUNCINESS", springConstants.SPRING_BOUNCINESS.value, 1);
check("Spring.SPRING_BREAK_FORCE", springConstants.SPRING_BREAK_FORCE.value, 250);
const gloveDefaults = boxingGloveDeclaration.defaults;
check("SpringBoxingGlove.m_SpringYDrive", gloveDefaults.m_SpringYDrive.value, 60);
check("SpringBoxingGlove.m_SpringYDriveDamper", gloveDefaults.m_SpringYDriveDamper.value, 3);
check("SpringBoxingGlove.m_targetDistanceY", gloveDefaults.m_targetDistanceY.value, 2.5);
check("SpringBoxingGlove.m_targetDeviationX", gloveDefaults.m_targetDeviationX.value, 0.01);
check("SpringBoxingGlove.m_WindingTime", gloveDefaults.m_WindingTime.value, 1);
check("SpringBoxingGlove.m_ShootTime", gloveDefaults.m_ShootTime.value, 1);
check("SpringBoxingGlove glove mass", boxingGloveDeclaration.gloveMass.value, 0.5);
check("SpringBoxingGlove winding mass", boxingGloveDeclaration.windingGloveMass.value, 0.01);
check("SpringBoxingGlove solverIterations factor", boxingGloveDeclaration.solverIterationsFactor.value, 1.6);
check("GameData.m_jointConnectionStrengthHigh", frameFacts.jointConnectionStrengthValues.m_jointConnectionStrengthHigh, 600);

// The glove body: every skin points at its own BoxingGlove*.prefab, whose rigidbody mass and
// solid collider become the content `glove.mass` / `glove.shapes`.
check("SpringBoxingGlove glove serialized mass", boxingGloveFamily.map((entry) => entry.glove?.serializedMass), [0.5, 0.5, 0.5, 0.5, 0.5]);
check("SpringBoxingGlove glove collider count", boxingGloveFamily.map((entry) => entry.glove?.colliders.length), [1, 1, 1, 1, 1]);
check("SpringBoxingGlove glove shape kinds", boxingGloveFamily.map((entry) => entry.glove?.shapes.map((shape) => shape.kind)), [["sphere"], ["sphere"], ["sphere"], ["sphere"], ["sphere"]]);
check("SpringBoxingGlove glove shape radii", boxingGloveFamily.map((entry) => entry.glove?.shapes.map((shape) => shape.radius)), [[0.3], [0.3], [0.3], [0.3], [0.3]]);
check("SpringBoxingGlove glove shape centres", boxingGloveFamily.map((entry) => entry.glove?.shapes.map((shape) => shape.center)), Array.from({ length: 5 }, () => [{ x: 0, y: 0, z: 0 }]));
check("SpringBoxingGlove glove excluded colliders", boxingGloveFamily.map((entry) => entry.glove?.excluded.length), [0, 0, 0, 0, 0]);
check("SpringBoxingGlove glove mass == Initialize mass", boxingGloveFamily.every((entry) => entry.glove?.serializedMass === boxingGloveDeclaration.gloveMass.value), true);
check(`INDeclarationSettingsExp SwitchableBoxingGlove (${inFeatures.SwitchableBoxingGlove.file})`, inFeatures.SwitchableBoxingGlove.value, false);
check("IN BoxingGloveLength", inFeatures.BoxingGloveLength.value, 1);

const failures = checks.filter((entry) => !entry.pass);
if (failures.length > 0) {
  console.error(`\n${failures.length} hard assertion(s) failed -- the original changed, refusing to report:`);
  for (const failure of failures) {
    console.error(`  - ${failure.label}: expected ${JSON.stringify(failure.expected)}, got ${JSON.stringify(failure.actual)}`);
  }
  console.error("");
  process.exit(1);
}

// StrongSpringConnection retunes the constants at Awake; StableSpringConnection overrides the
// part's mass. Both are false in the declaration defaults, so a vanilla spring keeps
// `SPRING_BREAK_FORCE` (250 N), its prefab `m_jointConnectionStrength` (Normal) and its own
// `m_mass`; the report still names the profile-B spelling it would take.
const strongSpringConnection = inFeatures.StrongSpringConnection.value === true;
const stableSpringConnection = inFeatures.StableSpringConnection.value === true;
const springRuntime = {
  strongSpringConnection,
  stableSpringConnection,
  breakForce: strongSpringConnection
    ? {
        value: springDeclaration.strongSpringOverride.factor * frameFacts.jointConnectionStrengthValues[springDeclaration.strongSpringOverride.strengthField],
        expression: `${springDeclaration.strongSpringOverride.strengthField} * ${springDeclaration.strongSpringOverride.factor}`,
        declared: springConstants.SPRING_BREAK_FORCE.value,
        file: springDeclaration.strongSpringOverride.file,
        line: springDeclaration.strongSpringOverride.line,
      }
    : { value: springConstants.SPRING_BREAK_FORCE.value, expression: "SPRING_BREAK_FORCE", declared: springConstants.SPRING_BREAK_FORCE.value },
  jointConnectionStrength: strongSpringConnection ? springDeclaration.strongSpringOverride.setsStrengthTo : null,
  mass: {
    value: springDeclaration.ensureRigidbodyMass.value,
    override: stableSpringConnection,
    requires: "StableSpringConnection",
    file: springDeclaration.ensureRigidbodyMass.file,
    line: springDeclaration.ensureRigidbodyMass.line,
  },
  breakDistance: springDeclaration.breakDistance,
};

/** `file:line` of a class's `CustomConnectToPart` override signature. */
function overrideCite(className) {
  const { file, text } = source(className);
  const match = /public override Joint CustomConnectToPart\([^)]*\)/.exec(text);
  return match ? `${file}:${lineOfIndex(text, match.index)}` : file;
}

const report = {
  bple: BPLE,
  wheelClasses,
  springClasses,
  declarations: Object.fromEntries([...declarations].map(([name, value]) => [name, value === null
    ? { customConnectToPart: false, note: "inherits BasePart.CustomConnectToPart (BasePart.cs:1214 returns null) -> Contraption.AddFixedJoint welds it rigidly" }
    : { customConnectToPart: true, ...value, source: overrideCite(name) }])),
  springPrefabs: wheelDeclaringSprings,
  unmappedSpringPrefabs,
  wheelPrefabs,
  parts,
  frames: frameFacts,
  inFeatures,
  classConstants: {
    Spring: springDeclaration.constants,
    SpringBoxingGlove: {
      ...boxingGloveDeclaration.defaults,
      gloveMass: boxingGloveDeclaration.gloveMass,
      windingMass: boxingGloveDeclaration.windingGloveMass,
      solverIterationsFactor: boxingGloveDeclaration.solverIterationsFactor,
    },
  },
  spring: {
    class: SPRING_CLASS,
    declaration: springDeclaration,
    runtime: springRuntime,
    jointPathHistogram: springPathHistogram,
    prefabs: springFamily,
  },
  boxingGlove: {
    class: BOXING_GLOVE_CLASS,
    declaration: boxingGloveDeclaration,
    jointPathHistogram: boxingGlovePathHistogram,
    prefabs: boxingGloveFamily,
  },
  hardAssertions: checks,
  warnings,
};

mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);

const md = [];
md.push("# 原版弹簧报告（轮子悬挂 / `Spring` / `SpringBoxingGlove`）", "");
md.push("来源：`tools/bple-springs/extract-springs.mjs`，扫描 `Assembly-CSharp` 的类与全部 `Part_*.prefab`，类常量从 `.cs` 文本解析并硬断言。每条数值都带 `file:line` 或 prefab 字段名。", "");
md.push("## 轮子族与 `CustomConnectToPart`", "");
for (const name of wheelClasses) {
  const declaration = declarations.get(name);
  md.push(`- \`${name}\`：${declaration === null ? "未覆写 → `BasePart.cs:1214` 返回 null → `Contraption.AddFixedJoint` 刚性焊接（六轴全 Locked，`enablePreprocessing` 见下）" : `覆写 → 角运动 ${declaration.angular.join("/")}，线运动 ${declaration.linear.join("/")}，弹簧字段 \`${declaration.stiffnessField}\` 默认 ${declaration.defaultStiffness}，damper ${declaration.damper}，limit ${declaration.restOffset}，bounciness ${declaration.bounciness}（${overrideCite(name)}）`}`);
}

md.push("", "## 全量 prefab 里声明了弹簧的（唯一来源）", "");
md.push("| prefab | partTypeId | 类 | stiffness | 来源 |", "|---|---|---|---|---|");
for (const [name, entry] of Object.entries(prefabs).filter(([, value]) => value.springClass).sort()) {
  md.push(`| \`${name}\` | ${entry.partTypeId ?? "未映射"} | \`${entry.wheelClass}\` | ${entry.stiffness} | ${entry.stiffnessSource} |`);
}

md.push("", "## 轮子族 prefab 一览", "");
md.push("| prefab | partTypeId | 类 | stiffness |", "|---|---|---|---|");
for (const entry of wheelPrefabs) {
  md.push(`| \`${entry.prefab}\` | ${entry.partTypeId ?? "未映射"} | \`${entry.wheelClass}\` | ${entry.stiffness ?? "—（无弹簧字段）"} |`);
}

md.push("", "## 内容映射", "");
for (const [id, entry] of Object.entries(parts).sort((left, right) => Number(left[0]) - Number(right[0]))) {
  if (entry.suspension === null) continue;
  md.push(`- \`${id}\` ${entry.name} — \`${entry.prefab}\`：stiffness ${entry.suspension.stiffness}，damper ${entry.suspension.damper}，restOffset ${entry.suspension.restOffset}`);
}

if (unmappedSpringPrefabs.length > 0) {
  md.push("", "## 有弹簧但不在内容里的 prefab", "", unmappedSpringPrefabs.map((name) => `\`${name}\``).join(", "), "");
}

const colliderText = (collider) => `${collider.type} layer ${collider.layer}${collider.isTrigger ? " trigger" : ""}`
  + `${collider.size ? ` ${collider.size.x}×${collider.size.y}×${collider.size.z}` : ""}`
  + `${collider.radius !== null ? ` r ${collider.radius}` : ""}（行 ${collider.line}）`;

md.push("", "## 弹簧族 `Spring`（`Part_Spring_01..04_SET`）", "");
md.push(`类常量（从 \`Spring.cs\` 文本解析）：`
  + `\`SPRING_LIMIT_SPRING\` = ${springConstants.SPRING_LIMIT_SPRING.value}（${springConstants.SPRING_LIMIT_SPRING.file}:${springConstants.SPRING_LIMIT_SPRING.line}）、`
  + `\`SPRING_DAMPING\` = ${springConstants.SPRING_DAMPING.value}（:${springConstants.SPRING_DAMPING.line}）、`
  + `\`SPRING_LIMIT\` = ${springConstants.SPRING_LIMIT.value}（:${springConstants.SPRING_LIMIT.line}）、`
  + `\`SPRING_BOUNCINESS\` = ${springConstants.SPRING_BOUNCINESS.value}（:${springConstants.SPRING_BOUNCINESS.line}）、`
  + `\`SPRING_BREAK_FORCE\` = ${springConstants.SPRING_BREAK_FORCE.value}（:${springConstants.SPRING_BREAK_FORCE.line}）。`);
md.push(`分叉（\`${SPRING_CLASS}.cs\`）：\`StableSpringConnection && (customPartIndex == 0 || customPartIndex == 2)\`（${springDeclaration.stableGuard.file}:${springDeclaration.stableGuard.line}）`
  + `→ \`SpringJoint\`（弹力绳）：spring ${springConstants.SPRING_LIMIT_SPRING.value} N/m、damper ${springConstants.SPRING_DAMPING.value}、minDistance/maxDistance 0、anchor (0, -0.5, 0)、enablePreprocessing ${springDeclaration.springJoint.enablePreprocessing}`
  + `（${springDeclaration.springJoint.file}:${springDeclaration.springJoint.line}，扩展方法 JointExtensions.cs:5-12）；`
  + `否则 \`ConfigurableJoint\`：角 ${springDeclaration.configurableJoint.angular.join("/")}，线 ${springDeclaration.configurableJoint.linear.join("/")}，linearLimit ${springConstants.SPRING_LIMIT.value} 处 bounciness ${springConstants.SPRING_BOUNCINESS.value}，`
  + `linearLimitSpring ${springConstants.SPRING_LIMIT_SPRING.value}/${springConstants.SPRING_DAMPING.value}，configuredInWorldSpace ${springDeclaration.configurableJoint.configuredInWorldSpace}，enablePreprocessing ${springDeclaration.configurableJoint.enablePreprocessing}`
  + `（${springDeclaration.configurableJoint.file}:${springDeclaration.configurableJoint.line}）。`);
md.push(`运行时改写：\`StrongSpringConnection\` = ${springRuntime.strongSpringConnection}（声明默认）→ breakForce = \`${springRuntime.breakForce.expression}\` = ${springRuntime.breakForce.value}`
  + `（${springRuntime.breakForce.file ?? springRuntime.breakForce.declared}；GameData \`m_jointConnectionStrengthHigh\` = ${frameFacts.jointConnectionStrengthValues.m_jointConnectionStrengthHigh}），\`m_jointConnectionStrength\` = ${springRuntime.jointConnectionStrength ?? "prefab 的 Normal（未被改写）"}；`
  + `\`StableSpringConnection\` = ${springRuntime.stableSpringConnection} → \`EnsureRigidbody\` ${springRuntime.mass.override ? `mass = ${springRuntime.mass.value}（${springRuntime.mass.file}:${springRuntime.mass.line}）` : "不改写，取 prefab 自己的 `m_mass`（表内每行）"}。`
  + ` B 档（\`INSettingsBExp.json\`，mod）才是 true/true → 1 kg bungee + 1200 N。`
  + `断裂阈值 ${springRuntime.breakDistance.value} m（${springRuntime.breakDistance.file}:${springRuntime.breakDistance.line}），仅在无 SuperGlue 时判定。`, "");
md.push("| prefab | partTypeId | `customPartIndex` | `m_mass`（序列化） | 有效 mass | `m_jointConnectionType`/`Strength`/`Direction` | `m_customJointConnectionDirection` | 碰撞体 | 关节路径 |", "|---|---|---|---|---|---|---|---|---|");
for (const entry of springFamily) {
  md.push(`| \`${entry.prefab}\` | ${entry.partTypeId ?? "未映射"} | ${entry.customPartIndex} | ${entry.mass} | ${springRuntime.mass.override ? springRuntime.mass.value : entry.mass} | ${entry.jointConnectionType}/${entry.jointConnectionStrength}/${entry.jointConnectionDirection} | ${entry.customJointConnectionDirection} | ${entry.colliders.map(colliderText).join("；")} | ${entry.jointPath.route === "SpringJoint" ? "`SpringJoint`（弹力绳）" : "`ConfigurableJoint`（y 限位）"} |`);
}
md.push("", `端点半成品：${[...new Set(springFamily.map((entry) => `\`${entry.references.endPointPrefab?.prefab}\``))].join("、")}（断裂后 \`CreateSpringBody\` 实例化）。`);

md.push("", "## 拳套族 `SpringBoxingGlove`（`Part_SpringBoxingGlove_01..05_SET`）", "");
md.push(`\`${BOXING_GLOVE_CLASS}\` 不覆写 \`CustomConnectToPart\`（\`BasePart.cs:1214\` 返回 null），所以本体仍是 \`Contraption.AddFixedJoint\` 焊接（\`Contraption.cs:1504\`）；\`customPartIndex\` 不参与选路。`
  + `手套是 \`Initialize\` 实例化的第二个刚体，挂在 \`ConfigurableJoint\` 上：角 ${boxingGloveDeclaration.gloveJoint.angular.join("/")}、线 ${boxingGloveDeclaration.gloveJoint.linear.join("/")}、`
  + `yDrive spring \`m_SpringYDrive\`/damper \`m_SpringYDriveDamper\`/maximumForce ${boxingGloveDeclaration.gloveJoint.yDrive.maximumForce}、xDrive ${boxingGloveDeclaration.gloveJoint.xDrive.spring}/${boxingGloveDeclaration.gloveJoint.xDrive.damper}、`
  + `linearLimit ${boxingGloveDeclaration.gloveJoint.linearLimit.limit}、projectionMode ${boxingGloveDeclaration.gloveJoint.projectionMode} @ ${boxingGloveDeclaration.gloveJoint.projectionDistance.value}、enablePreprocessing ${boxingGloveDeclaration.gloveJoint.enablePreprocessing}`
  + `（${boxingGloveDeclaration.gloveJoint.file}:${boxingGloveDeclaration.gloveJoint.line}）。`);
md.push(`类默认值（\`SpringBoxingGlove.cs\`）：\`m_SpringYDrive\` = ${gloveDefaults.m_SpringYDrive.value}（:${gloveDefaults.m_SpringYDrive.line}）、\`m_SpringYDriveDamper\` = ${gloveDefaults.m_SpringYDriveDamper.value}（:${gloveDefaults.m_SpringYDriveDamper.line}）、`
  + `\`m_targetDistanceY\` = ${gloveDefaults.m_targetDistanceY.value}（:${gloveDefaults.m_targetDistanceY.line}）、\`m_targetDeviationX\` = ${gloveDefaults.m_targetDeviationX.value}（:${gloveDefaults.m_targetDeviationX.line}）、`
  + `\`m_WindingTime\` = ${gloveDefaults.m_WindingTime.value}、\`m_ShootTime\` = ${gloveDefaults.m_ShootTime.value}。`
  + `手套 mass：\`Initialize\` 写 ${boxingGloveDeclaration.gloveMass.value}（:${boxingGloveDeclaration.gloveMass.line}），回卷态写 ${boxingGloveDeclaration.windingGloveMass.value}（:${boxingGloveDeclaration.windingGloveMass.line}）；solverIterations × ${boxingGloveDeclaration.solverIterationsFactor.value}（:${boxingGloveDeclaration.solverIterationsFactor.line}）。`
  + `Shoot：targetPosition = ${boxingGloveDeclaration.shoot.target}，linearLimitSpring.spring = ${boxingGloveDeclaration.shoot.linearLimitSpring.value}（:${boxingGloveDeclaration.shoot.linearLimitSpring.line}）。`
  + `回卷：yDrive.positionSpring = ${boxingGloveDeclaration.wind.springFactor} × m_targetDistanceY × BoxingGloveLength、positionDamper = ${boxingGloveDeclaration.wind.springFactor} × ${boxingGloveDeclaration.wind.damperFactor} = ${boxingGloveDeclaration.wind.springFactor * boxingGloveDeclaration.wind.damperFactor}（:${boxingGloveDeclaration.wind.line}）。`, "");
md.push("| prefab | partTypeId | `customPartIndex` | `m_mass` | type/strength/direction | `m_customJointConnectionDirection` | 碰撞体 | 本体路径 | 手套 prefab（序列化 mass） | prefab yDrive 覆盖 | `m_targetDistanceY` | `m_ShootTime` | `m_checkRotation` |", "|---|---|---|---|---|---|---|---|---|---|---|---|---|");
for (const entry of boxingGloveFamily) {
  const overrides = entry.overrides;
  md.push(`| \`${entry.prefab}\` | ${entry.partTypeId ?? "未映射"} | ${entry.customPartIndex} | ${entry.mass} | ${entry.jointConnectionType}/${entry.jointConnectionStrength}/${entry.jointConnectionDirection} | ${entry.customJointConnectionDirection} | ${entry.colliders.map(colliderText).join("；")} | 焊接（AddFixedJoint） | \`${entry.glove?.prefab}\`（${entry.glove?.serializedMass}） | ${overrides.m_SpringYDrive}/${overrides.m_SpringYDriveDamper}（类默认 ${gloveDefaults.m_SpringYDrive.value}/${gloveDefaults.m_SpringYDriveDamper.value}） | ${overrides.m_targetDistanceY} | ${overrides.m_ShootTime}（类默认 ${gloveDefaults.m_ShootTime.value}） | ${overrides.m_checkRotation} |`);
}

md.push("", "### 手套刚体（`BoxingGlove*.prefab`，进内容的只有实体碰撞体）", "");
for (const entry of boxingGloveFamily) {
  const glove = entry.glove;
  if (!glove) continue;
  const shapeText = glove.shapes.map((shape) => `${shape.kind}${shape.radius !== null ? ` r ${shape.radius}` : ""} center (${shape.center?.x ?? 0}, ${shape.center?.y ?? 0}, ${shape.center?.z ?? 0})`).join("、");
  md.push(`- 皮肤 \`${entry.prefab}\` → \`${glove.prefab}\`：mass ${glove.serializedMass}，进内容 ${shapeText}`
    + (glove.excluded.length > 0 ? `；不进内容（trigger）：${glove.excluded.map((shape) => shape.kind).join("、")}` : "；无其它碰撞体"));
}

md.push("", "## IN 开关实际值", "");
for (const [name, entry] of Object.entries(inFeatures)) {
  md.push(`- \`${name}\` = ${JSON.stringify(entry.value)}（${entry.file}:${entry.line}${entry.override ? "" : "；`INSettingsBExp.json` 无覆盖，取声明默认"}）`);
}

md.push("", "## 提取器备注（与旧记载的差异）", "");
md.push("- prefab 字段名是 `customPartIndex`（`BasePart.cs:168`），不是 `m_customPartIndex`；带 `m_` 前缀的是 `m_mass`/`m_jointConnectionType`/`m_jointConnectionStrength`/`m_jointConnectionDirection`/`m_customJointConnectionDirection`/`m_endPointPrefab`/`m_BoxingGlovePrefab`。");
md.push("- `BoxingGloveLength` 只在 `INDeclarationSettingsExp.json:1172`（默认 1），`INSettingsBExp.json` 里没有这个键，所以 `inFeatures.BoxingGloveLength.override` 为 false。");
md.push("- `SPRING_BREAK_FORCE` 声明为 250（`Spring.cs:15`），但 `Awake` 在 `StrongSpringConnection` 下把它改为 `m_jointConnectionStrengthHigh * 2` = 1200（`Spring.cs:38`）并要求 `m_jointConnectionStrength = High`；prefab 的 `m_jointConnectionStrength` 仍是 1（Normal），运行值以 `spring.runtime` 为准。");
md.push("- 拳套 prefab 覆盖类默认：`m_SpringYDrive` 380（默认 60）、`m_SpringYDriveDamper` 3.5（默认 3）、`m_ShootTime` 0.4（默认 1）、`m_targetDeviationX` 0（默认 0.01）；`Part_SpringBoxingGlove_05_SET` 的 `m_targetDistanceY` 是 5，其余 2.5。");

md.push("", "## 关节刚度背景（只记录，不实现）", "");
md.push(`- \`Contraption.AddFixedJoint\`：\`enablePreprocessing = ${frameFacts.enablePreprocessing}\`（每端一个开关，逐字见 Contraption.cs:1527）。`);
md.push(`- 全量 prefab 中 \`m_jointPreprocessing: 1\` 的有 ${frameFacts.jointPreprocessingTrue} 个：${frameFacts.jointPreprocessingPrefabs.join(", ")}。`);
md.push(`- \`m_jointConnectionStrength\` 分布（原始枚举值）：${JSON.stringify(frameFacts.jointConnectionStrengthDistribution)}。`);
md.push(`- \`GetJointConnectionStrength\` 映射：${JSON.stringify(frameFacts.jointConnectionStrengthFields)} → ${JSON.stringify(frameFacts.jointConnectionStrengthValues)}。`);
md.push(`- \`FrameJointManager\` 框↔框断力：\`${frameFacts.frameJointManagerBreakForce}\`（预处理恒为 true，见 FrameJointManager.cs:172-184）。`);
md.push(`- ` + "`INSettingsBExp.json`：`StrongSpringConnection` = " + `${JSON.stringify(frameFacts.springFeatureValues.StrongSpringConnection)}，` + "`StableSpringConnection` = " + `${JSON.stringify(frameFacts.springFeatureValues.StableSpringConnection)}，` + "`OffRoadWheel` = " + `${JSON.stringify(frameFacts.springFeatureValues.OffRoadWheel)}（越野轮功能开关）。`);
if (warnings.length > 0) {
  md.push("", "## 警告", "");
  for (const warning of warnings) md.push(`- ${warning}`);
}

writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log(`wheel classes: ${wheelClasses.join(", ")}`);
console.log(`spring classes: ${springClasses.join(", ")}`);
console.log(`prefabs with a spring: ${Object.entries(prefabs).filter(([, entry]) => entry.springClass).length}`);
for (const [name, entry] of Object.entries(prefabs).filter(([, value]) => value.springClass && value.partTypeId !== null).sort()) {
  console.log(`  mapped ${entry.partTypeId} (${partByPrefab.get(name)?.name}) <- ${name}: stiffness ${entry.stiffness} (${entry.stiffnessSource})`);
}
console.log(`unmapped spring prefabs: ${unmappedSpringPrefabs.join(", ") || "-"}`);
console.log(`wheel prefabs: ${wheelPrefabs.length}; content parts carrying a suspension: ${Object.values(parts).filter((entry) => entry.suspension !== null).length}`);
console.log(`spring family (${springFamily.length}): ${springFamily.map((entry) => `${entry.prefab.replace(/^Part_Spring_/, "").replace(/_SET$/, "")}=idx${entry.customPartIndex}/${entry.jointPath.route}`).join(", ")}`);
console.log(`spring joint-path histogram: ${JSON.stringify(springPathHistogram)}`);
console.log(`boxing-glove family (${boxingGloveFamily.length}): ${boxingGloveFamily.map((entry) => `${entry.prefab.replace(/^Part_SpringBoxingGlove_/, "").replace(/_SET$/, "")}=idx${entry.customPartIndex}/${entry.jointPath.route}`).join(", ")}`);
console.log(`boxing-glove joint-path histogram: ${JSON.stringify(boxingGlovePathHistogram)}`);
console.log(`boxing-glove glove bodies: ${boxingGloveFamily.map((entry) => `${entry.prefab.replace(/^Part_SpringBoxingGlove_/, "").replace(/_SET$/, "")}=${entry.glove?.prefab}(${entry.glove?.shapes.map((shape) => `${shape.kind} r ${shape.radius} @ ${shape.center?.x ?? 0},${shape.center?.y ?? 0},${shape.center?.z ?? 0}`).join("+")}, mass ${entry.glove?.serializedMass})`).join(", ")}`);
console.log(`IN features: StrongSpringConnection=${JSON.stringify(inFeatures.StrongSpringConnection.value)} StableSpringConnection=${JSON.stringify(inFeatures.StableSpringConnection.value)} BoxingGloveLength=${JSON.stringify(inFeatures.BoxingGloveLength.value)} (${inFeatures.BoxingGloveLength.file}${inFeatures.BoxingGloveLength.override ? "" : " declaration default"})`);
console.log(`spring runtime: breakForce ${springRuntime.breakForce.value} (${springRuntime.breakForce.expression}), mass ${springRuntime.mass.value}, breakDistance ${springRuntime.breakDistance.value} m`);
console.log(`hard assertions: ${checks.length} passed`);
console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
