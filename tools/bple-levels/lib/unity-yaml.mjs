// Unity YAML readers shared by the extractor and the level builder: the file walk, the
// list scanner the prefabs need, the guid -> asset-path index, the per-level loader palettes
// and the episode manifests (play order).

import { readFileSync, readdirSync } from "node:fs";
import { basename, join, relative } from "node:path";

/// Every file under `directory`, recursively, in `readdirSync` order (which is deterministic
/// per directory for a fixed filesystem).
function* walk(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) yield* walk(path);
    else yield path;
  }
}

// Unity YAML is regular enough for a line scanner, with one quirk: list entries sit at the SAME
// indentation as their key (Unity writes `  m_prefabs:` then `  - {fileID: ..., guid: ...}`), so
// the block ends at the first non-list line at or above the key's indentation. An entry is its
// `- ` line plus every deeper line under it (a level info spans four lines).
function collectList(text, key) {
  const lines = text.split("\n");
  const header = lines.findIndex((line) => new RegExp(`^\\s*${key}:\\s*$`).test(line));
  if (header < 0) return [];
  const indent = lines[header].length - lines[header].trimStart().length;
  const entries = [];
  let current = null;
  for (let index = header + 1; index < lines.length; index += 1) {
    const line = lines[index];
    if (line.trim() === "") continue;
    const lineIndent = line.length - line.trimStart().length;
    if (line.trimStart().startsWith("- ")) {
      if (lineIndent < indent) break;
      if (current) entries.push(current.join("\n"));
      current = [line.trim()];
      continue;
    }
    if (lineIndent <= indent) break;
    if (current) current.push(line.trim());
  }
  if (current) entries.push(current.join("\n"));
  return entries;
}

/// `guid -> path relative to the BPLE root` for every `*.meta` under `Assets`. This is how a
/// palette entry (`m_prefabs` holds guids) becomes the prefab asset it points at.
export function buildGuidIndex(bpleRoot, assetsRoot) {
  const guidToPath = new Map();
  for (const file of walk(assetsRoot)) {
    if (!file.endsWith(".meta")) continue;
    const text = readFileSync(file, "utf8");
    const match = /^guid: ([0-9a-f]{32})\s*$/m.exec(text);
    if (match) guidToPath.set(match[1], relative(bpleRoot, file.slice(0, -".meta".length)).replaceAll("\\", "/"));
  }
  return guidToPath;
}

/// `scene name (lower-case) -> { sceneName, loaderPath, paletteGuids, referenceGuids, referenceCount }`
/// for every `<scene>_loader.prefab` under `Assets/Resources/levels`.
export function loadLoaders(bpleRoot, assetsRoot, check) {
  const loadersByScene = new Map();
  const loaderRoot = join(assetsRoot, "Resources", "levels");
  for (const file of walk(loaderRoot)) {
    if (!file.endsWith("_loader.prefab")) continue;
    const text = readFileSync(file, "utf8");
    const sceneName = /^\s*m_sceneName: (.+)$/m.exec(text)?.[1]?.trim();
    const referenceCount = collectList(text, "m_references").length;
    const guids = collectList(text, "m_prefabs").map((entry) => /guid: ([0-9a-f]{32})/.exec(entry)?.[1]);
    if (!sceneName) {
      check(false, `loader without m_sceneName: ${relative(bpleRoot, file)}`);
      continue;
    }
    for (const [index, guid] of guids.entries()) {
      check(Boolean(guid), `${relative(bpleRoot, file)}: m_prefabs[${index}] has no guid`);
    }
    const referenceGuids = collectList(text, "m_references").map((entry) => /guid: ([0-9a-f]{32})/.exec(entry)?.[1] ?? null);
    loadersByScene.set(sceneName.toLowerCase(), {
      sceneName,
      loaderPath: relative(bpleRoot, file).replaceAll("\\", "/"),
      paletteGuids: guids,
      referenceGuids,
      referenceCount,
    });
  }
  return loadersByScene;
}

/// The play order: `Assets/GameObject/Episode*Levels.prefab`, read into
/// `{ file, name, label, totalLevelCount, starLimits, levelInfos }` records. Episode manifests
/// list `- sceneName: X` plus the loader guid; the race and sandbox manifests use a different
/// class whose `m_levels` entries only carry `m_levelLoaderPath`, so the scene name comes off
/// that path.
export function loadEpisodes(bpleRoot, assetsRoot, check) {
  const episodes = [];
  for (const file of walk(join(assetsRoot, "GameObject"))) {
    if (!/Episode.*Levels\.prefab$/.test(file)) continue;
    const text = readFileSync(file, "utf8");
    const name = /^\s*m_name: (.+)$/m.exec(text)?.[1]?.trim();
    const label = /^\s*m_label: (.+)$/m.exec(text)?.[1]?.trim();
    const totalLevelCountRaw = /^\s*totalLevelCount: (-?\d+)$/m.exec(text)?.[1];
    const totalLevelCount = totalLevelCountRaw === undefined ? null : Number(totalLevelCountRaw);
    const starLimitsHex = /^\s*m_starLimits: ([0-9a-f]+)$/m.exec(text)?.[1];
    const starLimits = [];
    if (starLimitsHex) {
      const bytes = Buffer.from(starLimitsHex, "hex");
      for (let offset = 0; offset + 4 <= bytes.length; offset += 4) starLimits.push(bytes.readInt32LE(offset));
    }
    check(
      starLimits.length === 0 || starLimits.length === 9,
      `${basename(file)}: m_starLimits decoded to ${starLimits.length} int32, expected 9`,
    );
    const levelInfos = [...collectList(text, "m_levelInfos"), ...collectList(text, "m_levels")].map((entry) => {
      const explicit = /sceneName: (.+)$/.exec(entry)?.[1]?.trim();
      const path = /([^/\\]+)_loader\.prefab/.exec(entry)?.[1];
      const loaderGuid = /levelLoaderGUID: ([0-9a-f]{32})/.exec(entry)?.[1] ?? null;
      return { sceneName: explicit ?? path ?? null, loaderGuid };
    });
    check(levelInfos.length > 0, `${basename(file)}: no m_levelInfos entries`);
    for (const info of levelInfos) check(Boolean(info.sceneName), `${basename(file)}: level info without a scene name`);
    episodes.push({
      file: relative(bpleRoot, file).replaceAll("\\", "/"),
      name,
      label,
      totalLevelCount,
      starLimits,
      levelInfos,
    });
  }
  check(episodes.length >= 8, `expected at least 8 Episode*Levels manifests, found ${episodes.length}`);
  for (const episode of episodes) {
    check(
      episode.totalLevelCount === null || episode.totalLevelCount === episode.levelInfos.length,
      `${basename(episode.file)}: totalLevelCount ${episode.totalLevelCount} != ${episode.levelInfos.length} level infos`,
    );
  }
  check(
    episodes.reduce((total, episode) => total + episode.levelInfos.length, 0) === 277,
    `manifests list ${episodes.reduce((total, episode) => total + episode.levelInfos.length, 0)} levels, expected 277`,
  );
  return episodes;
}

/// `scene name (lower-case) -> { episode, index }`, the placement of every level in the play order.
export function episodeIndex(episodes) {
  const byScene = new Map();
  for (const episode of episodes) {
    episode.levelInfos.forEach((info, index) => {
      byScene.set(info.sceneName.toLowerCase(), { episode: episode.name, index });
    });
  }
  return byScene;
}
