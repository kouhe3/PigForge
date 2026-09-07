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

  it("reads identity quaternion as zero yaw", () => {
    expect(yawFromQuaternion([0, 0, 0, 1])).toBeCloseTo(0);
  });
});
