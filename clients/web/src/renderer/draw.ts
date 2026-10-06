import type { DrawEntity, MarqueeRect, PartContentDocument, PartDefinition, PartShape } from "@/schema/types";
import { activationOverlayFor, poseFor, type ActivationOverlay, type AnimationState } from "./animation";
import { layoutSprites, subEntityTexture, type PartTexture, type PartTextureSet } from "./atlas";
import { type Camera, worldToScreen } from "./camera";
import { conditionalSpriteVisible, connectableSides } from "./connectionVisuals";

const STATIC_FILL = "#5c6b52";
const DYNAMIC_FILL = "#c4a574";
const SELECT_STROKE = "#f0d090";
const PREVIEW_ALPHA = 0.45;
const ACTIVE_STROKE = "#ffd166";
/** Placeholder shape for entities whose content entry is unknown. */
const DEFAULT_SHAPE: PartShape = { kind: "box", halfExtents: [0.5, 0.5, 0.5] };

/** Rotates a part-local offset into the world frame (+y up). */
function rotatePoint(x: number, y: number, angle: number): { x: number; y: number } {
  const cos = Math.cos(angle);
  const sin = Math.sin(angle);
  return { x: x * cos - y * sin, y: x * sin + y * cos };
}

/**
 * The frame's paint order. A runtime sub-entity is drawn immediately *before* the part it belongs
 * to, so that part's art covers it: the original sorts equal-order sprites by distance to the
 * camera (IngameCamera.cs:440 sits at z = -15 looking towards +z), and a boxing glove's fist hangs
 * at z = 0.15 while its box sits at z = 0.1 -- the box is the nearer sprite, so it wins
 * (docs/specs/boxing-glove.md §3). Without this the fist is painted over its own box and the end of
 * the wind-back reads as the fist popping out of existence instead of sliding back inside.
 *
 * A sub-entity belongs to the nearest entity of its own part type that is a real part (it borrows
 * the host's `partTypeId` on the wire, PGFS v5); one with no such part keeps its published place.
 * The list is otherwise untouched -- level geometry, previews and every other part keep the
 * snapshot order, and each entity is painted exactly once.
 */
export function drawOrder(entities: readonly DrawEntity[]): readonly DrawEntity[] {
  let subEntities = false;
  for (const entity of entities) {
    if (entity.subEntity === true) {
      subEntities = true;
      break;
    }
  }

  if (!subEntities) {
    return entities;
  }

  const covered = new Map<number, DrawEntity[]>();
  const deferred = new Set<number>();
  for (const sub of entities) {
    if (sub.subEntity !== true) {
      continue;
    }

    let host: DrawEntity | undefined;
    let nearest = Number.POSITIVE_INFINITY;
    for (const candidate of entities) {
      if (candidate.subEntity === true || candidate.partTypeId !== sub.partTypeId) {
        continue;
      }

      const distance = Math.hypot(candidate.x - sub.x, candidate.y - sub.y);
      if (distance < nearest) {
        nearest = distance;
        host = candidate;
      }
    }

    if (host === undefined) {
      continue;
    }

    deferred.add(sub.entityId);
    const list = covered.get(host.entityId);
    if (list === undefined) {
      covered.set(host.entityId, [sub]);
    } else {
      list.push(sub);
    }
  }

  if (deferred.size === 0) {
    return entities;
  }

  const order: DrawEntity[] = [];
  for (const entity of entities) {
    if (deferred.has(entity.entityId)) {
      continue;
    }

    const under = covered.get(entity.entityId);
    if (under !== undefined) {
      order.push(...under);
    }

    order.push(entity);
  }

  return order;
}

/**
 * The axle a wheel's sprites turn about, part-local metres: the centre of the tire the part
 * content describes, which is the same point the server hinges the wheel body about
 * (`PartContentLibrary.DescribeWheel`). Sourcing it from the content keeps the spin correct
 * even when the art manifest is stale or absent (`pivot` is only the art-frame fallback).
 */
export function wheelAxle(part: PartDefinition, texture?: PartTexture): [number, number] | undefined {
  if (part.capabilities?.wheel !== true) {
    return undefined;
  }

  const tires = part.shapes.filter((shape) => shape.kind === "sphere" && (shape.radius ?? 0) > 0);
  if (tires.length > 0) {
    // Volume-weighted, matching the server: a wheel turns about its tires' combined centre.
    let weight = 0;
    let x = 0;
    let y = 0;
    for (const tire of tires) {
      const volume = (tire.radius ?? 0) ** 3;
      weight += volume;
      x += (tire.offset?.[0] ?? 0) * volume;
      y += (tire.offset?.[1] ?? 0) * volume;
    }
    return [x / weight, y / weight];
  }

  if (part.shapes.length === 1) {
    // Nothing round to roll (the original's propeller): its one shape is its own axle.
    return [part.shapes[0].offset?.[0] ?? 0, part.shapes[0].offset?.[1] ?? 0];
  }

  return texture?.pivot;
}

