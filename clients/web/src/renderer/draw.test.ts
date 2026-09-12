// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { drawFrame, wheelAxle } from "./draw";
import { createCamera } from "./camera";
import type { DrawEntity, PartContentDocument } from "@/schema/types";

function makeCtx() {
  const calls: Record<string, number> = {};
  const alphas: number[] = [];
  const translations: Array<[number, number]> = [];
  const draws: number[][] = [];
  const alpha = { value: 1 };
  const gradient: CanvasGradient = { addColorStop: () => {} } as unknown as CanvasGradient;
  const rotations: number[] = [];
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
  return { ctx: ctx as unknown as CanvasRenderingContext2D, calls, alphas, translations, draws, rotations };
}

const content: PartContentDocument = {
  format: "pigforge.part-content",
  schemaVersion: 1,
  contentVersion: "t",
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
    expect(calls.fillRect).toBe(2); // background + the part shape
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
