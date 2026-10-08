<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, watch } from "vue";
import { variantLabel, variantsOf } from "./builder/palette";
import { GOAL_ZONE, MAP_BOUNDS, PALETTE, PLAY_PARTS } from "./builder/slope";
import { MOVE_SNAP, TOOLS, type ToolId, placePose, toolByHotkey } from "./editor/tools";
import { attachCanvasGestures } from "./gesture/canvasGestures";
import { gadgetGroups, type GadgetGroup } from "./live/gadgets";
import { connectPlaySocket } from "./live/playSocket";
import { connectSnapshotSocket } from "./live/snapshotSocket";
import { createPlayerSession, type CommandKind } from "./live/playerSession";
import { createPlaybackClock, type PlaybackClock } from "./playback/clock";
import { createAnimationState, noteActivationEdges, resetAnimations, updateAnimations } from "./renderer/animation";
import { createAnimationClock } from "./renderer/animation/clock";
import { loadPartTextures, type PartTextureSet } from "./renderer/atlas";
import { partThumbnailDataUrl } from "./renderer/thumbnails";
import { drawFrame } from "./renderer/draw";
import { fitBounds } from "./renderer/camera";
import { groundTextureNames, loadGroundTextures, type GroundTextureSet } from "./renderer/terrain";
import { curveTextureNames } from "./renderer/terrainCurve";
import { cameraLimitsRect, zoneRect } from "./schema/levelContent";
import type { ClientCommand, DrawEntity } from "./schema/types";
import { useSessionStore } from "./stores/session";
import { viewState } from "./viewState";

const session = useSessionStore();
const canvas = ref<HTMLCanvasElement | null>(null);
// viewState.entities is replaced wholesale on every snapshot; the getter keeps gesture
// hit-testing pointed at the live array instead of a captured (stale) reference.
const entitiesRef = {
  get current(): readonly DrawEntity[] {
    return viewState.entities;
  },
};
const placeAngle = ref(0);
const placeScale = ref(1);
/** The pending placement's handedness (ADR-030); only a content part with `mirror` accepts it. */
const placeMirrored = ref(false);
const selectedPart = ref(4);
const tool = ref<ToolId>("place");
const activeTab = ref<"replay" | "live">("replay");
// Bumped on every snapshot so the switch bar recomputes (viewState is not reactive).
const liveFrame = ref(0);
// Selection count mirrored into a ref so the inspector updates without a snapshot.
const selectedCount = ref(0);
// The session module is Vue-free; mirror the gating inputs it exposes into refs.
const player = createPlayerSession();
const playerPhase = ref(player.phase);
const ownCount = ref(player.ownEntityIds.size);
let clock: PlaybackClock | null = null;
let detachGestures: (() => void) | null = null;
let disconnectLive: (() => void) | null = null;
let sendCommand: ((command: ClientCommand) => void) | null = null;
// Original-art sprite manifest: optional, absent in a clean checkout.
const partTextures = shallowRef<PartTextureSet | null>(null);
// The level's own ground textures: original art too, absent until `build-levels.mjs` ran. A terrain
// whose texture is missing keeps the flat ground colour (`drawTerrain`), so this is never fatal.
const groundTextures = shallowRef<GroundTextureSet | null>(null);
// Animation state and its wall clock; both stay outside Vue reactivity like the view state.
const animations = createAnimationState();
const animationClock = createAnimationClock();
let animationsRunning = false;
// Palette/gadget button icons, cached per partTypeId; empty strings never enter the map.
const partIcons = new Map<number, string | null>();

/** Original-art icon for a part button, or null to fall back to its text label. */
function partThumb(partTypeId: number): string | null {
  const textures = partTextures.value;
  if (textures === null) {
    return null;
  }

  if (!partIcons.has(partTypeId)) {
    partIcons.set(partTypeId, partThumbnailDataUrl(textures, partTypeId));
  }

  return partIcons.get(partTypeId) ?? null;
}

let raf = 0;

const canEdit = computed(() => playerPhase.value === "editing");
const canStart = computed(() => canEdit.value && ownCount.value > 0);
const canReset = computed(() => playerPhase.value === "materialized" || ownCount.value > 0);
const canPlace = computed(() => activeTab.value === "live" && session.mode === "live" && canEdit.value);
const phaseLabel = computed(() => (playerPhase.value === "materialized" ? "运行中" : "编辑中"));
const toolLabel = computed(() => TOOLS.find((entry) => entry.id === tool.value)?.label ?? "放置");

