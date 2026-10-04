# Repository Guidelines

## Project Overview

PigForge is a Unity-free, deterministic, server-authoritative multiplayer game core (Bad Piggies-style) built on .NET 10. The server owns all simulation state; clients are display-only consumers that receive binary snapshots (PGFS) and send build/start commands (PGFC). Physics is pluggable behind a port/adapter boundary: BepuPhysics v2 (managed, primary) and JoltPhysicsSharp (native, secondary). Unity 6 exists only as an offline reference runtime that exports replay files — it never references .NET assemblies.

Current state: the "multiplayer persistent sandbox" slice, **PLAY part switches** (`docs/specs/play-part-switches.md`), **marquee selection** (`docs/specs/multi-select.md`) and the **original variant catalog** (`docs/specs/part-variant-catalog.md`, ADR-004/006) are implemented and verified end-to-end: per-connection player ids, per-player part ownership, preview/ghost layouts that materialize on Start, per-player RESET, a continuously ticking world, per-part switches (bottom-centre bar, `1`–`9`/`0`/`A`… hotkeys, tap-to-toggle) whose state is server-authoritative and rides the snapshot, and 267 content parts = 44 bases + 223 variants imported from the original's `GameData.m_customParts` (plus IN extension parts it ships outside that registry, e.g. the BlasterTNT) (skins + the AlienTNT/BlasterTNT/AlienEgg effects: chain detonation, one-shot shockwave, super glue). `--play` hosts that sandbox room; its level (`content/levels/terrain-v1.json`) is a 60 m floor, a three-step hill and a 34 m three-plank long slope (geometry table in `docs/specs/multiplayer-sandbox.md`). The goal-based slope/terrain rooms remain as `PlayHost.CreateSlopeRoom`/`CreateTerrainRoom` (tests + future racing) and keep their pre-switch automatic behaviour. `tasks/plan.md` + `tasks/todo.md` are gitignored working state.

## Original-Game Baseline (content truth)

- **Upstream baseline: https://github.com/anstropleuton/BPLE (branch `main`)** — a decompilation of
  BPLE whose author only **fixes errors and adds no new features**, so it is the reference for "what
  the original does". Read it with the `github` tool (`file_read` / `search_code`), never `curl`.
