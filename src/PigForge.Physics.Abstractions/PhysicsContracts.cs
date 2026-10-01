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
	TriangleMesh,
	Compound
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
	ApplyImpulse,

	/// <summary>Skips contact generation for one body pair during the next step. The rules layer
	/// needs it to deliver a bounce: a contact constraint owns the normal velocity for as long as
	/// the pair touches, so a separation speed injected while it is still touching is pulled back
	/// to the constraint's own goal. The pair separates during the skipped step, and the next
	/// step collides normally again.</summary>
	SuppressContact,
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

	public static float Dot(PhysicsVector3 left, PhysicsVector3 right) =>
		(left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

	public static PhysicsVector3 Cross(PhysicsVector3 left, PhysicsVector3 right) => new(
		(left.Y * right.Z) - (left.Z * right.Y),
		(left.Z * right.X) - (left.X * right.Z),
		(left.X * right.Y) - (left.Y * right.X));
}

public readonly record struct PhysicsQuaternion(float X, float Y, float Z, float W)
{
	public static PhysicsQuaternion Identity => new(0, 0, 0, 1);

	public static PhysicsQuaternion FromZAngle(float radians)
	{
		float half = radians * 0.5f;
		return new(0f, 0f, MathF.Sin(half), MathF.Cos(half));
	}

	public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && float.IsFinite(W);

	/// <summary>Conjugate; for the unit quaternions used here this is the inverse rotation.</summary>
	public PhysicsQuaternion Inverse => new(-X, -Y, -Z, W);

	public static PhysicsQuaternion operator *(PhysicsQuaternion left, PhysicsQuaternion right) => new(
		(left.W * right.X) + (left.X * right.W) + (left.Y * right.Z) - (left.Z * right.Y),
		(left.W * right.Y) - (left.X * right.Z) + (left.Y * right.W) + (left.Z * right.X),
		(left.W * right.Z) + (left.X * right.Y) - (left.Y * right.X) + (left.Z * right.W),
		(left.W * right.W) - (left.X * right.X) - (left.Y * right.Y) - (left.Z * right.Z));

	/// <summary>Rotates a vector by this unit quaternion.</summary>
	public PhysicsVector3 Rotate(PhysicsVector3 value)
	{
		PhysicsVector3 u = new(X, Y, Z);
		PhysicsVector3 inner = PhysicsVector3.Cross(u, value) + (value * W);
		return value + (PhysicsVector3.Cross(u, inner) * 2f);
	}

	/// <summary>Prints only the four components: the generated member printer would
	/// recurse through <see cref="Inverse"/>, which is the same type.</summary>
	private bool PrintMembers(System.Text.StringBuilder builder)
	{
		builder.Append('(').Append(X).Append(", ").Append(Y).Append(", ").Append(Z).Append(", ").Append(W).Append(')');
		return true;
	}
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

/// <summary>Per-body surface properties. Restitution is honoured by the backend when
/// <see cref="PhysicsCapabilities.AppliesRestitutionNatively"/> is true (Jolt); a backend
/// without native support (Bepu v2 has no restitution term at all) still reports the
/// pre-solve contact normal and approach speed so the rules layer can synthesize the
/// same bounce as a deterministic impulse pair (see GameplayRules restitution).</summary>
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

public sealed record SphereShapeDefinition : ShapeDefinition
{
	public SphereShapeDefinition(float radius)
		: base(PhysicsShapeKind.Sphere)
	{
		if (!float.IsFinite(radius) || radius <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(radius), radius, "A sphere radius must be finite and positive.");
		}

		Radius = radius;
	}

	public float Radius { get; }
}


/// <summary>One child of a compound shape: a leaf shape at a fixed offset from the body origin.</summary>
public sealed record CompoundChild(ShapeDefinition Shape, PhysicsVector3 Offset, PhysicsQuaternion Rotation)
{
	public CompoundChild(ShapeDefinition shape, PhysicsVector3 offset)
		: this(shape, offset, PhysicsQuaternion.Identity)
	{
	}
}