/** Switch-bar groups: own, materialised, switchable parts grouped by type. */
const gadgets = computed<GadgetGroup[]>(() => {
  void liveFrame.value;
  void ownCount.value;
  return gadgetGroups(viewState.entities, player.ownEntityIds, session.content);
});
const canSwitch = computed(() => activeTab.value === "live" && playerPhase.value === "materialized" && gadgets.value.length > 0);

/** The base part of the current selection: variant rows hang off their base entry. */
const selectedBaseId = computed(() => {
  const part = session.content?.parts.find((entry) => entry.partTypeId === selectedPart.value);
  return part?.variantOf ?? selectedPart.value;
});

/** Part lookup for the move-drag contact snap; rebuilt when content arrives. */
const partById = computed(() => new Map((session.content?.parts ?? []).map((part) => [part.partTypeId, part])));

function syncPlayer(): void {
  playerPhase.value = player.phase;
  ownCount.value = player.ownEntityIds.size;
}

/** Records the command for its PGFA, then sends it; the server owns identity and validation. */
function dispatch(build: (sequence: number) => ClientCommand, kind: CommandKind, entityId?: number): void {
  if (!sendCommand) {
    return;
  }
  sendCommand(build(player.noteSent(kind, entityId)));
}

/** Picking a part starts placement, like a Besiege block pick. */
function selectPalettePart(partTypeId: number): void {
  selectedPart.value = partTypeId;
  // A different part may not declare the mirror at all, and the server refuses a mirrored part
  // that has no `capabilities.mirror` (ADR-030): arming a part resets the pending handedness.
  placeMirrored.value = false;
  tool.value = "place";
}

function startSimulation(): void {
  dispatch((sequence) => ({ kind: 3, sequence, playerId: 0, tick: 0 }), 3);
}

function resetSimulation(): void {
  dispatch((sequence) => ({ kind: 5, sequence, playerId: 0, tick: session.liveTick }), 5);
}

/**
 * PGFC kind 0. A click carries no drag to align with, so placement snaps on the client: both
 * axes to the 0.5 grid, then flush against a neighbouring part the click points at (flush
 * contact is what makes the server connect two parts). The server still validates the command
 * and owns identity, geometry and the phase.
 */
function placePart(x: number, y: number): void {
  const pose = { x, y, yaw: placeAngle.value, scale: placeScale.value };
  const snapped = placePose(pose);
  dispatch(
    (sequence) => ({
      kind: 0,
      sequence,
      playerId: 0,
      tick: 0,
      partTypeId: selectedPart.value,
      x: snapped.x,
      y: snapped.y,
      angle: placeAngle.value,
      scale: placeScale.value,
      mirrored: placeMirrored.value,
    }),
    0,
  );
}

/** PGFC kind 6: reposition a placed preview part (world metres). */
function movePart(entityId: number, x: number, y: number): void {
  dispatch((sequence) => ({ kind: 6, sequence, playerId: 0, tick: 0, entityId, x, y }), 6, entityId);
}

/**
 * PGFC kind 2: rotate a placed preview part to an absolute yaw (radians) and handedness. Both
 * fields are absolute (ADR-030), so every rotate carries the mirror the client is showing --
 * a rotate that sent `mirrored: false` would silently unmirror the part.
 */
function rotateToAngle(entityId: number, angle: number, mirrored: boolean): void {
  dispatch((sequence) => ({ kind: 2, sequence, playerId: 0, tick: 0, entityId, angle, mirrored }), 2, entityId);
}

/** True when the content gives this part the original's FlipVertically handedness (ADR-030). */
function canMirror(entity: DrawEntity): boolean {
  return partById.value.get(entity.partTypeId)?.capabilities?.mirror === true;
}

/** PGFC kind 7: rescale a placed preview part (0.25–4). */
function scalePart(entityId: number, scale: number): void {
  dispatch((sequence) => ({ kind: 7, sequence, playerId: 0, tick: 0, entityId, scale }), 7, entityId);
}

function removePart(entity: DrawEntity): void {
  dispatch(
    (sequence) => ({ kind: 1, sequence, playerId: 0, tick: 0, entityId: entity.entityId }),
    1,
    entity.entityId,
  );
}

/**
 * Selected entities this client may edit: own, Editing phase, ascending by id. Batch
 * actions send one command per part; the server validates each and nothing rolls back.
 */
