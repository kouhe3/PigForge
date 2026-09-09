import { createCamera, type Camera } from "./renderer/camera";
import type { DrawEntity, MarqueeRect, ToolPreviewPose } from "./schema/types";

/** Frame, camera, selection, marquee, and the in-flight tool preview live outside Vue reactivity (web-client-spec §3.1). */
export const viewState: {
  camera: Camera;
  entities: DrawEntity[];
  /** Ordered selection; [0] is the primary entity. */
  selectedIds: number[];
  /** World-space box drawn by the select tool while dragging. */
  marquee: MarqueeRect | null;
  preview: ToolPreviewPose | null;
} = {
  camera: createCamera(),
  entities: [],
  selectedIds: [],
  marquee: null,
  preview: null,
};
