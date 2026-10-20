<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { devPageUrl, parseDevQuery, type DevWorldSelection } from "@/live/devWorld";

interface ScenarioRow {
  id: string;
  name: string;
  description: string;
  level: string;
}

interface LevelRow {
  path: string;
  area: string;
  name: string;
}

const scenarios = ref<ScenarioRow[]>([]);
const levels = ref<LevelRow[]>([]);
const worldErrors = ref<string[]>([]);
const loadError = ref("");
const state = parseDevQuery(typeof window === "undefined" ? "" : window.location.search);
const current = computed(() =>
  state.scenario !== undefined ? `scenario:${state.scenario}` : state.level !== undefined ? `level:${state.level}` : "",
);
const levelGroups = computed(() => {
  const groups = new Map<string, LevelRow[]>();
  for (const level of levels.value) {
    const key = level.area.length > 0 ? level.area : "content/levels";
    const list = groups.get(key) ?? [];
    list.push(level);
    groups.set(key, list);
  }

  return [...groups.entries()].sort((left, right) => left[0].localeCompare(right[0]));
});

function reloadPage(): void {
  window.location.reload();
}

function onSelect(event: Event): void {
  navigate((event.target as HTMLSelectElement).value);
}

function navigate(value: string): void {
  let selection: DevWorldSelection = {};
  if (value.startsWith("scenario:")) {
    selection = { scenario: value.slice("scenario:".length) };
  } else if (value.startsWith("level:")) {
    selection = { level: value.slice("level:".length) };
  }

  window.location.href = devPageUrl(window.location.search, selection);
}

async function refresh(): Promise<void> {
  try {
    const response = await fetch("/dev/worlds", { headers: { accept: "application/json" } });
    if (!response.ok) {
      throw new Error(`/dev/worlds returned ${response.status}`);
    }

    const body = (await response.json()) as { scenarios: ScenarioRow[]; levels: LevelRow[]; errors: string[] };
    scenarios.value = body.scenarios;
    levels.value = body.levels;
    worldErrors.value = body.errors;
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : String(error);
  }
}

onMounted(refresh);
</script>

<template>
  <aside class="dev-panel" aria-label="开发世界">
    <header>
      <strong>开发世界</strong>
      <button type="button" title="重新加载本场景" @click="reloadPage">重开</button>
    </header>
    <select aria-label="选择场景或关卡" :value="current" @change="onSelect">
      <option value="">沙盒（terrain-v1）</option>
      <optgroup label="场景">
        <option v-for="scenario in scenarios" :key="scenario.id" :value="`scenario:${scenario.id}`">
          {{ scenario.name }}（{{ scenario.id }}）
        </option>
      </optgroup>
      <optgroup v-for="[area, group] in levelGroups" :key="area" :label="area">
        <option v-for="level in group" :key="level.path" :value="`level:${level.path}`">
          {{ level.name }}
        </option>
      </optgroup>
    </select>
    <p v-if="loadError" class="error">{{ loadError }}</p>
    <p v-for="error in worldErrors" :key="error" class="error">{{ error }}</p>
    <p class="hint">切换会整页重载；普通模式不带 ?dev=1。</p>
  </aside>
</template>

<style scoped>
.dev-panel {
  position: fixed;
  right: 12px;
  bottom: 12px;
  z-index: 40;
  width: 280px;
  padding: 10px 12px;
  border: 1px solid var(--border, #444);
  border-radius: 8px;
  background: var(--surface, rgba(20, 20, 20, 0.92));
  color: var(--fg, #eee);
  font-size: 12px;
  display: grid;
  gap: 8px;
}

.dev-panel header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.dev-panel select {
  width: 100%;
}

.dev-panel .error {
  margin: 0;
  color: var(--error, #f66);
  white-space: pre-wrap;
}

.dev-panel .hint {
  margin: 0;
  color: var(--muted, #999);
}
</style>