function editableSelectedEntities(): DrawEntity[] {
  if (!canEdit.value) {
    return [];
  }

  return viewState.entities
    .filter((entity) => viewState.selectedIds.includes(entity.entityId) && player.ownEntityIds.has(entity.entityId))
    .sort((left, right) => left.entityId - right.entityId);
}

/** PGFC kind 9: toggles every switchable part of one type this player owns. */
function toggleGadget(group: GadgetGroup): void {
  const active = group.kind === "trigger" ? true : !group.active;
  // One command per part of the group, not one per part type: the bar's group is (type, effect
  // direction), the original's own key (`Contraption.ActivatePartType(type, direction)`,
  // Contraption.cs:955-1021), so two fans aimed opposite ways are two buttons. The server still
  // validates each part's ownership and switchability on its own.
  for (const entityId of group.entityIds) {
    dispatch((sequence) => ({ kind: 8, sequence, playerId: 0, tick: 0, entityId, active }), 8, entityId);
  }
}

/** PGFC kind 8: in play mode a tap on an own switchable part flips its switch. */
function togglePartFromCanvas(entityId: number): void {
  if (!canSwitch.value || !player.ownEntityIds.has(entityId)) {
    return;
  }

  const entity = viewState.entities.find((entry) => entry.entityId === entityId);
  const kind = entity === undefined
    ? undefined
    : session.content?.parts.find((part) => part.partTypeId === entity.partTypeId)?.capabilities?.activation;
  if (entity === undefined || (kind !== "toggle" && kind !== "trigger")) {
    return;
  }

  const active = kind === "trigger" ? true : !entity.active;
  dispatch((sequence) => ({ kind: 8, sequence, playerId: 0, tick: 0, entityId, active }), 8, entityId);
}

function paint(now: number): void {
  const node = canvas.value;
  if (!node) {
    return;
  }
  const ctx = node.getContext("2d");
  if (!ctx) {
    return;
  }
  const ratio = window.devicePixelRatio || 1;
  const width = node.clientWidth;
  const height = node.clientHeight;
  if (node.width !== Math.floor(width * ratio) || node.height !== Math.floor(height * ratio)) {
    node.width = Math.floor(width * ratio);
    node.height = Math.floor(height * ratio);
  }
  ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
  // A tool drag previews locally: draw the entity at its candidate pose in the ghost
  // style (bodyId 0) without mutating the authoritative snapshot array.
  const preview = viewState.preview;
  const entities = preview
    ? viewState.entities.map((entity) =>
        entity.entityId === preview.entityId
          ? { ...entity, x: preview.x, y: preview.y, yaw: preview.yaw, scale: preview.scale, bodyId: 0 }
          : entity,
      )
    : viewState.entities;
  // Animation is presentation only: it advances while the view shows running physics — a
  // materialized sandbox build or a playing replay — and freezes everywhere else (build mode,
  // pause, single stepping, a part being dragged). The clock gates on the same flag, and the
  // rising edge clears every phase, so Start/RESET and replay play restart from zero.
  const running = activeTab.value === "live" ? playerPhase.value === "materialized" : clock !== null && clock.playing;
  const dt = animationClock.step(now, running);
  if (running && !animationsRunning) {
    resetAnimations(animations);
}
  animationsRunning = running;
  updateAnimations(animations, entities, session.content, partTextures.value, dt);
  // The loaded level owns the goal zone and the bounds; the builder's constants are only the
  // fallback before a level arrives (and the replay viewer's goal marker, which no level feeds).
  const level = session.level;
  drawFrame(
    ctx,
    viewState.camera,
    entities,
    session.content,
    viewState.selectedIds,
    level ? zoneRect(level.goalZone) : activeTab.value === "live" ? undefined : GOAL_ZONE,
    level ? zoneRect(level.bounds) : MAP_BOUNDS,
    partTextures.value,
    viewState.marquee,
    animations,
    level?.terrain ?? null,
    groundTextures.value,
  );
  raf = requestAnimationFrame(paint);
}

async function readJson(file: File): Promise<unknown> {
  return JSON.parse(await file.text()) as unknown;
}

async function onContent(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) {
    return;
  }
  session.loadContent(await readJson(file));
}

async function onReplay(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) {
    return;
  }
  if (session.loadReplay(await readJson(file))) {
    rebuildClock();
  }
}

