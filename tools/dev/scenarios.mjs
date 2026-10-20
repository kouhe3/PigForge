#!/usr/bin/env node
// Validates the dev-harness scenarios (`docs/specs/dev-harness.md`):
// content/scenarios/*.json must name an existing level, an existing partTypeId per build entry,
// finite numbers, a unique id and the schema's exact keys. Read-only; `--check` exits 1 on any
// problem, `--list` prints what the dev drawer would see.
//
// Usage:
//   node tools/dev/scenarios.mjs [--check] [--list] [--root <dir>]

import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { flag, arg } from "../lib/args.mjs";
import { REPO, CONTENT } from "../lib/paths.mjs";

const ROOT = resolve(arg("root", REPO));
const DIR = join(ROOT, "content", "scenarios");
const LEVELS = join(ROOT, "content", "levels");
const CONTENT_FILE = arg("content", CONTENT);
const LIST = flag("list");
const CHECK = flag("check");

const content = JSON.parse(readFileSync(CONTENT_FILE, "utf8"));
const partIds = new Set(content.parts.map((part) => part.partTypeId));
const problems = [];
const scenarios = [];

const fail = (file, message) => problems.push(`${file}: ${message}`);
const isIdentifier = (value) => typeof value === "string" && /^[a-z0-9-]{1,64}$/.test(value);
const finite = (value) => typeof value === "number" && Number.isFinite(value);

const ROOT_KEYS = new Set(["format", "schemaVersion", "id", "name", "description", "level", "build", "start"]);
const PART_KEYS = new Set(["partTypeId", "x", "y", "angle", "scale", "mirrored"]);

function unknownKeys(object, allowed, where, file) {
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) fail(file, `${where}: unknown property '${key}'`);
  }
}

if (!existsSync(DIR)) {
  console.log(`no scenario directory: ${DIR}`);
} else {
  const files = readdirSync(DIR).filter((name) => name.endsWith(".json")).sort();
  for (const file of files) {
    const path = join(DIR, file);
    let document;
    try {
      document = JSON.parse(readFileSync(path, "utf8"));
    } catch (error) {
      fail(file, `invalid JSON (${error.message})`);
      continue;
    }

    if (document === null || typeof document !== "object" || Array.isArray(document)) {
      fail(file, "root: must be a JSON object");
      continue;
    }

    unknownKeys(document, ROOT_KEYS, "root", file);
    if (document.format !== "pigforge.scenario") fail(file, "format: expected 'pigforge.scenario'");
    if (document.schemaVersion !== 1) fail(file, "schemaVersion: only 1 is supported");
    if (!isIdentifier(document.id)) fail(file, "id: must be 1-64 lowercase letters, digits or hyphens");
    else if (`${document.id}.json` !== file) fail(file, `id: '${document.id}' does not match the file name`);
    if (typeof document.name !== "string" || document.name.length === 0) fail(file, "name: must be a non-empty string");
    if (document.description !== undefined && typeof document.description !== "string") fail(file, "description: must be a string");
    if (document.start !== undefined && typeof document.start !== "boolean") fail(file, "start: must be true or false");

    if (typeof document.level !== "string" || document.level.length === 0) {
      fail(file, "level: must be a non-empty string");
    } else if (!existsSync(join(LEVELS, document.level))) {
      fail(file, `level: '${document.level}' is not under content/levels`);
    }

    if (!Array.isArray(document.build)) {
      fail(file, "build: must be an array");
    } else {
      document.build.forEach((part, index) => {
        const where = `build[${index}]`;
        if (part === null || typeof part !== "object" || Array.isArray(part)) {
          fail(file, `${where}: must be an object`);
          return;
        }

        unknownKeys(part, PART_KEYS, where, file);
        if (!partIds.has(part.partTypeId)) fail(file, `${where}.partTypeId: ${part.partTypeId} is not in content/parts.json`);
        for (const key of ["x", "y"]) {
          if (!finite(part[key])) fail(file, `${where}.${key}: must be a finite number`);
        }

        for (const key of ["angle", "scale"]) {
          if (part[key] !== undefined && !finite(part[key])) fail(file, `${where}.${key}: must be a finite number`);
        }

        const scale = part.scale ?? 1;
        if (scale <= 0 || scale > 4) fail(file, `${where}.scale: must be in (0, 4]`);
        if (part.mirrored !== undefined && typeof part.mirrored !== "boolean") fail(file, `${where}.mirrored: must be true or false`);
      });
    }

    scenarios.push(document);
  }
}

const ids = scenarios.map((scenario) => scenario.id);
const duplicate = ids.find((id, index) => ids.indexOf(id) !== index);
if (duplicate) problems.push(`duplicate scenario id '${duplicate}'`);

if (LIST) {
  for (const scenario of scenarios.sort((left, right) => String(left.id).localeCompare(String(right.id)))) {
    console.log(`${scenario.id.padEnd(16)} ${scenario.level.padEnd(28)} ${scenario.build.length} part(s)${scenario.start ? " [start]" : ""}  ${scenario.name}`);
  }
}

if (problems.length > 0) {
  console.error(`scenario problems (${problems.length}):`);
  for (const problem of problems) console.error(`  - ${problem}`);
  process.exit(1);
}

console.log(`scenarios: ${scenarios.length} ok`);
