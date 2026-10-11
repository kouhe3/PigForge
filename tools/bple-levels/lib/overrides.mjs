// The `PrefabOverrides` half of the original's level format: a per-instance text block in the
// original's own `ObjectDeserializer` format that rebuilds the instance's components on load
// (`LevelLoader.cs:198-208` -> `ObjectDeserializer.ReadFile`).
//
// The text is one property per line, one tab per depth level:
//
//   GameObject <name>                     the block's own root; ObjectDeserializer.ReadFile ignores
//                                         the whole block unless this name equals the instance's
//                                         label (`LevelLoader.cs:113` sets the label from the file)
//   \tComponent <type>                    a component to add (the namespace before the last `.`
//                                         is dropped when the type is looked up)
//   \t\t<Type> <name> = <value>           a leaf property: Integer / Float / Boolean / Enum /
//                                         String (quoted) / ObjectReference (an index into the
//                                         loader's `m_references`)
//   \t\t<Type> <name>                     a block property whose children are one level deeper:
//                                         Vector2 / Vector3 / Quaternion / Color / Rect / Bounds /
//                                         Generic / Array (`ArraySize` + `Element <index>`) / ...
//   \t\tArraySize size = <n>              inside an Array
//   \t\tElement <index>                   inside an Array
//
// This module only *reads* the text: it returns the tree and the handful of fields a PigForge level
// needs. Nothing is interpreted -- a consumer names the component and the field it wants.

import { readFileSync } from "node:fs";
import { join } from "node:path";

/// Parses one override block into a tree. Each node is `{ type, name, value, children }`, where
/// `value` is the raw text after `=` (`null` for a block line) and `children` are the lines one tab
/// deeper. A leading byte-order mark is stripped (the pack's writer emits one, and the original's
/// `StreamReader` hides it).
export function parseOverrideText(text) {
  const lines = text.replace(/^\uFEFF/, "").split("\n");
  const root = { type: null, name: null, value: null, children: [] };
  // Depth -> the node its children hang off. Depth 1 is the root's own block (ObjectDeserializer
  // starts at depth 1 for a GameObject's contents).
  const stack = [root];
  for (const line of lines) {
    const content = line.replace(/^\t+/, "");
    if (content.trim() === "") continue;
    const depth = line.length - content.length;
    const fields = content.split(" ");
    const type = fields[0];
    const equals = fields.indexOf("=");
    const name = equals < 0 ? fields.slice(1).join(" ") : fields.slice(1, equals).join(" ");
    const value = equals < 0 ? null : fields.slice(equals + 1).join(" ");
    const node = { type, name, value, children: [] };
    // The first line sits at depth 0; a line one deeper than the current node hangs off it. A line
    // at an already-open depth pops back to that depth's node.
    stack.length = Math.min(stack.length, depth + 1);
    stack[depth].children.push(node);
    stack[depth + 1] = node;
  }
  return root.children[0] ?? null;
}

/// The `Component <name>` child of a block, matching the original's own lookup: the namespace before
/// the last `.` is dropped (`ObjectDeserializer.ReadObject`).
export function componentOf(node, componentName) {
  if (!node) return null;
  return node.children.find(
    (child) => child.type === "Component" && child.name.split(".").pop() === componentName,
  ) ?? null;
}

/// The first child property with this name.
export function fieldOf(node, name) {
  if (!node) return null;
  return node.children.find((child) => child.name === name) ?? null;
}

/// A `Float <name> = <v>` leaf as the float32 the original's own `float.Parse` -> `float` assignment
/// produces (Unity stores `Vector2` components as float).
function floatOf(node, name, where, check) {
  const field = fieldOf(node, name);
  const text = field?.value ?? null;
  const value = text === null ? NaN : Number(text);
  check(Number.isFinite(value), `${where}: expected a finite Float ${name}, got ${JSON.stringify(text)}`);
  return Math.fround(value);
}

/// A `Vector2 <name>` block's two components.
function vector2Of(node, name, where, check) {
  const block = fieldOf(node, name);
  check(block !== null && block.value === null, `${where}: missing Vector2 ${name}`);
  return [floatOf(block, "x", where, check), floatOf(block, "y", where, check)];
}

