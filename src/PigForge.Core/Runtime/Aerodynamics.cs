using PigForge.Physics.Abstractions;

namespace PigForge.Core;

/// <summary>
/// The original's wing and tail lift, as one clamped <c>|v|^2</c> response curve evaluated in the
/// part's own frame (docs/specs/part-mirror.md). Both classes run the same shape of rule in
/// <c>FixedUpdate</c>:
///
/// <code>
/// Wings.cs:104-118   v = velocity - WindVelocity;  right = transform.right
///                    x = (IsFlipped() ? -1 : 1) * Sign(Cross(v, right).z) * Angle(v, right)
///                    force = liftConstant * v.sqrMagnitude * curve.Get(x) * Cross(transform.forward, v.normalized)
///                    ClampMagnitude(force, 100); AddForce(force, ForceMode.Force)
/// Tail.cs:57-75      num = IsFlipped() ? -1 : 1
///                    num2 = Sign(Cross(Vector3.right, right).z) * Angle(new Vector3(num, 0, 0), right)
///                    right = AngleAxis(0.4 * (num2 - 30), transform.forward) * right
///                    x = num * Sign(Cross(v, right).z) * Angle(v, right)
///                    ... then the wing's force line with liftConstant from the prefab
/// </code>
///
/// Everything here is a source constant, not content: the response-curve tables are class fields
/// built in <c>Start</c>, the 100 N clamp is literals in both classes, and the two classes' only
/// per-prefab input is <c>m_liftConstant</c> (0.8 / 1.5 for the wooden and metal wings, 0.2 / 1.0
/// for the tails), which travels in content as <c>capabilities.wing.liftConstant</c> /
/// <c>capabilities.tail</c> and is extracted by <c>tools/bple-aero</c>.
///
/// The force is what the original adds per <c>FixedUpdate</c>, so PigForge's fixed 60 Hz tick
/// takes <c>/ 60</c> of it as one impulse -- the conversion ADR-013 decision 4 established and
/// every other extracted force in this codebase uses (<c>tools/bple-fans</c>, <c>tools/bple-lift</c>).
///
/// Two behaviours are deliberately <b>not</b> modelled: the original's <c>WindVelocity</c> (it
/// belongs to wind zones, which PigForge has none of, so it is always zero here) and the
/// <c>SwitchableWing</c>/<c>SwitchableTail</c> IN gates (the declaration defaults are false, so a
/// wing and a tail are always live; profile B would add a switch, G105).
/// </summary>
public static class Aerodynamics
{
    /// <summary>The original's per-tick force is a per-second force: one fixed tick is 1/60 s.</summary>
    public const float ForcePerTickDivisor = 60f;

    /// <summary><c>ClampMagnitude(force, 100f)</c> (Wings.cs:115, Tail.cs:72).</summary>
    public const float MaximumForceNewtons = 100f;

    /// <summary>
    /// <c>Mathf.Sign</c> as Unity defines it: <b>1 for zero</b>, not 0 (Unity's
    /// <c>Mathf.Sign(f) = f &gt;= 0 ? 1 : -1</c>). The difference is visible at exactly 180 degrees
    /// of attack, where the cross product's Z is zero while the angle is not.
    /// </summary>
    private static float Sign(float value) => value < 0f ? -1f : 1f;

    /// <summary>
    /// The wing's angle of attack <c>x</c>: the signed angle from the velocity to the part's own
    /// right axis, flipped when the build pose is mirrored (Wings.cs:111).
    /// </summary>
    public static float WingAngleOfAttack(PhysicsVector3 velocity, PhysicsVector3 right, bool mirrored)
    {
        float sign = Sign(PhysicsVector3.Cross(velocity, right).Z);
        return (mirrored ? -1f : 1f) * sign * Angle(velocity, right);
    }

    /// <summary>
    /// The tail's angle of attack: the same signed angle, but measured against the part's right axis
    /// after the original's own <c>0.4 * (num2 - 30)</c> twist about the part's forward axis
    /// (Tail.cs:63-68). <paramref name="mirrored"/> enters twice, exactly as the original does it:
    /// once as <c>num</c> in the twist, and again in the final sign.
    /// </summary>
    public static float TailAngleOfAttack(PhysicsVector3 velocity, PhysicsVector3 right, PhysicsVector3 forward, bool mirrored)
    {
        float num = mirrored ? -1f : 1f;
        float twist = Sign(PhysicsVector3.Cross(new PhysicsVector3(1f, 0f, 0f), right).Z)
            * Angle(new PhysicsVector3(num, 0f, 0f), right);
        right = Rotate(right, forward, 0.4f * (twist - 30f));
        float sign = Sign(PhysicsVector3.Cross(velocity, right).Z);
        return num * sign * Angle(velocity, right);
    }

