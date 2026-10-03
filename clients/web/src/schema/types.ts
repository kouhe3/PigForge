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

export interface PartCapabilities {
  pig?: boolean;
  wheel?: boolean;
  motor?: { thrustPerTick: number; directionX: -1 | 0 | 1 };
  tnt?: { fuseTicks: number; chainDetonate?: boolean; igniteOnImpact?: boolean };
  balloon?: number;
  fan?: { thrustPerTick: number; directionX: number; directionY: number };
  spring?: number;
  rocket?: { thrustPerTick: number; directionX: -1 | 0 | 1; directionY?: -1 | 0 | 1; durationTicks: number; explodeRadius?: number; explodeImpulse?: number };
  egg?: boolean;
  wing?: { liftCoef: number; maxLift?: number };
  tail?: number;
  umbrella?: number;
  gearbox?: boolean;
  detacher?: boolean;
  bellows?: number;
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
  parts: PartDefinition[];
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
  | { kind: 0; sequence: number; playerId: number; tick: number; partTypeId: number; x: number; y: number; angle: number; scale: number }
  | { kind: 1; sequence: number; playerId: number; tick: number; entityId: number }
  | { kind: 2; sequence: number; playerId: number; tick: number; entityId: number; angle: number }
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
