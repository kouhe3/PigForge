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

	/// <summary>
	/// A Unity-ConfigurableJoint-shaped linear drive: one driven axis (the original's
	/// <c>yMotion Limited</c> plus its yDrive), a second driven axis (its xDrive), a rigidly
	/// locked third axis (zMotion Locked), a soft band limit on the driven axis and a locked
	/// relative rotation (angular XYZ Locked). The payload travels in
	/// <see cref="JointDefinition.Configurable"/>; the boxing glove is the first consumer
	/// (docs/specs/boxing-glove.md §3). Like <see cref="Revolute"/>, this kind suppresses
	/// contacts between its two bodies: the original's glove is <c>IgnoreCollision</c>'d
	/// against the part it belongs to, exactly as a wheel's tire is against its mounts.
	/// </summary>
	Configurable,

	/// <summary>
	/// A rigid six-degree-of-freedom weld with an optional sprung compliance: the two local
	/// anchors are held coincident and the two bodies' local frames aligned, which is the pose
	/// the seam split that consumes this kind builds both halves in. A zero
	/// <see cref="JointDefinition.SpringFrequency"/> is rigid; a positive one turns the whole
	/// constraint into a spring, the mechanism the weld-compliance spec fits to the original's
	/// bends.
	/// </summary>
	Weld
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

/// <summary>
/// Unity's <c>PhysicMaterialCombine</c>: how two surfaces' friction coefficients blend. The
/// original's assets use Average (the built-in default) and Multiply (the wheel tyres, which is
/// what makes a tire slide); Unity resolves a pair by the higher-priority mode,
/// Average &lt; Minimum &lt; Multiply &lt; Maximum, and applies that mode alone.
/// </summary>
public enum FrictionCombine
{
	Average = 0,
	Minimum = 1,
	Multiply = 2,
	Maximum = 3
}

/// <summary>Per-body surface properties. Restitution is honoured by the backend when
/// <see cref="PhysicsCapabilities.AppliesRestitutionNatively"/> is true (Jolt); a backend
/// without native support (Bepu v2 has no restitution term at all) still reports the
/// pre-solve contact normal and approach speed so the rules layer can synthesize the
/// same bounce as a deterministic impulse pair (see GameplayRules restitution).</summary>
public sealed record PhysicsMaterial(float Restitution, float Friction, FrictionCombine FrictionCombine = FrictionCombine.Average)
{
	public static PhysicsMaterial Default { get; } = new(Restitution: 0f, Friction: 0.8f);

