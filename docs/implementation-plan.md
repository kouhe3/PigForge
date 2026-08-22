# Implementation Plan: PigForge 无 Unity 核心与物理后端

## Overview

在 .NET 10 中建立不依赖 Unity 的数据导向游戏核心、版本化回放边界和可替换物理后端。Unity 只作为离线参考运行时；MagicPhysX 作为第一生产候选后端。实现顺序以风险优先：先锁定跨运行时契约和物理生命周期，再迁移 BPLE 内容与玩法，最后加入联机传输。

## Architecture Decisions

1. **net10-first**：Core、Protocol、Physics abstractions、Server 统一 `net10.0`；不为 Unity 降级或多目标化。
2. **schema over assembly**：Unity 和服务端通过命令、快照、事件、回放 Schema 交换数据，不共享 Unity 不兼容的 .NET 程序集。
3. **semantic backend port**：物理契约表达 PigForge 需要的能力和数据，不暴露 Unity/PhysX 类型。
4. **one authority**：线上每个房间只有一个权威物理世界；Unity 只做参考和离线差异比较。
5. **ECS incrementally**：先使用稳定句柄、分离组件存储和批处理；完整 archetype ECS 与手工内存延后到 profiler 证明需要时。
6. **replay before networking**：先让相同输入可重放、可比较，再设计在线快照和预测。

## Dependency Graph

```text
Versioned command/replay schema
              ↓
Physics contract and replay harness
       ┌──────┴────────┐
       ↓               ↓
Unity reference    MagicPhysX adapter
       └──────┬────────┘
              ↓
Portable content definitions
              ↓
ECS world and gameplay rules
              ↓
Server room and fixed Tick
              ↓
Network transport and client
```

## Phase 1: Contracts and Replay

### Task 1: Define versioned physics schema

- Input: body definitions, joint definitions, commands, random seed, content version.
- Output: snapshots, contacts, break events, entity lifecycle, final result.
- Acceptance: schema includes explicit version and does not contain Unity or native pointer fields.
- Verify: serialize/deserialize round-trip tests and invalid version rejection.
- Files likely touched: `schemas/`, `src/PigForge.Protocol/`, `tests/`.

### Task 2: Implement fixed Tick replay runner

- Acceptance: same input file runs for an exact Tick count; output ordering is stable.
- Verify: run the same replay twice and compare event sequence/final hash.
- Files likely touched: `src/PigForge.Core/`, `src/PigForge.Server/`, `tests/`.

## Phase 2: Physics Backends

### Task 3: Add Unity reference adapter

- Scope: independent Unity project/assembly; no reference to net10 Core or Server.
- Acceptance: creates a ground and dynamic box, uses explicit fixed simulation, exports the shared replay format.
- Verify: Unity runtime smoke test and replay file inspection.
- Files likely touched: `unity/` and schema mapping only.

### Task 4: Add MagicPhysX backend smoke test

- Scope: Foundation, Physics, Dispatcher, Scene, Material, Box, fixed Tick, release.
- Acceptance: no Unity dependency; native resources release; missing native backend fails explicitly.
- Verify: .NET 10 integration test on Windows x64; run repeated fixed-Tick replay.
- Files likely touched: `src/PigForge.Physics.MagicPhysX/`, `tests/PigForge.Physics.Tests/`.

### Task 5: Add cross-backend differential report

- Acceptance: Unity and MagicPhysX runs compare snapshots, events, final result, and tolerance bands.
- Verify: known simple scene passes event-level comparison; differences are reported with Tick and EntityId.
- Files likely touched: `src/PigForge.Replay/` or `tests/PigForge.Replay.Tests/`.

## Phase 3: Portable Content and ECS

### Task 6: Define portable part and collision content

- Acceptance: body, shape, material, mass, inertia, connection and break definitions contain no Unity asset references.
- Verify: fixture content loads and validates before simulation.
- Files likely touched: `schemas/`, `src/PigForge.Core/`, `content/` fixtures.

### Task 7: Implement stable EntityId/component stores

- Acceptance: slot reuse increments generation; stale IDs fail; hot stores avoid per-Tick allocation.
- Verify: destruction/reuse tests, allocation benchmark, state snapshot test.
- Files likely touched: `src/PigForge.Core/`, `tests/PigForge.Core.Tests/`.

