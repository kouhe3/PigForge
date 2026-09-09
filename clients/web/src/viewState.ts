import { createCamera, type Camera } from "./renderer/camera";
import type { DrawEntity, ToolPreviewPose } from "./schema/types";

/** Frame, camera, and the in-flight tool preview live outside Vue reactivity (web-client-spec §3.1). */
export const viewState: {
  camera: Camera;
  entities: DrawEntity[];
  selectedId: number | null;
  preview: ToolPreviewPose | null;
} = {
  camera: createCamera(),
  entities: [],
  selectedId: null,
  preview: null,
};