	/// <summary>
	/// Unity's pair blend: the pair uses whichever side declares the higher-priority combine
	/// mode and applies that mode alone to both coefficients. Two Average surfaces therefore
	/// average (the historical PigForge behaviour); a Multiply tyre against an Average ground
	/// multiplies instead of averaging, which is what the original actually runs.
	/// </summary>
	public float FrictionWith(PhysicsMaterial other) => (FrictionCombine)Math.Max((int)FrictionCombine, (int)other.FrictionCombine) switch
	{
		FrictionCombine.Minimum => MathF.Min(Friction, other.Friction),
		FrictionCombine.Multiply => Friction * other.Friction,
		FrictionCombine.Maximum => MathF.Max(Friction, other.Friction),
		_ => (Friction + other.Friction) * 0.5f
	};
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
		PhysicsMaterial? material = null,
		PhysicsConstraintMask constraints = PhysicsConstraintMask.None,
		float linearDamping = 0f,
		float angularDamping = 0f,
		float maximumAngularSpeed = 0f)
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

		if (!float.IsFinite(linearDamping) || linearDamping < 0f || !float.IsFinite(angularDamping) || angularDamping < 0f)
		{
			throw new ArgumentOutOfRangeException(nameof(linearDamping), linearDamping, "Damping must be finite and non-negative.");
		}

		if (!float.IsFinite(maximumAngularSpeed) || maximumAngularSpeed < 0f)
		{
			throw new ArgumentOutOfRangeException(nameof(maximumAngularSpeed), maximumAngularSpeed, "A maximum angular speed must be finite and non-negative (zero means unlimited).");
		}

		Mode = mode;
		Position = position;
		Rotation = rotation;
		Mass = mass;
		LinearVelocity = linearVelocity;
		AngularVelocity = angularVelocity;
		Material = material ?? PhysicsMaterial.Default;
		Shapes = shapes;
		Constraints = constraints;
		LinearDamping = linearDamping;
		AngularDamping = angularDamping;
		MaximumAngularSpeed = maximumAngularSpeed;
	}

	public PhysicsBodyMode Mode { get; }
	public PhysicsVector3 Position { get; }
	public PhysicsQuaternion Rotation { get; }
	public float Mass { get; }
	public PhysicsVector3 LinearVelocity { get; }
	public PhysicsVector3 AngularVelocity { get; }
	public PhysicsMaterial Material { get; }
	public IReadOnlyList<ShapeDefinition> Shapes { get; }

	/// <summary>
	/// Degrees of freedom the body must never integrate. The original is 2.5D: every
	/// contraption body freezes the Z translation and the X/Y rotations
	/// (<c>RigidbodyConstraints</c> 56 — <c>Sandbag.cs:135</c>, <c>Pig.cs:235</c>), so the
	/// build plane is the only plane a body can leave. <see cref="PhysicsConstraintMask.None"/>
	/// leaves the body unconstrained, which is what the physics tests and the replay path use.
	/// </summary>
	public PhysicsConstraintMask Constraints { get; }

	/// <summary>
	/// Unity's <c>Rigidbody.drag</c> (renamed <c>linearDamping</c> in Unity 6): the original's
	/// every part carries one, defaulting to <c>BasePart.EnsureRigidbody</c>'s 0.2 and rising to
	/// 1 on a wing or tail, 2 on a balloon, 10 on a sandbag and 0.5 on the king pig
	/// (<c>tools/bple-damping</c>). The backend applies it as the original's PhysX does,
	/// <c>v *= max(0, 1 - linearDamping * dt)</c>, after gravity and before the solver
	/// (<c>DyBodyCoreIntegrator.h::bodyCoreComputeUnconstrainedVelocity</c>). Zero is frictionless.
	/// </summary>
	public float LinearDamping { get; }

	/// <summary>Unity's <c>Rigidbody.angularDrag</c> (<c>angularDamping</c> in Unity 6), the same
	/// value family as <see cref="LinearDamping"/> — 0.05 by default, 0.2 on a wing or tail, 0.5 on
	/// a balloon, 10 on a sandbag, 1 on the king pig.</summary>
	public float AngularDamping { get; }

	/// <summary>
	/// Unity's <c>Rigidbody.maxAngularVelocity</c>, in radians per second; zero means unlimited.
	/// The original never sets it, so every one of its rigidbodies inherits the project default
	/// <c>m_DefaultMaxAngularSpeed: 7</c> (<c>ProjectSettings/DynamicsManager.asset</c>) — PhysX
	/// clamps the <b>magnitude</b> of the angular velocity, after damping and before the solver
	/// (<c>DyBodyCoreIntegrator.h</c>: <c>if (angVelSq &gt; maxAngularVelocitySq)
	/// angularVelocity *= PxSqrt(maxAngularVelocitySq / angVelSq)</c>). Content carries it once, at
	/// the document level, because it is a project-wide default rather than a per-part choice
	/// (<c>tools/bple-damping</c>).
	/// </summary>
	public float MaximumAngularSpeed { get; }
}


