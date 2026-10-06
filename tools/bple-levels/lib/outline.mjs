// The collision outline of a terrain is the boundary of its **fill** mesh.
//
// `LevelLoader.CreateCollider` (LevelLoader.cs:339-380) walks the fill mesh's vertex table IN
// ORDER -- vertex i connects to vertex i + 1, both duplicated at z = +-depth/2 -- so the game
// silently assumes that table is one closed loop. This module measures what the boundary really
// is, from the triangles themselves: the edges used by exactly one triangle are the boundary, and
// because every boundary vertex carries an even number of them, the outline always decomposes
// into closed loops. Measured 2026-10-06 across the 1648 collider-carrying terrains: **1643** are
// the vertex table exactly, **4** are two loops pinched at one vertex (the vertex table walks
// through it twice), and **1** is a single loop over 549 of its 550 vertices (one vertex is not
// on the boundary at all, and extruding the table would spike out to it). The converter therefore
// walks the loops -- `outlineLoops` -- instead of trusting the vertex order.
//
// The walk follows the boundary half-edges (a boundary edge is used by exactly one triangle, so
// that triangle fixes its direction). A half-edge's successor is found by walking the triangle fan
// at its target: step from the current triangle to the one sharing the next edge, until the fan
// runs out of triangles -- that open side is the next boundary edge of the same loop. This is
// what makes a pinch work: two loops touching at one vertex sit on two fans that share no
// triangle there, so the walk keeps each loop to its own region instead of crossing into the
// other (the vertex table's own order passes through the pinch twice and is ambiguous).

export const OUTLINE_LOOP = "vertex list is the boundary loop";

const edgeKey = (a, b) => (a < b ? `${a}:${b}` : `${b}:${a}`);
const normalizeDegrees = (degrees) =>
  [...degrees.entries()].sort((left, right) => left[0] - right[0]).map(([degree, vertices]) => `${degree}:${vertices}`).join(" ");
const highestOf = (degrees) => (degrees.size === 0 ? 0 : Math.max(...degrees.keys()));

/// The undirected boundary of a fill mesh plus the degrees that ride on it.
function boundaryStructure(fill) {
  const indices = fill.indices;
  if (!indices || indices.length === 0 || indices.length % 3 !== 0) {
    return { usable: false, reason: "no triangles", boundary: new Set(), neighbours: new Map(), degrees: new Map(), links: "", highestDegree: 0 };
  }

  const edges = new Map();
  const bump = (a, b) => {
    const key = edgeKey(a, b);
    edges.set(key, (edges.get(key) ?? 0) + 1);
  };
  for (let index = 0; index < indices.length; index += 3) {
    bump(indices[index], indices[index + 1]);
    bump(indices[index + 1], indices[index + 2]);
    bump(indices[index + 2], indices[index]);
  }

  const boundary = new Set();
  for (const [key, count] of edges) if (count === 1) boundary.add(key);
  if (boundary.size === 0) {
    return { usable: false, reason: "no boundary edges", boundary, neighbours: new Map(), degrees: new Map(), links: "", highestDegree: 0 };
  }

  const neighbours = new Map();
  for (const key of boundary) {
    const [a, b] = key.split(":").map(Number);
    neighbours.set(a, (neighbours.get(a) ?? 0) + 1);
    neighbours.set(b, (neighbours.get(b) ?? 0) + 1);
  }
  const degrees = new Map();
  for (const degree of neighbours.values()) degrees.set(degree, (degrees.get(degree) ?? 0) + 1);
  return {
    usable: true,
    reason: null,
    boundary,
    neighbours,
    degrees,
    links: normalizeDegrees(degrees),
    highestDegree: highestOf(degrees),
  };
}

/// The report's outline classification: a stable label plus the counts behind it. A terrain whose
/// boundary is not "vertex list is the boundary loop" cannot be extruded by trusting the vertex
/// order; the label says how it differs and `outlineLoops` is what a converter must use.
export function classifyOutline(fill) {
  const structure = boundaryStructure(fill);
  if (!structure.usable) {
    return { label: structure.reason, vertices: fill.vertexCount, boundaryEdges: 0, boundaryVertices: 0, links: "" };
  }

  const { boundary, neighbours, highestDegree, links } = structure;
  // A boundary whose vertices all have an EVEN number of boundary edges decomposes into closed
  // loops -- a degree-4 vertex is a pinch, where two loops touch at one point. An odd degree means
  // the outline is an open chain, which no extrusion can close.
  if (highestDegree % 2 !== 0) {
    return { label: "open boundary chain", vertices: fill.vertexCount, boundaryEdges: boundary.size, boundaryVertices: neighbours.size, links };
  }

  const loop = new Set();
  for (let index = 0; index < fill.vertexCount; index += 1) {
    const next = (index + 1) % fill.vertexCount;
    loop.add(edgeKey(index, next));
  }
  const same = boundary.size === loop.size && [...boundary].every((key) => loop.has(key));
  if (same) {
    return { label: OUTLINE_LOOP, vertices: fill.vertexCount, boundaryEdges: boundary.size, boundaryVertices: neighbours.size, links };
  }

  return {
    label:
      `closed loops over ${neighbours.size} of ${fill.vertexCount} vertices` +
      (highestDegree > 2 ? ` (pinched at degree ${highestDegree})` : ""),
    vertices: fill.vertexCount,
    boundaryEdges: boundary.size,
    boundaryVertices: neighbours.size,
    links,
  };
}

