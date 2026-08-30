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

public enum PhysicsBodyMode
{
	Static,
	Dynamic
}

public enum PhysicsJointKind
{
	Fixed,
	Distance,
	Revolute,
	Configurable
}

public enum PhysicsCommandKind
{
	ApplyImpulse
}

public enum PhysicsEventKind
{
	ContactStarted,
	ContactPersisted,
	ContactEnded,
	JointBroken,
	BodyCreated,
	BodyDestroyed
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

	public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);

	public static float Distance(PhysicsVector3 left, PhysicsVector3 right)
	{
		float dx = left.X - right.X;
		float dy = left.Y - right.Y;
		float dz = left.Z - right.Z;
		return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
	}

	public static PhysicsVector3 Normalize(PhysicsVector3 value)
	{
		float length = MathF.Sqrt((value.X * value.X) + (value.Y * value.Y) + (value.Z * value.Z));
		return length <= float.Epsilon ? Zero : new PhysicsVector3(value.X / length, value.Y / length, value.Z / length);
	}

	public static PhysicsVector3 operator -(PhysicsVector3 left, PhysicsVector3 right) =>
		new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

	public static PhysicsVector3 operator +(PhysicsVector3 left, PhysicsVector3 right) =>
		new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

	public static PhysicsVector3 operator *(PhysicsVector3 left, float scalar) =>
		new(left.X * scalar, left.Y * scalar, left.Z * scalar);
}

