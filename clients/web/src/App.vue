<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from "vue";
import { attachCanvasGestures } from "./gesture/canvasGestures";
import { connectSnapshotSocket } from "./live/snapshotSocket";
import { createPlaybackClock, type PlaybackClock } from "./playback/clock";
import { drawFrame } from "./renderer/draw";
import { useSessionStore } from "./stores/session";
import { viewState } from "./viewState";

const session = useSessionStore();
const canvas = ref<HTMLCanvasElement | null>(null);
const entitiesRef = { current: viewState.entities };
let clock: PlaybackClock | null = null;
let detachGestures: (() => void) | null = null;
let disconnectLive: (() => void) | null = null;
let raf = 0;

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
  entitiesRef.current = viewState.entities;
  drawFrame(ctx, viewState.camera, viewState.entities, session.content, viewState.selectedId);
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
  session.setErrors([]);
  disconnectLive = connectSnapshotSocket(
    session.liveUrl,
    (snapshot) => session.applyLiveEntities(snapshot.tick, snapshot.entities),
    (message) => session.setErrors([message]),
  );
}

function selectedEntity() {
  const id = viewState.selectedId;
  return viewState.entities.find((entity) => entity.entityId === id) ?? null;
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
  const node = canvas.value;
  if (node) {
    detachGestures = attachCanvasGestures(node, viewState.camera, entitiesRef, (message) => {
      if (message.kind === "SelectEntity") {
        viewState.selectedId = message.entityId;
      }
    });
  }
  raf = requestAnimationFrame(paint);
});

onUnmounted(() => {
  cancelAnimationFrame(raf);
  clock?.dispose();
  detachGestures?.();
  disconnectLive?.();
});
</script>

<template>
  <div class="layout">
    <header class="toolbar">
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
      <label>
        实时快照
        <input v-model="session.liveUrl" aria-label="快照 WebSocket 地址" />
      </label>
      <button type="button" @click="connectLive">连接房间</button>
      <span class="meta">模式 {{ session.mode }} · tick {{ session.tick }}</span>
    </header>
    <main class="stage">
      <canvas ref="canvas" role="img" aria-label="物理快照画布，拖拽平移，滚轮缩放"></canvas>
    </main>
    <aside class="side">
      <h1>检查器</h1>
      <p v-if="!selectedEntity()" class="meta">点击实体查看位姿。客户端不提交位置、断裂或胜负。</p>
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
      <h2 v-if="session.errors.length">加载错误</h2>
      <ul v-if="session.errors.length" class="errors" role="alert">
        <li v-for="(error, index) in session.errors" :key="index">{{ error }}</li>
      </ul>
    </aside>
    <footer class="timeline">
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