function rebuildClock(): void {
  clock?.dispose();
  const document = session.replay;
  if (!document) {
    return;
  }
  clock = createPlaybackClock(document.header.simulationTicks, document.header.fixedTickRate, (tick) => {
    session.applyReplayTick(tick);
  });
  clock.speed = session.speed;
}

function togglePlay(): void {
  if (!clock) {
    return;
  }
  if (clock.playing) {
    clock.pause();
    session.playing = false;
  } else {
    clock.speed = session.speed;
    clock.play();
    session.playing = true;
  }
}

function step(delta: number): void {
  clock?.step(delta);
}

function connectLive(): void {
  const snapshotOnly = session.liveUrl.includes("/snapshots");
  const url = session.liveUrl;
  disconnectLive?.();
  sendCommand = null;
  session.setErrors([]);
  session.loadContent(PLAY_PARTS);
  // The level document lives on the same server as the socket: re-read it on every (re)connect,
  // and let a missing one fall back to the builder's own zone and bounds.
  void session.loadLiveLevel();
  // A player's life is its socket: the server drops a departed player's parts, so every
  // connection starts from an empty building plane and the local identity goes with it.
  player.reset();
  syncPlayer();
  if (snapshotOnly) {
    disconnectLive = connectSnapshotSocket(
      url,
      (frame) => {
        noteActivationEdges(animations, frame.entities, partTextures.value);
        session.applyLiveEntities(frame.tick, frame.entities, frame.phase);
        liveFrame.value += 1;
      },
      (message) => session.setErrors([message]),
    );
    return;
  }
  const play = connectPlaySocket(
    url,
    (snapshot) => {
      // Every decoded snapshot is inspected for a switch edge: a trigger part spends its press in
      // one tick, so a frame-rate reader would drop the edge as soon as two snapshots land between
      // two paints (see `renderer/animation/activation.ts`).
      noteActivationEdges(animations, snapshot.entities, partTextures.value);
      session.applyLiveEntities(snapshot.tick, snapshot.entities, snapshot.phase);
      liveFrame.value += 1;
    },
    (ack) => {
      const error = player.applyAck(ack);
      syncPlayer();
      if (error !== null) {
        session.setErrors([error]);
      }
    },
    (message) => session.setErrors([message]),
    () => {
      // The socket went away and so did the player: the server cleared its parts, so the
      // ownership list is stale -- drop it and 连接房间 starts a fresh player.
      player.reset();
      syncPlayer();
    },
  );
  sendCommand = play.send;
  disconnectLive = play.close;
}

function selectedEntity() {
  const id = viewState.selectedIds[0];
  return viewState.entities.find((entity) => entity.entityId === id) ?? null;
}

/** Applies a gesture selection message; the primary is the first id. */
function applySelection(entityIds: number[], mode: "replace" | "add" | "toggle"): void {
  if (mode === "replace") {
    viewState.selectedIds = [...entityIds];
  } else if (mode === "add") {
    const merged = new Set(viewState.selectedIds);
    for (const entityId of entityIds) {
      merged.add(entityId);
    }
    viewState.selectedIds = [...merged];
  } else {
    const next = new Set(viewState.selectedIds);
    for (const entityId of entityIds) {
      if (next.has(entityId)) {
        next.delete(entityId);
      } else {
        next.add(entityId);
      }
    }
    viewState.selectedIds = [...next];
  }

  selectedCount.value = viewState.selectedIds.length;
}

