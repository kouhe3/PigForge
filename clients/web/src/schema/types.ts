/** Types aligned to schemas/physics-replay-v2.schema.json and part-content-v1.schema.json. */

export type Vec3 = [number, number, number];
export type Quat = [number, number, number, number];

export type ShapeKind = "box" | "sphere" | "capsule" | "convexMesh" | "triangleMesh";

export interface PartShape {
  kind: ShapeKind;
  halfExtents?: Vec3;
  radius?: number;
}

export interface PartCapabilities {
  pig?: boolean;
  wheel?: boolean;
  motor?: { thrustPerTick: number; directionX: -1 | 0 | 1 };
  tnt?: { fuseTicks: number };
  balloon?: number;
  fan?: { thrustPerTick: number; directionX: number; directionY: number };
}

export interface PartDefinition {
  partTypeId: number;
  name: string;
  mode: "static" | "dynamic";
  mass: number;
  capabilities?: PartCapabilities;
  shapes: PartShape[];
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
}

export type ClientCommand =
  | { kind: 0; sequence: number; playerId: number; tick: number; partTypeId: number; x: number; y: number; angle: number; scale: number }
  | { kind: 1; sequence: number; playerId: number; tick: number; entityId: number }
  | { kind: 2; sequence: number; playerId: number; tick: number; entityId: number; angle: number }
  | { kind: 3; sequence: number; playerId: number; tick: number }
  | { kind: 5; sequence: number; playerId: number; tick: number };

export type GestureMessage =
  | { kind: "CameraChanged"; panX: number; panY: number; scale: number }
  | { kind: "SelectEntity"; entityId: number | null }
  | { kind: "PlaceRequested"; x: number; y: number }
  | { kind: "PartScaleChanged"; scale: number };
