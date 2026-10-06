/** Types aligned to schemas/physics-replay-v2.schema.json and part-content-v1.schema.json. */

export type Vec3 = [number, number, number];
export type Quat = [number, number, number, number];

export type ShapeKind = "box" | "sphere" | "capsule" | "convexMesh" | "triangleMesh";

export interface PartShape {
  kind: ShapeKind;
  halfExtents?: Vec3;
  radius?: number;
  /** Part-local shape offset, +y up (wheels carry their support box at the top). */
  offset?: Vec3;
  /**
   * Connection-gated geometry: an `attachment` marker (the collider the original shows on one
   * part-local side) or a `frame` (the mounting bracket a part is aligned and welded by). The
   * spawn-time body includes the attachment markers the connection state shows and excludes the
   * rest, and a wing's `frame` selects its two-state root collider (Wings.cs:62-71); drag snapping
   * and connection proximity line up against both. Occupancy does not -- that is the part's
   * declared `gridBox`.
   */
  condition?:
    | {
        kind: "attachment";
        side: "top" | "bottom" | "left" | "right" | "topLeft" | "topRight" | "bottomLeft" | "bottomRight";
      }
    | { kind: "frame" };
}

/**
 * The original Spring's own joint: a breakable bungee rope (`bungee`, the SpringJoint branch for
 * customPartIndex 0/2 under StableSpringConnection, Spring.cs:104) or a y soft limit (`limit`, the
 * ConfigurableJoint branch, Spring.cs:120). Every value is extracted per skin by `tools/bple-springs`
 * from the class constants and the published IN switches; never authored.
 */
export interface PartSpring {
  /** SPRING_LIMIT_SPRING, 250 N/m (Spring.cs:7). */
  stiffness: number;
  /** SPRING_DAMPING, 20 N*s/m (Spring.cs:9). */
  damper: number;
  /** SPRING_LIMIT, 0.1 m; the joint's y linear limit (Spring.cs:11). */
  limit: number;
  /** SPRING_BOUNCINESS, 1; the linear-limit bounciness (Spring.cs:13). */
  bounciness: number;
  /**
   * The published breaking force: SPRING_BREAK_FORCE, 250 N in the declaration defaults
   * (Spring.cs:15). Profile B's IN StrongSpringConnection is what doubles it to 1200.
   */
  breakForce: number;
}

/** One axis of the glove joint's position drive (spring N/m, damper N*s/m). */
export interface PartGloveDrive {
  spring: number;
  damper: number;
}

/** The throw: target distance along the part's local -Y, lateral deviation, and how long it stays out. */
export interface PartGloveShoot {
  distanceY: number;
  deviationX: number;
  time: number;
  limitSpring: number;
}

/** The wind-back: the limp glove's mass and the softer drive that pulls it home. */
export interface PartGloveWind {
  time: number;
  mass: number;
  driveSpring: number;
  driveDamper: number;
}

/**
 * The interactive SpringBoxingGlove (spec docs/specs/boxing-glove.md): a second rigid body "glove"
 * held to the host by a y-limited, y/x-driven joint, shot out on trigger and wound back. Extracted
 * by `tools/bple-springs` from the part prefab and its BoxingGlove*.prefab reference; never authored.
 */
export interface PartGlove {
  /** The glove prefab's rigidbody mass (0.5). */
  mass: number;
  /** The glove prefab's colliders (a SphereCollider r 0.3). */
  shapes: PartShape[];
  /** The host-glove joint's y linear limit (1). */
  limit: number;
  yDrive: PartGloveDrive;
  xDrive: PartGloveDrive;
  /** ConfigurableJoint.projectionDistance (0.1, SpringBoxingGlove.cs:252). */
  projectionDistance: number;
  shoot: PartGloveShoot;
  wind: PartGloveWind;
  /** The glove rigidbody's solver iteration factor (1.6, SpringBoxingGlove.cs:262). */
  solverIterationScale: number;
}

