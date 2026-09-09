# Repository Guidelines

## Project Overview

PigForge is a Unity-free, deterministic, server-authoritative multiplayer game core (Bad Piggies-style) built on .NET 10. The server owns all simulation state; clients are display-only consumers that receive binary snapshots (PGFS) and send build/start commands (PGFC). Physics is pluggable behind a port/adapter boundary: BepuPhysics v2 (managed, primary) and JoltPhysicsSharp (native, secondary). Unity 6 exists only as an offline reference runtime that exports replay files — it never references .NET assemblies.

Current state: the "multiplayer persistent sandbox" slice (see `tasks/plan.md` + `tasks/todo.md`, gitignored) is implemented and verified end-to-end: per-connection player ids, per-player part ownership, preview/ghost layouts that materialize on Start, per-player RESET, and a continuously ticking world. `--play` now hosts that sandbox room; the goal-based slope/terrain rooms remain as `PlayHost.CreateSlopeRoom`/`CreateTerrainRoom` (tests + future racing).

## Architecture & Data Flow

Dependency direction (no cycles; Core never sees Protocol, a physics backend, or Unity):

```
Core -> Physics.Abstractions
Physics.Bepu / Physics.Jolt -> Physics.Abstractions
Replay -> Physics.Abstractions + Protocol
Server -> Core + Physics.Abstractions + Protocol + Physics.Bepu
Benchmarks -> Server + Physics.Bepu
UnityReference -> (none; file/schema exchange only)
clients/web -> (none; wire formats only, never .NET assemblies)
```

**Server tick pipeline** (`GameRoom.Tick`, `src/PigForge.Server/GameRoom.cs:582-620`), manually ticked at 60 Hz for determinism:

1. `_world.ApplyCommands(_output.Commands)` — impulses produced by rules on the previous tick
2. `_world.Step(_timeStep)` — fixed timestep (`1 / tickRateHz`)
3. `CopySnapshots` + `DrainEvents` into reused buffers
4. `GameplayRules.Tick(...)` — emits `PhysicsCommand`s, `DestroyedEntities`, `DetachedEntities`
5. Cleanup — unbind destroyed entities, detach, seam-split compounds when an applied impulse exceeds `BreakImpulse`

Building mode manipulates pure `ConstructionRules` state (no physics bodies); `Start()` runs `CompoundAssembler.Assemble` (union-find over connections) into one physics body per cluster.

**Wire formats** (all v2, little-endian binary, hand-written with `BinaryPrimitives`/`Span`/`ref struct`):

- PGFS snapshots: 15-byte header + 68-byte entities; building phase `0x10` publishes layout with `physicsBodyId = 0`
- PGFC commands (`ClientCommandKind` 0–5: PlacePart, RemovePart, RotatePart, StartSimulation, EnterBuildMode, Retry), PGFA acks
- Replay JSON per `schemas/physics-replay-v2.schema.json`

**Client flow**: `PlayHost` (`ws://127.0.0.1:5088/play`) → `CommandFrame.TryDecode` → `GameRoom.Submit` → PGFA ack → PGFS broadcast each tick. `DemoSnapshotHost` (`/snapshots`) is broadcast-only. No client hosts simulation.

## Key Directories

- `src/PigForge.Core` — engine-agnostic gameplay: ECS-lite (`Entities/EntityStore.cs`, `ComponentStore.cs`), construction + compound assembly (`Construction/`), rules engine (`Runtime/GameplayRules.cs`), strict content parsers (`Content/`)
- `src/PigForge.Physics.Abstractions` — `IPhysicsWorld` contract + PigForge-owned math (`PhysicsContracts.cs`, single file)
- `src/PigForge.Physics.Bepu` — primary managed backend (`BepuPhysicsWorld.cs`)
- `src/PigForge.Physics.Jolt` — native second backend (`JoltPhysicsWorld.cs`), **not in `PigForge.slnx`**
- `src/PigForge.Protocol` — PGFS/PGFC wire codecs + replay contract records/validator
- `src/PigForge.Replay` — headless replay runner + cross-backend diff (`ReplayRunner.cs`, `ReplayDiffReport.cs`)
- `src/PigForge.Server` — `GameRoom` (authoritative room), `PlayHost`/`DemoSnapshotHost`, `Program.cs` entry
- `src/PigForge.Benchmarks` — custom perf harness (no BenchmarkDotNet)
- `tests/` — 5 xUnit projects (Core, Protocol, Replay, Physics, Server Tests)
- `clients/web` — Vue 3 + TS + Vite + Pinia SPA (pnpm)
- `unity/PigForge.UnityReference` — Unity 6000.5.6f1 reference exporter, isolated
- `content/` — `parts.json` (46 parts, partTypeId 1–46), `levels/slope-v1.json`, `levels/terrain-v1.json`
- `schemas/` — cross-runtime JSON Schema contracts: `part-content-v1`, `level-content-v1`, `physics-replay-v2`, `client-command-v1`