/** Diameter of the tire a wheel part rolls on, or undefined when it has no round shape. */
function tireDiameter(part: PartDefinition): number | undefined {
  let radius = 0;
  for (const shape of part.shapes) {
    if (shape.kind === "sphere" && (shape.radius ?? 0) > radius) {
      radius = shape.radius ?? 0;
    }
  }
  return radius > 0 ? radius * 2 : undefined;
}

/**
 * Which sprites turn with the wheel. The manifest's per-sprite `rotates` flags are the
 * authority — only the prefab chain knows which art node the original drives. A manifest
 * without any flags (an older generated file) falls back to the sprite that draws the tire:
 * the round one whose art box is the size of the tire it rolls on.
 */
function turningSprites(texture: PartTexture, part: PartDefinition): boolean[] {
  if (texture.sprites.some((sprite) => sprite.rotates)) {
    return texture.sprites.map((sprite) => sprite.rotates === true);
  }

  const diameter = tireDiameter(part);
  const spin = texture.sprites.map(() => false);
  if (diameter === undefined) {
    return spin;
  }

  let best = -1;
  let bestError = 0.15;
  texture.sprites.forEach((sprite, index) => {
    const error = Math.max(Math.abs(sprite.sx - diameter) / diameter, Math.abs(sprite.sy - diameter) / diameter);
    if (error <= bestError) {
      bestError = error;
      best = index;
    }
  });
  if (best >= 0) {
    spin[best] = true;
  }
  return spin;
}

