// Vanilla IN settings = the *declaration* defaults (`INDeclarationSettingsExp.json`).
//
// `INSettings{A,AExp,B,BExp,O,OExp}.json` belong to BPLE's IN ("Innovation") mod framework: the
// game boots into `INVersionSelector` (`INVersionSelector.cs:62-67` -> `INSettings.Initialize(0..3)`,
// mapped in `INSettings.cs:304-320`) and loads one of four profiles. The declaration file holds the
// default of every key; profile A (39 overrides) and O (27) only switch on UI/editor tooling, while
// profile B (130 overrides) turns every IN system and part on *and re-balances physics*
// (`FanSpeed 1.0 -> 6.0`, `BalloonForce 1.0 -> 2.0`, `RocketForce 1.0 -> 2.0`,
// `ConnectionStrength 1.0 -> 2.0`, `Switchable*/Rotatable*/Stable*/Strong*` false -> true, ...).
//
// User decision (2026-10-06): **PigForge's baseline is the declaration default** -- the values the
// retail game runs, e.g. a rocket is a one-shot and TNT never auto-aligns. Profile B is a mod and
// must not be the source for any original part's numbers
// (`docs/specs/in-settings-profiles.md`, gaps G104/G105).
//
// Usage from an extractor:
//     import { loadVanillaSettings } from "../in-settings/vanilla-settings.mjs";
//     const settings = loadVanillaSettings(BPLE);
//     settings.getFloat("FanSpeed");   // 1.0
//     settings.getBool("RotatableTNT"); // false
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";

export const VANILLA_SETTINGS_NAME = "INDeclarationSettingsExp.json";

/** Reads `INDeclarationSettingsExp.json` and returns lookups over it. `file` is the path that was
 * read, so a report can cite it. */
export function loadVanillaSettings(bpleRoot) {
  const file = join(bpleRoot, "Assets", "TextAsset", VANILLA_SETTINGS_NAME);
  if (!existsSync(file)) {
    throw new Error(`vanilla IN settings not found: ${file}`);
  }

  const values = new Map();
  const collect = (node) => {
    if (Array.isArray(node)) {
      for (const item of node) {
        collect(item);
      }
      return;
    }

    if (node && typeof node === "object") {
      if (typeof node.name === "string" && "value" in node) {
        values.set(node.name, node.value);
      }

      for (const value of Object.values(node)) {
        collect(value);
      }
    }
  };

  collect(JSON.parse(readFileSync(file, "utf8")));

  /** Throws rather than guessing: a missing key means the original changed shape. */
  const require = (name) => {
    if (!values.has(name)) {
      throw new Error(`${VANILLA_SETTINGS_NAME} has no '${name}'`);
    }

    return values.get(name);
  };

  return {
    file,
    has: (name) => values.has(name),
    get: require,
    getFloat: (name) => {
      const value = require(name);
      if (value === "Infinity") {
        return Number.POSITIVE_INFINITY;
      }

      const number = typeof value === "number" ? value : Number(value);
      if (!Number.isFinite(number)) {
        throw new Error(`${VANILLA_SETTINGS_NAME} '${name}' is not a finite number: ${JSON.stringify(value)}`);
      }

      return number;
    },
    getBool: (name) => {
      const value = require(name);
      if (typeof value === "boolean") {
        return value;
      }

      if (value === "True" || value === "False") {
        return value === "True";
      }

      throw new Error(`${VANILLA_SETTINGS_NAME} '${name}' is not a boolean: ${JSON.stringify(value)}`);
    },
  };
}