/// <summary>
/// The linear-drive payload of a <see cref="PhysicsJointKind.Configurable"/> joint: Unity's
/// ConfigurableJoint reduced to what the original's parts actually use. Three orthogonal
/// directions are anchored in body A's frame:
/// <list type="bullet">
/// <item><see cref="DriveAxisInA"/> is driven to <see cref="DriveTargetOffset"/> by a spring
/// (the original's <c>yMotion Limited</c> plus its yDrive with a targetPosition), and carries
/// the soft band limit <see cref="LimitMinimumOffset"/>..<see cref="LimitMaximumOffset"/> —
/// rigid when <see cref="LimitFrequency"/> is zero;</item>
/// <item><see cref="LateralAxisInA"/> is driven to <see cref="LateralTargetOffset"/> (its
/// xDrive);</item>
/// <item>the axis perpendicular to both is held rigidly at zero, the original's
/// <c>zMotion Locked</c>.</item>
/// </list>
/// Body B's rotation relative to A is held at <see cref="JointDefinition.RestRotation"/>
/// (angular XYZ Locked). Distances are metres along the named axis, spring frequencies are Hz
/// and damping ratios are the solver's ratio (see <see cref="JointDefinition.SpringFrequency"/>).
/// </summary>
public sealed record ConfigurableJointDefinition(
	PhysicsVector3 DriveAxisInA,
	float DriveTargetOffset,
	float DriveFrequency,
	float DriveDampingRatio,
	PhysicsVector3 LateralAxisInA,
	float LateralTargetOffset,
	float LateralFrequency,
	float LateralDampingRatio,
	float LimitMinimumOffset,
	float LimitMaximumOffset,
	float LimitFrequency,
	float LimitDampingRatio)
{
	/// <summary>Rejects a payload no backend can build: a zero or parallel axis pair, a
	/// reversed limit band, or a non-positive drive frequency.</summary>
	internal void Validate(string parameterName)
	{
		if (!DriveAxisInA.IsFinite || !LateralAxisInA.IsFinite
			|| DriveAxisInA == PhysicsVector3.Zero || LateralAxisInA == PhysicsVector3.Zero)
		{
			throw new ArgumentException("A configurable joint needs two non-zero axis directions.", parameterName);
		}

		if (PhysicsVector3.Cross(DriveAxisInA, LateralAxisInA) == PhysicsVector3.Zero)
		{
			throw new ArgumentException("A configurable joint's two driven axes must not be parallel.", parameterName);
		}

		if (!float.IsFinite(DriveTargetOffset) || !float.IsFinite(LateralTargetOffset)
			|| !float.IsFinite(LimitMinimumOffset) || !float.IsFinite(LimitMaximumOffset)
			|| LimitMinimumOffset > LimitMaximumOffset)
		{
			throw new ArgumentOutOfRangeException(parameterName, "A configurable joint needs finite targets and 0 <= limitMinimum <= limitMaximum.");
		}

		if (!float.IsFinite(DriveFrequency) || DriveFrequency <= 0f || !float.IsFinite(DriveDampingRatio) || DriveDampingRatio < 0f
			|| !float.IsFinite(LateralFrequency) || LateralFrequency <= 0f || !float.IsFinite(LateralDampingRatio) || LateralDampingRatio < 0f
			|| !float.IsFinite(LimitFrequency) || LimitFrequency < 0f || !float.IsFinite(LimitDampingRatio) || LimitDampingRatio < 0f)
		{
			throw new ArgumentOutOfRangeException(parameterName, "A configurable joint needs positive drive frequencies and a non-negative, finite limit frequency.");
		}
	}
}