### Task 8: Migrate construction rules

- Scope: place, remove, rotate, grid occupancy, connection validity and limits.
- Acceptance: rules run without Unity or physics-native types.
- Verify: boundary and invalid-command tests plus replay fixtures.
- Files likely touched: `src/PigForge.Core/`, `tests/PigForge.Core.Tests/`.

### Task 9: Migrate runtime gameplay rules

- Scope: motors, wheels, pigs, damage, TNT, joint breaks and level completion.
- Acceptance: server rules consume physics events/snapshots and determine outcomes without renderer state.
- Verify: typical and stress replay fixtures against the selected behavior baseline.
- Files likely touched: `src/PigForge.Core/`, `tests/`, `replays/` fixtures.

## Phase 4: Headless Server

### Task 10: Implement room-owned fixed Tick loop

- Acceptance: one room owns one authoritative physics scene; input, physics and rules phases are ordered; shutdown releases resources.
- Verify: start room, run fixed Ticks, stop room, confirm no leaked handles and stable final hash.
- Files likely touched: `src/PigForge.Server/`, `src/PigForge.Physics.MagicPhysX/`, tests.

### Task 11: Add command validation and idempotence

- Acceptance: duplicate, stale, out-of-order and capability-invalid commands are rejected or handled deterministically.
- Verify: command boundary tests and replay with malformed inputs.
- Files likely touched: `src/PigForge.Protocol/`, `src/PigForge.Server/`, tests.

### Task 12: Add snapshot publication

- Acceptance: snapshots contain only server-owned state; payload size and frequency are measured; no JSON in the Tick hot path.
- Verify: snapshot byte benchmark and end-to-end local consumer test.
- Files likely touched: `src/PigForge.Protocol/`, `src/PigForge.Server/`, tests.

## Phase 5: Performance and Client

### Task 13: Establish performance baseline

- Measure typical/stress scenes: Tick p50/p95/p99, allocation, GC pauses, native memory, snapshot size, concurrent rooms.
- Acceptance: recorded baseline is reproducible and no optimization is accepted without comparison.

### Task 14: Add client adapter

- Acceptance: client consumes snapshots and renders state; it cannot authoritatively set position, contact, break or result.
- Verify: connect local client to one server room and compare displayed state to server snapshots.

## Checkpoints

### Checkpoint A: Backend proof

- [ ] Schema round-trip passes
- [ ] Replay repeats identically for the selected backend
- [ ] Unity reference and MagicPhysX smoke scenes produce comparable events

### Checkpoint B: Gameplay proof

- [ ] Portable content validates before simulation
- [ ] Construction and runtime rules run without Unity
- [ ] Typical BPLE fixture reaches the same observable result as the baseline

### Checkpoint C: Server proof

- [ ] Headless server starts without Unity
- [ ] Room fixed Tick is authoritative
- [ ] Invalid and duplicate commands are safe
- [ ] Snapshot publication is bounded and measured

## Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| MagicPhysX native version differs from Unity PhysX integration | High | Pin native version, run replay comparison, preserve behavior versions |
| Interface leaks backend-specific features | High | semantic capability contract, opaque IDs, adapter-only native types |
| ECS rewrite consumes effort before behavior is known | High | use sparse component stores first; migrate one vertical gameplay slice |
| Managed allocation causes Tick spikes | Medium | measure allocation/GC first, then pool hot buffers; avoid global unsafe rewrite |
| Unity reference assembly constrains net10 | High | separate Unity project; share schema/replay, not net10 assembly |
| Physics callback writes violate backend lifecycle | High | phase-owned scene access and event buffers; checked-build integration tests |
| Content cooking differs across backends | High | offline versioned cooking and content hash in replay |

## Definition of Done

- A change has a behavior test or replay fixture when it changes observable simulation.
- All backend resources have explicit ownership and release paths.
- Core/Server compile on .NET 10 without Unity references.
- Tests, build, and relevant replay/benchmark commands pass.
- Text files remain UTF-8 LF.
- No commit is made until the staged change has been reviewed.
