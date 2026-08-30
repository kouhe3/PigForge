using PigForge.Physics.Abstractions;

namespace PigForge.Core;

public readonly record struct EntityTransform(PhysicsVector3 Position, PhysicsQuaternion Rotation, float Scale = 1f);

public readonly record struct PhysicsBodyLink(PhysicsBodyId Body);

public readonly record struct PartLink(uint PartTypeId);

public sealed class TransformStore(EntityStore entities) : ComponentStore<EntityTransform>(entities);

public sealed class PhysicsBodyStore(EntityStore entities) : ComponentStore<PhysicsBodyLink>(entities);

public sealed class PartStore(EntityStore entities) : ComponentStore<PartLink>(entities);
