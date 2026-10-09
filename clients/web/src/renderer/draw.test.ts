// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { createAnimationState, noteActivationEdges, updateAnimations } from "./animation";
import type { PartTexture, PartTextureSet } from "./atlas";
import { drawFrame, drawOrder, propDepthBuckets, wheelAxle } from "./draw";
import { createCamera } from "./camera";
import type { DrawEntity, PartContentDocument } from "@/schema/types";
import type { LevelProp, LevelTerrain } from "@/schema/levelContent";
import type { LevelPropsSet } from "./levelProps";

function makeCtx() {
  const calls: Record<string, number> = {};
  const alphas: number[] = [];
  const translations: Array<[number, number]> = [];
  const draws: number[][] = [];
  const drawAlphas: number[] = [];
  const alpha = { value: 1 };
  const gradient: CanvasGradient = { addColorStop: () => {} } as unknown as CanvasGradient;
  const rotations: number[] = [];
  const scales: Array<[number, number]> = [];
  const ctx = {
    canvas: { clientWidth: 800, clientHeight: 600, width: 800, height: 600 },
    get globalAlpha(): number {
      return alpha.value;
    },
    set globalAlpha(value: number) {
      alpha.value = value;
    },
    fillRect: () => {
      calls.fillRect = (calls.fillRect ?? 0) + 1;
      alphas.push(alpha.value);
    },
    strokeRect: () => {
      calls.strokeRect = (calls.strokeRect ?? 0) + 1;
    },
    drawImage: (...args: unknown[]) => {
      calls.drawImage = (calls.drawImage ?? 0) + 1;
      draws.push(args.slice(1) as number[]);
      drawAlphas.push(alpha.value);
    },
    beginPath: () => {
      calls.beginPath = (calls.beginPath ?? 0) + 1;
    },
    arc: () => {
      calls.arc = (calls.arc ?? 0) + 1;
    },
    fill: () => {
      calls.fill = (calls.fill ?? 0) + 1;
    },
    stroke: () => {
      calls.stroke = (calls.stroke ?? 0) + 1;
    },
    fillText: () => {
      calls.fillText = (calls.fillText ?? 0) + 1;
    },
    moveTo: () => {},
    lineTo: () => {},
    save: () => {},
    restore: () => {},
    translate: (x: number, y: number) => {
      translations.push([x, y]);
    },
    rotate: (angle: number) => {
      rotations.push(angle);
    },
    scale: (x: number, y: number) => {
      calls.scale = (calls.scale ?? 0) + 1;
      scales.push([x, y]);
    },
    setLineDash: () => {},
    createRadialGradient: () => {
      calls.createRadialGradient = (calls.createRadialGradient ?? 0) + 1;
      return gradient;
    },
    fillStyle: "",
    strokeStyle: "",
    lineWidth: 1,
    font: "",
    textAlign: "",
    textBaseline: "",
  };
  return { ctx: ctx as unknown as CanvasRenderingContext2D, calls, alphas, translations, draws, drawAlphas, rotations, scales };
}