export interface PartCapabilities {
  pig?: boolean;
  wheel?: boolean;
  motor?: { thrustPerTick: number; directionX: -1 | 0 | 1 };
  tnt?: { fuseTicks: number; chainDetonate?: boolean; igniteOnImpact?: boolean };
  balloon?: number;
  /** A FanPropeller: the fan, the plane propeller and the rotor are one class in the original.
   * `maxSpeed` is the top speed along the thrust axis per unit power factor (absent = uncapped,
   * the propeller); `rotor` adds the overspeed brake. See docs/specs/fan-propeller.md. */
  fan?: { thrustPerTick: number; directionX: number; directionY: number; maxSpeed?: number; rotor?: boolean };
  /**
   * The original Spring's own joint (Spring.cs:100-134): a breakable bungee rope (SpringJoint)
   * or a y soft limit (ConfigurableJoint) holding the two neighbours at their assembly distance.
   * It never applies an impulse -- the old bounce-pad reading is gone. Extracted per skin by
   * `tools/bple-springs`, never authored.
   */
  spring?: PartSpring;
  /**
   * The interactive SpringBoxingGlove (spec docs/specs/boxing-glove.md): a second rigid body
   * "glove" held to the host by a y-limited, y/x-driven joint, shot out on trigger and wound
   * back. Extracted by `tools/bple-springs` from the part prefab and its BoxingGlove*.prefab
   * reference, never authored.
   */
  glove?: PartGlove;
  /**
   * The original's three-phase burn (Rocket.cs:228-300), in 60 Hz ticks: `ignitionTicks` is a
   * no-thrust phase for the bottle family (`visualization`), `boostTicks` is full thrust and
   * `endTicks` the linear ramp back to zero; `maxSpeed` is `m_maximumSpeed`, past which the force
   * divides by `1 + v - maxSpeed` (Rocket.cs:529-541). A charge with `explodeRadius` blasts where
   * it stands when the burn ends and survives as a spent husk. Written by tools/bple-rockets.
   */
  rocket?: {
    thrustPerTick: number;
    directionX: -1 | 0 | 1;
    directionY?: -1 | 0 | 1;
    ignitionTicks: number;
    boostTicks: number;
    endTicks: number;
    maxSpeed: number;
    visualization?: boolean;
    explodeRadius?: number;
    explodeImpulse?: number;
  };
  egg?: boolean;
  /** The original's `Wings.m_liftConstant` (Wings.cs:6), the only wing number its clamped |v|^2 response curve reads. */
  wing?: { liftConstant: number };
  /** The original's `Tail.m_liftConstant` (Tail.cs:6), used by the same curve. */
  tail?: number;
  /**
   * The original's `BasePart.m_autoAlign == FlipVertically`: this part's build pose has a
   * handedness -- a 180-degree turn about the part's own up axis, applied inside its own frame
   * before the yaw -- which is what the PGFS mirror bit carries (ADR-030). Absent means false.
   */
  mirror?: boolean;
  umbrella?: number;
  gearbox?: boolean;
  detacher?: boolean;
  /**
   * The original's Bellows: the puff ramps `(1 - (1 - num/0.5)^2)` over 0.5 s along the part's own
   * +X, then the cycle waits 0.3 s plus the skin's inflate before another puff is accepted
   * (Bellows.cs:92-121,123-142). `thrustPerTick` is `m_boostForce / 60` and `inflateTicks` the
   * skin's inflate; the two other phase lengths are class constants. Written by tools/bple-bellows.
   */
  bellows?: { directionX: -1 | 0 | 1; directionY?: -1 | 0 | 1; thrustPerTick: number; inflateTicks: number };
  light?: number;
  grapple?: { impulse: number; directionX?: number; directionY?: number };
  blaster?: { radius: number; impulse: number; chainRadius?: number };
  glue?: boolean;
  /** "toggle" keeps a persistent effect on/off; "trigger" is a one-shot action. */
  activation?: "toggle" | "trigger";
  /** True only for frames: their cell hosts one enclosed part. Enclosable is derived as !canEnclose. */
  canEnclose?: boolean;
  /** The weld role of the pair predicate: both ends non-`none` and at least one `source` (ADR-011). */
  jointConnectionType?: "none" | "source" | "target";
  /** The original's per-part weld strength, which scales the seam threshold (ADR-015). */
  jointConnectionStrength?: "weak" | "normal" | "high" | "extreme" | "highlyExtreme";
  /** The part-local sides this part may weld on; build-time alignment only snaps on those. */
  jointConnectionDirection?: "any" | "right" | "up" | "left" | "down" | "leftAndRight" | "upAndDown" | "none";
  /** Elastic wheel attachment: the original's linear-limit spring (N/m, N*s/m) holding the wheel at restOffset along its own Y. */
  suspension?: { stiffness: number; damper: number; restOffset: number };
}

