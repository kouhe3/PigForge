# Physics Replay Contract v1

## Purpose

`physics-replay-v1.schema.json` is the language-neutral contract for recording and comparing PigForge simulation runs. It is not a runtime JSON serialization requirement. A future binary codec must preserve the same fields and validation rules.

## Top-level document

```text
format
header
initialState
commands
frames
finalResult
```

`format` is exactly `pigforge.physics.replay`.

## Header

```text
protocolVersion          = 1
contentVersion           non-empty content catalog identifier
physicsBehaviorVersion   non-empty behavior/compatibility identifier
stateHashAlgorithm       = sha256-canonical-v1
fixedTickRate             1..240
simulationTicks           1..100000
randomSeed                uint32
```

`contentVersion` resolves `PartTypeId` to mass, collision shapes, materials, inertia and other immutable content. The replay stores references to that version instead of embedding engine-specific assets.

## Initial state

`initialState.entities` contains the initial entity and body states. Each entry requires:

```text
EntityId        positive uint32, unique in the list
PhysicsBodyId   positive uint32, unique in the list
PartTypeId      positive uint32
Position        3 finite float values
Rotation        4 finite float values
LinearVelocity  3 finite float values
AngularVelocity 3 finite float values
```

`initialState.joints` contains the initial constraint graph. Each entry requires:

```text
JointId       positive uint32, unique
BodyA         an existing initial PhysicsBodyId
BodyB         a different existing initial PhysicsBodyId
Kind          FIXED | DISTANCE | REVOLUTE | CONFIGURABLE
Constraints   uint32 bit mask
BreakForce    finite non-negative number
BreakTorque   finite non-negative number
```

## Commands

Commands are ordered by the tuple:

```text
(Tick ascending, Sequence ascending)
```

For equal `Tick`, `Sequence` must be strictly increasing. `Sequence` is positive and `PlayerId` is positive.

Commands with `Tick = 0` are applied after loading `initialState` and before the first physics step. Commands with `Tick = N` are applied immediately before the physics step that produces frame `N`.

v1 commands:

```text
PLACE_PART
    partTypeId, gridX, gridY, rotation(0..3)

REMOVE_PART
    entityId

ROTATE_PART
    entityId, rotation(0..3)

START_SIMULATION
    no additional fields

ENTER_BUILD_MODE
    policy(CLEAR|KEEP)
```

Unknown command kinds are invalid in v1. New command kinds may be added to v1 additively (documents that use only earlier kinds remain valid); any change to an existing command's shape requires a new protocol version.

## Frames

Frames have strictly increasing positive `Tick` values. Each frame contains:

The document contains exactly `SimulationTicks` frames, numbered contiguously from `1` through `SimulationTicks`.

```text
snapshots
    full state entries for the entities visible at this Tick

events
    ordered observable events generated during this Tick
```

v1 event field requirements:

```text
CONTACT_STARTED / CONTACT_PERSISTED / CONTACT_ENDED
    bodyA and bodyB required

JOINT_BROKEN
    jointId required

ENTITY_CREATED / ENTITY_DESTROYED
    entityId required
```

Event order within a Tick is part of the replay output and must be deterministic for a selected backend configuration.

## Final result

```text
outcome        SUCCESS | FAILURE | ABORTED
completedTick  uint32
stateHash      64 hexadecimal characters
```

## Canonical state hash v1

`sha256-canonical-v1` hashes the final logical state only, not the renderer state, native pointers, event callback addresses or memory layout.

The byte stream is little-endian and contains:

```text
ASCII bytes: "pigforge.state.v1" followed by one zero byte
uint32       completedTick
uint32       entityCount
```

Then entities sorted by ascending `EntityId`. For each entity:

```text
uint32 entityId
uint32 physicsBodyId
uint32 partTypeId
float32 position.x, position.y, position.z
float32 rotation.x, rotation.y, rotation.z, rotation.w
float32 linearVelocity.x, linearVelocity.y, linearVelocity.z
float32 angularVelocity.x, angularVelocity.y, angularVelocity.z
```

All floating-point values are serialized as IEEE 754 binary32 bit patterns in little-endian order. The hash input must reject non-finite values before serialization. No locale, text formatting, JSON property order or dictionary iteration order is involved.

## Validation and compatibility

- Boundary validation happens before a replay enters Core or a physics backend.
- v1 rejects unknown fields in the JSON Schema representation.
- A future additive field requires a documented compatibility decision; a changed meaning or ordering rule requires a new version.
- Replay comparison first checks event sequence and final outcome, then applies configured position/rotation tolerances. Different physics backends are not assumed to be bit-identical.