/// <summary>
/// A rigid composition of leaf shapes. Child offsets are expressed relative to the body
/// origin, and the body origin is the assembly's centre of mass — creators position the
/// body accordingly so snapshots report the centre-of-mass frame.
/// </summary>
public sealed record CompoundShapeDefinition : ShapeDefinition
{
	public CompoundShapeDefinition(IReadOnlyList<CompoundChild> children)
		: base(PhysicsShapeKind.Compound)
	{
		ArgumentNullException.ThrowIfNull(children);
		if (children.Count == 0)
		{
			throw new ArgumentException("A compound shape requires at least one child.", nameof(children));
		}

		foreach (CompoundChild? child in children)
		{
			if (child is null || child.Shape is null)
			{
				throw new ArgumentException("A compound shape cannot contain null children.", nameof(children));
			}

			if (child.Shape is CompoundShapeDefinition)
			{
				throw new ArgumentException("A compound shape cannot contain nested compounds.", nameof(children));
			}

			if (!child.Offset.IsFinite)
			{
				throw new ArgumentOutOfRangeException(nameof(children), "A compound child offset must contain only finite values.");
			}

			if (!child.Rotation.IsFinite)
			{
				throw new ArgumentOutOfRangeException(nameof(children), "A compound child rotation must contain only finite values.");
			}
		}

		Children = children;
	}

	public IReadOnlyList<CompoundChild> Children { get; }
}