## Development Commands

```powershell
# .NET (from repo root)
dotnet restore PigForge.slnx
dotnet test PigForge.slnx
dotnet build PigForge.slnx -c Release
dotnet run --project src/PigForge.Server/PigForge.Server.csproj -c Release -- --play
#   -> ws://127.0.0.1:5088/play (multiplayer sandbox: previews + per-player Start/RESET)
dotnet run --project src/PigForge.Server/PigForge.Server.csproj -c Release -- --demo-ws
#   -> ws://127.0.0.1:5088/snapshots (broadcast demo)
dotnet run --project src/PigForge.Benchmarks/PigForge.Benchmarks.csproj -c Release

# Web (from clients/web)
pnpm install
pnpm test    # vitest run
pnpm build   # vue-tsc --noEmit && vite build
pnpm dev     # vite on :5173, proxies WS /play and /snapshots to :5088
```

No CI exists. Web has no ESLint/Prettier; .NET has no analyzer packages — `TreatWarningsAsErrors=true` is the only style gate.

## Code Conventions & Common Patterns

- **Determinism is first-class**: manual fixed ticking, iteration sorted by `EntityId`, sleeping disabled in both backends, canonical state hashes. Changing simulation order or tick semantics will break double-run hash tests.
- **No DI container**: manual composition root (`PlayHost`/`DemoSnapshotHost`/`BaselineRunner`); backend selection via `GameRoomOptions.WorldFactory` (`Func<IPhysicsWorld>`) — Bepu is hardcoded in `PlayHost`.
- **Interfaces only where needed**: `IPhysicsWorld`, `IReplaySimulation`, `IReplayPhysicsContent`; everything else is sealed concrete records/classes.
- **Guard clauses at boundaries**: `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrEmpty`, `ObjectDisposedException.ThrowIf`; explicit `NotSupportedException` for unimplemented capabilities (joints are declared but unsupported in both backends).
- **Hot paths are allocation-free**: reused `_snapshotBuffer`/`_eventBuffer`, `ref struct` readers/writers, `stackalloc`, `CollectionsMarshal.AsSpan`, `ref`-returning enumerators. Tests assert zero allocation on steady-state paths.
- **Async only at transport edges**: `HttpListener` + `WebSocket.ReceiveAsync/SendAsync`, `PeriodicTimer`, `CancellationToken`; simulation itself is synchronous.
- **No `System.Text.Json` serializer, no source generators**: content is hand-parsed via `JsonDocument` with exhaustive error accumulation (`PartContentException.Errors`); wire/replay hashing is hand-written span/binary code. Rejects unknown properties, duplicate keys, GUID-like engine refs, non-finite values.
- **Content is data-driven**: `content/parts.json` + `content/levels/*.json` validated against `schemas/` at startup (`PartContentLibrary.Load`, `LevelContentLibrary`).
- **Naming**: English, behavioral; PascalCase; no underscores in identifiers. Commit messages: Conventional Commits, lowercase types (`feat:`, `docs:`), imperative, no scope.
- **Git discipline**: never commit to `main` — work on `next`; `tasks/`, `.agents/`, `artifacts/`, `replays/` are gitignored local working state, never commit them.
- **Encoding**: UTF-8, LF, final newline, no trailing whitespace (`.editorconfig` + `.gitattributes`).

## Important Files

