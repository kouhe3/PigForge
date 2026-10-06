import { describe, expect, it } from "vitest";
import { createCamera, fitBounds, screenToWorld, worldToScreen, yawFromQuaternion } from "./camera";

describe("camera", () => {
  it("round-trips world and screen at the canvas centre", () => {
    const camera = createCamera();
    camera.x = 2;
    camera.y = 3;
    const screen = worldToScreen(camera, 2, 3, 800, 600);
    expect(screen.x).toBeCloseTo(400);
    expect(screen.y).toBeCloseTo(300);
    const world = screenToWorld(camera, 400, 300, 800, 600);
    expect(world.x).toBeCloseTo(2);
    expect(world.y).toBeCloseTo(3);
  });

  it("click CSS pixels round-trip only when mapping uses CSS size not backing size", () => {
    const camera = createCamera();
    camera.x = 0;
    camera.y = 0;
    camera.scale = 40;
    const cssWidth = 800;
    const cssHeight = 600;
    const dpr = 2;
    const clickX = 200;
    const clickY = 150;
    const world = screenToWorld(camera, clickX, clickY, cssWidth, cssHeight);
    const drawnOnBacking = worldToScreen(camera, world.x, world.y, cssWidth * dpr, cssHeight * dpr);
    expect(drawnOnBacking.x).not.toBeCloseTo(clickX);
    const drawnOnCss = worldToScreen(camera, world.x, world.y, cssWidth, cssHeight);
    expect(drawnOnCss.x).toBeCloseTo(clickX);
    expect(drawnOnCss.y).toBeCloseTo(clickY);
  });

  it("reads identity quaternion as zero yaw", () => {
    expect(yawFromQuaternion([0, 0, 0, 1])).toBeCloseTo(0);
  });
});

describe("fitBounds", () => {
  it("centres on the rectangle and fits its tighter axis", () => {
    const camera = fitBounds({ minX: -30, minY: -12, maxX: 30, maxY: 30 }, 800, 600, 0);
    expect(camera.x).toBeCloseTo(0);
    expect(camera.y).toBeCloseTo(9);
    expect(camera.scale).toBeCloseTo(Math.min(800 / 60, 600 / 42));
  });

  it("keeps the whole rectangle inside the canvas, padding included", () => {
    const rect = { minX: 12, minY: -0.5, maxX: 16, maxY: 2.5 };
    const camera = fitBounds(rect, 800, 600);
    const topLeft = worldToScreen(camera, rect.minX, rect.maxY, 800, 600);
    const bottomRight = worldToScreen(camera, rect.maxX, rect.minY, 800, 600);
    expect(topLeft.x).toBeGreaterThan(0);
    expect(topLeft.y).toBeGreaterThan(0);
    expect(bottomRight.x).toBeLessThan(800);
    expect(bottomRight.y).toBeLessThan(600);
  });

  it("fits a rectangle flat on one axis and keeps the default zoom for a point", () => {
    expect(fitBounds({ minX: 0, minY: 0, maxX: 10, maxY: 0 }, 800, 600, 0).scale).toBeCloseTo(80);
    expect(fitBounds({ minX: 5, minY: 5, maxX: 5, maxY: 5 }, 800, 600)).toEqual({ x: 5, y: 5, scale: createCamera().scale });
  });
});
