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

### Task 3: Add Unity reference adapter — Complete

- Scope: independent Unity project/assembly; no reference to net10 Core or Server.
- Acceptance: creates a ground and dynamic box, uses explicit fixed simulation, exports the shared replay format.
- Verify: Unity Test Framework PlayMode smoke test passes via Unity CLI (`-runTests -testPlatform PlayMode`, exit 0); exported replay conforms to `physics-replay-v1` schema checks.
- Files: `unity/PigForge.UnityReference/` (`ReferenceReplayExporter.cs`, PlayMode smoke test, runtime/tests asmdef).

### Task 4: Add Bepu backend and replay adapter — Complete

- Scope: pure C# BepuPhysics v2 adapter with static Ground, dynamic Box, fixed Tick, impulse commands, initial velocities, snapshots, deterministic contact events, explicit disposal, and a physics replay adapter.
- Acceptance: no Unity or native runtime dependency; unsupported shape/joint capabilities fail explicitly; destroyed bodies release their shape resources; replay snapshots and events map stable logical IDs.
- Verify: .NET 10 integration tests cover falling Box contact, impulse-before-Step, unsupported capabilities, body lifecycle, deterministic repeated replay hash/event sequence, and snapshot output.
- Files: `src/PigForge.Physics.Bepu/`, `src/PigForge.Replay/PhysicsReplaySimulation.cs`, `tests/PigForge.Physics.Tests/`, `tests/PigForge.Replay.Tests/`.

### Task 4.5: Add Jolt backend — Complete

- Scope: JoltPhysicsSharp 2.22 adapter behind the same `IPhysicsWorld` contract.
- Acceptance: native RID assets are pinned (body-to-BodyID locked mapping with sequence-number stale guard), missing native runtime fails explicitly (wrapped DllNotFoundException/BadImageFormatException), and the adapter passes the shared contract tests.
- Verify: Windows x64 tests pass (`17` physics tests incl. Jolt repeat-run determinism and Jolt/Bepu event-level/landing/impulse comparison); Linux x64 remains an optional follow-up.
- Files: `src/PigForge.Physics.Jolt/JoltPhysicsWorld.cs`, `tests/PigForge.Physics.Tests/JoltPhysicsContractTests.cs`.

- Scope: optional native JoltPhysicsSharp adapter behind the same `IPhysicsWorld` contract; use only after Bepu behavior and performance baselines exist.
- Acceptance: native RID assets are pinned, missing native runtime fails explicitly, and the adapter passes the shared contract tests.
- Verify: Windows x64 integration test first, then Linux x64; compare replay snapshots and contact event sequences against Bepu with tolerance reporting.
- Files likely touched: `src/PigForge.Physics.Jolt/`, `tests/PigForge.Physics.Tests/`.

### Task 5: Add cross-backend differential report — Complete

- Acceptance: any two backend runs compare snapshots, events, final result, and tolerance bands.
- Verify: `10` replay tests cover snapshot/event/final-result diff reporting by Tick and EntityId, event tick-tolerance windows, and a real Bepu vs Jolt box drop whose first contact aligns within the window while solver divergence is reported honestly.
- Files: `src/PigForge.Replay/ReplayDiffReport.cs`, `tests/PigForge.Replay.Tests/ReplayDiffTests.cs`.


## Phase 3: Portable Content and ECS

### Task 6: Define portable part and collision content — Complete

- Acceptance: engine-agnostic part content with no Unity asset references; per-kind shape field whitelists; startup-time rejection of invalid documents.
- Verify: `13` Core content tests pass (sample content load, GUID-like field rejection, mass/mode rules, duplicate ids, one-pass error reporting, BodyDefinition mapping).
- Files: `schemas/part-content-v1.schema.json`, `content/parts.json`, `src/PigForge.Core/Content/` (document DTOs, parser, library).
- Note: material fields (restitution/friction) are deferred to the runtime-rules revision per ADR-002.

### Task 7: Add generation-safe EntityId and component stores — Complete

- Acceptance: old handles die after slot reuse; hot path has no per-tick allocations.
- Verify: `21` Core entity tests pass (recycle invalidation across a generation window with documented 12-bit wrap semantics, stale-write rejection, live-only enumeration, zero-allocation steady-state benchmark over 512-entity batch loops).
- Files: `src/PigForge.Core/WorldState.cs` (EntityId packing), `src/PigForge.Core/Entities/` (EntityStore, ComponentStore, Transform/PhysicsBody/Part stores), `tests/PigForge.Core.Tests/EntityStoreTests.cs`.

