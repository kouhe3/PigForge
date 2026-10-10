// Level-props extractor: classify every prefab the level pack's palettes reference (the 368 non-part
// props) and write the *drawable* ones out as the client's own manifest, next to the atlas PNGs they
// draw from. A prop is drawable when every renderer-bearing GameObject of its hierarchy is a
// `Sprite`/`UnmanagedSprite` quad and no unmodelled script is in the way: that is the 251 single-quad
// decorations, the 13 background sets (50 to 250 quads each, with the parallax layers the original's
// `ParallaxManager` moves) and a handful of multi-quad props (`Stone*_2`, `Lit*`).
//
// The classification and the art formulas live in `tools/lib/props.mjs` (shared with the level
// builder, which needs the same verdict per instance); the palette itself is the level pack's, so
// the walk here is the same one `tools/bple-levels/extract-levels.mjs` does.
//
// Usage: node tools/bple-props/extract-props.mjs [--bple <path>] [--out <dir>] [--json <path>] [--md <path>] [--dry-run]
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { basename, join, resolve } from "node:path";
import { arg, flag } from "../lib/args.mjs";
import { assets as assetsOf, bpleProject, REPO, TASKS } from "../lib/paths.mjs";
import { openProps, pngSize, propId, PropException } from "../lib/props.mjs";
import { fail, sortedEntries, writeJsonArtifact, writeMarkdownArtifact } from "../lib/report.mjs";
import { discoverDataFiles, loadPartMap } from "../bple-levels/lib/pack.mjs";
import { readLevel } from "../bple-levels/lib/reader.mjs";
import { buildGuidIndex, loadLoaders } from "../bple-levels/lib/unity-yaml.mjs";

const BPLE = bpleProject("BPLE 2022.1.9");
const ASSETS = assetsOf(BPLE);
/** Where the client reads its level art from (the same directory the part atlases and the level
 * ground textures already land in; the PNGs there are gitignored). */
const OUT_DIR = resolve(arg("out", join(REPO, "clients", "web", "public", "assets", "original")));
const MANIFEST = join(OUT_DIR, "level-props.json");
const OUT_JSON = resolve(arg("json", join(TASKS, "bple-props-report.json")));
const OUT_MD = resolve(arg("md", join(TASKS, "bple-props-report.md")));
const DRY_RUN = flag("dry-run");

if (!existsSync(ASSETS)) fail(`BPLE project not found: ${ASSETS}\nPass --bple <path to a BPLE project root>.`);

const failures = [];
const check = (condition, message) => {
  if (!condition) failures.push(message);
};

// ---------------------------------------------------------------- the level pack's palette
const guidToPath = buildGuidIndex(BPLE, ASSETS);
const loadersByScene = loadLoaders(BPLE, ASSETS, check);
// The palette also holds the 8 part families the levels pre-place (20 instances); they are parts, not
// props, so the census here counts the other 368 prefabs.
const { prefabToPartTypeId } = loadPartMap();
const palette = new Set();
for (const loader of loadersByScene.values()) {
  for (const guid of loader.paletteGuids) {
    const path = guidToPath.get(guid);
    check(Boolean(path), `palette guid ${guid} resolves to no asset`);
    if (path && prefabToPartTypeId.get(basename(path, ".prefab")) === undefined) palette.add(path);
  }
}
check(palette.size === 368, `palette props (non-part prefabs): ${palette.size}, expected 368`);

// Every instance in the pack, so the report can say how much of the level face each class covers
// (and a prop that stops being placed shows up as a count, not as silence).
const counts = new Map();
let instances = 0;
let levels = 0;
for (const { file } of discoverDataFiles(ASSETS)) {
  const scene = basename(file).replace(/_data\.bytes$/, "").toLowerCase();
  const loader = loadersByScene.get(scene);
  check(Boolean(loader), `${basename(file)}: no LevelLoader prefab for ${scene}`);
  const data = readLevel(readFileSync(file));
  const seen = new Set();
  for (const instance of data.instanceList) {
    const path = loader ? guidToPath.get(loader.paletteGuids[instance.prefabIndex]) : undefined;
    const name = path ? basename(path, ".prefab") : `<unresolved:${instance.prefabIndex}>`;
    const row = counts.get(name) ?? { name, instances: 0, levels: 0, withData: 0 };
    row.instances += 1;
    if (instance.dataType !== 0) row.withData += 1;
    counts.set(name, row);
    seen.add(name);
    instances += 1;
  }
  for (const name of seen) counts.get(name).levels += 1;
  levels += 1;
}
check(instances === 26072, `prefab instances: ${instances}, expected 26072`);
check(levels === 277, `levels: ${levels}, expected 277`);

