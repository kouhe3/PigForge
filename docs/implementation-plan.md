# Implementation Plan: PigForge 无 Unity 核心与物理后端

## Overview

在 .NET 10 中建立不依赖 Unity 的数据导向游戏核心、版本化回放边界和可替换物理后端。Unity 只作为离线参考运行时；BepuPhysics v2 作为第一生产候选后端，JoltPhysicsSharp 作为第二后端候选。实现顺序以风险优先：先锁定跨运行时契约和物理生命周期，再迁移 BPLE 内容与玩法，最后加入联机传输。

## Architecture Decisions

1. **net10-first**：Core、Protocol、Physics abstractions、Server 统一 `net10.0`；不为 Unity 降级或多目标化。
2. **schema over assembly**：Unity 和服务端通过命令、快照、事件、回放 Schema 交换数据，不共享 Unity 不兼容的 .NET 程序集。
3. **semantic backend port**：物理契约表达 PigForge 需要的能力和数据，不暴露 Unity/PhysX 类型。
4. **one authority**：线上每个房间只有一个权威物理世界；Unity 只做参考和离线差异比较。
5. **ECS incrementally**：先使用稳定句柄、分离组件存储和批处理；完整 archetype ECS 与手工内存延后到 profiler 证明需要时。
6. **replay before networking**：先让相同输入可重放、可比较，再设计在线快照和预测。

## Dependency Graph

Versioned command/replay schema
              ↓
Physics contract and replay harness
       ┌──────┴────────┐
       ↓               ↓
Unity reference    Bepu adapter
                       ↓
                  Jolt adapter
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

### Task 1: Define versioned physics schema — Complete

- Input: body definitions, joint definitions, commands, random seed, content version.
- Output: snapshots, contacts, break events, entity lifecycle, final result.
- Acceptance: schema includes explicit version and does not contain Unity or native pointer fields.
- Verify: JSON Schema parse, protocol validation tests, invalid version/order/hash/joint/null-frame/non-finite/Tick-limit rejection pass (`8` protocol tests).
- Files: `schemas/physics-replay-v1.schema.json`, `docs/specs/physics-replay-v1.md`, `src/PigForge.Protocol/ReplayContracts.cs`, `src/PigForge.Protocol/ReplayDocumentValidator.cs`, `tests/PigForge.Protocol.Tests/UnitTest1.cs`.

### Task 2: Implement fixed Tick replay runner — Complete

- Acceptance: input is validated before simulation; Tick 0 commands run before Tick 1; exactly `SimulationTicks` frames are emitted; output order and hash are stable.
- Verify: `3` Replay tests cover Tick ordering, exact frame count, invalid-input short circuit, and order-independent canonical state hashing.
- Files: `src/PigForge.Replay/ReplayRunner.cs`, `src/PigForge.Protocol/ReplayContracts.cs`, `src/PigForge.Protocol/ReplayDocumentValidator.cs`, `tests/PigForge.Replay.Tests/ReplayRunnerTests.cs`.

### Task 2.5: Define physics world command and lifecycle contract — Complete

- Scope: static/dynamic body definitions, finite shape and transform validation, batched impulse commands, typed body lifecycle/contact/joint events, and explicit disposal behavior for implementations.
- Acceptance: runtime boundary exposes `ApplyCommands(ReadOnlySpan<PhysicsCommand>)`; invalid IDs, non-finite values, invalid mass/shape/joint definitions fail explicitly; contract tests cover apply → step → snapshot → event ordering.
- Verify: `7` `PigForge.Physics.Tests` tests pass; full solution test and build remain clean.
- Files: `src/PigForge.Physics.Abstractions/PhysicsContracts.cs`, `tests/PigForge.Physics.Tests/PhysicsContractTests.cs`, `tests/PigForge.Physics.Tests/PigForge.Physics.Tests.csproj`.

## Phase 2: Physics Backends

### Task 3: Add Unity reference adapter

- Scope: independent Unity project/assembly; no reference to net10 Core or Server.
- Acceptance: creates a ground and dynamic box, uses explicit fixed simulation, exports the shared replay format.
- Verify: Unity runtime smoke test and replay file inspection.
- Files likely touched: `unity/` and schema mapping only.

### Task 4: Add Bepu backend and replay adapter — Complete

- Scope: pure C# BepuPhysics v2 adapter with static Ground, dynamic Box, fixed Tick, impulse commands, initial velocities, snapshots, deterministic contact events, explicit disposal, and a physics replay adapter.
- Acceptance: no Unity or native runtime dependency; unsupported shape/joint capabilities fail explicitly; destroyed bodies release their shape resources; replay snapshots and events map stable logical IDs.
- Verify: .NET 10 integration tests cover falling Box contact, impulse-before-Step, unsupported capabilities, body lifecycle, deterministic repeated replay hash/event sequence, and snapshot output.
- Files: `src/PigForge.Physics.Bepu/`, `src/PigForge.Replay/PhysicsReplaySimulation.cs`, `tests/PigForge.Physics.Tests/`, `tests/PigForge.Replay.Tests/`.

### Task 4.5: Add Jolt backend — Planned

- Scope: optional native JoltPhysicsSharp adapter behind the same `IPhysicsWorld` contract; use only after Bepu behavior and performance baselines exist.
- Acceptance: native RID assets are pinned, missing native runtime fails explicitly, and the adapter passes the shared contract tests.
- Verify: Windows x64 integration test first, then Linux x64; compare replay snapshots and contact event sequences against Bepu with tolerance reporting.
- Files likely touched: `src/PigForge.Physics.Jolt/`, `tests/PigForge.Physics.Tests/`.

### Task 5: Add cross-backend differential report

- Acceptance: Unity, Bepu, and Jolt runs compare snapshots, events, final result, and tolerance bands.
- Verify: known simple scene passes event-level comparison; differences are reported with Tick and EntityId.
- Files likely touched: `src/PigForge.Replay/` or `tests/PigForge.Replay.Tests/`.


## Phase 3: Portable Content and ECS

### Task 6: Define portable part and collision content

- Acceptance: body, shape, material, mass, inertia, connection and break definitions contain no Unity asset references.
- Verify: fixture content loads and validates before simulation.
- Files likely touched: `schemas/`, `src/PigForge.Core/`, `content/` fixtures.


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
- Files likely touched: `src/PigForge.Server/`, `src/PigForge.Physics.Bepu/`, tests.

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
- [ ] Unity reference, Bepu, and Jolt smoke scenes produce comparable events

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
| Bepu and Jolt behavior differs from Unity PhysX | High | Keep one server authority, pin behavior versions, run replay comparison and preserve tolerance reports |
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