/// <summary>
/// A constraint between two dynamic bodies. Anchors and axes are expressed in each
/// body's local frame; a <see cref="PhysicsJointKind.Revolute"/> joint needs unit axes.
/// A <see cref="PhysicsJointKind.Distance"/> joint is a rope: the two anchors may be
/// anywhere between <see cref="MinimumDistance"/> and <see cref="MaximumDistance"/> apart,
/// with the given spring pulling toward that band. Collapsing the band
/// (<see cref="MinimumDistance"/> == <see cref="MaximumDistance"/>) removes the slack, which is
/// the spring's model: a load-carrying distance link settled at the assembly spacing, exactly
/// what the original's <c>SpringJoint</c> builds at its auto-configured rest pose
/// (docs/specs/spring-joint.md).
/// <see cref="BreakForce"/> and <see cref="BreakImpulse"/> tear a joint down once its reaction
/// grows past a threshold; a backend that enforces them reports
/// <see cref="PhysicsEventKind.JointBroken"/> so the rules layer notices.
/// A <see cref="PhysicsJointKind.Revolute"/> joint is rigid unless it carries a
/// <see cref="LocalSuspensionAxis"/>. That axis then adds a second degree of freedom
/// beside the free spin — a linear one, sprung at <see cref="SuspensionRestOffset"/> —
/// which is how Unity's ConfigurableJoint expresses an elastic wheel (Locked x/z,
/// Limited y, linear-limit spring).
/// A <see cref="PhysicsJointKind.Configurable"/> joint is the same idea generalised: its
/// axes, targets, springs and limit band travel in <see cref="Configurable"/>, and it holds
/// body B's rotation at <see cref="RestRotation"/> instead of leaving a spin axis free.
/// A <see cref="PhysicsJointKind.Weld"/> joint locks all six degrees of freedom at the two
/// local anchors: it holds <see cref="LocalAnchorA"/> (in body A's frame) and
/// <see cref="LocalAnchorB"/> (in body B's frame) coincident and body B's local frame at
/// <see cref="RestRotation"/> inside body A's (identity when omitted), so the rest pose of the
/// pair is fixed by the anchors and that rotation — not by assuming the frames are aligned.
/// A positive
/// <see cref="SpringFrequency"/> softens the whole weld (the original's chain of welded
/// frames bends under a bending moment, see docs/specs/weld-compliance.md); zero keeps it
/// rigid. A weld never suppresses contacts between its two bodies — the original's adjacent
/// parts still collide.
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
		float springDampingRatio = 1f,
		PhysicsVector3 localSuspensionAxis = default,
		float suspensionRestOffset = 0f,
		PhysicsQuaternion? restRotation = null,
		float breakImpulse = 0f,
		ConfigurableJointDefinition? configurable = null)
	{
		PhysicsQuaternion relativeRest = restRotation ?? PhysicsQuaternion.Identity;
		if (!relativeRest.IsFinite)
		{
			throw new ArgumentException("A joint's rest rotation must be finite.", nameof(restRotation));
		}

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

		if (!float.IsFinite(breakImpulse) || breakImpulse < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(breakImpulse), breakImpulse, "Break impulse must be finite and non-negative.");
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

		if (kind == PhysicsJointKind.Weld)
		{
			if (!float.IsFinite(springFrequency) || springFrequency < 0f)
			{
				throw new ArgumentOutOfRangeException(nameof(springFrequency), springFrequency, "A weld joint needs a finite, non-negative spring frequency; zero welds rigidly.");
			}

			if (!float.IsFinite(springDampingRatio) || springDampingRatio < 0f || springDampingRatio > 1f)
			{
				throw new ArgumentOutOfRangeException(nameof(springDampingRatio), springDampingRatio, "A weld joint needs a damping ratio in [0, 1].");
			}
		}

		if (!localSuspensionAxis.IsFinite || !float.IsFinite(suspensionRestOffset))
		{
			throw new ArgumentException("The suspension axis and rest offset must be finite.", nameof(localSuspensionAxis));
		}

		if (kind == PhysicsJointKind.Revolute && (localSuspensionAxis != PhysicsVector3.Zero || springFrequency != 0f))
		{
			if (localSuspensionAxis == PhysicsVector3.Zero)
			{
				throw new ArgumentException("A sprung revolute joint needs the suspension axis in body A's frame.", nameof(localSuspensionAxis));
			}

			if (!float.IsFinite(springFrequency) || springFrequency <= 0f
				|| !float.IsFinite(springDampingRatio) || springDampingRatio < 0f)
			{
				throw new ArgumentOutOfRangeException(nameof(springFrequency), springFrequency, "A sprung revolute joint needs a positive spring frequency and a non-negative damping ratio.");
			}
		}

		if (kind == PhysicsJointKind.Configurable)
		{
			if (configurable is null)
			{
				throw new ArgumentException("A configurable joint needs its linear-drive payload.", nameof(configurable));
			}

			configurable.Validate(nameof(configurable));
		}
		else if (configurable is not null)
		{
			throw new ArgumentException("Only a configurable joint carries a linear-drive payload.", nameof(configurable));
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
		LocalSuspensionAxis = localSuspensionAxis;
		SuspensionRestOffset = suspensionRestOffset;
		RestRotation = relativeRest;
		BreakImpulse = breakImpulse;
		Configurable = configurable;
	}

	public PhysicsJointKind Kind { get; }
	public PhysicsBodyId BodyA { get; }
	public PhysicsBodyId BodyB { get; }
	public PhysicsConstraintMask Constraints { get; }
	public float BreakForce { get; }
	public float BreakTorque { get; }

	/// <summary>
	/// Impulse magnitude (newton-seconds) the joint's reaction may reach before a backend tears it
	/// down; zero never breaks on impulse. The force and impulse thresholds are independent — a
	/// joint breaks when either is exceeded. The Bepu backend enforces them for
	/// <see cref="PhysicsJointKind.Distance"/> only (the spring/rope family, whose reaction is a
	/// single linear constraint impulse); the other kinds keep the rules layer's own impulse break
	/// (ADR-015/ADR-024), and Jolt enforces neither.
	/// </summary>
	public float BreakImpulse { get; }
	public PhysicsVector3 LocalAnchorA { get; }
	public PhysicsVector3 LocalAnchorB { get; }
	public PhysicsVector3 LocalAxisA { get; }
	public PhysicsVector3 LocalAxisB { get; }

	/// <summary>Rope band for <see cref="PhysicsJointKind.Distance"/>; zero otherwise.</summary>
	public float MinimumDistance { get; }

	public float MaximumDistance { get; }

	/// <summary>Spring of a distance joint or of a compliant weld in Hz; zero means a rigid
	/// weld and is unused by the other kinds.</summary>
	public float SpringFrequency { get; }

	/// <summary>Damping ratio of a distance joint or of a compliant weld; unused by the other
	/// kinds.</summary>
	public float SpringDampingRatio { get; }

	/// <summary>
	/// Sprung linear degree of freedom of a <see cref="PhysicsJointKind.Revolute"/> joint, in
	/// body A's local frame (the non-spinning parent: the axis must not ride the wheel's
	/// spin). Zero keeps the joint rigid along every translation.
	/// </summary>
	public PhysicsVector3 LocalSuspensionAxis { get; }

	/// <summary>Offset along <see cref="LocalSuspensionAxis"/> the spring holds the two
	/// anchors at; zero for a rigid revolute joint.</summary>
	public float SuspensionRestOffset { get; }

	/// <summary>
	/// Orientation of body B's local frame in body A's local frame at the weld's rest pose
	/// (<see cref="PhysicsJointKind.Weld"/> only; identity everywhere else). Both backends need
	/// it: a welded frame pair is normally placed at a quarter-turn step, so the weld has to hold
	/// the rotation the pair was built with instead of snapping B's frame onto A's. Body A's
	/// frame is the reference, so the value is <c>A.Rotation.Inverse * B.Rotation</c>.
	/// </summary>
	public PhysicsQuaternion RestRotation { get; }

	/// <summary>
	/// The linear-drive payload of a <see cref="PhysicsJointKind.Configurable"/> joint; null for
	/// every other kind. The anchors come from <see cref="LocalAnchorA"/> /
	/// <see cref="LocalAnchorB"/> and the held relative orientation from
	/// <see cref="RestRotation"/> — the same rest-pose contract a weld uses.
	/// </summary>
	public ConfigurableJointDefinition? Configurable { get; }

	/// <summary>
	/// The <see cref="PhysicsJointKind.Weld"/> factory: a six-degree-of-freedom weld holding
	/// the two local anchors coincident with body B's frame at <paramref name="restRotation"/>
	/// inside body A's (identity when omitted, i.e. the frames aligned). A zero
	/// <paramref name="springFrequency"/> welds rigidly; a positive one makes the whole
	/// constraint compliant at that frequency (band-limited to a damping ratio in [0, 1]).
	/// </summary>
	public static JointDefinition Weld(
		PhysicsBodyId bodyA,
		PhysicsBodyId bodyB,
		PhysicsVector3 localAnchorA = default,
		PhysicsVector3 localAnchorB = default,
		float breakForce = 0f,
		float breakTorque = 0f,
		float springFrequency = 0f,
		float springDampingRatio = 1f,
		PhysicsConstraintMask constraints = PhysicsConstraintMask.None,
		PhysicsQuaternion? restRotation = null) => new(
			PhysicsJointKind.Weld,
			bodyA,
			bodyB,
			constraints,
			breakForce,
			breakTorque,
			localAnchorA,
			localAnchorB,
			springFrequency: springFrequency,
			springDampingRatio: springDampingRatio,
			restRotation: restRotation);
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
	/// pair. It is the surface's own normal, so a body resting or sliding on something keeps
	/// reporting it (a driven wheel reads it as the ground it stands on, MotorWheel.cs:288).
	/// Only contact events carry it; other kinds report <see cref="PhysicsVector3.Zero"/>.</summary>
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

	/// <summary>
	/// Reshapes a dynamic body's mass without moving it: the local inertia tensor scales with the
	/// mass (uniform density), so the velocity and pose the body already carries survive. The
	/// original changes a rigidbody's mass at runtime — the glove goes limp (0.5 kg to 0.01 kg)
	/// for its wind-back (<c>SpringBoxingGlove.cs:280-330</c>) — and the pose must survive,
	/// because the body is mid-flight when it happens.
	/// </summary>
	void SetBodyMass(PhysicsBodyId body, float mass);

	/// <summary>
	/// Turns a body's collider on or off for the rest of the run: a disabled body generates no
	/// contact pair in either direction (the original's <c>Collider.enabled = false</c>, which
	/// the glove uses to bring the limp glove home through everything). The body keeps its shape
	/// and its snapshots; only its contacts go.
	/// </summary>
	void SetBodyCollisionEnabled(PhysicsBodyId body, bool enabled);

	PhysicsJointId CreateJoint(JointDefinition definition);
	void DestroyJoint(PhysicsJointId joint);
	void ApplyCommands(ReadOnlySpan<PhysicsCommand> commands);
	void Step(FixedTimeStep timeStep);
	int CopySnapshots(Span<PhysicsBodySnapshot> destination);
	int DrainEvents(Span<PhysicsEvent> destination);
}
