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

### Task 9: Migrate runtime gameplay rules - Complete (ADR-002 semantics)

- Acceptance: server rules consume physics events/snapshots and determine outcomes without renderer state; semantics per ADR-002 (no damage primitives).
- Verify: `8` gameplay rule tests - motor/wheel contact gating, pig indestructibility, TNT ignition on impact velocity change with fuse, pure-impulse blast at target centres of mass, goal-zone win trigger, map-bounds restart request, joint-break recording; typical level fixture (Bepu, block ignites TNT, blast delivers pig into goal zone) and stress fixture (76 bodies) deterministic across double runs.
- Files: `src/PigForge.Core/Runtime/`, `src/PigForge.Core/Content/LevelContentParser.cs`, `schemas/part-content-v1.schema.json` (material fields), `schemas/level-content-v1.schema.json`, `src/PigForge.Physics.Bepu/` (material pair friction, sleepless dynamics), `tests/PigForge.Core.Tests/GameplayRulesTests.cs`.

## Phase 4: Headless Server

### Task 10: Implement room-owned fixed Tick loop - Complete

- Acceptance: one room owns one authoritative physics scene; input, physics and rules phases are ordered; shutdown releases resources.
- Verify: `6` server tests pass - scripted-world phase-order assertions (apply -> step -> copy -> drain per tick, rules commands flowing through the input phase), destroyed entities release their bodies, Dispose is idempotent and releases the world exactly once, spawn sealed after first tick, and real-Bepu rooms produce identical double-run hashes with a stable outcome.
- Files: `src/PigForge.Server/GameRoom.cs`, `tests/PigForge.Server.Tests/GameRoomTests.cs`.

### Task 11: Add command validation and idempotence - Complete

- Acceptance: duplicate, stale, out-of-order and capability-invalid commands are rejected or handled deterministically.
- Verify: `11` server tests pass - per-player sequence idempotence (duplicates ignored, stale rejected, gaps allowed), rule rejections recorded without state mutation, mode gating (build commands after start rejected, start is one-way), and a malicious command script (duplicates + stale + invalid rotation + occupied cells + post-start build) producing identical double-run hashes and outcome logs.
- Files: `src/PigForge.Server/CommandValidator.cs`, `src/PigForge.Server/GameRoom.cs` (two-mode lifecycle, command pipeline), `tests/PigForge.Server.Tests/GameRoomTests.cs`.

## Phase 4.5: Feature Expansion (protocol v2)

Prerequisite: Tasks 10 and 11 (room loop and command validation) — construction commands currently have no path into the replay/server loop (`PhysicsReplaySimulation.ApplyCommand` rejects `PlacePartCommand`).

### Task 14.5: Build-mode lifecycle commands (issue #7) - Complete

- Scope: explicit `EnterBuildMode` command (protocol v1 additive) with clear/keep policy; keep semantics preserve the previous run's entities as a frozen in-place group at their last telemetry poses; clear semantics destroy everything and re-spawn the configured level actors.
- Acceptance: re-entering build mode without clearing reproduces the original "second vehicle" behaviour as a rule, deterministically, and counts toward part/connection limits and state hash.
- Verify: `15` protocol tests (EnterBuildMode validation incl. undefined-policy rejection); `6` new core tests (cells released on freeze, frozen parts non-editable and limit/hash-counted, ResetAll, gameplay reset allows relinking the same bodies); `4` new server tests (mode/tick gating with idempotence, keep freezes at telemetry poses and blocks edits while a second vehicle relaunches, clear restores level spawns and cells, real-Bepu double-run hash equality for both policies); full suite `108` tests green, Release build zero warnings.
- Files: `src/PigForge.Protocol/` (command, policy, validator), `schemas/physics-replay-v1.schema.json`, `docs/specs/physics-replay-v1.md`, `src/PigForge.Core/Construction/ConstructionRules.cs` (frozen group + ResetAll), `src/PigForge.Core/Runtime/GameplayRules.cs` (ResetForRebuild/ResetAll), `src/PigForge.Server/GameRoom.cs`, `src/PigForge.Server/CommandValidator.cs`, tests.
- Semantics notes: frozen entities release their grid cells and never connect to fresh placements (independent second vehicle); frozen pigs/TNT keep their gameplay roles and relaunch as dynamic bodies; build-phase commands always carry Tick 0 while `EnterBuildMode` must carry the room's current tick.