export interface PartDefinition {
  partTypeId: number;
  name: string;
  mode: "static" | "dynamic";
  mass: number;
  capabilities?: PartCapabilities;
  /** The original's build-grid cell box; absent means the default single cell at the origin. */
  gridBox?: GridBox;
  /**
   * The script the original prefab mounts to gate its conditional colliders
   * (Rocket.cs:139-165, TNT.cs:86-108, SpotLight.cs:70-105, GrapplingHook.cs:218-255,
   * Wings.cs:41-75). Copied from the sprite manifest by `tools/bple-connections`; the renderer
   * keeps its own copy in that manifest, and the server reads this one for the physics body.
   */
  connectionVisual?: ConnectionVisual;
  shapes: PartShape[];
  /** Base partTypeId this entry is a skin/variant of. Never points at another variant. */
  variantOf?: number;
  /** Display label for the variant (falls back to `name`). */
  variantName?: string;
  /**
   * Unity's `Rigidbody.drag` / `angularDrag` where the original's part class overrides
   * `BasePart.EnsureRigidbody`'s pair; absent means the document's `physics.damping`. Extracted by
   * `tools/bple-damping`; the client only reads it as part data.
   */
  damping?: PartDamping;
}

/** Unity's per-rigidbody `Rigidbody.drag` / `angularDrag` (linearDamping / angularDamping in Unity 6). */
export interface PartDamping {
  linear: number;
  angular: number;
}

export type ConnectionVisual = "attachmentFallback" | "attachmentPlain" | "attachmentEight" | "frame";

/**
 * The original's per-part build-grid cell box (`m_gridXmin/m_gridXmax/m_gridYmin/m_gridYmax`,
 * `BasePart.cs:197-200`): the inclusive rectangle of build-grid cells a part occupies around its
 * own grid coordinate. Extracted per prefab by `tools/bple-grid`, never authored. Absent means the
 * original's default, one cell at the origin -- 332 of the original's 343 prefabs. The cell box is
 * what blocks a cell; a collider that overhangs a neighbouring cell does not.
 */
export interface GridBox {
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
}


export interface PartContentDocument {
  format: "pigforge.part-content";
  schemaVersion: 1;
  contentVersion: string;
  /**
   * The original's project-wide defaults every rigidbody inherits (`tools/bple-damping`): Unity's
   * `Physics.defaultMaxAngularSpeed` (its `ProjectSettings/DynamicsManager.asset` declares 7 rad/s,
   * a magnitude clamp no script or prefab overrides) and the `BasePart.EnsureRigidbody` damping
   * pair (0.2 / 0.05).
   */
  physics: PartContentPhysics;
  parts: PartDefinition[];
}

/** The original's project-wide rigidbody defaults (see {@link PartContentDocument.physics}). */
export interface PartContentPhysics {
  maximumAngularSpeed: number;
  damping: PartDamping;
}

export interface ReplayEntityState {
  entityId: number;
  physicsBodyId: number;
  partTypeId: number;
  position: Vec3;
  rotation: Quat;
  linearVelocity: Vec3;
  angularVelocity: Vec3;
  scale: number;
}

export interface ReplayHeader {
  protocolVersion: 2;
  contentVersion: string;
  physicsBehaviorVersion: string;
  stateHashAlgorithm: "sha256-canonical-v2";
  fixedTickRate: number;
  simulationTicks: number;
  randomSeed: number;
}

export type ReplayEvent =
  | { kind: "CONTACT_STARTED" | "CONTACT_PERSISTED" | "CONTACT_ENDED"; bodyA: number; bodyB: number }
  | { kind: "JOINT_BROKEN"; jointId: number }
  | { kind: "ENTITY_CREATED" | "ENTITY_DESTROYED"; entityId: number };

export interface ReplayFrame {
  tick: number;
  snapshots: ReplayEntityState[];
  events: ReplayEvent[];
}

export interface ReplayDocument {
  format: "pigforge.physics.replay";
  header: ReplayHeader;
  initialState: { entities: ReplayEntityState[]; joints: unknown[] };
  commands: unknown[];
  frames: ReplayFrame[];
  finalResult: { outcome: "SUCCESS" | "FAILURE" | "ABORTED"; completedTick: number; stateHash: string };
}