public readonly record struct PhysicsQuaternion(float X, float Y, float Z, float W)
{
	public static PhysicsQuaternion Identity => new(0, 0, 0, 1);

	public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && float.IsFinite(W);
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

/// <summary>Per-body surface properties. Restitution is solver-dependent: Jolt applies it natively, Bepu v2 has no restitution support.</summary>
public sealed record PhysicsMaterial(float Restitution, float Friction)
{
	public static PhysicsMaterial Default { get; } = new(Restitution: 0f, Friction: 0.8f);
}

public abstract record ShapeDefinition(PhysicsShapeKind Kind);

public sealed record BoxShapeDefinition : ShapeDefinition
{
	public BoxShapeDefinition(float halfExtentX, float halfExtentY, float halfExtentZ)
		: base(PhysicsShapeKind.Box)
	{
		ValidateHalfExtent(halfExtentX, nameof(halfExtentX));
		ValidateHalfExtent(halfExtentY, nameof(halfExtentY));
		ValidateHalfExtent(halfExtentZ, nameof(halfExtentZ));
		HalfExtentX = halfExtentX;
		HalfExtentY = halfExtentY;
		HalfExtentZ = halfExtentZ;
	}

	public float HalfExtentX { get; }
	public float HalfExtentY { get; }
	public float HalfExtentZ { get; }

	private static void ValidateHalfExtent(float value, string parameterName)
	{
		if (!float.IsFinite(value) || value <= 0)
		{
			throw new ArgumentOutOfRangeException(parameterName, value, "A box half extent must be finite and positive.");
		}
	}
}

public sealed class BodyDefinition
{
	public BodyDefinition(
		PhysicsVector3 position,
		PhysicsQuaternion rotation,
		float mass,
		IReadOnlyList<ShapeDefinition> shapes,
		PhysicsVector3 linearVelocity = default,
		PhysicsVector3 angularVelocity = default)
		: this(PhysicsBodyMode.Dynamic, position, rotation, mass, shapes, linearVelocity, angularVelocity)
	{
	}

	public BodyDefinition(
		PhysicsBodyMode mode,
		PhysicsVector3 position,
		PhysicsQuaternion rotation,
		float mass,
		IReadOnlyList<ShapeDefinition> shapes,
		PhysicsVector3 linearVelocity = default,
		PhysicsVector3 angularVelocity = default,
		PhysicsMaterial? material = null)
	{
		if (!Enum.IsDefined(mode))
		{
			throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown physics body mode.");
		}

		if (!position.IsFinite)
		{
			throw new ArgumentOutOfRangeException(nameof(position), "A body position must contain only finite values.");
		}

		if (!rotation.IsFinite)
		{
			throw new ArgumentOutOfRangeException(nameof(rotation), "A body rotation must contain only finite values.");
		}

		if (!float.IsFinite(mass) || (mode == PhysicsBodyMode.Dynamic ? mass <= 0 : mass != 0))
		{
			string requirement = mode == PhysicsBodyMode.Dynamic
				? "A dynamic body mass must be finite and positive."
				: "A static body mass must be zero.";
			throw new ArgumentOutOfRangeException(nameof(mass), mass, requirement);
		}

		if (!linearVelocity.IsFinite)
		{
			throw new ArgumentOutOfRangeException(nameof(linearVelocity), "A body linear velocity must contain only finite values.");
		}

		if (!angularVelocity.IsFinite)
		{
			throw new ArgumentOutOfRangeException(nameof(angularVelocity), "A body angular velocity must contain only finite values.");
		}

		if (mode == PhysicsBodyMode.Static && (linearVelocity != PhysicsVector3.Zero || angularVelocity != PhysicsVector3.Zero))
		{
			throw new ArgumentException("A static body cannot have an initial velocity.");
		}

		ArgumentNullException.ThrowIfNull(shapes);
		if (shapes.Count == 0)
		{
			throw new ArgumentException("A body must contain at least one collision shape.", nameof(shapes));
		}

		for (int index = 0; index < shapes.Count; index++)
		{
			if (shapes[index] is null)
			{
				throw new ArgumentException("A body cannot contain a null collision shape.", nameof(shapes));
			}
		}

		if (material is not null && (!float.IsFinite(material.Restitution) || material.Restitution is < 0 or > 1
			|| !float.IsFinite(material.Friction) || material.Friction < 0))
		{
			throw new ArgumentOutOfRangeException(nameof(material), "A physics material must have restitution in [0, 1] and non-negative finite friction.");
		}

		Mode = mode;
		Position = position;
		Rotation = rotation;
		Mass = mass;
		LinearVelocity = linearVelocity;
		AngularVelocity = angularVelocity;
		Material = material ?? PhysicsMaterial.Default;
		Shapes = shapes;
	}

	public PhysicsBodyMode Mode { get; }
	public PhysicsVector3 Position { get; }
	public PhysicsQuaternion Rotation { get; }
	public float Mass { get; }
	public PhysicsVector3 LinearVelocity { get; }
	public PhysicsVector3 AngularVelocity { get; }
	public PhysicsMaterial Material { get; }
	public IReadOnlyList<ShapeDefinition> Shapes { get; }
}


public sealed record JointDefinition
{
	public JointDefinition(
		PhysicsJointKind kind,
		PhysicsBodyId bodyA,
		PhysicsBodyId bodyB,
		PhysicsConstraintMask constraints,
		float breakForce,
		float breakTorque)
	{
		if (!Enum.IsDefined(kind))
		{
			throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown physics joint kind.");
		}

		if (!bodyA.IsValid || !bodyB.IsValid || bodyA == bodyB)
		{
			throw new ArgumentException("A joint must connect two different valid bodies.");
		}

		if (!float.IsFinite(breakForce) || breakForce < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(breakForce), breakForce, "Break force must be finite and non-negative.");
		}

		if (!float.IsFinite(breakTorque) || breakTorque < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(breakTorque), breakTorque, "Break torque must be finite and non-negative.");
		}

		Kind = kind;
		BodyA = bodyA;
		BodyB = bodyB;
		Constraints = constraints;
		BreakForce = breakForce;
		BreakTorque = breakTorque;
	}

	public PhysicsJointKind Kind { get; }
	public PhysicsBodyId BodyA { get; }
	public PhysicsBodyId BodyB { get; }
	public PhysicsConstraintMask Constraints { get; }
	public float BreakForce { get; }
	public float BreakTorque { get; }
}

