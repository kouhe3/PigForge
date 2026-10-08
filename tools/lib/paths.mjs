// Where every tools/bple-* entry point reads from. Every path is derived from this module's own
// location, so no tool depends on the process's working directory, and each `--<option>` override
// is read in exactly one place.

import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { arg } from "./args.mjs";

/// The repository root (`tools/lib/` up two levels).
export const REPO = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");

/// `content/parts.json`: the hand-authored content document every applier rewrites.
export const CONTENT = join(REPO, "content", "parts.json");

/// `tools/bple-textures/part-map.json`: partTypeId -> the prefab a part was extracted from.
export const PART_MAP = join(REPO, "tools", "bple-textures", "part-map.json");

/// `clients/web/public/assets/original/part-textures.json`: the sprite manifest the bracket and
/// connection rules are derived through.
export const MANIFEST = join(REPO, "clients", "web", "public", "assets", "original", "part-textures.json");

/// `tasks/`: report artifacts nobody commits (gitignored working state).
export const TASKS = join(REPO, "tasks");

/// `artifacts/`: the same, for the reports that predate `tasks/`.
export const ARTIFACTS = join(REPO, "artifacts");

/// The original Unity project to read: `--bple`, then `BPLE_ROOT`. `defaultProject` is the copy
/// the tool asserts against -- `BPLE 2022.1.9` (the pristine copy pinned to the original's own
/// 2021.3 editor) for the tools that quote `.cs` text, `BPLE_Unity6` for the rest.
export const bpleProject = (defaultProject = "BPLE_Unity6") =>
  resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", defaultProject)));

/// `<bple>/Assets`.
export const assets = (bple) => join(bple, "Assets");

/// `<bple>/Assets/GameObject`: every `Part_*.prefab`.
export const gameObjects = (bple) => join(assets(bple), "GameObject");

/// `<bple>/Assets/Scripts/Assembly-CSharp`: the flat decompiled script folder.
export const scriptAssembly = (bple) => join(assets(bple), "Scripts", "Assembly-CSharp");

/// `--content <file>`, defaulting to `content/parts.json`.
export const contentFile = () => resolve(arg("content", CONTENT));

/// `--report <file>`, defaulting to `tasks/bple-<name>-report.json` (what the companion extractor
/// writes).
export const applyReport = (name) => resolve(arg("report", join(TASKS, `bple-${name}-report.json`)));

/// `--report <file>` for the reports that live outside `tasks/`.
export const reportFile = (fallback) => resolve(arg("report", fallback));

/// `--json <file>`, defaulting to `tasks/bple-<name>-report.json` (the extractor's own report).
export const reportJson = (name) => resolve(arg("json", join(TASKS, `bple-${name}-report.json`)));

/// `--md <file>`, defaulting to `tasks/bple-<name>-report.md`.
export const reportMd = (name) => resolve(arg("md", join(TASKS, `bple-${name}-report.md`)));