/// <summary>Volume of a leaf shape; compounds weigh their children by it.</summary>
public static class ShapeMetrics
{
	public static float Volume(ShapeDefinition shape)
	{
		ArgumentNullException.ThrowIfNull(shape);
		return shape switch
		{
			BoxShapeDefinition box => 8f * box.HalfExtentX * box.HalfExtentY * box.HalfExtentZ,
			SphereShapeDefinition sphere => (4f / 3f) * MathF.PI * sphere.Radius * sphere.Radius * sphere.Radius,
			_ => throw new NotSupportedException($"Shape kind {shape.Kind} has no volume.")
		};
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


/// <summary>
/// A constraint between two dynamic bodies. Anchors and axes are expressed in each
/// body's local frame; a <see cref="PhysicsJointKind.Revolute"/> joint needs unit axes.
/// A <see cref="PhysicsJointKind.Distance"/> joint is a rope: the two anchors may be
/// anywhere between <see cref="MinimumDistance"/> and <see cref="MaximumDistance"/> apart,
/// with the given spring pulling toward that band.
/// </summary>
public sealed record JointDefinition
{
	public JointDefinition(
		PhysicsJointKind kind,
		PhysicsBodyId bodyA,
		PhysicsBodyId bodyB,
		PhysicsConstraintMask constraints,
		float breakForce,
		float breakTorque,
		PhysicsVector3 localAnchorA = default,
		PhysicsVector3 localAnchorB = default,
		PhysicsVector3 localAxisA = default,
		PhysicsVector3 localAxisB = default,
		float minimumDistance = 0f,
		float maximumDistance = 0f,
		float springFrequency = 0f,
		float springDampingRatio = 1f)
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

		if (!localAnchorA.IsFinite || !localAnchorB.IsFinite || !localAxisA.IsFinite || !localAxisB.IsFinite)
		{
			throw new ArgumentException("Joint anchors and axes must be finite.");
		}

		if (kind == PhysicsJointKind.Revolute
			&& (localAxisA == PhysicsVector3.Zero || localAxisB == PhysicsVector3.Zero))
		{
			throw new ArgumentException("A revolute joint requires a non-zero local axis on both bodies.", nameof(localAxisA));
		}

		if (kind == PhysicsJointKind.Distance)
		{
			if (!float.IsFinite(minimumDistance) || !float.IsFinite(maximumDistance)
				|| minimumDistance < 0f || maximumDistance <= 0f || minimumDistance > maximumDistance)
			{
				throw new ArgumentOutOfRangeException(nameof(maximumDistance), maximumDistance, "A distance joint needs 0 <= minimumDistance <= maximumDistance and a positive maximum.");
			}

			if (!float.IsFinite(springFrequency) || springFrequency <= 0f
				|| !float.IsFinite(springDampingRatio) || springDampingRatio < 0f)
			{
				throw new ArgumentOutOfRangeException(nameof(springFrequency), springFrequency, "A distance joint needs a positive spring frequency and a non-negative damping ratio.");
			}
		}

		Kind = kind;
		BodyA = bodyA;
		BodyB = bodyB;
		Constraints = constraints;
		BreakForce = breakForce;
		BreakTorque = breakTorque;
		LocalAnchorA = localAnchorA;
		LocalAnchorB = localAnchorB;
		LocalAxisA = localAxisA;
		LocalAxisB = localAxisB;
		MinimumDistance = minimumDistance;
		MaximumDistance = maximumDistance;
		SpringFrequency = springFrequency;
		SpringDampingRatio = springDampingRatio;
	}

	public PhysicsJointKind Kind { get; }
	public PhysicsBodyId BodyA { get; }
	public PhysicsBodyId BodyB { get; }
	public PhysicsConstraintMask Constraints { get; }
	public float BreakForce { get; }
	public float BreakTorque { get; }
	public PhysicsVector3 LocalAnchorA { get; }
	public PhysicsVector3 LocalAnchorB { get; }
	public PhysicsVector3 LocalAxisA { get; }
	public PhysicsVector3 LocalAxisB { get; }

	/// <summary>Rope band for <see cref="PhysicsJointKind.Distance"/>; zero otherwise.</summary>
	public float MinimumDistance { get; }

	public float MaximumDistance { get; }

	/// <summary>Spring of a distance joint in Hz; zero for other kinds.</summary>
	public float SpringFrequency { get; }

	public float SpringDampingRatio { get; }
}

public readonly record struct PhysicsCommand
{
	private PhysicsCommand(
		PhysicsCommandKind kind,
		PhysicsBodyId body,
		PhysicsBodyId secondBody,
		PhysicsVector3 impulse,
		PhysicsVector3 worldPoint)
	{
		Kind = kind;
		Body = body;
		SecondBody = secondBody;
		Impulse = impulse;
		WorldPoint = worldPoint;
	}

	public PhysicsCommandKind Kind { get; }
	public PhysicsBodyId Body { get; }

	/// <summary>The other end of a <see cref="PhysicsCommandKind.SuppressContact"/> pair; invalid
	/// for every other command kind.</summary>
	public PhysicsBodyId SecondBody { get; }

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

		return new(PhysicsCommandKind.ApplyImpulse, body, default, impulse, worldPoint);
	}

	/// <summary>Skips contact generation between two bodies for exactly one step.</summary>
	public static PhysicsCommand SuppressContact(PhysicsBodyId body, PhysicsBodyId secondBody)
	{
		if (!body.IsValid || !secondBody.IsValid || body == secondBody)
		{
			throw new ArgumentException("A suppressed contact must reference two different valid bodies.");
		}

		return new(PhysicsCommandKind.SuppressContact, body, secondBody, default, default);
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
		PhysicsJointId joint,
		PhysicsVector3 contactNormal,
		float approachSpeed)
	{
		Kind = kind;
		BodyA = bodyA;
		BodyB = bodyB;
		Joint = joint;
		ContactNormal = contactNormal;
		ApproachSpeed = approachSpeed;
	}

	public PhysicsEventKind Kind { get; }
	public PhysicsBodyId BodyA { get; }
	public PhysicsBodyId BodyB { get; }
	public PhysicsJointId Joint { get; }