- `src/PigForge.Server/Program.cs` — CLI entry (`--play` / `--demo-ws`, no args prints usage)
- `src/PigForge.Server/GameRoom.cs` — authoritative room, 5-phase tick, command execution, snapshot publishing
- `src/PigForge.Server/PlayHost.cs` — WS play host (`CreateSandboxRoom` for `--play`, per-connection player ids); loads content via `FindRepositoryRoot` (walks up from `AppContext.BaseDirectory` — running outside the repo tree throws)
- `src/PigForge.Physics.Abstractions/PhysicsContracts.cs` — `IPhysicsWorld` + all semantic types (shapes, joints, commands, events, snapshots, `PhysicsVector3`/`PhysicsQuaternion`)
- `src/PigForge.Core/Runtime/GameplayRules.cs` — 935-line rules engine (ADR-002 semantics; note `GameplayConfig.Default.MaxTicks = 0` vs `PlayHost` passing 1200)
- `src/PigForge.Core/Construction/CompoundAssembler.cs` — cluster assembly/seam split
- `src/PigForge.Protocol/SnapshotWire.cs`, `CommandWire.cs` — wire codecs (fixed sizes: 15-byte header, 68-byte entity)
- `Directory.Build.props` — net10.0, ImplicitUsings, Nullable, LangVersion latest, TreatWarningsAsErrors
- `docs/decisions/ADR-001-*.md` — net10 physics boundary; `ADR-002-*.md` — no-damage runtime semantics (**binding** for any gameplay change)
- `clients/web/vite.config.ts` — dev server port 5173 + WS proxy; `clients/web/src/schema/decodeSnapshot.ts`/`encodeCommand.ts` — client wire codecs

## Runtime/Tooling Preferences

- **.NET 10 SDK** required (no `global.json` pin); solution is `.slnx` format (recent SDK needed). Both src and tests target `net10.0` uniformly.
- **pnpm** for web (lockfile v9); no `packageManager`/`engines` pin, no `.nvmrc` — Node ≥ 20.19 implied by Vite 7.
- Windows/PowerShell dev machine; all dotnet/pnpm commands are cross-platform.
- Unity 6000.5.6f1 (URP) only for the `unity/` reference project; never a compile target for `.NET` code.
- Jolt loads native `joltc.dll` (win-x64 RID); `Foundation.Init/Shutdown` is ref-counted under a static lock; `ServerGarbageCollection=false` set in its csproj.

## Testing & QA

- **Run everything**: `dotnet test PigForge.slnx` (~156 Fact/Theory cases incl. real-Bepu fixtures). Per project: `dotnet test tests/PigForge.Server.Tests/PigForge.Server.Tests.csproj`; filter e.g. `--filter FullyQualifiedName~GameplayRules` / `~SlopePlay`.
- **Framework**: xUnit, global `Using Include="Xunit"` (no explicit `using Xunit;`). No mock libraries — hand-written fakes (`ScriptedPhysicsWorld`, `RecordingReplaySimulation`, `GameplayHarness`, `PhysicsDrivenLevel`). No `IClassFixture`/`[Collection]`/async lifecycle; tests construct their own SUT.
- **Dominant convention**: run twice, compare a `long` state hash or byte stream for determinism; cross-backend (Bepu vs Jolt) differentials assert event-level agreement within tolerances and require genuine solver divergence to be *reported*, not hidden.
- **Assertions**: xUnit `Assert.*` only, expected-before-actual; physics outcomes via `Assert.InRange`/`precision:`; allocation checks via `GC.GetAllocatedBytesForCurrentThread()` delta == 0.
- **Fixture files**: JSON as C# raw string literals, or repo-relative paths resolved by walking up from `AppContext.BaseDirectory` (`FindRepositoryFile`/`FindRepositoryRoot`); never embedded resources.
- **Coverage**: `coverlet.collector` is referenced but unconfigured — no gate; can run `dotnet test PigForge.slnx --collect:"XPlat Code Coverage"`.
- **Web tests**: Vitest `environment: "node"` (not jsdom, despite jsdom dependency), colocated `*.test.ts`.
- Known staleness: `tests/PigForge.Core.Tests` pins older xunit 2.5.3/SDK 17.8.0 than siblings; `Protocol.Tests`/`Replay.Tests` csproj missing `<IsTestProject>`; `schemas/client-command-v1.schema.json` kind enum 0–3 is stale vs 6 command kinds; Unity reference exporter still emits replay v1 vs .NET v2.