- **Local copies** (read-only for us): `C:/tmp/BAD_PIGGIES/BPLE 2022.1.9` (pristine; pins Unity
  **2021.3.45f2**, which is the original's own editor — use this editor, not the installed Unity 6,
  whenever a measurement must match the original's PhysX), and `C:/tmp/BAD_PIGGIES/BPLE_Unity6`
  (the same project migrated to Unity 6000.5.6f1, plus commits made from the PigForge side).
- **When a local file disagrees with upstream, check upstream first**: the difference is either a
  local drift (the PigForge-side commits — e.g. `BasePart.EnsureRigidbody`'s 2.5D constraints were
  changed from the original `(RigidbodyConstraints)56` to `0`, and `KingPig.EnsureRigidbody` is an
  added override) or genuinely original. Never treat the migrated copy's text as untouched.
- **Never hand-write a value the original defines**: extract it with a `tools/bple-*` pipeline
  (`extract-*.mjs` reports, `apply-*.mjs` writes, histogram assertions on drift), and record the
  `file:line` alongside. Numbers measured from the original (e.g. the weld probe in
  `unity/PigForge.WeldProbe`) are the only admissible substitute for an authored value.

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

**Server tick pipeline** (`GameRoom.Tick`, `src/PigForge.Server/GameRoom.cs`), manually ticked at 60 Hz for determinism:

1. `_world.ApplyCommands(_output.Commands)` — impulses produced by rules on the previous tick
2. `_world.Step(_timeStep)` — fixed timestep (`1 / tickRateHz`)
3. `CopySnapshots` + `DrainEvents` into reused buffers
4. `GameplayRules.Tick(...)` — emits `PhysicsCommand`s, `DestroyedEntities`, `DetachedEntities`
5. Cleanup — unbind destroyed entities, detach, seam-split compounds when an applied impulse exceeds `BreakImpulse`, and drop a frame weld the same way (`GameRoom.BreakWeldsFromAppliedCommands`)

Building mode manipulates pure `ConstructionRules` state (no physics bodies); `Start()` runs `CompoundAssembler.Assemble` (union-find over connections) into one physics body per cluster, **except that two frames never merge**: a seam whose both ends are `canEnclose` (the original's `Frame` family) stays two bodies joined by a compliant `Constraints.Weld` at the original's own anchors and still colliding, so a frame chain bends under its own weight exactly as the original does (ADR-024; compliance fitted to the original's own chain measurement), and every wheel part gets its own body on a revolute joint anchored at its **tire centre** (ADR-008/009): the wheel body carries only its tire spheres, its non-rotating mounts (the support box) ride the parent body, and jointed pairs do not collide, so wheels roll instead of skidding. A wheel whose content carries `capabilities.suspension` gets the original's elastic attachment instead of a rigid axle: the same revolute joint plus a sprung linear degree of freedom along the wheel's own Y, converted from the extracted N/m and N*s/m (ADR-012). No catalogued wheel declares one yet (see ADR-012 §决策 2 for the OffRoadWheel finding).

**Wire formats** (little-endian binary, hand-written with `BinaryPrimitives`/`Span`/`ref struct`; PGFS v4, PGFC v2):

- PGFS snapshots (v4): 15-byte header + 73-byte entities (`attachYaw:f` then trailing `flags:u8`, bit0 = part switch on; `attachYaw` is the frame a part's non-spinning sprites are rigid to — a hinged wheel's parent body — so its axle follows the chassis); building phase `0x10` publishes layout with `physicsBodyId = 0`
- PGFC commands (`ClientCommandKind` 0–9: PlacePart, RemovePart, RotatePart, StartSimulation, EnterBuildMode, Retry, MovePart, ScalePart, SetPartActive, SetPartTypeActive), PGFA acks
- Replay JSON per `schemas/physics-replay-v2.schema.json`

**Client flow**: `PlayHost` (`ws://127.0.0.1:5088/play`, optionally `?session=<opaque id>`) → `CommandFrame.TryDecode` → `GameRoom.Submit` → PGFA ack → PGFS broadcast each tick. A connection's player id is per-connection, except that `PlayHost`'s `PlaySessions` resumes the same id for a socket carrying a session id it has already issued (the client's per-page-load id; the query string is transport only — frames are untouched). `DemoSnapshotHost` (`/snapshots`) is broadcast-only. No client hosts simulation.

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
- `clients/web` — Vue 3 + TS + Vite + Pinia SPA (pnpm); build-mode tools (place/select/move/rotate/scale) in `src/editor/tools.ts` + `src/gesture/canvasGestures.ts` (marquee selection included); play-mode switch bar in `src/live/gadgets.ts` + `App.vue`
- `unity/PigForge.UnityReference` — Unity 6000.5.6f1 reference exporter, isolated
- `content/` — `parts.json` (267 entries: 44 bases with ids 1–46 — 3 and 29 dropped as PigForge-only inventions — plus 223 original variants 47–269; the `suspension` capability exists (ADR-012) but no catalogued wheel declares one yet: the original's only spring wheel is the OffRoadWheel IN extension part, see ADR-012 §决策 2; `variantOf`/`variantName` group skins under their base, `capabilities.activation` declares part switches, `tnt.chainDetonate`/`igniteOnImpact`, `blaster`, `glue`, `suspension`, `jointConnectionStrength` (the per-part weld strength that scales a seam's break threshold, ADR-015) and the part-level `gridBox` (the original's build-grid cell box that occupancy uses; absent = the default origin single cell, written only for the KingPig/GoldenPig 3x2 family — ADR-020) carry the original effects; each shape may carry a part-local `offset` — wheels are a tire sphere plus a support box, see ADR-007 — a `condition` (a joint attachment bracket: drag snapping and connection proximity always see it, cell occupancy never does, and the spawn-time physics body carries only the sides the connection state shows -- ADR-017/ADR-021), the part-level `connectionVisual` (the original script that gates those conditional colliders and a wing's two-state root box; absent = the part has none, written by `tools/bple-connections`), the `fan` capability's `maxSpeed`/`rotor` (the one `FanPropeller` class behind the fan, the plane propeller and the rotor: `m_defaultSpeed x IN <X>Speed` per unit power factor, and `m_isRotor` — the plane propellers are still deferred, ADR-022, written by `tools/bple-fans`), and each `material` may carry Unity's `frictionCombine` mode, a wheel taking its tyre collider's material, ADR-016), `levels/slope-v1.json`, `levels/terrain-v1.json`
- `schemas/` — cross-runtime JSON Schema contracts: `part-content-v1`, `level-content-v1`, `physics-replay-v2`, `client-command-v1`
- `tools/bple-variants/` — original variant registry importer (`import-variants.mjs` + curated `variant-overrides.json`): appends skins/effect variants to `content/parts.json` and the `variants` section of the texture map; idempotent, append-only
only for parts whose prefab declares something other than the origin single cell — ADR-020).
`tools/bple-fans/` — writes every fan/propeller/rotor part's `capabilities.fan` from the original's `FanPropeller` fields (`m_force` / `m_forceDirection` / `m_isRotor` / `m_defaultSpeed`): `thrustPerTick = m_force / 60` (ADR-013 decision 4's conversion, also the unit of `GameplayConfig.SeamBreakImpulse`), `maxSpeed = m_defaultSpeed x IN <X>Speed`, `rotor = m_isRotor`; the 10 plane propellers are reported but deferred (their `PropellerSpeed` is `Infinity`, i.e. no cap at all) — ADR-022.
`tools/bple-connections/` — writes the part-level `connectionVisual` rule into `content/parts.json`, copied from the texture manifest's already-derived prefab-mount rule (Rocket → `attachmentFallback`, TNT/BlasterTNT → `attachmentPlain`, SpotLight/GrapplingHook → `attachmentEight`, Wings → `frame`); idempotent, and run after `apply-brackets.mjs` so a glider keeps its `frame` shape — ADR-021.

## Development Commands

```powershell
# .NET (from repo root)
dotnet restore PigForge.slnx
dotnet test PigForge.slnx
dotnet build PigForge.slnx -c Release
dotnet run --project src/PigForge.Server/PigForge.Server.csproj -c Release -- --play
#   -> ws://127.0.0.1:5088/play[?session=<id>] (multiplayer sandbox: previews + per-player
#      Start/RESET; the browser client appends its own session id so a reconnect keeps its parts)
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
- **Elasticity has one owner per backend**: `PhysicsCapabilities.AppliesRestitutionNatively` says whether the solver applies `PhysicsMaterial.Restitution` itself (Jolt does) or the rules layer must synthesize it from the contact event's `ContactNormal`/`ApproachSpeed` (Bepu 2.4.0 has no restitution term at all — see ADR-010). Never apply both. The rules layer aggregates restitution/mass per **body** (max restitution, summed mass) because a contact event names bodies, not shapes, and `content/parts.json` values are original-`PhysicMaterial` truth only as far as `tools/bple-materials` extracted them.
- **Interfaces only where needed**: `IPhysicsWorld`, `IReplaySimulation`, `IReplayPhysicsContent`; everything else is sealed concrete records/classes.
- **Guard clauses at boundaries**: `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrEmpty`, `ObjectDisposedException.ThrowIf`; explicit `NotSupportedException` for unimplemented capabilities (of the joint kinds, `Revolute` and `Distance` have implementations, and only in the Bepu backend — ADR-008/009, ADR-011, ADR-012).
- **Assembly follows the original's joint capability rule** (ADR-011): whether two adjacent parts are welded at all comes from each part's content `capabilities.jointConnectionType` (`none`/`source`/`target`) and the verbatim `Contraption.cs:690` predicate — both ends non-`none` and at least one `source`. Nothing is special-cased: the pig, king pig, egg and engine are `none` and therefore weld to nothing. A part is welded to a frame regardless of that rule only when it is **enclosed** by it (`capabilities.canEnclose` on the frame + `canBeEnclosed` elsewhere), which is also how a pig rides inside a wooden frame. Balloons and sandbags are `none` too — they attach after Start through a runtime `Distance` joint to the first `source` (or pig) anchor found within 10 cells along `capabilities.attachment.direction`. Never hand-write these content values: `tools/bple-joints/extract-joints.mjs` reads them from the original's prefabs.
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
- `src/PigForge.Core/Runtime/GameplayRules.cs` — 1100-line rules engine (ADR-002 semantics, part switches; note `GameplayConfig.Default.MaxTicks = 0` vs `PlayHost` passing 1200)
- `src/PigForge.Core/Construction/CompoundAssembler.cs` — cluster assembly/seam split, wheel hinge collection (`CollectHinges`), volume-centre body poses (a lone member is centred in its own frame, so single-centred-shape static parts stay primitive bodies), wheel bodies on the axle with their mounts (`CompoundAttachment`) hosted by the parent
- `src/PigForge.Protocol/SnapshotWire.cs`, `CommandWire.cs` — wire codecs (fixed sizes: 15-byte header, 73-byte entity)
- `Directory.Build.props` — net10.0, ImplicitUsings, Nullable, LangVersion latest, TreatWarningsAsErrors
while connection proximity keeps using the collider union; `ADR-021` — the connection state also decides physics: the spawn-time body carries the conditional attachment brackets the layout shows (a hidden marker is a trigger in the original, `Rocket.cs:157-160`) and a wing's root box switches between its thin top-only and thick bottom form (`Wings.cs:62-71`)
`ADR-022` — the fan, the plane propeller and the rotor are one `FanPropeller` (one `fan` capability; the rotor is no longer balloon lift and no longer destroyed by its switch), and `ADR-023` — a compound split drops the pending commands aimed at the body it just destroyed (`GameRoom.DropPendingCommands`), which used to wedge the room on the next tick
`ADR-024` — two frames are two bodies joined by a compliant weld instead of one compound (fitted to the original's own chain measurement), with the weld carrying the power cluster edge and its own break threshold
- `docs/intent/*.md` + `docs/specs/*.md` — confirmed intent and the authoritative per-slice spec (e.g. `advanced-building.md` for build-mode move/rotate/scale, `multi-select.md` for marquee selection and PGFA error messages, `play-part-switches.md` for part switches)
- `clients/web/vite.config.ts` — dev server port 5173 + WS proxy; `clients/web/src/schema/decodeSnapshot.ts`/`encodeCommand.ts` — client wire codecs; `clients/web/src/editor/tools.ts` — tool math/snaps; `clients/web/src/live/gadgets.ts` — switch-bar grouping/hotkeys

- `clients/web/src/renderer/` — Canvas 2D frame painter (`draw.ts`: original-art sprites, spinning sprites pinned to the axle `wheelAxle` derives from the part content, shape fallback), optional BPLE texture manifest (`atlas.ts`, accepts `schemaVersion` 2, 3 and 4 — a v4 part carries `connectionVisual` and its conditional sprites a local `condition`: the side of an `*Attachment` marker or one of a wing's two mounts), `connectionVisuals.ts` (gates those sprites on the neighbour layout, reproducing the original's `ChangeVisualConnections` rules; the weld predicate matches `CompoundAssembler.CanMergePair`), `animation/` (fan/rotor blade spin, pig face clips + expression machine, wall-clock gating — presentation only, never on the wire), thumbnails; `clients/web/src/live/restYaw.ts` — build-orientation tracker that keeps a rolling wheel's non-spinning sprites (its axle) off the roll

## Runtime/Tooling Preferences

- **.NET 10 SDK** required (no `global.json` pin); solution is `.slnx` format (recent SDK needed). Both src and tests target `net10.0` uniformly.
- **pnpm** for web (lockfile v9); no `packageManager`/`engines` pin, no `.nvmrc` — Node ≥ 20.19 implied by Vite 7.
- Windows/PowerShell dev machine; all dotnet/pnpm commands are cross-platform.
- Unity 6000.5.6f1 (URP) only for the `unity/` reference project; never a compile target for `.NET` code.
- Jolt loads native `joltc.dll` (win-x64 RID); `Foundation.Init/Shutdown` is ref-counted under a static lock; `ServerGarbageCollection=false` set in its csproj.

## Testing & QA

- **Framework**: xUnit, global `Using Include="Xunit"` (no explicit `using Xunit;`). No mock libraries — hand-written fakes (`ScriptedPhysicsWorld`, `RecordingReplaySimulation`, `GameplayHarness`, `PhysicsDrivenLevel`). No `IClassFixture`/`[Collection]`/async lifecycle; tests construct their own SUT.
- **Run everything**: `dotnet test PigForge.slnx` (449 cases incl. real-Bepu fixtures: Core 234, Server 110, Protocol 39, Replay 11, Physics 55 = 44 non-Jolt + 11 Jolt). Per project: `dotnet test tests/PigForge.Server.Tests/PigForge.Server.Tests.csproj`; filter e.g. `--filter FullyQualifiedName~GameplayRules` / `~SlopePlay` / `~WoodenCart`. Known flakiness: the Jolt-backed `PigForge.Physics.Tests` occasionally crashes inside `JPH_PhysicsSystem_Update` when the whole project runs in parallel; the Jolt tests pass 11/11 in isolation and the project does not reference Core, so a crash is not a signal from a gameplay change.
- **Dominant convention**: run twice, compare a `long` state hash or byte stream for determinism; cross-backend (Bepu vs Jolt) differentials assert event-level agreement within tolerances and require genuine solver divergence to be *reported*, not hidden.
- **Assertions**: xUnit `Assert.*` only, expected-before-actual; physics outcomes via `Assert.InRange`/`precision:`; allocation checks via `GC.GetAllocatedBytesForCurrentThread()` delta == 0.
- **Fixture files**: JSON as C# raw string literals, or repo-relative paths resolved by walking up from `AppContext.BaseDirectory` (`FindRepositoryFile`/`FindRepositoryRoot`); never embedded resources.
- **Coverage**: `coverlet.collector` is referenced but unconfigured — no gate; can run `dotnet test PigForge.slnx --collect:"XPlat Code Coverage"`.
- **Web tests**: Vitest `environment: "node"` (not jsdom, despite jsdom dependency), colocated `*.test.ts` (224 cases).
- Known staleness: `tests/PigForge.Core.Tests` pins older xunit 2.5.3/SDK 17.8.0 than siblings; `Protocol.Tests`/`Replay.Tests` csproj missing `<IsTestProject>`; Unity reference exporter still emits replay v1 vs .NET v2.