/// The level's own camera limits: `LevelManager.m_cameraLimits` (`LevelManager.cs:9-16`, field at
/// `:104`) out of whichever override block defines it. `topLeft` is the rectangle's top-left corner
/// and `size` extends right and down -- `IngameCamera.cs:599` builds the rect as
/// `(topLeft.x, topLeft.y - size.y, size.x, size.y)`. PigForge reads it as the pig's own bound
/// (`Pig.cs:396-403`). Every one of the 277 levels overrides it, so a level without one is drift,
/// not a case to cover; `check` records that.
export function readCameraLimits(overrides, check) {
  const found = [];
  for (const { node, text } of overrides) {
    const root = parseOverrideText(text);
    // The block only applies to the instance it names (ObjectDeserializer.ReadFile); a mismatched
    // root is a level-file oddity, not this level's data.
    if (root?.type !== "GameObject" || root.name !== node.name) continue;
    const manager = componentOf(root, "LevelManager");
    const limits = fieldOf(manager, "m_cameraLimits");
    if (limits === null) continue;
    const where = `level ${node.name} m_cameraLimits`;
    found.push({
      topLeft: vector2Of(limits, "topLeft", where, check),
      size: vector2Of(limits, "size", where, check),
    });
  }

  check(found.length === 1, `${overrides.length} override block(s), ${found.length} carrying LevelManager.m_cameraLimits, expected exactly 1`);
  return found[0] ?? null;
}

// ------------------------------------------------------------------ construction limits
//
// A level's own build limits (`LevelManager.m_constructionGridRows` and `m_partTypeCounts`) are
// *patches* on the `LevelManager` prefab's table, not whole arrays: `ObjectDeserializer.ReadArray`
// (`ObjectDeserializer.cs:720-800`) starts from what the prefab holds, truncates the list to the
// block's own `ArraySize`, and then lets every `Element <index>` rewrite the slot already there.
// Which fields an element carries decides what it rewrites, and the answer differs between a
// `List<int>` (`m_constructionGridRows`) and a `List<PartCount>` (`m_partTypeCounts`, whose element
// type is a *class*, `LevelManager.cs:25`) -- so both are read here, next to the prefab table they
// patch, and the level builder never re-derives either.

/// The `LevelManager` prefab's own `m_partTypeCounts` table and `m_constructionGridRows` base.
/// Both are read out of the prefab, never hard-coded: a slot no level rewrites is only knowable
/// here, and `Level_Sandbox_02`/`Level_Sandbox_06` rewrite exactly one row of the grid default.
export function readLevelManagerDefaults(bpleRoot, check) {
  const file = join(bpleRoot, "Assets", "GameObject", "LevelManager.prefab");
  const text = readFileSync(file, "utf8");

  // Unity serialises a `List<int>` as a hex blob of little-endian int32s (`ff030000` = 1023).
  const hex = /^\s*m_constructionGridRows:\s*([0-9a-fA-F]*)\s*$/m.exec(text)?.[1] ?? null;
  check(hex !== null && hex.length % 8 === 0, `${file}: m_constructionGridRows is not a whole number of int32s`);
  const bytes = Buffer.from(hex ?? "", "hex");
  const gridRows = [];
  for (let offset = 0; offset + 4 <= bytes.length; offset += 4) gridRows.push(bytes.readInt32LE(offset));

  // `- type: <n>` / `    count: <n>`, one entry per line, until the list ends.
  const lines = text.split("\n");
  const header = lines.findIndex((line) => /^\s*m_partTypeCounts:\s*$/.test(line));
  check(header >= 0, `${file}: no m_partTypeCounts list`);
  const partTypeCounts = [];
  for (let index = header + 1; header >= 0 && index < lines.length; index += 2) {
    const type = /^\s*- type:\s*(-?\d+)\s*$/.exec(lines[index]);
    if (!type) break;
    const count = /^\s*count:\s*(-?\d+)\s*$/.exec(lines[index + 1] ?? "");
    check(count !== null, `${file}: m_partTypeCounts entry at line ${index + 1} has no count`);
    partTypeCounts.push({ type: Number(type[1]), count: count === null ? 0 : Number(count[1]) });
  }

  // The default grid is the widest one (7 rows of 10 bits) and the table is the 35 slots every
  // level's override is written against; a change to either silently re-keys every level, so both
  // are pinned here rather than left to a first consumer to notice.
  check(
    gridRows.length === 7 && gridRows.every((row) => row === 1023),
    `${file}: the default construction grid is ${JSON.stringify(gridRows)}, expected 7 x 1023`,
  );
  check(partTypeCounts.length === 35, `${file}: m_partTypeCounts has ${partTypeCounts.length} entries, expected 35`);
  partTypeCounts.forEach((entry, index) => {
    // Pig first, then PartType 1-9, then the ids above 10 in order (`BasePart.cs:23`).
    const expected = index === 0 ? 10 : index <= 9 ? index : index + 1;
    check(entry.type === expected, `${file}: m_partTypeCounts[${index}].type is ${entry.type}, expected ${expected}`);
  });

  return { gridRows, partTypeCounts };
}