	/// <summary>Unit contact normal oriented so that pushing <see cref="BodyA"/> along
	/// <c>+ContactNormal</c> and <see cref="BodyB"/> along <c>-ContactNormal</c> separates the
	/// pair. Only contact events carry it; other kinds report <see cref="PhysicsVector3.Zero"/>.</summary>
	public PhysicsVector3 ContactNormal { get; }

	/// <summary>Relative approach speed along <see cref="ContactNormal"/>, measured before the
	/// solver ran, in metres per second; zero for a pair that is merely resting or sliding.
	/// Only contact events carry it.</summary>
	public float ApproachSpeed { get; }

	public static PhysicsEvent ContactStarted(
		PhysicsBodyId bodyA,
		PhysicsBodyId bodyB,
		PhysicsVector3 contactNormal = default,
		float approachSpeed = 0f) =>
		Contact(PhysicsEventKind.ContactStarted, bodyA, bodyB, contactNormal, approachSpeed);

	public static PhysicsEvent ContactPersisted(
		PhysicsBodyId bodyA,
		PhysicsBodyId bodyB,
		PhysicsVector3 contactNormal = default,
		float approachSpeed = 0f) =>
		Contact(PhysicsEventKind.ContactPersisted, bodyA, bodyB, contactNormal, approachSpeed);

	public static PhysicsEvent ContactEnded(PhysicsBodyId bodyA, PhysicsBodyId bodyB) =>
		Contact(PhysicsEventKind.ContactEnded, bodyA, bodyB, default, 0f);

	public static PhysicsEvent JointBroken(PhysicsJointId joint)
	{
		if (!joint.IsValid)
		{
			throw new ArgumentException("A joint break event must reference a valid joint.", nameof(joint));
		}

		return new(PhysicsEventKind.JointBroken, default, default, joint, default, 0f);
	}

	public static PhysicsEvent BodyCreated(PhysicsBodyId body) => Lifecycle(PhysicsEventKind.BodyCreated, body);
	public static PhysicsEvent BodyDestroyed(PhysicsBodyId body) => Lifecycle(PhysicsEventKind.BodyDestroyed, body);

	private static PhysicsEvent Contact(
		PhysicsEventKind kind,
		PhysicsBodyId bodyA,
		PhysicsBodyId bodyB,
		PhysicsVector3 contactNormal,
		float approachSpeed)
	{
		if (!bodyA.IsValid || !bodyB.IsValid || bodyA == bodyB)
		{
			throw new ArgumentException("A contact event must reference two different valid bodies.");
		}

		if (!contactNormal.IsFinite)
		{
			throw new ArgumentOutOfRangeException(nameof(contactNormal), "A contact normal must contain only finite values.");
		}

		if (!float.IsFinite(approachSpeed) || approachSpeed < 0f)
		{
			throw new ArgumentOutOfRangeException(nameof(approachSpeed), "An approach speed must be finite and non-negative.");
		}

		if (approachSpeed > 0f && contactNormal == PhysicsVector3.Zero)
		{
			throw new ArgumentException("A contact event with a non-zero approach speed needs a contact normal.", nameof(contactNormal));
		}

		return new(kind, bodyA, bodyB, default, contactNormal, approachSpeed);
	}

	private static PhysicsEvent Lifecycle(PhysicsEventKind kind, PhysicsBodyId body)
	{
		if (!body.IsValid)
		{
			throw new ArgumentException("A body lifecycle event must reference a valid body.", nameof(body));
		}

		return new(kind, body, default, default, default, 0f);
	}
}

public sealed record PhysicsCapabilities(
	IReadOnlySet<PhysicsJointKind> SupportedJointKinds,
	bool SupportsContinuousCollision,
	bool SupportsPerBodyInertia,
	/// <summary>True when the backend owns the restitution term and applies it itself (Jolt);
	/// false when the rules layer must synthesize the bounce instead (Bepu v2 has no
	/// restitution term), so the two are never applied together.</summary>
	bool AppliesRestitutionNatively);

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