export interface SnapshotEntity {
  entityId: number;
  physicsBodyId: number;
  partTypeId: number;
  position: Vec3;
  rotation: Quat;
  linearVelocity: Vec3;
  angularVelocity: Vec3;
  scale: number;
  /**
   * World Z yaw (radians) of the frame this part's non-spinning sprites are attached to:
   * the hinge's parent body for a hinged wheel, the part's own frame otherwise (PGFS v4).
   */
  attachYaw: number;
  /** Per-entity snapshot flags bit0: the part's switch is on. */
  active: boolean;
  /**
   * Per-entity snapshot flags bit1 (PGFS v5): the entity is a runtime sub-entity of another
   * entity (a boxing glove's fist, a broken spring's endpoint -- ADR-027). It borrows its host's
   * `partTypeId` on the wire, so the renderer draws the part's own sub-entity art instead.
   */
  subEntity: boolean;
  /**
   * Per-entity snapshot flags bit2 (PGFS v6): the part's build pose is mirrored (ADR-030), the
   * handedness a float yaw cannot express. The renderer draws such a part with its art mirrored
   * in the part's own frame, and the manifest's sprite depth stays as it was for the wing art.
   */
  mirrored: boolean;
}

export interface SnapshotFrame {
  version: number;
  tick: number;
  phase: number;
  entities: SnapshotEntity[];
}

export interface DrawEntity {
  entityId: number;
  partTypeId: number;
  x: number;
  y: number;
  yaw: number;
  scale: number;
  vx: number;
  vy: number;
  /** Wire physicsBodyId: 0 = preview (no physics body), non-zero = live body. */
  bodyId: number;
  /** Switch state from the snapshot (false for parts without a switch). */
  active: boolean;
  /**
   * The snapshot's sub-entity flag (PGFS v5; false for replay documents). Such an entity is a
   * runtime body of its host part, drawn with the manifest's sub-entity art when it has any.
   */
  subEntity?: boolean;
  /**
   * The snapshot's mirror flag (PGFS v6; false for replay documents, which have no such bit).
   */
  mirrored?: boolean;
  /**
   * Orientation of the frame this part's non-spinning sprites are attached to, from the
   * snapshot's `attachYaw` (PGFS v4). A rolling wheel's `yaw` integrates its roll, so its
   * mounts need this angle instead to stay rigid to the chassis (see `drawFrame`). Absent
   * for replay documents, which carry no attach frame.
   */
  attachYaw?: number;
  /**
   * Build orientation, remembered by the tracker while the entity's layout was still a
   * preview. Only a fallback for sources without an attach frame (replay documents): the
   * renderer then keeps the mounts at the angle the part was built at. See `live/restYaw.ts`.
   */
  restYaw?: number;
}

export type ClientCommand =
  | { kind: 0; sequence: number; playerId: number; tick: number; partTypeId: number; x: number; y: number; angle: number; scale: number; mirrored: boolean }
  | { kind: 1; sequence: number; playerId: number; tick: number; entityId: number }
  | { kind: 2; sequence: number; playerId: number; tick: number; entityId: number; angle: number; mirrored: boolean }
  | { kind: 3; sequence: number; playerId: number; tick: number }
  | { kind: 5; sequence: number; playerId: number; tick: number }
  | { kind: 6; sequence: number; playerId: number; tick: number; entityId: number; x: number; y: number }
  | { kind: 7; sequence: number; playerId: number; tick: number; entityId: number; scale: number }
  | { kind: 8; sequence: number; playerId: number; tick: number; entityId: number; active: boolean }
  | { kind: 9; sequence: number; playerId: number; tick: number; partTypeId: number; active: boolean };

/** A locally previewed build pose during a move/rotate/scale drag (never authoritative). */
export interface ToolPreviewPose {
  entityId: number;
  x: number;
  y: number;
  yaw: number;
  scale: number;
}

/** World-space rectangle drawn by the select tool while dragging. */
export interface MarqueeRect {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export type GestureMessage =
  | { kind: "CameraChanged"; panX: number; panY: number; scale: number }
  | { kind: "SelectEntities"; entityIds: number[]; mode: "replace" | "add" | "toggle" }
  | { kind: "Marquee"; rect: MarqueeRect | null }
  | { kind: "PlaceRequested"; x: number; y: number }
  | { kind: "PartScaleChanged"; scale: number }
  | { kind: "ToolPreview"; preview: ToolPreviewPose | null }
  | { kind: "MoveRequested"; entityId: number; x: number; y: number }
  | { kind: "RotateRequested"; entityId: number; angle: number }
  | { kind: "ScaleRequested"; entityId: number; scale: number };