export function drawFrame(
  ctx: CanvasRenderingContext2D,
  camera: Camera,
  entities: readonly DrawEntity[],
  content: PartContentDocument | null,
  selectedIds: readonly number[],
  goal?: { minX: number; minY: number; maxX: number; maxY: number },
  bounds?: { minX: number; minY: number; maxX: number; maxY: number },
  textures?: PartTextureSet | null,
  marquee?: MarqueeRect | null,
  animations?: AnimationState | null,
): void {
  const width = ctx.canvas.clientWidth || ctx.canvas.width;
  const height = ctx.canvas.clientHeight || ctx.canvas.height;
  ctx.fillStyle = "#1c211c";
  ctx.fillRect(0, 0, width, height);
  drawGrid(ctx, camera, width, height);
  if (goal) {
    const a = worldToScreen(camera, goal.minX, goal.maxY, width, height);
    const b = worldToScreen(camera, goal.maxX, goal.minY, width, height);
    ctx.fillStyle = "rgba(80, 160, 90, 0.25)";
    ctx.fillRect(a.x, a.y, b.x - a.x, b.y - a.y);
  }
  if (bounds) {
    const a = worldToScreen(camera, bounds.minX, bounds.maxY, width, height);
    const b = worldToScreen(camera, bounds.maxX, bounds.minY, width, height);
    ctx.strokeStyle = "rgba(220, 100, 90, 0.85)";
    ctx.lineWidth = 2;
    ctx.setLineDash([8, 6]);
    ctx.strokeRect(a.x, a.y, b.x - a.x, b.y - a.y);
    ctx.setLineDash([]);
  }
  const parts = new Map<number, PartDefinition>();
  if (content) {
    for (const part of content.parts) {
      parts.set(part.partTypeId, part);
    }
  }
  // Which sides of each part a neighbour would weld to, for the sprites the original shows per
  // connection side. Only parts whose manifest carries a rule are resolved, so a scene of plain
  // blocks costs a single pass with no neighbour lookups.
  const connections = connectableSides(
    entities,
    (partTypeId) => parts.get(partTypeId),
    (entity) => (textures ?? null)?.parts.get(entity.partTypeId)?.connectionVisual !== undefined,
  );

  for (const entity of drawOrder(entities)) {
    const part = parts.get(entity.partTypeId);
    const origin = worldToScreen(camera, entity.x, entity.y, width, height);
    // The sandbox broadcasts previews with physicsBodyId 0: they are not physical
    // bodies yet, so they render translucent and without a halo or name label.
    const preview = entity.bodyId === 0;
    // Light parts cast a radial glow; the radius scales like the world (spatial
    // light range), so zooming in amplifies the halo like the original game.
    const lightRadius = part?.capabilities?.light;
    if (!preview && lightRadius !== undefined && lightRadius > 0) {
      const halo = lightRadius * entity.scale * camera.scale;
      ctx.save();
      const glow = ctx.createRadialGradient(origin.x, origin.y, 0, origin.x, origin.y, halo);
      glow.addColorStop(0, "rgba(255, 240, 180, 0.55)");
      glow.addColorStop(1, "rgba(255, 240, 180, 0)");
      ctx.fillStyle = glow;
      ctx.beginPath();
      ctx.arc(origin.x, origin.y, halo, 0, Math.PI * 2);
      ctx.fill();
      ctx.restore();
    }
    ctx.save();
    if (preview) {
      ctx.globalAlpha = PREVIEW_ALPHA;
    }
    ctx.translate(origin.x, origin.y);
    const shape = part?.shapes[0];
    const atlasImages = textures?.atlases;
    // A runtime sub-entity draws its own art (the manifest's sub-entity list), not its host part's.
    const texture = subEntityTexture(textures?.parts.get(entity.partTypeId), entity.subEntity === true);
    const pixelScale = entity.scale * camera.scale;
    // A wheel part's body rolls, so `yaw` carries the accumulated spin. Its turning sprites
    // spin about the axle the content describes; the mounts stay rigid to the frame the part
    // is attached to — the hinge's parent body (snapshot `attachYaw`), or, for a source
    // without that frame (a replay document), the angle the part was built at. Build-mode
    // previews (`bodyId` 0) are not rolled by physics yet, so they follow `yaw` wholesale.
    const axle = part ? wheelAxle(part, texture) : undefined;
    const mountFrame = entity.attachYaw ?? entity.restYaw;
    const rolling = axle !== undefined && entity.bodyId !== 0 && mountFrame !== undefined;
    const rest = rolling ? mountFrame! : entity.yaw;
    // Content offsets are part-local, so the axle scales with the part like the sprites do.
    const axleOffset = rolling && axle
      ? rotatePoint(axle[0] * entity.scale, axle[1] * entity.scale, entity.yaw)
      : null;
    // Which sprites turn with the wheel (manifest flags, with a tire-size fallback).
    const turning = rolling && part !== undefined && texture !== undefined ? turningSprites(texture, part) : undefined;
    // Every sprite offset below is measured in the manifest's own frame, so a mount is placed
    // relative to the tire — the sprite pinned on the axle — and the pair keeps the relative
    // placement the manifest authored. Measuring a mount from the content axle instead would
    // mix two frames and drop a small wheel's fork underneath its tire.
    const placed = texture !== undefined && atlasImages !== undefined ? layoutSprites(texture, entity.scale) : undefined;
    const tire = turning && placed !== undefined ? placed.find((_, index) => turning[index]) : undefined;
    const entitySides = connections.get(entity.entityId);
    // The one sprite the activation draws in the world instead of the part's frame: the blast
    // ring (behind the part's art) or the launched cork (above it). The record is reused between
    // entities, so it is consumed inside this iteration.
    const activation = animations ? activationOverlayFor(animations, entity.entityId) : null;
    if (activation !== null && activation.behind && atlasImages !== undefined) {
      drawActivationOverlay(ctx, activation, atlasImages, camera, entity.scale, width, height);
    }
    // The build pose's handedness (PGFS v6, ADR-030). The original turns a mirrored part's whole
    // frame 180 degrees about its own up axis, so in the plane every sprite offset mirrors in x, a
    // sprite's own rotation runs the other way, and its art is mirrored rather than turned. Only
    // the wing and tail families can be mirrored and neither is a hinged wheel, so a mirrored
    // entity never takes the rolling-mount branch below.
    const mirrored = entity.mirrored === true;
    if (texture && atlasImages && texture.sprites.every((sprite) => atlasImages.get(sprite.atlas) !== undefined)) {
      // Original art: drawn at the BPLE world size and offsets, so part visuals match
      placed?.forEach((placement, index) => {
        // A conditional marker (an `*Attachment` or a wing mount) is drawn only while the
        // layout puts a weldable neighbour on the side it stands for.
        const condition = placement.sprite.condition;
        if (
          condition !== undefined &&
          !conditionalSpriteVisible(condition, texture?.connectionVisual, entitySides, entity.yaw)
        ) {
          return;
        }
        // A pose is the manifest sprite unless the animation state replaced it with its clip's
        // current frame, which carries its own rect, size and centre (see `animation/index.ts`).
        const pose = animations ? poseFor(animations, entity, index, placement.sprite) : null;
        if (pose !== null && !pose.visible) return;
        const drawn = pose?.sprite ?? placement.sprite;
        const image = textures.atlases.get(drawn.atlas);
        if (!image) return;
        // The ignition jitter offsets the whole node, so it rides the sprite's own centre.
        const jitterX = pose?.offsetX ?? 0;
        const jitterY = pose?.offsetY ?? 0;
        // Sprite offsets are in manifest metres, so everything scales with the part like the
        // content shapes do; the spin foreshortening shrinks one axis about the sprite centre.
        // The mirror negates the sprite's x offset in the part's own frame (ADR-030).
        const centreX = (mirrored ? -1 : 1) * (drawn.cx + jitterX) * entity.scale;
        const centreY = (drawn.cy + jitterY) * entity.scale;
        const w = drawn.sx * pixelScale * (pose?.scaleX ?? 1);
        const h = drawn.sy * pixelScale * (pose?.scaleY ?? 1);
        // Position and orientation in the world frame, both taken from the part origin.
        let offsetX = Math.cos(entity.yaw) * centreX - Math.sin(entity.yaw) * centreY;
        let offsetY = Math.sin(entity.yaw) * centreX + Math.cos(entity.yaw) * centreY;
        let angle = entity.yaw + (pose?.rot ?? 0);
        if (axleOffset) {
          if (turning?.[index] || tire === undefined) {
            // The tire spins on the axle: it stays centred there instead of orbiting it.
            offsetX = axleOffset.x;
            offsetY = axleOffset.y;
          } else {
            // Mounts sit still: keep the offset from the tire the manifest authored.
            const fixed = rotatePoint(centreX - tire.x, centreY - tire.y, rest);
            offsetX = axleOffset.x + fixed.x;
            offsetY = axleOffset.y + fixed.y;
            angle = rest;
          }
        }

        ctx.save();
        if (pose !== null && pose.alpha !== 1) ctx.globalAlpha *= pose.alpha;
        ctx.translate(offsetX * pixelScale, -offsetY * pixelScale);
        // `angle` is the yaw plus this sprite's pose rotation, so the sprite's own rotation is
        // `angle - yaw + drawn.rot`; the mirror reverses it about the part's up axis.
        const spriteRotation = (angle - entity.yaw) + drawn.rot;
        ctx.rotate(-(mirrored ? entity.yaw - spriteRotation : entity.yaw + spriteRotation));
        // A negative node scale in the original mirrors the art (a wing's top mount is its
        // bottom mount drawn upside down), so mirror in the sprite's own rotated frame. The build
        // pose's mirror composes with it: the original's node scale and its flip are scalars on the
        // same node, so the two signs multiply.
        const flipX = (placement.sprite.flipX === true) !== mirrored;
        const flipY = placement.sprite.flipY === true;
        if (flipX || flipY) {
          ctx.scale(flipX ? -1 : 1, flipY ? -1 : 1);
        }
        ctx.drawImage(image, drawn.x, drawn.y, drawn.w, drawn.h, -w / 2, -h / 2, w, h);
        ctx.restore();
      });
    } else {
      // Shape placeholders live in the part's own frame: rotate the context like the
      // original sprite path did before the wheel pivot took over placement.
      ctx.save();
      ctx.rotate(-entity.yaw);
      if (mirrored) {
        // The pose is Rz(yaw) * Ry(180): mirror the part's own frame (ADR-030).
        ctx.scale(-1, 1);
      }

      const fill = part?.mode === "static" ? STATIC_FILL : DYNAMIC_FILL;
      const stroke = part?.mode === "static" ? "#8a9a7a" : "#d8b880";
      const shapes = part?.shapes?.length ? part.shapes : [DEFAULT_SHAPE];
      for (const entry of shapes) {
        const [offsetX, offsetY] = entry.offset ?? [0, 0, 0];
        const shifted = offsetX !== 0 || offsetY !== 0;
        if (shifted) {
          ctx.save();
          ctx.translate(offsetX * pixelScale, -offsetY * pixelScale);
        }
        ctx.fillStyle = fill;
        ctx.strokeStyle = stroke;
        ctx.lineWidth = 1;
        if (entry.kind === "sphere" && entry.radius) {
          const radius = entry.radius * pixelScale;
          ctx.beginPath();
          ctx.arc(0, 0, radius, 0, Math.PI * 2);
          ctx.fill();
          ctx.stroke();
        } else {
          const hx = (entry.halfExtents?.[0] ?? 0.5) * pixelScale;
          const hy = (entry.halfExtents?.[1] ?? 0.5) * pixelScale;
          ctx.fillRect(-hx, -hy, hx * 2, hy * 2);
          ctx.strokeRect(-hx, -hy, hx * 2, hy * 2);
        }
        if (shifted) {
          ctx.restore();
        }
      }
      ctx.restore();
    }
    // The launched cork flies in world space, above the part it left.
    if (activation !== null && !activation.behind && atlasImages !== undefined) {
      drawActivationOverlay(ctx, activation, atlasImages, camera, entity.scale, width, height);
    }
    // A switchable part with its switch on gets an amber ring around its shape.
    if (!preview && entity.active && part?.capabilities?.activation !== undefined) {
      ctx.save();
      ctx.rotate(-entity.yaw);
      strokeActive(ctx, shape, pixelScale);
      ctx.restore();
    }
    // Untextured parts are still placeholders: stamp the type name at the collision centre.
    if (!preview && part && part.name && !texture) {
      ctx.fillStyle = "rgba(255, 255, 255, 0.85)";
      ctx.font = "10px system-ui, sans-serif";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(part.name, 0, 0);
    }
    if (selectedIds.includes(entity.entityId)) {
      ctx.strokeStyle = SELECT_STROKE;
      ctx.lineWidth = 2;
      ctx.strokeRect(-4, -4, 8, 8);
    }
    ctx.restore();
  }

  if (marquee) {
    const a = worldToScreen(camera, marquee.minX, marquee.maxY, width, height);
    const b = worldToScreen(camera, marquee.maxX, marquee.minY, width, height);
    ctx.fillStyle = "rgba(255, 209, 102, 0.12)";
    ctx.fillRect(a.x, a.y, b.x - a.x, b.y - a.y);
    ctx.strokeStyle = SELECT_STROKE;
    ctx.lineWidth = 1;
    ctx.setLineDash([4, 4]);
    ctx.strokeRect(a.x, a.y, b.x - a.x, b.y - a.y);
    ctx.setLineDash([]);
  }
}

