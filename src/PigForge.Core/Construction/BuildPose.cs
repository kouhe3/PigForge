using PigForge.Physics.Abstractions;

namespace PigForge.Core.Construction;

/// <summary>
/// The build plane's pose algebra: a placed part's orientation is a <b>yaw plus a handedness</b>,
/// not a rotation. The original's <c>BasePart.SetFlipped</c> (BasePart.cs:639-651) turns the part
/// 180 degrees about its own up axis <i>inside its own frame</i> -- per part class, and before the
/// grid yaw (<c>Tail.cs:106-121</c>: <c>localRotation = Euler(num3, num4, 90 * num2)</c> with the
/// 180 applied about X or Y depending on the quarter turn, which composes to
/// <c>Rz(yaw) * Ry(180)</c>). That transform is a rotation, but it is a <b>mirror in the plane</b>:
/// no float yaw can express it, which is why the mirror travels as its own bit on the wire
/// (ADR-030) and the client draws a mirrored sprite rather than a turned one.
///
/// Chirality is deliberately not recoverable from a quaternion: a mirrored pose at a quarter turn
/// is exactly a 180-degree rotation about an in-plane axis, so (yaw, mirror) and (yaw + 180,
/// mirror) can describe the same rotation. The build state therefore keeps the mirror bit beside
/// the pose (<see cref="ConstructionRules"/>) and only composes/decomposes it here for the geometry
/// that needs it, which is why <see cref="IsMirrored"/> is only meaningful for a pose this class
/// produced.
/// </summary>
public static class BuildPose
{
    /// <summary>The original's flip as a rotation: <c>Quaternion.AngleAxis(180, Vector3.up)</c>.</summary>
    private static PhysicsQuaternion Flip => new(0f, 1f, 0f, 0f);

    /// <summary>Build-plane pose quaternion: <c>Rz(yaw)</c>, mirrored as <c>Rz(yaw) * Ry(180)</c>.</summary>
    public static PhysicsQuaternion Rotation(float yaw, bool mirrored)
    {
        PhysicsQuaternion rotation = PhysicsQuaternion.FromZAngle(yaw);
        return mirrored ? rotation * Flip : rotation;
    }

    /// <summary>
    /// True when the pose carries the mirror. Exact for a pose from <see cref="Rotation"/> (the
    /// mirrored form always has its local +Z pointing at -Z); a tumbled physics pose is not a build
    /// pose and this must not be asked of one.
    /// </summary>
    public static bool IsMirrored(PhysicsQuaternion rotation) =>
        1f - (2f * ((rotation.X * rotation.X) + (rotation.Y * rotation.Y))) < 0f;

    /// <summary>
    /// The pose's yaw: the Z angle of <c>rotation</c> for an unmirrored pose, and of
    /// <c>rotation * Ry(-180)</c> for a mirrored one (the mirror cancels, leaving the yaw).
    /// </summary>
    public static float YawOf(PhysicsQuaternion rotation, bool mirrored)
    {
        if (mirrored)
        {
            rotation *= Flip;
        }

        return MathF.Atan2(
            2f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1f - (2f * ((rotation.Y * rotation.Y) + (rotation.Z * rotation.Z))));
    }
}