/// The `ArraySize` of an `ObjectDeserializer` array. The original reads it before the elements and
/// truncates the list to it (`ObjectDeserializer.cs:727-748`); every array in the pack has one, so
/// its absence is drift rather than a shorter array.
function arraySizeOf(node, where, check) {
  const size = node.children.find((child) => child.type === "ArraySize");
  const value = Number(size?.value);
  check(size !== undefined && Number.isInteger(value) && value >= 0, `${where}: expected an ArraySize, got ${JSON.stringify(size?.value ?? null)}`);
  return Number.isInteger(value) && value >= 0 ? value : null;
}

/// A truncated copy of `defaults`, with every `Element <index>` applied the way `ReadArray` applies
/// it to a `List<int>` (`ObjectDeserializer.cs:749-767`): the element's first property is the
/// value, an index inside the list replaces the slot, and one at its end appends. An index past the
/// end is drift (the original's own padding adds a single slot, never a gap). `patched` counts the
/// slots the block itself wrote -- the rest of the result is the prefab's own value.
function patchIntArray(node, defaults, where, check) {
  const entries = defaults.slice();
  let patched = 0;
  const size = arraySizeOf(node, where, check);
  if (size !== null) while (entries.length > size) entries.pop();
  for (const element of node.children.filter((child) => child.type === "Element")) {
    const index = Number(element.name);
    check(Number.isInteger(index) && index >= 0, `${where}: Element index ${JSON.stringify(element.name)} is not a non-negative integer`);
    if (!Number.isInteger(index) || index < 0) continue;
    check(index <= entries.length, `${where}: Element ${index} is past the end of ${entries.length} slot(s)`);
    if (index > entries.length) continue;
    const data = element.children[0];
    const value = Number(data?.value);
    check(data?.type === "Integer" && data.name === "data" && Number.isInteger(value), `${where}: Element ${index} has no Integer data`);
    if (index === entries.length) entries.push(value);
    else entries[index] = value;
    patched += 1;
  }
  return { values: entries, patched };
}