/**
 * One sprite an activation draws in the world rather than in the part's frame: the blaster's
 * expanding ring (behind the part's art) or the launched cork (above it). Both scale with the
 * part's own build scale, like the rest of its art.
 */
function drawActivationOverlay(
  ctx: CanvasRenderingContext2D,
  overlay: ActivationOverlay,
  atlases: ReadonlyMap<string, CanvasImageSource>,
  camera: Camera,
  partScale: number,
  width: number,
  height: number,
): void {
  const image = atlases.get(overlay.atlas);
  if (image === undefined) return;
  const point = worldToScreen(camera, overlay.worldX, overlay.worldY, width, height);
  const scale = partScale * camera.scale;
  const w = overlay.worldW * scale;
  const h = overlay.worldH * scale;
  ctx.save();
  ctx.globalAlpha *= overlay.alpha;
  ctx.translate(point.x, point.y);
  ctx.rotate(-overlay.rotation);
  ctx.drawImage(image, overlay.x, overlay.y, overlay.w, overlay.h, -w / 2, -h / 2, w, h);
  ctx.restore();
}

function strokeActive(ctx: CanvasRenderingContext2D, shape: PartShape | undefined, pixelScale: number): void {
  ctx.strokeStyle = ACTIVE_STROKE;
  ctx.lineWidth = 2;
  if (shape?.kind === "sphere" && shape.radius) {
    ctx.beginPath();
    ctx.arc(0, 0, shape.radius * pixelScale + 2, 0, Math.PI * 2);
    ctx.stroke();
    return;
  }

  const hx = (shape?.halfExtents?.[0] ?? 0.5) * pixelScale + 2;
  const hy = (shape?.halfExtents?.[1] ?? 0.5) * pixelScale + 2;
  ctx.strokeRect(-hx, -hy, hx * 2, hy * 2);
}

function drawGrid(ctx: CanvasRenderingContext2D, camera: Camera, width: number, height: number): void {
  ctx.strokeStyle = "#2a332a";
  ctx.lineWidth = 1;
  const step = camera.scale;
  const origin = worldToScreen(camera, 0, 0, width, height);
  ctx.beginPath();
  for (let x = origin.x % step; x < width; x += step) {
    ctx.moveTo(x, 0);
    ctx.lineTo(x, height);
  }
  for (let y = origin.y % step; y < height; y += step) {
    ctx.moveTo(0, y);
    ctx.lineTo(width, y);
  }
  ctx.stroke();
}
