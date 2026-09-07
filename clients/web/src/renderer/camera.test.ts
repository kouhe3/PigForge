import { describe, expect, it } from "vitest";
import { createCamera, screenToWorld, worldToScreen, yawFromQuaternion } from "./camera";

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