// ---------------------------------------------------------------- classify + extract
const props = openProps(BPLE);
const byKind = new Map();
const drawable = new Map();
const quadsByProp = new Map();
/** prop id -> the prefab it came from, so two prefabs cannot fold onto one id. */
const ids = new Map();
for (const path of [...palette].sort()) {
  const name = basename(path, ".prefab");
  const usage = counts.get(name);
  check(Boolean(usage), `${name}: classified but never placed`);
  let record;
  try {
    record = props.read(path);
  } catch (error) {
    check(error instanceof PropException, `${name}: ${error.stack}`);
    check(false, `${name}: ${error.message}`);
    continue;
  }
  const kind = byKind.get(record.kind) ?? { props: 0, instances: 0, levelRefs: 0 };
  kind.props += 1;
  kind.instances += usage?.instances ?? 0;
  kind.levelRefs += usage?.levels ?? 0;
  byKind.set(record.kind, kind);

  if (!record.drawable) continue;
  for (const quad of record.quads) {
    // A colour-only quad (`_Custom/Unlit_Monochrome`, the background sets' `*_Fill` layers) paints no
    // atlas at all: it carries `_Color` instead of a source rect.
    if (quad.color !== undefined) {
      check(quad.color.length === 4 && quad.color.every((channel) => channel >= 0 && channel <= 1), `${name}: colour quad ${JSON.stringify(quad.color)}`);
      continue;
    }
    check(quad.atlas.endsWith(".png"), `${name}: atlas ${quad.atlas} is not a png`);
    check(quad.sx > 0 && quad.sy > 0, `${name}: non-positive quad size ${quad.sx}x${quad.sy}`);
    check(props.atlases.has(quad.atlas), `${name}: atlas ${quad.atlas} has no file in the project`);
  }
  drawable.set(name, record);
  quadsByProp.set(name, record.quads.length);
  const id = propId(name);
  check(!/\s/.test(id), `${name}: prop id "${id}" still carries whitespace`);
  check(!ids.has(id) || ids.get(id) === name, `${name}: prop id "${id}" collides with ${ids.get(id)}`);
  ids.set(id, name);
}
// The single-quad decorations keep their own count, so a prop that stops being a plain decoration
// (or a background set that stops being drawable) shows up as a number here.
const singleQuads = [...drawable.keys()].filter((name) => quadsByProp.get(name) === 1);
const decorInstances = singleQuads.reduce((total, name) => total + (counts.get(name)?.instances ?? 0), 0);
check(singleQuads.length === 257, `single-quad props: ${singleQuads.length}, expected 257`);
check(decorInstances === 15172, `single-quad instances: ${decorInstances}, expected 15172`);
const multiQuads = [...drawable.keys()].filter((name) => quadsByProp.get(name) > 1).sort();
const drawableInstances = [...drawable.keys()].reduce((total, name) => total + (counts.get(name)?.instances ?? 0), 0);
check(drawable.size === 266, `drawable props: ${drawable.size}, expected 266`);
check(drawableInstances === 15430, `drawable instances: ${drawableInstances}, expected 15430`);
const quadTotal = [...quadsByProp.values()].reduce((total, count) => total + count, 0);
check(quadTotal === 953, `quads: ${quadTotal}, expected 953`);
// The multi-quad families are the level's backdrop sets plus two stones; a family leaving or joining
// the list is a re-classification, not a detail.
const EXPECTED_MULTI_QUADS = [
  "Stone2_2",
  "Stone5_2",
  "background_cave_01_set 1",
  "background_forest_01_set 1",
  "background_halloween",
  "background_jungle_01_set",
  "background_mm_01_set",
  "background_night_01_set 1",
  "background_plateau_01_set",
];
check(multiQuads.join(",") === EXPECTED_MULTI_QUADS.join(","), `multi-quad props: ${multiQuads.join(",")}`);
console.log(`multi-quad props: ${multiQuads.join(", ")}`);
const classified = [...byKind.values()].reduce((total, row) => total + row.instances, 0);
check(classified === 26052, `classified instances: ${classified}, expected 26052 (the pack's 26072 minus the 20 placed parts)`);

const atlasSizes = {};
for (const [name, path] of props.atlases) {
  const size = pngSize(path);
  check(Boolean(size), `${name}: not a PNG`);
  atlasSizes[name] = { width: size[0], height: size[1] };
}