    /// <summary>
    /// The wing's response curve (Wings.cs:76-88): a piecewise linear table clamped at both ends
    /// (<c>ResponseCurve.Get</c>, ResponseCurve.cs:26-52). Its asymmetry is the aerodynamics: a
    /// small positive angle of attack lifts, a stall past ~19 degrees drops it, and a negative one
    /// pushes down.
    /// </summary>
    public static float WingCoefficient(float angleOfAttack) => Interpolate(
        WingCurveX,
        WingCurveY,
        angleOfAttack);

    /// <summary>The tail's response curve (Tail.cs:32-41), same shape and the same interpolation.</summary>
    public static float TailCoefficient(float angleOfAttack) => Interpolate(
        TailCurveX,
        TailCurveY,
        angleOfAttack);

    /// <summary>
    /// The lift force in newtons per second, before the tick conversion: the original's
    /// <c>liftConstant * |v|^2 * coefficient * Cross(forward, v.normalized)</c>, clamped to
    /// <see cref="MaximumForceNewtons"/>. A velocity of zero produces no force (Unity's
    /// <c>Vector3.normalized</c> of a zero vector is zero, not NaN).
    /// </summary>
    public static PhysicsVector3 Force(
        float liftConstant,
        PhysicsVector3 velocity,
        float coefficient,
        PhysicsVector3 forward)
    {
        float speedSquared = PhysicsVector3.Dot(velocity, velocity);
        if (speedSquared <= 0f || coefficient == 0f)
        {
            return PhysicsVector3.Zero;
        }

        PhysicsVector3 lift = (PhysicsVector3.Cross(forward, PhysicsVector3.Normalize(velocity)) * speedSquared) * (liftConstant * coefficient);
        float magnitude = MathF.Sqrt(PhysicsVector3.Dot(lift, lift));
        return magnitude > MaximumForceNewtons ? lift * (MaximumForceNewtons / magnitude) : lift;
    }

    /// <summary>The part's world direction for a local axis, given the part's pose (ADR-029).</summary>
    public static PhysicsVector3 Axis(PhysicsQuaternion partRotation, PhysicsVector3 localAxis) =>
        partRotation.Rotate(localAxis);

    /// <summary>
    /// Unity's <c>Quaternion.AngleAxis(degrees, axis) * value</c> as Rodrigues' formula, in the
    /// plane the tail's twist needs. A zero axis leaves the vector alone, as Unity's does.
    /// </summary>
    private static PhysicsVector3 Rotate(PhysicsVector3 value, PhysicsVector3 axis, float degrees)
    {
        PhysicsVector3 unit = PhysicsVector3.Normalize(axis);
        if (unit == PhysicsVector3.Zero)
        {
            return value;
        }

        float radians = degrees * (MathF.PI / 180f);
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return (value * cos)
            + (PhysicsVector3.Cross(unit, value) * sin)
            + (unit * (PhysicsVector3.Dot(unit, value) * (1f - cos)));
    }

    /// <summary>Unity's <c>Vector3.Angle</c>: the unsigned angle in degrees, 0 for a zero vector.</summary>
    private static float Angle(PhysicsVector3 from, PhysicsVector3 to)
    {
        float fromLength = MathF.Sqrt(PhysicsVector3.Dot(from, from));
        float toLength = MathF.Sqrt(PhysicsVector3.Dot(to, to));
        if (fromLength <= float.Epsilon || toLength <= float.Epsilon)
        {
            return 0f;
        }

        float cosine = PhysicsVector3.Dot(from, to) / (fromLength * toLength);
        return MathF.Acos(Math.Clamp(cosine, -1f, 1f)) * (180f / MathF.PI);
    }

    private static float Interpolate(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, float x)
    {
        if (x <= xs[0])
        {
            return ys[0];
        }

        for (int index = 0; index < xs.Length - 1; index++)
        {
            if (x <= xs[index + 1])
            {
                float span = xs[index + 1] - xs[index];
                return ys[index] + ((ys[index + 1] - ys[index]) * ((x - xs[index]) / span));
            }
        }

        return ys[^1];
    }

    /// <summary>Wings.cs:76-88, in the order the class adds them.</summary>
    private static readonly float[] WingCurveX = [-180f, -135f, -90f, -45f, -10f, 10f, 15f, 19f, 22f, 45f, 90f, 135f, 180f];

    private static readonly float[] WingCurveY = [0f, -0.2f, 0f, -0.2f, 0f, 1.5f, 1.75f, 0.8f, 0.1f, 0.2f, 0f, -0.2f, 0f];

    /// <summary>Tail.cs:32-41, in the order the class adds them.</summary>
    private static readonly float[] TailCurveX = [-180f, -135f, -90f, -45f, -10f, 10f, 45f, 90f, 135f, 180f];

    private static readonly float[] TailCurveY = [0f, -1.5f, 0f, -1.5f, 0f, 1f, 1.5f, 0f, -1.5f, 0f];
}
