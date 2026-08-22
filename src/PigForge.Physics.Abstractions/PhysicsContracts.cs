using System.Collections.Generic;

namespace PigForge.Physics.Abstractions;

[Flags]
public enum PhysicsConstraintMask
{
	None = 0,
	LockPositionX = 1 << 0,
	LockPositionY = 1 << 1,
	LockPositionZ = 1 << 2,
	LockRotationX = 1 << 3,
	LockRotationY = 1 << 4,
	LockRotationZ = 1 << 5
}

public enum PhysicsShapeKind
{
	Box,
	Sphere,
	Capsule,
	ConvexMesh,
	TriangleMesh
}

public enum PhysicsJointKind
{
	Fixed,
	Distance,
	Revolute,
	Configurable
}

public enum PhysicsEventKind
{
	ContactStarted,
	ContactPersisted,
	ContactEnded,
	JointBroken
}

public readonly record struct PhysicsBodyId(uint Value)
{
	public bool IsValid => Value != 0;
}

public readonly record struct PhysicsJointId(uint Value)
{
	public bool IsValid => Value != 0;
}

public readonly record struct PhysicsVector3(float X, float Y, float Z)
{
	public static PhysicsVector3 Zero => new(0, 0, 0);
}

public readonly record struct PhysicsQuaternion(float X, float Y, float Z, float W)
{
	public static PhysicsQuaternion Identity => new(0, 0, 0, 1);
}

public readonly record struct FixedTimeStep(float Seconds)
{
	public static FixedTimeStep FromSeconds(float seconds)
	{
		if (!float.IsFinite(seconds) || seconds <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A fixed time step must be finite and positive.");
		}

		return new FixedTimeStep(seconds);
	}
}

public abstract record ShapeDefinition(PhysicsShapeKind Kind);

public sealed record BoxShapeDefinition(
	float HalfExtentX,
	float HalfExtentY,
	float HalfExtentZ) : ShapeDefinition(PhysicsShapeKind.Box);

public sealed class BodyDefinition
{
	public BodyDefinition(
		PhysicsVector3 position,
		PhysicsQuaternion rotation,
		float mass,
		IReadOnlyList<ShapeDefinition> shapes)
	{
		if (!float.IsFinite(mass) || mass <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(mass), mass, "A dynamic body mass must be finite and positive.");
		}

		ArgumentNullException.ThrowIfNull(shapes);
		if (shapes.Count == 0)
		{
			throw new ArgumentException("A body must contain at least one collision shape.", nameof(shapes));
		}

		Position = position;
		Rotation = rotation;
		Mass = mass;
		Shapes = shapes;
	}

	public PhysicsVector3 Position { get; }
	public PhysicsQuaternion Rotation { get; }
	public float Mass { get; }
	public IReadOnlyList<ShapeDefinition> Shapes { get; }
}

public sealed record JointDefinition(
	PhysicsJointKind Kind,
	PhysicsBodyId BodyA,
	PhysicsBodyId BodyB,
	PhysicsConstraintMask Constraints,
	float BreakForce,
	float BreakTorque);

public readonly record struct PhysicsBodySnapshot(
	PhysicsBodyId Body,
	PhysicsVector3 Position,
	PhysicsQuaternion Rotation,
	PhysicsVector3 LinearVelocity,
	PhysicsVector3 AngularVelocity);

public readonly record struct PhysicsEvent(
	PhysicsEventKind Kind,
	PhysicsBodyId BodyA,
	PhysicsBodyId BodyB,
	PhysicsJointId Joint);

public sealed record PhysicsCapabilities(
	IReadOnlySet<PhysicsJointKind> SupportedJointKinds,
	bool SupportsContinuousCollision,
	bool SupportsPerBodyInertia);

public interface IPhysicsWorld : IDisposable
{
	PhysicsCapabilities Capabilities { get; }

	PhysicsBodyId CreateBody(BodyDefinition definition);
	void DestroyBody(PhysicsBodyId body);
	PhysicsJointId CreateJoint(JointDefinition definition);
	void DestroyJoint(PhysicsJointId joint);
	void Step(FixedTimeStep timeStep);
	int CopySnapshots(Span<PhysicsBodySnapshot> destination);
	int DrainEvents(Span<PhysicsEvent> destination);
}