### Task 8: Migrate construction rules — Complete

- Scope: place, remove, rotate, grid occupancy, connection validity and limits.
- Acceptance: rules run without Unity or physics-native types.
- Verify: `11` boundary/invalid-command tests (occupancy conflicts, unknown parts, invalid rotation, part/connection limits, removal semantics, blocked rotation with reconnection) plus a deterministic double-run replay fixture asserting identical layout hash and rejection sequence.
- Files: `src/PigForge.Core/Construction/ConstructionRules.cs`, `tests/PigForge.Core.Tests/ConstructionRulesTests.cs`.

### Task 9: Migrate runtime gameplay rules — Implemented with known semantic deviations; revision required per ADR-002

- Status: first implementation exists (`40` tests, typical fixture wins via Bepu, stress fixture with 76 bodies deterministic across double runs), but its semantics were written before ADR-002 and deviate from the original game.
- Known deviations (must be reworked together with their tests): impact damage kills pigs (pigs are indestructible), blast damage (TNT is impulse-only), `Won = all pigs dead` (win is delivery into the goal trigger zone), `Failed = timeout` (confirmed fail path is pig out of bounds triggering restart).
- Revision acceptance: no damage/hp primitives anywhere in runtime rules (only impulse, joint break thresholds, position triggers, reset); material restitution/friction move from hardcoded backend values into part content; goal zone and map bounds come from a level content format.
- Verify: typical and stress replay fixtures re-recorded against ADR-002 semantics; deterministic double-run hashes.
- Files likely touched: `src/PigForge.Core/Runtime/`, `src/PigForge.Core/Content/`, `schemas/part-content-v1.schema.json` (material fields), tests.
- Reference: `docs/decisions/ADR-002-runtime-rules-semantics.md`.

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

## Phase 4.5: Feature Expansion (protocol v2)

Prerequisite: Tasks 10 and 11 (room loop and command validation) — construction commands currently have no path into the replay/server loop (`PhysicsReplaySimulation.ApplyCommand` rejects `PlacePartCommand`).

### Task 14.5: Build-mode lifecycle commands (issue #7)

- Scope: explicit `EnterBuildMode` command with clear/keep policy; keep semantics preserve the previous contraption as a frozen or re-launchable entity group (deterministic group copy).
- Acceptance: re-entering build mode without clearing reproduces the original "second vehicle" behaviour as a rule, deterministically, and counts toward part/connection limits and state hash.
- Verify: replay fixtures for clear and keep policies; double-run hash equality.

### Task 15: Free placement, scaling and compound merging (issue #4, protocol v2)

- Scope: break the grid coupling introduced by our own rules, not by physics — commands gain free angle and scale; replay entity state gains scale and it enters the canonical state hash; construction occupancy rewrites from cell ownership to spatial-hash coarse filtering plus OBB overlap; connections move to attachment-point/proximity semantics; optional compound merging for joint-count reduction with seam-based splitting per ADR-002 (impulse threshold, no damage accumulation).
- Acceptance: replay protocol version bumps with backward-rejection of invalid versions; merged compounds split deterministically along seams on threshold breach.
- Verify: protocol v2 round-trip and rejection tests; construction OBB tests; compound split replay fixtures with double-run hash equality.

## Phase 5: Performance and Client

### Task 16: Establish performance baseline

- Measure typical/stress scenes: Tick p50/p95/p99, allocation, GC pauses, native memory, snapshot size, concurrent rooms.
- Acceptance: recorded baseline is reproducible and no optimization is accepted without comparison.

### Task 17: Add client adapter

- Client form factor is frozen in `docs/specs/web-client-spec.md`: a Vue 3 + Canvas 2D web client under `clients/web/` (form A replay viewer needs no server; form B live client depends on this task and Task 12 snapshot publication).
- Acceptance: client consumes snapshots and renders state; it cannot authoritatively set position, contact, break or result.
- Verify: connect local web client (form B) to one server room and compare displayed state to server snapshots.

## Checkpoints

### Checkpoint A: Backend proof

- [ ] Schema round-trip passes
- [ ] Replay repeats identically for the selected backend
- [ ] Unity reference, Bepu, and Jolt smoke scenes produce comparable events

### Checkpoint B: Gameplay proof

- [ ] Portable content validates before simulation
- [ ] Construction and runtime rules run without Unity
- [ ] Runtime rules conform to ADR-002 semantics (deviation list cleared)
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