public readonly record struct PhysicsCommand
{
	private PhysicsCommand(
		PhysicsCommandKind kind,
		PhysicsBodyId body,
		PhysicsVector3 impulse,
		PhysicsVector3 worldPoint)
	{
		Kind = kind;
		Body = body;
		Impulse = impulse;
		WorldPoint = worldPoint;
	}

	public PhysicsCommandKind Kind { get; }
	public PhysicsBodyId Body { get; }
	public PhysicsVector3 Impulse { get; }
	public PhysicsVector3 WorldPoint { get; }

	public static PhysicsCommand ApplyImpulse(
		PhysicsBodyId body,
		PhysicsVector3 impulse,
		PhysicsVector3 worldPoint)
	{
		if (!body.IsValid)
		{
			throw new ArgumentException("An impulse command must target a valid body.", nameof(body));
		}

		if (!impulse.IsFinite)
		{
			throw new ArgumentOutOfRangeException(nameof(impulse), "An impulse must contain only finite values.");
		}

		if (!worldPoint.IsFinite)
		{
			throw new ArgumentOutOfRangeException(nameof(worldPoint), "An impulse point must contain only finite values.");
		}

		return new(PhysicsCommandKind.ApplyImpulse, body, impulse, worldPoint);
	}
}

public readonly record struct PhysicsBodySnapshot(
	PhysicsBodyId Body,
	PhysicsVector3 Position,
	PhysicsQuaternion Rotation,
	PhysicsVector3 LinearVelocity,
	PhysicsVector3 AngularVelocity);

public readonly record struct PhysicsEvent
{
	private PhysicsEvent(
		PhysicsEventKind kind,
		PhysicsBodyId bodyA,
		PhysicsBodyId bodyB,
		PhysicsJointId joint)
	{
		Kind = kind;
		BodyA = bodyA;
		BodyB = bodyB;
		Joint = joint;
	}

	public PhysicsEventKind Kind { get; }
	public PhysicsBodyId BodyA { get; }
	public PhysicsBodyId BodyB { get; }
	public PhysicsJointId Joint { get; }

	public static PhysicsEvent ContactStarted(PhysicsBodyId bodyA, PhysicsBodyId bodyB) => Contact(PhysicsEventKind.ContactStarted, bodyA, bodyB);
	public static PhysicsEvent ContactPersisted(PhysicsBodyId bodyA, PhysicsBodyId bodyB) => Contact(PhysicsEventKind.ContactPersisted, bodyA, bodyB);
	public static PhysicsEvent ContactEnded(PhysicsBodyId bodyA, PhysicsBodyId bodyB) => Contact(PhysicsEventKind.ContactEnded, bodyA, bodyB);

	public static PhysicsEvent JointBroken(PhysicsJointId joint)
	{
		if (!joint.IsValid)
		{
			throw new ArgumentException("A joint break event must reference a valid joint.", nameof(joint));
		}

		return new(PhysicsEventKind.JointBroken, default, default, joint);
	}

	public static PhysicsEvent BodyCreated(PhysicsBodyId body) => Lifecycle(PhysicsEventKind.BodyCreated, body);
	public static PhysicsEvent BodyDestroyed(PhysicsBodyId body) => Lifecycle(PhysicsEventKind.BodyDestroyed, body);

	private static PhysicsEvent Contact(PhysicsEventKind kind, PhysicsBodyId bodyA, PhysicsBodyId bodyB)
	{
		if (!bodyA.IsValid || !bodyB.IsValid || bodyA == bodyB)
		{
			throw new ArgumentException("A contact event must reference two different valid bodies.");
		}

		return new(kind, bodyA, bodyB, default);
	}

	private static PhysicsEvent Lifecycle(PhysicsEventKind kind, PhysicsBodyId body)
	{
		if (!body.IsValid)
		{
			throw new ArgumentException("A body lifecycle event must reference a valid body.", nameof(body));
		}

		return new(kind, body, default, default);
	}
}

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
	void ApplyCommands(ReadOnlySpan<PhysicsCommand> commands);
	void Step(FixedTimeStep timeStep);
	int CopySnapshots(Span<PhysicsBodySnapshot> destination);
	int DrainEvents(Span<PhysicsEvent> destination);
}