/// Every triangle that uses each undirected edge, as `{ from, to, opposite }` directed edges. A
/// boundary edge has exactly one such user (which fixes its direction); an interior edge has two,
/// running opposite ways; anything else is non-manifold and the walk refuses to guess.
function edgeFaces(fill) {
  const faces = new Map();
  const indices = fill.indices;
  for (let index = 0; index < indices.length; index += 3) {
    const a = indices[index];
    const b = indices[index + 1];
    const c = indices[index + 2];
    for (const [from, to, opposite] of [[a, b, c], [b, c, a], [c, a, b]]) {
      const key = edgeKey(from, to);
      if (!faces.has(key)) faces.set(key, []);
      faces.get(key).push({ from, to, opposite });
    }
  }
  return faces;
}

/// The successor of boundary half-edge `from -> to`: walk the triangle fan at `to` starting from
/// the owning triangle's outgoing edge, through shared edges, until the fan opens onto the void.
/// That open edge is the next boundary half-edge of the same loop. Returns null when the fan is
/// degenerate or non-manifold (the caller must then skip the terrain).
function successor(faces, from, to) {
  const users = faces.get(edgeKey(from, to));
  if (!users) return null;
  const owner = users.find((user) => user.from === from && user.to === to);
  if (!owner) return null;

  let ray = owner.opposite;
  const seen = new Set();
  for (;;) {
    const key = edgeKey(to, ray);
    if (seen.has(key)) return null;
    seen.add(key);
    const neighbours = faces.get(key) ?? [];
    const outgoing = neighbours.find((user) => user.from === to && user.to === ray);
    if (!outgoing) return null;
    const other = neighbours.find((user) => !(user.from === to && user.to === ray));
    if (other === undefined) return [to, ray];
    if (other.to !== to || other.from !== ray) return null;
    ray = other.opposite;
  }
}

/// Follows the boundary half-edges. Returns the loops as vertex index sequences, or a reason why
/// the boundary could not be walked.
function traceAll(faces, boundary, starts) {
  const visited = new Set();
  const loops = [];
  for (const [startFrom, startTo] of starts) {
    if (visited.has(`${startFrom}:${startTo}`)) continue;
    const loop = [startFrom];
    let from = startFrom;
    let to = startTo;
    for (;;) {
      const key = `${from}:${to}`;
      if (visited.has(key)) return { loops: null, reason: `boundary half-edge ${key} reached twice` };
      visited.add(key);
      loop.push(to);
      const next = successor(faces, from, to);
      if (next === null) return { loops: null, reason: `no boundary successor for ${from}->${to}` };
      if (next[0] === startFrom && next[1] === startTo) break;
      from = next[0];
      to = next[1];
    }
    loop.pop();
    loops.push(loop);
  }

  if (visited.size !== starts.length) {
    return { loops: null, reason: `walked ${visited.size} of ${starts.length} boundary half-edges` };
  }
  for (const loop of loops) {
    if (loop.length < 3) return { loops: null, reason: `a loop has ${loop.length} points` };
    if (new Set(loop).size !== loop.length) {
      return { loops: null, reason: `a loop repeats a vertex (${loop.length} points, ${new Set(loop).size} distinct)` };
    }
  }
  return { loops, reason: null };
}

/// The terrain's collision outline as closed loops of vertex indices. Returns
/// `{ label, clean, loops, diagnostics }`; `loops` is null when the boundary is not a set of
/// closed loops (an open chain, no triangles, a non-manifold fan) and the caller must skip the
/// terrain. `clean` is true when the vertex table already is the boundary, in which case the loops
/// are the vertex table itself, in order -- byte-for-byte what the original would extrude.
export function outlineLoops(fill) {
  const classification = classifyOutline(fill);
  const structure = boundaryStructure(fill);
  const diagnostics = {
    label: classification.label,
    vertices: fill.vertexCount,
    boundaryEdges: classification.boundaryEdges,
    boundaryVertices: classification.boundaryVertices,
    links: classification.links,
    reason: null,
  };

  if (!structure.usable) return { label: classification.label, clean: false, loops: null, diagnostics };
  if (structure.highestDegree % 2 !== 0) return { label: classification.label, clean: false, loops: null, diagnostics };

  if (classification.label === OUTLINE_LOOP) {
    const loop = [];
    for (let index = 0; index < fill.vertexCount; index += 1) loop.push(index);
    return { label: classification.label, clean: true, loops: [loop], diagnostics };
  }

  const faces = edgeFaces(fill);
  const starts = [];
  for (const key of structure.boundary) {
    const [a, b] = key.split(":").map(Number);
    // The single owning triangle fixes the boundary edge's direction.
    starts.push(faces.get(key).some((user) => user.from === a && user.to === b) ? [a, b] : [b, a]);
  }
  starts.sort((left, right) => left[0] - right[0] || left[1] - right[1]);

  const traced = traceAll(faces, structure.boundary, starts);
  if (traced.loops === null) {
    diagnostics.reason = traced.reason;
    return { label: classification.label, clean: false, loops: null, diagnostics };
  }
  return { label: classification.label, clean: false, loops: traced.loops, diagnostics };
}