// ---------------------------------------------------------------- write the client manifest
const manifest = {
  format: "pigforge.level-props",
  schemaVersion: 2,
  unitsPerPixel: 20 / 768,
  atlases: Object.fromEntries(Object.entries(atlasSizes).sort(([a], [b]) => (a < b ? -1 : 1))),
  props: Object.fromEntries([...drawable.keys()].sort().map((name) => [drawable.get(name).id, { quads: drawable.get(name).quads }])),
};
const manifestText = `${JSON.stringify(manifest, null, 2)}\n`;

let changed = 0;
if (!DRY_RUN) {
  mkdirSync(OUT_DIR, { recursive: true });
  if (!existsSync(MANIFEST) || readFileSync(MANIFEST, "utf8") !== manifestText) {
    writeFileSync(MANIFEST, manifestText, "utf8");
    changed += 1;
  }
  for (const [name, path] of props.atlases) {
    const target = join(OUT_DIR, name);
    const bytes = readFileSync(path);
    if (!existsSync(target) || !readFileSync(target).equals(bytes)) {
      writeFileSync(target, bytes);
      changed += 1;
    }
  }
}

const report = {
  format: "pigforge.bple-props",
  schemaVersion: 1,
  source: { bpleRoot: BPLE },
  counts: {
    palettePrefabs: palette.size,
    levels,
    instances,
    drawableProps: drawable.size,
    drawableInstances,
    singleQuadProps: singleQuads.length,
    singleQuadInstances: decorInstances,
    quadTotal,
    kinds: Object.fromEntries(sortedEntries(byKind)),
    atlases: Object.fromEntries(sortedEntries(props.atlases)),
    atlasSizes,
  },
  props: [...drawable.keys()].sort().map((name) => ({
    id: drawable.get(name).id,
    name,
    kind: drawable.get(name).kind,
    instances: counts.get(name)?.instances ?? 0,
    levels: counts.get(name)?.levels ?? 0,
    quads: drawable.get(name).quads,
  })),
  failures,
};
if (!DRY_RUN) {
  writeJsonArtifact(OUT_JSON, report);
  const quadCounts = new Map();
  for (const record of drawable.values()) {
    for (const quad of record.quads) {
      const row = quadCounts.get(quad.atlas) ?? { atlas: quad.atlas, quads: 0, props: new Set() };
      row.quads += 1;
      row.props.add(record.name);
      quadCounts.set(quad.atlas, row);
    }
  }
  writeMarkdownArtifact(OUT_MD, [
    "# 原版关卡道具件分类（`tools/bple-props/extract-props.mjs`）",
    "",
    `- 调色板 prefab **${palette.size}**、实例 **${instances}**、关卡 **${levels}**`,
    `- 可绘制道具 **${drawable.size}** 个 prefab / **${drawableInstances}** 个实例、**${report.counts.quadTotal}** 个四边面（写进 \`level-props.json\`）`,
    `  - 其中单四边面装饰件 **${singleQuads.length}** 个 / **${decorInstances}** 个实例`,
    `  - 多四边面（背景组等）**${drawable.size - singleQuads.length}** 个 prefab`,
    "",
    "| 类 | prefab | 实例 | 关卡引用 |",
    "|---|---|---|---|",
    ...[...byKind.entries()]
      .sort((a, b) => b[1].instances - a[1].instances)
      .map(([kind, row]) => `| ${kind} | ${row.props} | ${row.instances} | ${row.levelRefs} |`),
    "",
    "| 图集 | props | 四边面 |",
    "|---|---|---|",
    ...[...quadCounts.values()]
      .sort((a, b) => b.quads - a.quads)
      .map((row) => `| ${row.atlas} | ${row.props.size} | ${row.quads} |`),
    "",
  ]);
}

console.log(`palette prefabs: ${palette.size}, instances: ${instances}`);
console.log(`drawable: ${drawable.size} props / ${drawableInstances} instances / ${report.counts.quadTotal} quads across ${props.atlases.size} atlases`);
console.log(`  single-quad: ${singleQuads.length} props / ${decorInstances} instances`);
console.log(`kinds: ${[...byKind.entries()].map(([kind, row]) => `${kind}=${row.props}/${row.instances}`).join(" ")}`);
console.log(DRY_RUN ? "dry run: nothing written" : `manifest + atlas writes: ${changed} changed`);
if (failures.length > 0) {
  for (const message of failures) console.error(`FAIL ${message}`);
  process.exit(1);
}