function onKey(event: KeyboardEvent): void {
  // Never steal keys from the toolbar's inputs/selects.
  const target = event.target;
  if (
    target instanceof HTMLInputElement ||
    target instanceof HTMLSelectElement ||
    target instanceof HTMLTextAreaElement ||
    (target instanceof HTMLElement && target.isContentEditable)
  ) {
    return;
  }
  if (activeTab.value === "live") {
    if (playerPhase.value === "materialized") {
      const key = event.key.length === 1 ? event.key.toUpperCase() : event.key;
      const group = gadgets.value.find((entry) => entry.hotkey === key);
      if (group !== undefined) {
        event.preventDefault();
        toggleGadget(group);
        return;
      }
    }
    const hotkeyTool = toolByHotkey(event.key);
    if (hotkeyTool !== null) {
      tool.value = hotkeyTool;
      return;
    }
    if (tool.value === "move") {
      const dx = event.key === "ArrowLeft" ? -MOVE_SNAP : event.key === "ArrowRight" ? MOVE_SNAP : 0;
      const dy = event.key === "ArrowUp" ? MOVE_SNAP : event.key === "ArrowDown" ? -MOVE_SNAP : 0;
      const targets = dx !== 0 || dy !== 0 ? editableSelectedEntities() : [];
      if (targets.length > 0) {
        event.preventDefault();
        for (const target of targets) {
          movePart(target.entityId, target.x + dx, target.y + dy);
        }
        return;
      }
    }
  }
  if (event.key === "q" || event.key === "Q") {
    placeAngle.value -= Math.PI / 12;
  } else if (event.key === "e" || event.key === "E") {
    placeAngle.value += Math.PI / 12;
  } else if (event.key === "r" || event.key === "R") {
    // Rotate every selected part a visible increment; a fresh placeAngle of 0 would
    // produce no visible change, so accumulate from each part's current yaw.
    for (const target of editableSelectedEntities()) {
      rotateToAngle(target.entityId, target.yaw + Math.PI / 12, target.mirrored === true);
    }
  } else if (event.key === "f" || event.key === "F") {
    // The original's `Flip`: a part whose prefab says FlipVertically toggles its handedness
    // (Contraption.cs:1918-1944). Other parts cannot be flipped at all, and a part with nothing
    // selected flips the pending placement so the next click places it mirrored.
    const targets = editableSelectedEntities().filter(canMirror);
    if (targets.length > 0) {
      for (const target of targets) {
        rotateToAngle(target.entityId, target.yaw, target.mirrored !== true);
      }
    } else {
      const armed = partById.value.get(selectedPart.value);
      if (armed?.capabilities?.mirror === true) {
        placeMirrored.value = !placeMirrored.value;
      }
    }
  } else if (event.key === "Delete" || event.key === "Backspace") {
    for (const target of editableSelectedEntities()) {
      removePart(target);
    }
  }
}

watch(
  () => session.speed,
  (speed) => {
    if (clock) {
      clock.speed = speed;
    }
  },
);

watch(canEdit, (editable) => {
  if (!editable && tool.value !== "select") {
    tool.value = "select";
  }
});

// A level frames the view the way the original's camera sees it: the level's own camera rectangle
// when it has one (v5 -- `LevelManager.m_cameraLimits`, what the original clamps its camera to),
// otherwise its terrain bounds. `viewState.camera` is the module singleton the gesture handlers hold
// by reference, so the fit copies its fields instead of replacing the object.
watch(
  () => session.level,
  (level) => {
    const node = canvas.value;
    if (level === null || node === null) {
      return;
    }
    const rect = level.cameraLimits ? cameraLimitsRect(level.cameraLimits) : zoneRect(level.bounds);
    Object.assign(viewState.camera, fitBounds(rect, node.clientWidth, node.clientHeight));
  },
);

// The level's ground and `_curve` art, fetched from this origin (original art is extracted locally,
// never committed). Missing files are dropped by the loader, and the ground and the band along each
// outline then fall back to the flat fill.
watch(
  () => session.level,
  (level) => {
    const terrains = level?.terrain ?? [];
    const names = [...groundTextureNames(terrains), ...curveTextureNames(terrains)];
    if (names.length === 0) {
      groundTextures.value = null;
      return;
    }

    void loadGroundTextures(names).then((textures) => {
      groundTextures.value = textures;
    });
  },
);

