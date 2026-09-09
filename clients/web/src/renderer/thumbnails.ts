/**
 * Palette/gadget button icons: the same original-art composite the canvas draws,
 * fitted into a small square. Pure layout math plus one thin canvas blit, so the
 * button layer never duplicates the sprite-placement rules. Missing art is not an
 * error — callers fall back to the text label.
 */
import type { PartTexture, PartTextureSet } from "./atlas";

export interface ThumbnailBounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export interface ThumbnailPlacement {
  sprite: PartTexture["sprites"][number];
  /** Sprite centre in canvas pixels (top-left origin). */
  x: number;
  y: number;
  /** Draw size in canvas pixels. */
  w: number;
  h: number;
}

/** Composite extent in part-local world units, honouring per-sprite rotation. */
export function compositeBounds(texture: PartTexture): ThumbnailBounds {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const sprite of texture.sprites) {
    const cos = Math.abs(Math.cos(sprite.rot));
    const sin = Math.abs(Math.sin(sprite.rot));
    const halfWidth = (sprite.sx * cos + sprite.sy * sin) / 2;
    const halfHeight = (sprite.sx * sin + sprite.sy * cos) / 2;
    minX = Math.min(minX, sprite.cx - halfWidth);
    maxX = Math.max(maxX, sprite.cx + halfWidth);
    minY = Math.min(minY, sprite.cy - halfHeight);
    maxY = Math.max(maxY, sprite.cy + halfHeight);
  }

  return { minX, minY, maxX, maxY };
}

/**
 * Fits the composite into a `size`×`size` square with `padding`, centred on the
 * composite (not the part origin) so off-centre art still fills the button.
 */
export function thumbnailPlacements(texture: PartTexture, size: number, padding = 3): ThumbnailPlacement[] {
  const bounds = compositeBounds(texture);
  const width = Math.max(bounds.maxX - bounds.minX, 1e-6);
  const height = Math.max(bounds.maxY - bounds.minY, 1e-6);
  const usable = Math.max(size - padding * 2, 1);
  const scale = Math.min(usable / width, usable / height);
  const centerX = (bounds.minX + bounds.maxX) / 2;
  const centerY = (bounds.minY + bounds.maxY) / 2;
  return texture.sprites.map((sprite) => ({
    sprite,
    x: size / 2 + (sprite.cx - centerX) * scale,
    // World +y is up, canvas +y is down.
    y: size / 2 - (sprite.cy - centerY) * scale,
    w: sprite.sx * scale,
    h: sprite.sy * scale,
  }));
}

/** Blits a part composite into an already-sized context. False when no sprite was drawable. */
export function drawThumbnail(
  ctx: CanvasRenderingContext2D,
  textures: PartTextureSet,
  partTypeId: number,
  size: number,
  padding = 3,
): boolean {
  const texture = textures.parts.get(partTypeId);
  if (texture === undefined) {
    return false;
  }

  ctx.clearRect(0, 0, size, size);
  ctx.imageSmoothingEnabled = false;
  let drew = false;
  for (const placement of thumbnailPlacements(texture, size, padding)) {
    const image = textures.atlases.get(placement.sprite.atlas);
    if (image === undefined) {
      continue;
    }

    ctx.save();
    ctx.translate(placement.x, placement.y);
    ctx.rotate(-placement.sprite.rot);
    ctx.drawImage(
      image,
      placement.sprite.x,
      placement.sprite.y,
      placement.sprite.w,
      placement.sprite.h,
      -placement.w / 2,
      -placement.h / 2,
      placement.w,
      placement.h,
    );
    ctx.restore();
    drew = true;
  }

  return drew;
}

/**
 * Renders one part's icon as a data URL, or null when the part has no art or any
 * sprite's atlas failed to load (caller shows the text label instead).
 */
export function partThumbnailDataUrl(
  textures: PartTextureSet,
  partTypeId: number,
  size = 64,
  padding = 3,
): string | null {
  const texture = textures.parts.get(partTypeId);
  if (texture === undefined || texture.sprites.some((sprite) => textures.atlases.get(sprite.atlas) === undefined)) {
    return null;
  }

  const canvas = document.createElement("canvas");
  canvas.width = size;
  canvas.height = size;
  const ctx = canvas.getContext("2d");
  if (ctx === null || !drawThumbnail(ctx, textures, partTypeId, size, padding)) {
    return null;
  }

  return canvas.toDataURL("image/png");
}