const content: PartContentDocument = {
  format: "pigforge.part-content",
  schemaVersion: 1,
  contentVersion: "t",
  physics: { maximumAngularSpeed: 7, damping: { linear: 0.2, angular: 0.05 } },

  parts: [
    { partTypeId: 44, name: "flashlight", mode: "dynamic", mass: 0.4, capabilities: { light: 3 }, shapes: [{ kind: "box", halfExtents: [0.2, 0.2, 0.2] }] },
    { partTypeId: 1, name: "block", mode: "dynamic", mass: 1, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
    { partTypeId: 8, name: "engine", mode: "dynamic", mass: 1, capabilities: { motor: { thrustPerTick: 2, directionX: 1 }, activation: "toggle" }, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
    { partTypeId: 7, name: "wheel", mode: "dynamic", mass: 0.5, capabilities: { wheel: true }, shapes: [{ kind: "box", halfExtents: [0.2, 0.32, 0.5], offset: [0, 0.1702, 0] }, { kind: "sphere", radius: 0.33, offset: [0.0106, -0.2057, 0] }] },
    { partTypeId: 14, name: "small-wheel", mode: "dynamic", mass: 0.3, capabilities: { wheel: true }, shapes: [{ kind: "box", halfExtents: [0.15, 0.16, 0.5], offset: [0, 0.17, 0] }, { kind: "sphere", radius: 0.18, offset: [0.0051, 0.1424, 0] }] },
  ],
};

const light: DrawEntity = { entityId: 1, partTypeId: 44, x: 2, y: 2, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 11, active: false };
const block: DrawEntity = { entityId: 2, partTypeId: 1, x: 4, y: 2, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 12, active: false };
const preview: DrawEntity = { ...light, entityId: 3, bodyId: 0 };
const engine: DrawEntity = { ...block, entityId: 4, partTypeId: 8, active: false };

describe("drawFrame light halo", () => {
  it("casts a radial glow for light parts", () => {
    const { ctx, calls } = makeCtx();
    drawFrame(ctx, createCamera(), [light], content, []);
    expect(calls.createRadialGradient).toBe(1);
    expect(calls.arc).toBe(1); // the glow circle; the box part itself is a rect
  });

  it("does not glow for plain parts", () => {
    const { ctx, calls } = makeCtx();
    drawFrame(ctx, createCamera(), [block], content, []);
    expect(calls.createRadialGradient).toBeUndefined();
  });
});

describe("drawFrame switch outline", () => {
  it("outlines a switchable part only while its switch is on", () => {
    const off = makeCtx();
    drawFrame(off.ctx, createCamera(), [{ ...engine, active: false }], content, []);
    expect(off.calls.strokeRect).toBe(1);

    const on = makeCtx();
    drawFrame(on.ctx, createCamera(), [{ ...engine, active: true }], content, []);
    expect(on.calls.strokeRect).toBe(2);
  });

  it("ignores the flag on a part without a switch", () => {
    const { ctx, calls } = makeCtx();
    drawFrame(ctx, createCamera(), [{ ...block, active: true }], content, []);
    expect(calls.strokeRect).toBe(1);
  });
});

describe("drawFrame previews", () => {
  it("renders bodyId 0 translucent, without a halo or name label", () => {
    const { ctx, calls, alphas } = makeCtx();
    drawFrame(ctx, createCamera(), [preview], content, []);
    expect(calls.createRadialGradient).toBeUndefined();
    expect(calls.fillRect).toBe(2); // background + the part shape
    expect(alphas[alphas.length - 1]).toBeCloseTo(0.45);
    expect(calls.fillText).toBeUndefined();
  });

  it("keeps the halo, full alpha, and the label for a live body", () => {
    const { ctx, calls, alphas } = makeCtx();
    drawFrame(ctx, createCamera(), [light], content, []);
    expect(calls.createRadialGradient).toBe(1);
    expect(alphas[alphas.length - 1]).toBe(1);
    expect(calls.fillText).toBe(1);
  });
});

describe("drawFrame entity placement", () => {
  it("translates each entity to its own screen position before drawing", () => {
    const { ctx, translations } = makeCtx();
    drawFrame(ctx, createCamera(), [light, block], content, []);
    // camera { x: 4, y: 2, scale: 36 } over an 800x600 canvas:
    // world (2,2) -> (328,300), world (4,2) -> (400,300).
    expect(translations).toEqual([
      [328, 300],
      [400, 300],
    ]);
  });
});

describe("drawFrame original-art textures", () => {
  const textures = (image: CanvasImageSource | undefined) => ({
    atlases: new Map([["A.png", image as CanvasImageSource]]),
    parts: new Map([
      [
        1,
        {
          bbox: [1, 1] as [number, number],
          sprites: [{ atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0, cy: 0, sx: 2, sy: 3, rot: 0, rotates: false }],
        },
      ],
    ]),
  });

  it("blits the manifest rect at its original world size and drops the name label", () => {
    const { ctx, calls, draws } = makeCtx();
    const image = {} as CanvasImageSource;
    drawFrame(ctx, createCamera(), [block], content, [], undefined, undefined, textures(image));
    expect(calls.drawImage).toBe(1);
    // 2x3 world-unit sprite at camera scale 36 -> 72x108, independent of the 1x1 shape.
    expect(draws[0]).toEqual([10, 20, 100, 100, -36, -54, 72, 108]);
    expect(calls.fillRect).toBe(1); // background only: the shape path is skipped
    expect(calls.fillText).toBeUndefined();
  });

  it("falls back to the shape when the atlas image is missing", () => {
    const { ctx, calls } = makeCtx();
    drawFrame(ctx, createCamera(), [block], content, [], undefined, undefined, textures(undefined));
    expect(calls.drawImage).toBeUndefined();
    expect(calls.fillRect).toBe(2); // background + the shape fallback
  });

  it("blits the sub-entity's own art for a flagged entity", () => {
    // A boxing glove's fist: the sub-entity borrows part 28's type on the wire (PGFS v5 bit1), so
    // without the manifest's sub-entity list the renderer would draw a second glove part over it.
    const gloveTextures = {
      atlases: new Map([["A.png", {} as CanvasImageSource]]),
      parts: new Map([
        [
          28,
          {
            bbox: [1, 1] as [number, number],
            sprites: [{ atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0, cy: 0, sx: 2, sy: 3, rot: 0, rotates: false }],
            subSprites: [{ atlas: "A.png", x: 300, y: 400, w: 50, h: 60, cx: 0, cy: 0, sx: 1, sy: 1, rot: 0, rotates: false }],
          },
        ],
      ]),
    };
    const glove: DrawEntity = { ...block, entityId: 5, partTypeId: 28 };

    const host = makeCtx();
    drawFrame(host.ctx, createCamera(), [glove], content, [], undefined, undefined, gloveTextures);
    expect(host.draws[0].slice(0, 4)).toEqual([10, 20, 100, 100]);

    const fist = makeCtx();
    drawFrame(fist.ctx, createCamera(), [{ ...glove, subEntity: true }], content, [], undefined, undefined, gloveTextures);
    expect(fist.draws[0].slice(0, 4)).toEqual([300, 400, 50, 60]);
  });

  it("paints the host part over its own sub-entity", () => {
    // The original sorts equal-order sprites by distance to the camera, and the box (z = 0.1) is
    // nearer than the fist (z = 0.15): the box is painted last, so a retracting fist slides in
    // behind it instead of popping out of existence (docs/specs/boxing-glove.md §3).
    const gloveTextures = {
      atlases: new Map([["A.png", {} as CanvasImageSource]]),
      parts: new Map([
        [
          28,
          {
            bbox: [1, 1] as [number, number],
            sprites: [{ atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0, cy: 0, sx: 2, sy: 3, rot: 0, rotates: false }],
            subSprites: [{ atlas: "A.png", x: 300, y: 400, w: 50, h: 60, cx: 0, cy: 0, sx: 1, sy: 1, rot: 0, rotates: false }],
          },
        ],
      ]),
    };
    const glove: DrawEntity = { ...block, entityId: 5, partTypeId: 28 };
    const fist: DrawEntity = { ...glove, entityId: 6, subEntity: true, y: 2.1 };

    // The snapshot lists the box first (it owns the lower entity id); the frame paints the fist
    // before it all the same.
    const { ctx, draws } = makeCtx();
    drawFrame(ctx, createCamera(), [glove, fist], content, [], undefined, undefined, gloveTextures);
    expect(draws.map((rect) => rect.slice(0, 4))).toEqual([
      [300, 400, 50, 60],
      [10, 20, 100, 100],
    ]);
  });

describe("drawFrame level props", () => {
  // A recorder that keeps the *order* of the paint calls, so the depth buckets can be checked against
  // the ground's own fill and the entities without depending on call counts. Images carry a `tag` the
  // recorder reads back, which is how a bucket is identified in the log.
  function makeOrderCtx() {
    const order: string[] = [];
    const ctx = {
      canvas: { clientWidth: 800, clientHeight: 600, width: 800, height: 600 },
      globalAlpha: 1,
      fillStyle: "",
      strokeStyle: "",
      lineWidth: 1,
      font: "",
      textAlign: "",
      textBaseline: "",
      fillRect: () => { order.push("fillRect"); },
      strokeRect: () => { order.push("strokeRect"); },
      beginPath: () => { order.push("beginPath"); },
      moveTo: () => {},
      lineTo: () => {},
      closePath: () => {},
      arc: () => { order.push("arc"); },
      fill: () => { order.push("fill"); },
      stroke: () => { order.push("stroke"); },
      fillText: () => {},
      drawImage: (image: unknown) => {
        order.push(`image:${(image as { tag?: string } | null)?.tag ?? "?"}`);
      },
      save: () => {},
      restore: () => {},
      translate: () => {},
      rotate: () => {},
      scale: () => {},
      setLineDash: () => {},
      createRadialGradient: () => ({ addColorStop: () => {} }),
      createPattern: () => ({ setTransform: () => {} }),
    };
    return { ctx: ctx as unknown as CanvasRenderingContext2D, order };
  }

  const terrain: LevelTerrain = { position: [0, 0, 0], depth: 10, loops: [[[0, 0], [4, 0], [0, 3]]] };
  const textured = (image: CanvasImageSource): PartTextureSet => ({
    atlases: new Map([["A.png", image]]),
    parts: new Map([
      [1, { bbox: [1, 1] as [number, number], sprites: [{ atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0, cy: 0, sx: 2, sy: 3, rot: 0, rotates: false }] }],
    ]),
  });

  const props: LevelProp[] = [
    { id: "far", x: 0, y: 0, z: 15, rotation: 0, scaleX: 1, scaleY: 1 },
    { id: "ground", x: 0, y: 0, z: 0, rotation: 0, scaleX: 1, scaleY: 1 },
    { id: "mid", x: 0, y: 0, z: -1, rotation: 0, scaleX: 1, scaleY: 1 },
    { id: "plane", x: 0, y: 0, z: -5, rotation: 0, scaleX: 1, scaleY: 1 },
    { id: "near", x: 0, y: 0, z: -6, rotation: 0, scaleX: 1, scaleY: 1 },
  ];
  const set: LevelPropsSet = {
    atlases: new Map(props.map((prop) => [`${prop.id}.png`, { tag: prop.id } as unknown as CanvasImageSource])),
    props: new Map(props.map((prop) => [prop.id, { atlas: `${prop.id}.png`, x: 0, y: 0, w: 1, h: 1, cx: 0, cy: 0, sx: 1, sy: 1 }])),
  };

  it("splits the level's own depths into the frame's four breaks", () => {
    const buckets = propDepthBuckets([...props, { ...props[0], id: "hidden", z: -214 }]);
    expect(buckets.far.map((prop) => prop.id)).toEqual(["far"]);
    expect(buckets.ground.map((prop) => prop.id)).toEqual(["ground"]);
    expect(buckets.mid.map((prop) => prop.id)).toEqual(["mid"]);
    expect(buckets.plane.map((prop) => prop.id)).toEqual(["plane"]);
    expect(buckets.near.map((prop) => prop.id)).toEqual(["near", "hidden"]);
    // Farthest first inside a bucket, and the level's own order for equal depths.
    expect(propDepthBuckets([{ ...props[0], z: 5 }, { ...props[0], id: "tie", z: 5 }]).far.map((prop) => prop.id))
      .toEqual(["far", "tie"]);
  });

  it("paints the decorations at those breaks, around the ground and the entities", () => {
    const { ctx, order } = makeOrderCtx();
    drawFrame(
      ctx,
      createCamera(),
      [block],
      content,
      [],
      undefined,
      undefined,
      textured({ tag: "part" } as unknown as CanvasImageSource),
      null,
      null,
      [terrain],
      null,
      { props, set },
    );
    const at = (marker: string): number => order.indexOf(marker);
    // The ground's own flat fill is the marker between the two hemispheres: the far bucket lands
    // behind it, the ground bucket in front of it, and the plane/near buckets past the cart.
    expect(order.filter((entry) => entry.startsWith("image:"))).toEqual([
      "image:far",
      "image:ground",
      "image:mid",
      "image:part",
      "image:plane",
      "image:near",
    ]);
    expect(at("image:far")).toBeLessThan(at("fill"));
    expect(at("fill")).toBeLessThan(at("image:ground"));
    expect(at("image:ground")).toBeLessThan(at("image:part"));
    expect(at("image:part")).toBeLessThan(at("image:plane"));
    expect(at("image:plane")).toBeLessThan(at("image:near"));
  });

  it("draws the same frame when the level has no decorations", () => {
    const withProps = makeOrderCtx();
    const without = makeOrderCtx();
    drawFrame(withProps.ctx, createCamera(), [block], content, [], undefined, undefined, textured({ tag: "part" } as unknown as CanvasImageSource), null, null, [terrain], null, { props, set });
    drawFrame(without.ctx, createCamera(), [block], content, [], undefined, undefined, textured({ tag: "part" } as unknown as CanvasImageSource), null, null, [terrain], null, null);
    expect(without.order).not.toContain("image:far");
    expect(without.order.filter((entry) => entry === "fill").length).toBeGreaterThan(0);
  });
});

describe("drawOrder", () => {
  const host: DrawEntity = { ...block, entityId: 5, partTypeId: 28 };
  const otherHost: DrawEntity = { ...block, entityId: 7, partTypeId: 28, x: 20 };

  it("returns the frame untouched when nothing is a sub-entity", () => {
    const entities = [block, light];
    expect(drawOrder(entities)).toBe(entities);
  });

  it("moves a sub-entity directly before the part it is nearest to", () => {
    const near: DrawEntity = { ...host, entityId: 6, subEntity: true, y: host.y - 2.5 };
    const far: DrawEntity = { ...host, entityId: 8, subEntity: true, x: otherHost.x, y: otherHost.y };
    const order = drawOrder([host, otherHost, near, far]);
    // Each fist is painted immediately before its own box, whatever order the frame listed them in.
    expect(order.map((entity) => entity.entityId)).toEqual([6, 5, 8, 7]);
  });

  it("keeps an unpaired sub-entity where the frame put it", () => {
    const orphan: DrawEntity = { ...block, entityId: 9, partTypeId: 44, subEntity: true };
    const order = drawOrder([block, orphan]);
    expect(order.map((entity) => entity.entityId)).toEqual([2, 9]);
  });

  it("paints every entity exactly once", () => {
    const fist: DrawEntity = { ...host, entityId: 6, subEntity: true };
    const order = drawOrder([host, otherHost, fist, block]);
    expect(order).toHaveLength(4);
    expect(new Set(order.map((entity) => entity.entityId)).size).toBe(4);
  });
});

  /**
   * The reported display bugs, with the real wooden-wheel geometry (content part 7): the
   * axle sprite sits on the prefab root, the disc spins on the wheel pivot, and the pivot is
   * 0.21 below the part origin.
   */
  const TIRE: [number, number] = [0.0106, -0.2057];
  const wheelTextures = (image: CanvasImageSource | undefined, wheel: Partial<{ pivot: [number, number] | undefined }> = {}) => ({
    atlases: new Map([["A.png", image as CanvasImageSource]]),
    parts: new Map([
      [
        7,
        {
          bbox: [1, 1] as [number, number],
          pivot: [0.0106, -0.2107] as [number, number] | undefined,
          sprites: [
            { atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0.0029, cy: -0.0317, sx: 0.6563, sy: 0.6667, rot: 0, rotates: true },
            { atlas: "A.png", x: 200, y: 20, w: 100, h: 100, cx: 0, cy: 0, sx: 0.6875, sy: 0.8125, rot: 0, rotates: false },
          ],
          ...wheel,
        },
      ],
    ]),
  });

  /** An older generated manifest for the same wheel: no `pivot`, no per-sprite flags. */
  const staleWheelTextures = (image: CanvasImageSource | undefined) => {
    const set = wheelTextures(image, { pivot: undefined });
    const texture = set.parts.get(7)!;
    set.parts.set(7, { ...texture, sprites: texture.sprites.map((sprite) => ({ ...sprite, rotates: false })) });
    return set;
  };

  const SMALL_TIRE: [number, number] = [0.0051, 0.1424];
  const smallWheelTextures = (image: CanvasImageSource | undefined) => ({
    atlases: new Map([["A.png", image as CanvasImageSource]]),
    parts: new Map([
      [
        14,
        {
          bbox: [1, 1] as [number, number],
          pivot: [0.0051, 0.1424] as [number, number],
          sprites: [
            { atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0, cy: 0.0641, sx: 0.3854, sy: 0.4167, rot: 0, rotates: false },
            { atlas: "A.png", x: 200, y: 20, w: 100, h: 100, cx: 0.0006, cy: -0.0849, sx: 0.3646, sy: 0.375, rot: 0, rotates: true },
          ],
        },
      ],
    ]),
  });

  // The entity loop translates the origin once and then once per sprite, and nothing after
  // it translates, so the last two entries are the 2 sprites of the single entity under test.
  const spriteTranslations = (translations: Array<[number, number]>) => translations.slice(-2);

  it("centres the disc on the axle and leaves the axle on the build pose while rolling", () => {
    const image = {} as CanvasImageSource;
    const rolling: DrawEntity = { entityId: 9, partTypeId: 7, x: 0, y: 0, yaw: 1.5, scale: 1, vx: 0, vy: 0, bodyId: 21, active: false, restYaw: 0.25 };
    const { ctx, rotations, calls, translations } = makeCtx();
    drawFrame(ctx, createCamera(), [rolling], content, [], undefined, undefined, wheelTextures(image));

    expect(calls.drawImage).toBe(2);
    // The disc turns with the body; the axle keeps the orientation it was built at.
    expect(rotations).toEqual([-1.5, -0.25]);
    // Each translate argument is already relative to the entity origin.
    const [disc, axle] = spriteTranslations(translations);
    const axleX = Math.cos(1.5) * TIRE[0] - Math.sin(1.5) * TIRE[1];
    const axleY = Math.sin(1.5) * TIRE[0] + Math.cos(1.5) * TIRE[1];
    expect(disc[0]).toBeCloseTo(axleX * 36, 3);
    expect(disc[1]).toBeCloseTo(-axleY * 36, 3);
    // The mount keeps the offset from the tire that the manifest authored, so it never orbits
    // the wheel — measured in the manifest frame, not from the content axle.
    const fixedX = Math.cos(0.25) * (0 - 0.0029) - Math.sin(0.25) * (0 - -0.0317);
    const fixedY = Math.sin(0.25) * (0 - 0.0029) + Math.cos(0.25) * (0 - -0.0317);
    expect(axle[0]).toBeCloseTo((axleX + fixedX) * 36, 3);
    expect(axle[1]).toBeCloseTo(-(axleY + fixedY) * 36, 3);
  });

  /**
   * The reported bug: a hinged wheel's axle is welded to the chassis in the original, so it
   * must follow the chassis' rotation (snapshot `attachYaw`) rather than stay frozen at the
   * build angle while the cart pitches.
   */
  it("orients the mount by the attach frame the snapshot publishes", () => {
    const image = {} as CanvasImageSource;
    const pitching: DrawEntity = { entityId: 9, partTypeId: 7, x: 0, y: 0, yaw: 1.5, scale: 1, vx: 0, vy: 0, bodyId: 21, active: false, restYaw: 0, attachYaw: 0.9 };
    const { ctx, rotations } = makeCtx();
    drawFrame(ctx, createCamera(), [pitching], content, [], undefined, undefined, wheelTextures(image));

    // The tire spins with the body; the mount follows the attach frame, not the build angle.
    expect(rotations).toEqual([-1.5, -0.9]);
  });

  /**
   * The reported bug: the motor wheel's parts were stacked the wrong way round. The manifest's
   * `sprites` array is the paint order (the extractor sorts it far-to-near by the original's z,
   * which is what makes a wheel's spokes show through its rim hole and its block sit in front).
   * The renderer must blit that order untouched.
   */
  it("blits the composite in the manifest's own order", () => {
    const image = {} as CanvasImageSource;
    const rolling: DrawEntity = { entityId: 9, partTypeId: 7, x: 0, y: 0, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 21, active: false, restYaw: 0 };
    const { ctx, draws } = makeCtx();
    drawFrame(ctx, createCamera(), [rolling], content, [], undefined, undefined, wheelTextures(image));

    // drawImage's source rect identifies each sprite: the disc first, then the axle.
    expect(draws.map((args) => args[0])).toEqual([10, 200]);
  });

  it("takes the axle from the part content, not the manifest", () => {
    const bogus = wheelTextures({} as CanvasImageSource, { pivot: [9, 9] });
    expect(wheelAxle(content.parts[3], wheelTextures(undefined).parts.get(7))).toEqual(TIRE);
    // A bogus art pivot does not move the sprites: the content describes the tire.
    const rolling: DrawEntity = { entityId: 9, partTypeId: 7, x: 0, y: 0, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 21, active: false, restYaw: 0 };
    const { ctx, translations } = makeCtx();
    drawFrame(ctx, createCamera(), [rolling], content, [], undefined, undefined, bogus);
    const [disc] = spriteTranslations(translations);
    expect(disc[0]).toBeCloseTo(TIRE[0] * 36, 3);
    expect(disc[1]).toBeCloseTo(-TIRE[1] * 36, 3);
  });


  /**
   * The reported bug for the small wheel: its content axle sits far above the part origin
   * (0.14 m) while its art is anchored near the composite centre, so measuring the mount from
   * the content axle instead of from the tire dropped the fork underneath the tire.
   */
  it("keeps a mount above its tire when the content axle is far from the art anchor", () => {
    const image = {} as CanvasImageSource;
    const small: DrawEntity = { entityId: 9, partTypeId: 14, x: 0, y: 0, yaw: 1.2, scale: 1, vx: 0, vy: 0, bodyId: 31, active: false, restYaw: 0.25 };
    const { ctx, translations } = makeCtx();
    drawFrame(ctx, createCamera(), [small], content, [], undefined, undefined, smallWheelTextures(image));

    // Canvas +y is down: the mount must render above the tire, as the manifest places it.
    const [mount, tire] = spriteTranslations(translations);
    expect(mount[1]).toBeLessThan(tire[1]);
    // The tire itself is pinned on the content axle, whatever the art anchor says.
    const axleX = Math.cos(1.2) * SMALL_TIRE[0] - Math.sin(1.2) * SMALL_TIRE[1];
    const axleY = Math.sin(1.2) * SMALL_TIRE[0] + Math.cos(1.2) * SMALL_TIRE[1];
    expect(tire[0]).toBeCloseTo(axleX * 36, 3);
    expect(tire[1]).toBeCloseTo(-axleY * 36, 3);
  });

  it("has no axle for a part that cannot roll", () => {
    expect(wheelAxle(content.parts[1])).toBeUndefined();
  });

  /**
   * The reported bug: an older generated manifest (no `pivot`, no `rotates`) left the wheel
   * without an axle, so its whole composite swung about the part origin instead of the tire
   * spinning about the axle it rolls on.
   */
  it("spins the tire about the axle even when the manifest never described one", () => {
    const image = {} as CanvasImageSource;
    const rolling: DrawEntity = { entityId: 9, partTypeId: 7, x: 0, y: 0, yaw: 1.5, scale: 1, vx: 0, vy: 0, bodyId: 21, active: false, restYaw: 0.25 };
    const { ctx, rotations, translations } = makeCtx();
    drawFrame(ctx, createCamera(), [rolling], content, [], undefined, undefined, staleWheelTextures(image));

    expect(rotations).toEqual([-1.5, -0.25]);
    const [disc] = spriteTranslations(translations);
    const axleX = Math.cos(1.5) * TIRE[0] - Math.sin(1.5) * TIRE[1];
    const axleY = Math.sin(1.5) * TIRE[0] + Math.cos(1.5) * TIRE[1];
    expect(disc[0]).toBeCloseTo(axleX * 36, 3);
    expect(disc[1]).toBeCloseTo(-axleY * 36, 3);
  });

  it("keeps both sprites still in the world as the wheel rolls", () => {
    const image = {} as CanvasImageSource;
    const at = (yaw: number): DrawEntity => ({ entityId: 9, partTypeId: 7, x: 0, y: 0, yaw, scale: 1, vx: 0, vy: 0, bodyId: 21, active: false, restYaw: 0.25 });

    const built = makeCtx();
    drawFrame(built.ctx, createCamera(), [at(0.25)], content, [], undefined, undefined, wheelTextures(image));
    const rolled = makeCtx();
    drawFrame(rolled.ctx, createCamera(), [at(2.75)], content, [], undefined, undefined, wheelTextures(image));

    // The axle keeps a constant offset from the disc however far the wheel has rolled:
    // it stays mounted, instead of orbiting the hub with the body.
    const [builtDisc, builtAxle] = spriteTranslations(built.translations);
    const [rolledDisc, rolledAxle] = spriteTranslations(rolled.translations);
    expect(rolledAxle[0] - rolledDisc[0]).toBeCloseTo(builtAxle[0] - builtDisc[0], 6);
    expect(rolledAxle[1] - rolledDisc[1]).toBeCloseTo(builtAxle[1] - builtDisc[1], 6);
    // The disc follows the roll; the axle stays on the build angle.
    expect(rolled.rotations).toEqual([-2.75, -0.25]);
  });

  it("uses the part yaw for every sprite when the build angle was never observed", () => {
    const image = {} as CanvasImageSource;
    const rolling: DrawEntity = { entityId: 9, partTypeId: 7, x: 0, y: 0, yaw: 1.5, scale: 1, vx: 0, vy: 0, bodyId: 21, active: false };
    const { ctx, rotations } = makeCtx();
    drawFrame(ctx, createCamera(), [rolling], content, [], undefined, undefined, wheelTextures(image));
    expect(rotations).toEqual([-1.5, -1.5]);
  });

  it("treats a build-mode preview as unrolled, so rotating the part turns its axle too", () => {
    const image = {} as CanvasImageSource;
    const previewRolled: DrawEntity = { entityId: 9, partTypeId: 7, x: 0, y: 0, yaw: 0.9, scale: 1, vx: 0, vy: 0, bodyId: 0, active: false, restYaw: 0.25 };
    const { ctx, rotations, translations } = makeCtx();
    drawFrame(ctx, createCamera(), [previewRolled], content, [], undefined, undefined, wheelTextures(image));

    // No physics roll yet: both sprites follow the dragged angle.
    expect(rotations).toEqual([-0.9, -0.9]);
    const [disc] = spriteTranslations(translations);
    const discX = Math.cos(0.9) * 0.0029 - Math.sin(0.9) * -0.0317;
    expect(disc[0]).toBeCloseTo(discX * 36, 3);
  });
});

describe("drawFrame selection and marquee", () => {
  it("draws one selection box per selected entity plus the marquee", () => {
    const { ctx, calls } = makeCtx();
    drawFrame(
      ctx,
      createCamera(),
      [light, block],
      content,
      [light.entityId, block.entityId],
      undefined,
      undefined,
      undefined,
      { minX: 1, minY: 1, maxX: 5, maxY: 3 },
    );

    expect(calls.strokeRect).toBe(5); // 2 part outlines + 2 selection boxes + 1 marquee
  });
});

describe("drawFrame multi-shape placeholders", () => {
  it("draws every shape of a part at its local offset", () => {
    const { ctx, calls, translations } = makeCtx();
    const wheelContent: PartContentDocument = {
      ...content,
      parts: [
        ...content.parts,
        {
          partTypeId: 7,
          name: "wheel",
          mode: "dynamic",
          mass: 0.5,
          shapes: [
            { kind: "sphere", radius: 0.33, offset: [0.01, -0.2, 0] },
            { kind: "box", halfExtents: [0.2, 0.32, 0.5], offset: [0, 0.17, 0] },
          ],
        },
      ],
    };
    const wheel: DrawEntity = { entityId: 5, partTypeId: 7, x: 4, y: 2, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 13, active: false };

    drawFrame(ctx, createCamera(), [wheel], wheelContent, []);

    expect(calls.fillRect).toBe(2); // background + support box
    expect(calls.arc).toBe(1); // tire circle
    // camera { x: 4, y: 2, scale: 36 } over 800x600 puts the wheel at screen (400,300).
    expect(translations).toContainEqual([0.01 * 36, 0.2 * 36]);
    expect(translations).toContainEqual([0, -0.17 * 36]);
  });
});

describe("drawFrame animation", () => {
  const image = {} as CanvasImageSource;
  /** A fan blade as the extractor emits it: `rotates` plus the spin descriptor (schema v3). */
  const bladeTextures = {
    atlases: new Map([["A.png", image]]),
    parts: new Map([
      [
        1,
        {
          bbox: [2, 1] as [number, number],
          sprites: [
            { atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0, cy: 0, sx: 2, sy: 3, rot: 0, rotates: true, spin: { axis: "x" as const, maxDegreesPerSecond: 1700 } },
          ],
        },
      ],
    ]),
  };

  it("compresses only the spin axis of a blade", () => {
    const { ctx, draws } = makeCtx();
    const spinning = { ...block, active: true };
    const state = createAnimationState();
    // One step at 1700 deg/s over 45/1700 s: the blade has turned 45 degrees, |cos| = 0.7071.
    updateAnimations(state, [spinning], content, bladeTextures, 45 / 1700);
    drawFrame(ctx, createCamera(), [spinning], content, [], undefined, undefined, bladeTextures, null, state);
    expect(draws[0][6]).toBeCloseTo(72); // width: the 2-unit sprite at camera scale 36
    expect(draws[0][7]).toBeCloseTo(76.3675); // height: 108 compressed by |cos 45|
    expect(draws[0][5]).toBeCloseTo(-38.1838);
  });

  it("draws the static sprite while the clock is frozen", () => {
    const { ctx, draws } = makeCtx();
    const idle = { ...block, active: true };
    const state = createAnimationState();
    // dt 0 is how build mode, a paused replay and a dragged preview reach the renderer.
    updateAnimations(state, [idle], content, bladeTextures, 0);
    drawFrame(ctx, createCamera(), [idle], content, [], undefined, undefined, bladeTextures, null, state);
    expect(draws[0]).toEqual([10, 20, 100, 100, -36, -54, 72, 108]);
  });

  /** A frame-animated sprite: the shipped pig's face, whose clips swap the whole descriptor. */
  const faceTextures = {
    atlases: new Map([["A.png", image]]),
    parts: new Map([
      [
        1,
        {
          bbox: [1, 1] as [number, number],
          sprites: [
            {
              atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0, cy: 0, sx: 2, sy: 3, rot: 0, rotates: false,
              clips: {
                Normal: {
                  loop: false,
                  frames: [
                    { atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0, cy: 0, sx: 2, sy: 3, rot: 0, seconds: 0.05 },
                    { atlas: "A.png", x: 200, y: 20, w: 80, h: 60, cx: 0.5, cy: 0, sx: 1.5, sy: 2, rot: 0, seconds: 0.05 },
                  ],
                },
              },
            },
          ],
        },
      ],
    ]),
  };

  it("draws the clip's current frame instead of the manifest sprite", () => {
    const { ctx, draws, translations } = makeCtx();
    const state = createAnimationState();
    // Past the first frame's 0.05 s the player holds frame 1 and its own rect and size.
    updateAnimations(state, [block], content, faceTextures, 0.06);
    drawFrame(ctx, createCamera(), [block], content, [], undefined, undefined, faceTextures, null, state);
    expect(draws[0]).toEqual([200, 20, 80, 60, -27, -36, 54, 72]);
    // The frame's own centre (0.5 world units) replaces the static sprite's offset.
    const last = translations[translations.length - 1];
    expect(last[0]).toBeCloseTo(18);
    expect(last[1]).toBeCloseTo(0);
  });

  /** The bottle's activation (manifest part 25): two content sprites cross-fading for a second. */
  const bottleActivation = {
    seconds: 2,
    jitter: { sprites: [0, 1, 2, 7], radius: 0.1, seconds: 1 },
    fade: [
      { sprite: 1, from: 1, to: 0, start: 0, seconds: 1 },
      { sprite: 0, from: 0, to: 1, start: 0, seconds: 1 },
      { sprite: 0, from: 1, to: 0, start: 1, seconds: 1 },
    ],
    launch: { sprite: 7, start: 1, speed: 20, spinDegreesPerSecond: 200, lifetime: 0.75 },
  };
  const bottleTextures: PartTextureSet = {
    atlases: new Map([["A.png", image]]),
    parts: new Map([
      [
        25,
        {
          bbox: [1, 1] as [number, number],
          sprites: [
            { atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0, cy: 0, sx: 2, sy: 3, rot: 0, rotates: false },
            { atlas: "A.png", x: 200, y: 20, w: 100, h: 100, cx: 0, cy: 0, sx: 2, sy: 3, rot: 0, rotates: false },
          ],
          activation: bottleActivation,
        },
      ],
    ]),
  };

  it("blits a mid-fade bottle's content sprite at a reduced globalAlpha", () => {
    const { ctx, draws, drawAlphas } = makeCtx();
    const bottle: DrawEntity = { ...block, entityId: 11, partTypeId: 25, active: true };
    const state = createAnimationState(() => 0.5);
    noteActivationEdges(state, [bottle], bottleTextures);
    updateAnimations(state, [bottle], content, bottleTextures, 0.5);
    drawFrame(ctx, createCamera(), [bottle], content, [], undefined, undefined, bottleTextures, null, state);

    // Both content legs are half way at 0.5 s, so each of the two blits is halved. The mock's
    // save/restore are no-ops, so only the first blit still carries its own alpha.
    expect(draws).toHaveLength(2);
    expect(drawAlphas[0]).toBeCloseTo(0.5, 6);
  });

  /** The blaster's blast (manifest part 52): a standalone quad, not a sprite of the atlas. */
  const blasterTextures: PartTextureSet = {
    atlases: new Map([
      ["A.png", image],
      ["Blast_Texture.png", image],
    ]),
    parts: new Map([
      [
        52,
        {
          bbox: [1, 1] as [number, number],
          sprites: [{ atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0, cy: 0, sx: 1, sy: 1, rot: 0, rotates: false }],
          activation: {
            seconds: 2,
            ring: {
              atlas: "Blast_Texture.png",
              x: 0,
              y: 0,
              w: 2048,
              h: 2048,
              startRadius: 0.5,
              radiusVelocity: 160,
              radiusDrag: 0.2,
              stepSeconds: 0.02,
              alphaNumerator: 64,
              alphaCap: 0.25,
            },
          },
        },
      ],
    ]),
  };

  it("blits a mid-blast blaster's ring at its own rect, 2 * radius * camera scale wide", () => {
    const { ctx, draws, drawAlphas } = makeCtx();
    const blaster: DrawEntity = { ...block, entityId: 12, partTypeId: 52, active: true };
    const state = createAnimationState(() => 0.5);
    noteActivationEdges(state, [blaster], blasterTextures);
    updateAnimations(state, [blaster], content, blasterTextures, 0.5);
    drawFrame(ctx, createCamera(), [blaster], content, [], undefined, undefined, blasterTextures, null, state);

    // The ring is drawn first, behind the part's own sprite. A 0.5 s frame runs 24 of the
    // descriptor's 0.02 s steps, so the radius is 73.57521223001905 world units.
    expect(draws).toHaveLength(2);
    expect(draws[0].slice(0, 4)).toEqual([0, 0, 2048, 2048]);
    const size = 2 * 73.57521223001905 * (1 * 36);
    expect(draws[0][4]).toBeCloseTo(-size / 2, 3);
    expect(draws[0][5]).toBeCloseTo(-size / 2, 3);
    expect(draws[0][6]).toBeCloseTo(size, 3);
    expect(draws[0][7]).toBeCloseTo(size, 3);
    // min(64 / radius^2, 0.25) at radius 73.57521223001905 is well under the cap.
    expect(drawAlphas[0]).toBeCloseTo(0.011822707007822538, 9);
  });
});

describe("drawFrame conditional connection sprites", () => {
  // A wooden glider wing (target) next to a weldable block (source): the manifest carries both
  // mounts, and only the one the neighbour state picks is drawn.
  const wingContent: PartContentDocument = {
    format: "pigforge.part-content",
    schemaVersion: 1,
    contentVersion: "t",
    physics: { maximumAngularSpeed: 7, damping: { linear: 0.2, angular: 0.05 } },

    parts: [
      { partTypeId: 31, name: "wing", mode: "dynamic", mass: 0.6, capabilities: { jointConnectionType: "target" }, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
      { partTypeId: 1, name: "block", mode: "dynamic", mass: 1, capabilities: { jointConnectionType: "source" }, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
    ],
  };
  const sprite = { atlas: "A.png", x: 0, y: 0, w: 10, h: 10, cx: 0.06, cy: 0, sx: 1, sy: 0.39, rot: 0, rotates: false };
  const wingTextures = (image: CanvasImageSource): PartTextureSet => ({
    atlases: new Map([["A.png", image]]),
    parts: new Map<number, PartTexture>([
      [
        31,
        {
          bbox: [2, 1] as [number, number],
          connectionVisual: "frame",
          sprites: [
            { ...sprite, cy: -0.3613, condition: { kind: "frame", mount: "bottom" } },
            { ...sprite, y: 20, cy: 0.39, flipY: true, condition: { kind: "frame", mount: "top" } },
          ],
        },
      ],
      [1, { bbox: [1, 1] as [number, number], sprites: [{ ...sprite, cx: 0, cy: 0, sy: 1 }] }],
    ]),
  });
  const wing: DrawEntity = { entityId: 1, partTypeId: 31, x: 0, y: 0, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 0, active: false };

  it("draws only the bottom mount while nothing connects", () => {
    const { ctx, calls, draws, scales } = makeCtx();
    drawFrame(ctx, createCamera(), [wing], wingContent, [], undefined, undefined, wingTextures({} as CanvasImageSource));
    expect(calls.drawImage).toBe(1);
    // The bottom mount's own source row.
    expect(draws[0][1]).toBe(0);
    expect(scales).toEqual([]);
  });

  it("swaps to the top mount once a weldable neighbour sits above", () => {
    const { ctx, draws, scales } = makeCtx();
    const above: DrawEntity = { ...wing, entityId: 2, partTypeId: 1, y: 1 };
    drawFrame(ctx, createCamera(), [wing, above], wingContent, [], undefined, undefined, wingTextures({} as CanvasImageSource));
    // The wing's top mount, then the block's own sprite.
    expect(draws).toHaveLength(2);
    expect(draws[0][1]).toBe(20);
    // The original draws the top mount as the bottom one mirrored through a negative node scale.
    expect(scales).toEqual([[1, -1]]);
  });

  it("keeps the bottom mount for a neighbour below", () => {
    const { ctx, draws } = makeCtx();
    const below: DrawEntity = { ...wing, entityId: 2, partTypeId: 1, y: -1 };
    drawFrame(ctx, createCamera(), [wing, below], wingContent, [], undefined, undefined, wingTextures({} as CanvasImageSource));
    expect(draws).toHaveLength(2);
    expect(draws[0][1]).toBe(0);
  });
});

describe("drawFrame mirrored parts", () => {
  // A wing-like part: a body sprite offset in x, plus a mount with its own rotation, so both
  // effects of the mirror are observable (ADR-030). At yaw 0 the part's own frame is the world
  // frame, which keeps the expected offsets literal.
  const mirrorTextures = (image: CanvasImageSource | undefined) => ({
    atlases: new Map([["A.png", image as CanvasImageSource]]),
    parts: new Map([
      [
        31,
        {
          bbox: [2, 2] as [number, number],
          sprites: [
            { atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: -0.5, cy: -0.15, sx: 1.9, sy: 0.6122, rot: 0, rotates: false },
            { atlas: "A.png", x: 200, y: 20, w: 100, h: 100, cx: 0.31, cy: 0, sx: 1.1, sy: 1.016, rot: 0.5, rotates: false },
          ],
        },
      ],
    ]),
  });

  const wing = (mirrored: boolean): DrawEntity => ({
    entityId: 30, partTypeId: 31, x: 0, y: 0, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 5, active: false, mirrored,
  });

  /** The last `expected.length` entries, component by component (the canvas maths is float). */
  const expectTail = (actual: Array<[number, number]>, expected: Array<[number, number]>) => {
    const tail = actual.slice(-expected.length);
    expect(tail).toHaveLength(expected.length);
    tail.forEach(([x, y], index) => {
      expect(x).toBeCloseTo(expected[index]![0], 4);
      expect(y).toBeCloseTo(expected[index]![1], 4);
    });
  };

  it("mirrors the offsets, reverses the sprite rotation and mirrors the art", () => {
    const image = {} as CanvasImageSource;
    const plain = makeCtx();
    drawFrame(plain.ctx, createCamera(), [wing(false)], content, [], undefined, undefined, mirrorTextures(image));
    const mirrored = makeCtx();
    drawFrame(mirrored.ctx, createCamera(), [wing(true)], content, [], undefined, undefined, mirrorTextures(image));

    // Sprite offsets are the part-local centres, so the mirror negates x and leaves y (camera
    // scale 36): the body sprite's (-0.5, -0.15) becomes (0.5, -0.15).
    expectTail(plain.translations, [[-18, 5.4], [11.16, 0]]);
    expectTail(mirrored.translations, [[18, 5.4], [-11.16, 0]]);

    // A sprite's world angle is the yaw plus its own rotation; the mirror reverses that rotation
    // (the canvas negates the angle again, hence the sign flip below).
    const plainRotations = plain.rotations.slice(-2);
    const mirroredRotations = mirrored.rotations.slice(-2);
    expect(plainRotations[0]).toBeCloseTo(0, 5);
    expect(mirroredRotations[0]).toBeCloseTo(0, 5);
    expect(plainRotations[1]).toBeCloseTo(-0.5, 5);
    expect(mirroredRotations[1]).toBeCloseTo(0.5, 5);

    // The art is mirrored rather than turned, in the sprite's own frame.
    expect(plain.scales).toEqual([]);
    expect(mirrored.scales).toEqual([[-1, 1], [-1, 1]]);
  });

  it("cancels a mirror against the manifest's own negative node scale", () => {
    // The original's node scale and its flip are scalars on the same node, so a sprite the
    // manifest already draws mirrored (a wing's top mount) comes back unmirrored.
    const flipped = {
      atlases: new Map([["A.png", {} as CanvasImageSource]]),
      parts: new Map([
        [
          31,
          {
            bbox: [2, 2] as [number, number],
            sprites: [
              { atlas: "A.png", x: 10, y: 20, w: 100, h: 100, cx: 0.31, cy: 0, sx: 1.1, sy: 1.016, rot: 0, rotates: false, flipX: true },
            ],
          },
        ],
      ]),
    };

    const plain = makeCtx();
    drawFrame(plain.ctx, createCamera(), [wing(false)], content, [], undefined, undefined, flipped);
    const mirrored = makeCtx();
    drawFrame(mirrored.ctx, createCamera(), [wing(true)], content, [], undefined, undefined, flipped);

    expect(plain.scales).toEqual([[-1, 1]]);
    expect(mirrored.scales).toEqual([]);
    expectTail(mirrored.translations, [[-11.16, 0]]);
  });
});