onMounted(() => {
  session.loadContent(PLAY_PARTS);
  void loadPartTextures().then((textures) => {
    partIcons.clear();
    partTextures.value = textures;
  });
  const node = canvas.value;
  if (node) {
    detachGestures = attachCanvasGestures(
      node,
      viewState.camera,
      entitiesRef,
      (message) => {
        if (message.kind === "SelectEntities") {
          applySelection(message.entityIds, message.mode);
          if (message.entityIds.length === 1 && message.mode !== "add") {
            togglePartFromCanvas(message.entityIds[0]);
          }
        } else if (message.kind === "Marquee") {
          viewState.marquee = message.rect;
        } else if (message.kind === "PlaceRequested" && canPlace.value) {
          placePart(message.x, message.y);
        } else if (message.kind === "PartScaleChanged") {
          placeScale.value = Math.min(4, Math.max(0.25, placeScale.value * message.scale));
        } else if (message.kind === "ToolPreview") {
          viewState.preview = message.preview;
        } else if (message.kind === "MoveRequested") {
          movePart(message.entityId, message.x, message.y);
        } else if (message.kind === "RotateRequested") {
          // A drag re-aims the part; its handedness is not part of the drag, so keep the mirror
          // the snapshot is showing (PGFC's rotate carries both fields, ADR-030).
          rotateToAngle(
            message.entityId,
            message.angle,
            viewState.entities.find((entity) => entity.entityId === message.entityId)?.mirrored === true,
          );
        } else if (message.kind === "ScaleRequested") {
          scalePart(message.entityId, message.scale);
        }
      },
      {
        tool: () => tool.value,
        canPlace: () => canPlace.value,
        isEditable: (entityId) => canEdit.value && player.ownEntityIds.has(entityId),
        partOf: (partTypeId) => partById.value.get(partTypeId),
        armedPart: () => selectedPart.value,
      },
    );
  }
  window.addEventListener("keydown", onKey);
  raf = requestAnimationFrame(paint);
});

