<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, watch } from "vue";
import { GOAL_ZONE, MAP_BOUNDS, PALETTE, PLAY_PARTS } from "./builder/slope";
import { MOVE_SNAP, TOOLS, type ToolId, toolByHotkey } from "./editor/tools";
import { attachCanvasGestures } from "./gesture/canvasGestures";
import { connectPlaySocket } from "./live/playSocket";
import { connectSnapshotSocket } from "./live/snapshotSocket";
import { createPlayerSession, type CommandKind } from "./live/playerSession";
import { createPlaybackClock, type PlaybackClock } from "./playback/clock";
import { loadPartTextures, type PartTextureSet } from "./renderer/atlas";
import { drawFrame } from "./renderer/draw";
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
const selectedPart = ref(4);
const tool = ref<ToolId>("place");
const activeTab = ref<"replay" | "live">("replay");
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
let raf = 0;

const canEdit = computed(() => playerPhase.value === "editing");
const canStart = computed(() => canEdit.value && ownCount.value > 0);
const canReset = computed(() => playerPhase.value === "materialized" || ownCount.value > 0);
const canPlace = computed(() => activeTab.value === "live" && session.mode === "live" && canEdit.value);
const phaseLabel = computed(() => (playerPhase.value === "materialized" ? "运行中" : "编辑中"));
const toolLabel = computed(() => TOOLS.find((entry) => entry.id === tool.value)?.label ?? "放置");

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
  tool.value = "place";
}

function startSimulation(): void {
  dispatch((sequence) => ({ kind: 3, sequence, playerId: 0, tick: 0 }), 3);
}

function resetSimulation(): void {
  dispatch((sequence) => ({ kind: 5, sequence, playerId: 0, tick: session.liveTick }), 5);
}

function placePart(x: number, y: number): void {
  dispatch(
    (sequence) => ({
      kind: 0,
      sequence,
      playerId: 0,
      tick: 0,
      partTypeId: selectedPart.value,
      x,
      y,
      angle: placeAngle.value,
      scale: placeScale.value,
    }),
    0,
  );
}

/** PGFC kind 6: reposition a placed preview part (world metres). */
function movePart(entityId: number, x: number, y: number): void {
  dispatch((sequence) => ({ kind: 6, sequence, playerId: 0, tick: 0, entityId, x, y }), 6, entityId);
}

