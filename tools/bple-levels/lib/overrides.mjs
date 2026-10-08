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