### Task 15: Free placement, scaling and compound merging (issue #4, protocol v2) - Complete

- Scope: break the grid coupling introduced by our own rules, not by physics — commands gain free angle and scale; replay entity state gains scale and it enters the canonical state hash; construction occupancy rewrites from cell ownership to spatial-hash coarse filtering plus OBB overlap; connections move to attachment-point/proximity semantics; optional compound merging for joint-count reduction with seam-based splitting per ADR-002 (impulse threshold, no damage accumulation).
- Acceptance: replay protocol version bumps with backward-rejection of invalid versions; merged compounds split deterministically along seams on threshold breach.
- Verify: protocol v2 round-trip and rejection tests; construction OBB tests (`FreePlacementTests`); compound merge/split Core fixtures with double-run hash equality (`CompoundAssemblerTests`); Bepu compound create/drop/dispose and static-compound rejection; GameRoom Start welds connected parts into one physics body. Full suite `131` tests green, Release build zero warnings.
- Files: `src/PigForge.Core/Construction/CompoundAssembler.cs`, `src/PigForge.Physics.Abstractions/PhysicsContracts.cs` (`CompoundShapeDefinition`), `src/PigForge.Physics.Bepu/BepuPhysicsWorld.cs`, `src/PigForge.Server/GameRoom.cs`, tests.

### Task 12: Add snapshot publication - Complete

- Acceptance: snapshots contain only server-owned state; payload size and frequency are measured; no JSON in the Tick hot path.
- Verify: `13` protocol tests (round-trip, truncated/foreign/bad-version rejection, 64 B per entity bound, 10k-frame zero-allocation encoding) and `4` server publishing tests (local consumer decodes tick/phase/entities, destroyed entities leave the frame, identical rooms publish identical byte streams, per-frame publishing is allocation-free).
- Files: `src/PigForge.Protocol/SnapshotWire.cs`, `src/PigForge.Server/GameRoom.cs` (`TryPublishSnapshot`), `tests/PigForge.Protocol.Tests/SnapshotWireTests.cs`, `tests/PigForge.Server.Tests/SnapshotPublishingTests.cs`.

## Phase 5: Performance and Client

### Task 16: Establish performance baseline - Complete

- Measure typical/stress scenes: Tick p50/p95/p99, allocation, GC pauses, native memory, snapshot size, concurrent rooms.
- Acceptance: recorded baseline is reproducible and no optimization is accepted without comparison.
- Verify: `dotnet run --project src/PigForge.Benchmarks/PigForge.Benchmarks.csproj -c Release` prints typical (4 bodies), stress (66 bodies) and concurrent-8 reports; allocation/snapshot columns match across two consecutive runs; percentile helper tests plus typical metric-presence test pass. Bepu has no native runtime — committed heap and working set stand in for native memory.
- Files: `src/PigForge.Benchmarks/`, `tests/PigForge.Server.Tests/PerformanceBaselineTests.cs`.

### Task 17: Add client adapter - Complete

- Client form factor is frozen in `docs/specs/web-client-spec.md`: a Vue 3 + Canvas 2D web client under `clients/web/` (form A replay viewer needs no server; form B live client depends on this task and Task 12 snapshot publication).
- Acceptance: client consumes snapshots and renders state; it cannot authoritatively set position, contact, break or result.
- Verify: `pnpm test` and `pnpm build` in `clients/web/` (replay v2 fixture, PGFS decoder matching `SnapshotWire`, camera round-trip); form B connects to `dotnet run --project src/PigForge.Server -- --demo-ws` at `ws://127.0.0.1:5088/snapshots` and draws inbound binary frames only — the socket never sends pose, contact, break or outcome.
- Files: `clients/web/`, `src/PigForge.Server/DemoSnapshotHost.cs`.

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
