import { createCamera, type Camera } from "./renderer/camera";
import type { DrawEntity } from "./schema/types";

/** Frame and camera live outside Vue reactivity (web-client-spec §3.1). */
export const viewState: {
  camera: Camera;
  entities: DrawEntity[];
  selectedId: number | null;
} = {
  camera: createCamera(),
  entities: [],
  selectedId: null,
};
