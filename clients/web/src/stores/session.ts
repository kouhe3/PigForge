import { defineStore } from "pinia";
import { ref } from "vue";
import type { DrawEntity, PartContentDocument, ReplayDocument, ReplayEvent } from "@/schema/types";
import { validatePartContent } from "@/schema/validateContent";
import { validateReplay } from "@/schema/validateReplay";
import { toDrawEntities } from "@/schema/toDrawEntities";
import { SNAPSHOT_BUILDING_PHASE } from "@/schema/decodeSnapshot";
import { createRestYawTracker } from "@/live/restYaw";
import { viewState } from "@/viewState";

export type ClientMode = "idle" | "replay" | "live";

export const useSessionStore = defineStore("session", () => {
  const mode = ref<ClientMode>("idle");
  const errors = ref<string[]>([]);
  const content = ref<PartContentDocument | null>(null);
  const replay = ref<ReplayDocument | null>(null);
  const tick = ref(1);
  const playing = ref(false);
  const speed = ref(1);
  const liveUrl = ref("ws://127.0.0.1:5088/play");
  const liveTick = ref(0);
  const livePhase = ref(0);
  const events = ref<ReplayEvent[]>([]);
  // Build orientations, so a rolling wheel's non-spinning sprites stay put (see the tracker).
  const restYaw = createRestYawTracker();

  function loadContent(value: unknown): boolean {
    const nextErrors = validatePartContent(value);
    if (nextErrors.length > 0) {
      errors.value = nextErrors;
      content.value = null;
      return false;
    }
    content.value = value as PartContentDocument;
    errors.value = [];
    return true;
  }

  function loadReplay(value: unknown): boolean {
    const nextErrors = validateReplay(value);
    if (nextErrors.length > 0) {
      errors.value = nextErrors;
      replay.value = null;
      return false;
    }
    const document = value as ReplayDocument;
    replay.value = document;
    mode.value = "replay";
    tick.value = 1;
    applyReplayTick(1);
    errors.value = [];
    return true;
  }

  function applyReplayTick(next: number): void {
    const document = replay.value;
    if (!document) {
      return;
    }
    const frame = document.frames[next - 1] ?? document.frames[0];
    tick.value = frame.tick;
    events.value = frame.events;
    // A replay has recorded bodies only, so the tracker falls back to each entity's first pose.
    viewState.entities = restYaw.apply(toDrawEntities(frame.snapshots), false);
  }

  function applyLiveEntities(nextTick: number, entities: DrawEntity[], phase = 0): void {
    mode.value = "live";
    liveTick.value = nextTick;
    livePhase.value = phase;
    tick.value = nextTick;
    viewState.entities = restYaw.apply(entities, phase === SNAPSHOT_BUILDING_PHASE);
  }

  function setErrors(next: string[]): void {
    errors.value = next;
  }

  return {
    mode,
    errors,
    content,
    replay,
    tick,
    playing,
    speed,
    liveUrl,
    liveTick,
    livePhase,
    events,
    loadContent,
    loadReplay,
    applyReplayTick,
    applyLiveEntities,
    setErrors,
  };
});
