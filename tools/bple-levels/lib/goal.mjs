// A level's finish trigger is one `Goal*` prefab instance (263 of the 277 levels; the other 14
// are sandbox/MM levels with none). The trigger volume is that prefab's own trigger BoxCollider,
// so the zone comes from the prefab asset -- never from a hand-written size.

import { readFileSync } from "node:fs";

/// Reads the single `BoxCollider` of a prefab asset: `{ size: [x, y, z], center: [x, y, z] }`.
/// The block is isolated first so a `m_Size` elsewhere in the YAML cannot be mistaken for it.
export function readBoxCollider(file, check) {
  const text = readFileSync(file, "utf8");
  const blocks = [...text.matchAll(/^BoxCollider:\s*$/gm)];
  check(blocks.length === 1, `${file}: expected exactly one BoxCollider, found ${blocks.length}`);
  if (blocks.length !== 1) return null;

  const start = blocks[0].index;
  const next = text.indexOf("--- !u!", start + 1);
  const block = text.slice(start, next < 0 ? text.length : next);
  const size = /^\s*m_Size:\s*\{x:\s*(-?[0-9.eE+]+),\s*y:\s*(-?[0-9.eE+]+),\s*z:\s*(-?[0-9.eE+]+)\}/m.exec(block);
  const center = /^\s*m_Center:\s*\{x:\s*(-?[0-9.eE+]+),\s*y:\s*(-?[0-9.eE+]+),\s*z:\s*(-?[0-9.eE+]+)\}/m.exec(block);
  check(Boolean(size), `${file}: BoxCollider has no m_Size`);
  check(Boolean(center), `${file}: BoxCollider has no m_Center`);
  if (!size || !center) return null;

  const parsed = {
    size: [Number(size[1]), Number(size[2]), Number(size[3])],
    center: [Number(center[1]), Number(center[2]), Number(center[3])],
  };
  check(parsed.size.every((value) => Number.isFinite(value) && value > 0), `${file}: BoxCollider m_Size must be finite and positive`);
  check(parsed.center.every(Number.isFinite), `${file}: BoxCollider m_Center must be finite`);
  return parsed;
}