onUnmounted(() => {
  cancelAnimationFrame(raf);
  clock?.dispose();
  detachGestures?.();
  disconnectLive?.();
  window.removeEventListener("keydown", onKey);
});
</script>
<template>
  <div class="layout">
    <header class="toolbar">
      <nav class="tabs" role="tablist" aria-label="视图切换">
        <button
          type="button"
          role="tab"
          :aria-selected="activeTab === 'replay'"
          :class="{ active: activeTab === 'replay' }"
          @click="activeTab = 'replay'"
        >回放查看器</button>
        <button
          type="button"
          role="tab"
          :aria-selected="activeTab === 'live'"
          :class="{ active: activeTab === 'live' }"
          @click="activeTab = 'live'"
        >建造</button>
      </nav>

      <template v-if="activeTab === 'replay'">
        <label>
          部件内容
          <input type="file" accept="application/json,.json" aria-label="加载 part-content JSON" @change="onContent" />
        </label>
        <label>
          回放
          <input type="file" accept="application/json,.json" aria-label="加载 physics-replay-v2 JSON" @change="onReplay" />
        </label>
        <button type="button" :disabled="!session.replay" @click="togglePlay">
          {{ session.playing ? "暂停" : "播放" }}
        </button>
        <button type="button" :disabled="!session.replay" @click="step(-1)">上一帧</button>
        <button type="button" :disabled="!session.replay" @click="step(1)">下一帧</button>
        <label>
          倍速
          <select v-model.number="session.speed" aria-label="播放倍速">
            <option :value="0.25">0.25×</option>
            <option :value="0.5">0.5×</option>
            <option :value="1">1×</option>
            <option :value="2">2×</option>
            <option :value="4">4×</option>
          </select>
        </label>
      </template>

      <template v-else>
        <label>
          房间
          <input v-model="session.liveUrl" aria-label="快照 WebSocket 地址" />
        </label>
        <button type="button" @click="connectLive">连接房间</button>
        <button type="button" :disabled="!canStart" @click="startSimulation">Start</button>
        <button type="button" :disabled="!canReset" @click="resetSimulation">RESET</button>
        <span class="tools" role="group" aria-label="编辑工具">
          <button
            v-for="entry in TOOLS"
            :key="entry.id"
            type="button"
            :class="{ active: tool === entry.id }"
            :aria-pressed="tool === entry.id"
            :disabled="entry.id !== 'select' && !canEdit"
            @click="tool = entry.id"
          >{{ entry.hotkey }} {{ entry.label }}</button>
        </span>
        <span class="meta">{{ toolLabel }} · {{ phaseLabel }} · tick {{ session.tick }} · 角 {{ placeAngle.toFixed(2) }} · 缩放 {{ placeScale.toFixed(2) }} · 镜像 {{ placeMirrored ? "开" : "关" }}</span>
      </template>
    </header>

    <main class="stage">
      <canvas
        ref="canvas"
        role="img"
        :class="activeTab === 'live' ? 'tool-' + tool : undefined"
        aria-label="物理快照画布：拖拽平移，滚轮缩放，工具 1-5 切换放置/选择/移动/旋转/缩放"
      ></canvas>
      <div v-if="canSwitch" class="gadget-bar" role="toolbar" aria-label="零件开关">
        <button
          v-for="group in gadgets"
          :key="group.partTypeId"
          type="button"
          class="gadget-button"
          :class="{ on: group.kind === 'toggle' && group.active, trigger: group.kind === 'trigger' }"
          :aria-pressed="group.kind === 'toggle' ? group.active : undefined"
          @click="toggleGadget(group)"
        >
          <span class="gadget-key">{{ group.hotkey }}</span>
          <img v-if="partThumb(group.partTypeId)" class="gadget-icon" :src="partThumb(group.partTypeId) ?? ''" alt="" />
          <span class="gadget-label">{{ group.label }}</span>
          <span v-if="group.count > 1" class="gadget-count">×{{ group.count }}</span>
        </button>
      </div>
    </main>

    <aside class="side">
      <template v-if="activeTab === 'live'">
        <h1>零件</h1>
        <p class="meta">工具 1–5：放置/选择/移动/旋转/缩放。拖动选中零件变换（移动默认自由，靠近零件自动贴合；Alt 吸附 0.5 网格），方向键微调移动。选择工具左键拖拽空白框选（Shift 加选），中键拖拽平移。Q/E 放置角，Alt+滚轮放置缩放，R 旋转，Delete 删除。不提交位姿。</p>
        <div class="palette">
          <template v-for="part in PALETTE" :key="part.partTypeId">
            <button
              type="button"
              :class="{ selected: selectedPart === part.partTypeId }"
              :title="part.label"
              :aria-label="part.label"
              @click="selectPalettePart(part.partTypeId)"
            >
              <img v-if="partThumb(part.partTypeId)" class="part-icon" :src="partThumb(part.partTypeId) ?? ''" alt="" />
              <span v-else>{{ part.label }}</span>
            </button>
            <div v-if="selectedBaseId === part.partTypeId && variantsOf(session.content, part.partTypeId).length" class="variants">
              <button
                v-for="(variant, index) in variantsOf(session.content, part.partTypeId)"
                :key="variant.partTypeId"
                type="button"
                :class="{ selected: selectedPart === variant.partTypeId }"
                :title="variantLabel(part.label, index + 1, variant)"
                :aria-label="variantLabel(part.label, index + 1, variant)"
                @click="selectPalettePart(variant.partTypeId)"
              >
                <img v-if="partThumb(variant.partTypeId)" class="part-icon" :src="partThumb(variant.partTypeId) ?? ''" alt="" />
                <span v-else>{{ variantLabel(part.label, index + 1, variant) }}</span>
              </button>
            </div>
          </template>
        </div>
      </template>

      <h1>检查器</h1>
      <p v-if="!selectedEntity()" class="meta">点击实体查看位姿；选择工具拖拽空白框选，Shift 加选。</p>
      <p v-else-if="selectedCount > 1" class="meta">已选中 {{ selectedCount }} 个零件，主选 #{{ selectedEntity()?.entityId }}。</p>
      <dl v-else>
        <dt>entityId</dt>
        <dd>{{ selectedEntity()?.entityId }}</dd>
        <dt>partTypeId</dt>
        <dd>{{ selectedEntity()?.partTypeId }}</dd>
        <dt>position</dt>
        <dd>{{ selectedEntity()?.x.toFixed(3) }}, {{ selectedEntity()?.y.toFixed(3) }}</dd>
        <dt>yaw</dt>
        <dd>{{ selectedEntity()?.yaw.toFixed(3) }}</dd>
        <dt>velocity</dt>
        <dd>{{ selectedEntity()?.vx.toFixed(3) }}, {{ selectedEntity()?.vy.toFixed(3) }}</dd>
      </dl>
      <h2>事件</h2>
      <ul v-if="session.events.length">
        <li v-for="(event, index) in session.events" :key="index">{{ event.kind }}</li>
      </ul>
      <p v-else class="meta">本 tick 无事件</p>
      <h2 v-if="session.errors.length">错误</h2>
      <ul v-if="session.errors.length" class="errors" role="alert">
        <li v-for="(error, index) in session.errors" :key="index">{{ error }}</li>
      </ul>
    </aside>

    <footer class="timeline" v-if="activeTab === 'replay'">
      <label>
        进度
        <input
          type="range"
          min="1"
          :max="session.replay?.header.simulationTicks ?? 1"
          :value="session.tick"
          :disabled="!session.replay"
          aria-label="回放进度"
          @input="session.applyReplayTick(Number(($event.target as HTMLInputElement).value))"
        />
      </label>
    </footer>
  </div>
</template>
