<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue";
import { GOAL_ZONE, MAP_BOUNDS, PALETTE, PLAY_PARTS } from "./builder/slope";
import { attachCanvasGestures } from "./gesture/canvasGestures";
import { connectPlaySocket } from "./live/playSocket";
import { connectSnapshotSocket } from "./live/snapshotSocket";
import { createPlaybackClock, type PlaybackClock } from "./playback/clock";
import { drawFrame } from "./renderer/draw";
import { SNAPSHOT_BUILDING_PHASE } from "./schema/decodeSnapshot";
import { useSessionStore } from "./stores/session";
import { viewState } from "./viewState";

const session = useSessionStore();
const canvas = ref<HTMLCanvasElement | null>(null);
const entitiesRef = { current: viewState.entities };
const placeAngle = ref(0);
const placeScale = ref(1);
const selectedPart = ref(4);
const activeTab = ref<"replay" | "live">("replay");
const sequence = ref(1);
let clock: PlaybackClock | null = null;
let detachGestures: (() => void) | null = null;
let disconnectLive: (() => void) | null = null;
let sendCommand: ((command: Parameters<ReturnType<typeof connectPlaySocket>["send"]>[0]) => void) | null = null;
let raf = 0;

const building = computed(() => session.mode === "live" && session.livePhase === SNAPSHOT_BUILDING_PHASE);
const outcome = computed(() => {
  if (session.livePhase === SNAPSHOT_BUILDING_PHASE) {
    return "建造";
  }
  if (session.livePhase === 1) {
    return "过关";
  }
  if (session.livePhase === 2) {
    return "失败：出界或超时";
  }
  if (session.mode === "live") {
    return "运行";
  }
  return session.mode;
});

const canRetry = computed(() => session.mode === "live" && session.liveTick > 0 && !building.value);

function retrySimulation(): void {
  sendCommand?.({ kind: 5, sequence: nextSequence(), playerId: 1, tick: session.liveTick });
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
  drawFrame(ctx, viewState.camera, viewState.entities, session.content, viewState.selectedId, GOAL_ZONE, MAP_BOUNDS);
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

function nextSequence(): number {
  const value = sequence.value;
  sequence.value = value + 1;
  return value;
}

function connectLive(): void {
  disconnectLive?.();
  sendCommand = null;
  session.setErrors([]);
  session.loadContent(PLAY_PARTS);
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
      if (ack.status !== 0) {
        session.setErrors([`命令 ${ack.sequence} 被拒绝 status=${ack.status} error=${ack.error}`]);
      }
    },
    (message) => session.setErrors([message]),
  );
  sendCommand = play.send;
  disconnectLive = play.close;
}

function startSimulation(): void {
  sendCommand?.({ kind: 3, sequence: nextSequence(), playerId: 1, tick: 0 });
}

function selectedEntity() {
  const id = viewState.selectedId;
  return viewState.entities.find((entity) => entity.entityId === id) ?? null;
}

function onKey(event: KeyboardEvent): void {
  if (event.key === "q" || event.key === "Q") {
    placeAngle.value -= Math.PI / 12;
  } else if (event.key === "e" || event.key === "E") {
    placeAngle.value += Math.PI / 12;
  } else if (event.key === "r" || event.key === "R") {
    const entity = selectedEntity();
    if (entity && building.value) {
      // Rotate the selected part a visible increment; a fresh placeAngle of 0 would
      // produce no visible change, so accumulate from the part's current yaw.
      sendCommand?.({
        kind: 2,
        sequence: nextSequence(),
        playerId: 1,
        tick: 0,
        entityId: entity.entityId,
        angle: entity.yaw + Math.PI / 12,
      });
    }
  } else if (event.key === "Delete" || event.key === "Backspace") {
    const entity = selectedEntity();
    if (entity && building.value) {
      sendCommand?.({ kind: 1, sequence: nextSequence(), playerId: 1, tick: 0, entityId: entity.entityId });
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

onMounted(() => {
  session.loadContent(PLAY_PARTS);
  const node = canvas.value;
  if (node) {
    detachGestures = attachCanvasGestures(
      node,
      viewState.camera,
      entitiesRef,
      (message) => {
        if (message.kind === "SelectEntity") {
          viewState.selectedId = message.entityId;
        }
        if (message.kind === "PlaceRequested" && building.value) {
          sendCommand?.({
            kind: 0,
            sequence: nextSequence(),
            playerId: 1,
            tick: 0,
            partTypeId: selectedPart.value,
            x: message.x,
            y: message.y,
            angle: placeAngle.value,
            scale: placeScale.value,
          });
        }
        if (message.kind === "PartScaleChanged") {
          placeScale.value = Math.min(4, Math.max(0.25, placeScale.value * message.scale));
        }
      },
      { building: () => building.value },
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
        <button type="button" :disabled="!building" @click="startSimulation">Start</button>
        <button type="button" :disabled="!canRetry" @click="retrySimulation">RETRY</button>
        <span class="meta">{{ outcome }} · tick {{ session.tick }} · 角 {{ placeAngle.toFixed(2) }} · 缩放 {{ placeScale.toFixed(2) }}</span>
      </template>
    </header>

    <main class="stage">
      <canvas ref="canvas" role="img" aria-label="物理快照画布，拖拽平移，滚轮缩放，点击放置"></canvas>
    </main>

    <aside class="side">
      <template v-if="activeTab === 'live'">
        <h1>零件</h1>
        <p class="meta">点击画布放置。Q/E 转角，Alt+滚轮缩放，R 旋转选中，Delete 删除。不提交位姿。</p>
        <div class="palette">
          <button
            v-for="part in PALETTE"
            :key="part.partTypeId"
            type="button"
            :class="{ selected: selectedPart === part.partTypeId }"
            @click="selectedPart = part.partTypeId"
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