/// A truncated copy of `defaults`, with every `Element <index>` applied the way `ReadArray` applies
/// it to a `List<PartCount>` (`ObjectDeserializer.cs:769-800`). The element type is a class, so the
/// element *patches the object already in the slot*: the original discards the element's first child
/// line (`Generic data`) and reads the rest by field name, which is why a level can write
/// `Integer count = 1` alone and keep the prefab's `Enum type`, and why an unseen field keeps
/// whatever the prefab (or an earlier element) left in the slot.
function patchPartCounts(node, defaults, where, check) {
  const entries = defaults.map(({ type, count }) => ({ type, count }));
  const size = arraySizeOf(node, where, check);
  if (size !== null) while (entries.length > size) entries.pop();
  for (const element of node.children.filter((child) => child.type === "Element")) {
    const index = Number(element.name);
    check(Number.isInteger(index) && index >= 0, `${where}: Element index ${JSON.stringify(element.name)} is not a non-negative integer`);
    if (!Number.isInteger(index) || index < 0) continue;
    check(index <= entries.length, `${where}: Element ${index} is past the end of ${entries.length} slot(s)`);
    if (index > entries.length) continue;
    check(
      element.children[0]?.type === "Generic",
      `${where}: Element ${index} does not open with the \`Generic data\` line the original consumes and drops`,
    );
    // A fresh `PartCount` (type 0, count 0) when the element is one past the end -- the object the
    // original inserts before reading into it.
    if (index === entries.length) entries.push({ type: 0, count: 0 });
    const fields = element.children.slice(1);
    const type = fields.find((child) => child.type === "Enum" && child.name === "type");
    const count = fields.find((child) => child.type === "Integer" && child.name === "count");
    if (type !== undefined) {
      const value = Number(type.value);
      check(Number.isInteger(value), `${where}: Element ${index} Enum type is ${JSON.stringify(type.value)}`);
      entries[index].type = value;
    }
    if (count !== undefined) {
      const value = Number(count.value);
      check(Number.isInteger(value), `${where}: Element ${index} Integer count is ${JSON.stringify(count.value)}`);
      entries[index].count = value;
    }
  }
  return entries;
}

/// The level's own build limits, out of the `LevelManager` component in the level file's
/// `PrefabOverrides` and the prefab table it patches (`readLevelManagerDefaults`).
///
/// Returns `{ rows, partTypeCounts, sandbox, secondStar }`:
///   rows            `m_constructionGridRows` after the patch -- the row bitmaps the original's own
///                   cell test reads (`LevelManager.CanPlacePartAtGridCell`, `LevelManager.cs:1280-1297`;
///                   the rectangle comes from `BaseGameMode.InitGameMode`, `BaseGameMode.cs:49-71`)
///   rowsPatched     how many of those rows the level's own override wrote; a level that writes
///                   fewer inherits the `LevelManager` prefab's row for the rest
///   partTypeCounts  `m_partTypeCounts` after the patch, one `{ type, count }` per slot, in order
///   sandbox         `m_sandbox`, the flag that makes `BaseGameMode.GetPartCount` add the player's
///                   own `GameProgress` unlocks to the table (`BaseGameMode.cs:568-578`)
///   secondStar      the second-star fields the block carries -- `m_secondStarConstructionGridRows`,
///                   `m_extraPartsForSecondStar`, `m_gridForSecondStar` -- which no reader in the
///                   decompiled source ever touches, so they are reported, never carried into content
export function readBuildLimits(overrides, defaults, check) {
  const found = [];
  for (const { node, text } of overrides) {
    const root = parseOverrideText(text);
    if (root?.type !== "GameObject" || root.name !== node.name) continue;
    const manager = componentOf(root, "LevelManager");
    if (manager !== null) found.push(manager);
  }
  check(found.length === 1, `${overrides.length} override block(s), ${found.length} carrying a LevelManager component, expected exactly 1`);
  const manager = found[0] ?? null;
  const where = "level LevelManager";

  const rowsNode = fieldOf(manager, "m_constructionGridRows");
  check(rowsNode !== null, `${where}: no m_constructionGridRows array`);
  const countsNode = fieldOf(manager, "m_partTypeCounts");
  check(countsNode !== null, `${where}: no m_partTypeCounts array`);
  const rowsPatch = rowsNode === null ? { values: [], patched: 0 } : patchIntArray(rowsNode, defaults.gridRows, `${where} m_constructionGridRows`, check);

  return {
    rows: rowsPatch.values,
    rowsPatched: rowsPatch.patched,
    partTypeCounts: countsNode === null ? [] : patchPartCounts(countsNode, defaults.partTypeCounts, `${where} m_partTypeCounts`, check),
    sandbox: fieldOf(manager, "m_sandbox")?.value === "True",
    secondStar: ["m_secondStarConstructionGridRows", "m_extraPartsForSecondStar", "m_gridForSecondStar"].filter(
      (name) => fieldOf(manager, name) !== null,
    ),
  };
}