/** PGFC kind 2: rotate a placed preview part to an absolute yaw (radians). */
function rotateToAngle(entityId: number, angle: number): void {
  dispatch((sequence) => ({ kind: 2, sequence, playerId: 0, tick: 0, entityId, angle }), 2, entityId);
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

function paint(): void {
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
  // The sandbox has no goal semantics, so the live view omits the local GOAL_ZONE.
  drawFrame(
    ctx,
    viewState.camera,
    entities,
    session.content,
    viewState.selectedId,
    activeTab.value === "live" ? undefined : GOAL_ZONE,
    MAP_BOUNDS,
    partTextures.value,
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
  disconnectLive?.();
  sendCommand = null;
  session.setErrors([]);
  session.loadContent(PLAY_PARTS);
  player.reset();
  syncPlayer();
  if (session.liveUrl.includes("/snapshots")) {
    disconnectLive = connectSnapshotSocket(
      session.liveUrl,
      (snapshot) => session.applyLiveEntities(snapshot.tick, snapshot.entities, snapshot.phase),
      (message) => session.setErrors([message]),
    );
    return;
  }
  const play = connectPlaySocket(
    session.liveUrl,
    (snapshot) => session.applyLiveEntities(snapshot.tick, snapshot.entities, snapshot.phase),
    (ack) => {
      const error = player.applyAck(ack);
      syncPlayer();
      if (error !== null) {
        session.setErrors([error]);
      }
    },
    (message) => session.setErrors([message]),
    () => {
      player.reset();
      syncPlayer();
    },
  );
  sendCommand = play.send;
  disconnectLive = play.close;
}

function selectedEntity() {
  const id = viewState.selectedId;
  return viewState.entities.find((entity) => entity.entityId === id) ?? null;
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
    const hotkeyTool = toolByHotkey(event.key);
    if (hotkeyTool !== null) {
      tool.value = hotkeyTool;
      return;
    }
    const entity = selectedEntity();
    if (tool.value === "move" && entity && canEdit.value && player.ownEntityIds.has(entity.entityId)) {
      const dx = event.key === "ArrowLeft" ? -MOVE_SNAP : event.key === "ArrowRight" ? MOVE_SNAP : 0;
      const dy = event.key === "ArrowUp" ? MOVE_SNAP : event.key === "ArrowDown" ? -MOVE_SNAP : 0;
      if (dx !== 0 || dy !== 0) {
        event.preventDefault();
        movePart(entity.entityId, entity.x + dx, entity.y + dy);
        return;
      }
    }
  }
  if (event.key === "q" || event.key === "Q") {
    placeAngle.value -= Math.PI / 12;
  } else if (event.key === "e" || event.key === "E") {
    placeAngle.value += Math.PI / 12;
  } else if (event.key === "r" || event.key === "R") {
    const entity = selectedEntity();
    if (entity && canEdit.value && player.ownEntityIds.has(entity.entityId)) {
      // Rotate the selected part a visible increment; a fresh placeAngle of 0 would
      // produce no visible change, so accumulate from the part's current yaw.
      rotateToAngle(entity.entityId, entity.yaw + Math.PI / 12);
    }
  } else if (event.key === "Delete" || event.key === "Backspace") {
    const entity = selectedEntity();
    if (entity && canEdit.value && player.ownEntityIds.has(entity.entityId)) {
      removePart(entity);
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

onMounted(() => {
  session.loadContent(PLAY_PARTS);
  void loadPartTextures().then((textures) => {
    partTextures.value = textures;
  });
  const node = canvas.value;
  if (node) {
    detachGestures = attachCanvasGestures(
      node,
      viewState.camera,
      entitiesRef,
      (message) => {
        if (message.kind === "SelectEntity") {
          viewState.selectedId = message.entityId;
        } else if (message.kind === "PlaceRequested" && canPlace.value) {
          placePart(message.x, message.y);
        } else if (message.kind === "PartScaleChanged") {
          placeScale.value = Math.min(4, Math.max(0.25, placeScale.value * message.scale));
        } else if (message.kind === "ToolPreview") {
          viewState.preview = message.preview;
        } else if (message.kind === "MoveRequested") {
          movePart(message.entityId, message.x, message.y);
        } else if (message.kind === "RotateRequested") {
          rotateToAngle(message.entityId, message.angle);
        } else if (message.kind === "ScaleRequested") {
          scalePart(message.entityId, message.scale);
        }
      },
      {
        tool: () => tool.value,
        canPlace: () => canPlace.value,
        isEditable: (entityId) => canEdit.value && player.ownEntityIds.has(entityId),
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
        <span class="meta">{{ toolLabel }} · {{ phaseLabel }} · tick {{ session.tick }} · 角 {{ placeAngle.toFixed(2) }} · 缩放 {{ placeScale.toFixed(2) }}</span>
      </template>
    </header>

    <main class="stage">
      <canvas
        ref="canvas"
        role="img"
        :class="activeTab === 'live' ? 'tool-' + tool : undefined"
        aria-label="物理快照画布：拖拽平移，滚轮缩放，工具 1-5 切换放置/选择/移动/旋转/缩放"
      ></canvas>
    </main>

    <aside class="side">
      <template v-if="activeTab === 'live'">
        <h1>零件</h1>
        <p class="meta">工具 1–5：放置/选择/移动/旋转/缩放。拖动选中零件变换，Alt 不吸附，方向键微调移动。Q/E 放置角，Alt+滚轮放置缩放，R 旋转，Delete 删除。不提交位姿。</p>
        <div class="palette">
          <button
            v-for="part in PALETTE"
            :key="part.partTypeId"
            type="button"
            :class="{ selected: selectedPart === part.partTypeId }"
            @click="selectPalettePart(part.partTypeId)"
          >{{ part.label }}</button>
        </div>
      </template>

      <h1>检查器</h1>
      <p v-if="!selectedEntity()" class="meta">点击实体查看位姿。</p>
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
