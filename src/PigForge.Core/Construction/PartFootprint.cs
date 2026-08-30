using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Construction;

/// <summary>
/// Build-plane projection of a placed part's collision shape: boxes become rotated
/// rectangles, spheres become circles. Used for spatial-hash coarse filtering, exact
/// overlap tests and proximity connections. The build plane is Z = 0 and rotations
/// project to their Z yaw; <paramref name="scale"/> multiplies linear dimensions.
/// </summary>
public readonly record struct PartFootprint
{
    private PartFootprint(
        PhysicsShapeKind kind,
        float halfExtentX,
        float halfExtentY,
        float radius,
        float positionX,
        float positionY,
        float cos,
        float sin)
    {
        Kind = kind;
        HalfExtentX = halfExtentX;
        HalfExtentY = halfExtentY;
        Radius = radius;
        PositionX = positionX;
        PositionY = positionY;
        Cos = cos;
        Sin = sin;
    }

    public PhysicsShapeKind Kind { get; }

    public float HalfExtentX { get; }

    public float HalfExtentY { get; }

    public float Radius { get; }

    public float PositionX { get; }

    public float PositionY { get; }

    public float Cos { get; }

    public float Sin { get; }

    public static PartFootprint ForPart(PartDefinition part, float positionX, float positionY, float angle, float scale)
    {
        PartShapeDefinition shape = part.Shapes[0];
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        return shape.Kind switch
        {
            PhysicsShapeKind.Box when shape.BoxHalfExtents is { Length: 3 } halfExtents
                => new(PhysicsShapeKind.Box, halfExtents[0] * scale, halfExtents[1] * scale, 0f, positionX, positionY, cos, sin),
            PhysicsShapeKind.Sphere when shape.Radius is float radius
                => new(PhysicsShapeKind.Sphere, 0f, 0f, radius * scale, positionX, positionY, cos, sin),
            _ => throw new NotSupportedException($"Part type {part.PartTypeId} has no build-plane footprint rule for shape kind {shape.Kind}.")
        };
    }

    public static PartFootprint ForPart(PartDefinition part, PhysicsVector3 position, PhysicsQuaternion rotation, float scale)
    {
        // Physics poses may tumble; the build plane projects them to their Z yaw.
        float yaw = MathF.Atan2(
            2f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1f - (2f * ((rotation.Y * rotation.Y) + (rotation.Z * rotation.Z))));
        return ForPart(part, position.X, position.Y, yaw, scale);
    }

    /// <summary>AABB in the build plane, expanded by <paramref name="margin"/>.</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) Bounds(float margin = 0f)
    {
        if (Kind == PhysicsShapeKind.Sphere)
        {
            return (PositionX - Radius - margin, PositionY - Radius - margin, PositionX + Radius + margin, PositionY + Radius + margin);
        }

        float extentX = (MathF.Abs(HalfExtentX * Cos) + MathF.Abs(HalfExtentY * Sin)) + margin;
        float extentY = (MathF.Abs(HalfExtentX * Sin) + MathF.Abs(HalfExtentY * Cos)) + margin;
        return (PositionX - extentX, PositionY - extentY, PositionX + extentX, PositionY + extentY);
    }

    /// <summary>
    /// Exact planar overlap test. A negative <paramref name="margin"/> shrinks both
    /// shapes, so marginal penetrations count as legal touching.
    /// </summary>
    public bool Overlaps(in PartFootprint other, float margin = 0f)
    {
        bool circle = Kind == PhysicsShapeKind.Sphere;
        bool otherCircle = other.Kind == PhysicsShapeKind.Sphere;
        return (circle, otherCircle) switch
        {
            (true, true) => CircleOverlapsCircle(in other, margin),
            (true, false) => CircleOverlapsRect(in other, margin),
            (false, true) => other.CircleOverlapsRect(in this, margin),
            _ => RectOverlapsRect(in other, margin)
        };
    }

    private bool RectOverlapsRect(in PartFootprint other, float margin)
    {
        float dx = other.PositionX - PositionX;
        float dy = other.PositionY - PositionY;

        // Separating-axis test on the two axis pairs; touching counts as separated.
        float projected = MathF.Abs((dx * Cos) + (dy * Sin));
        float extent = HalfExtentX
            + (other.HalfExtentX * MathF.Abs((other.Cos * Cos) + (other.Sin * Sin)))
            + (other.HalfExtentY * MathF.Abs((other.Cos * -Sin) + (other.Sin * Cos)))
            + margin;
        if (projected > extent)
        {
            return false;
        }

        projected = MathF.Abs((dx * -Sin) + (dy * Cos));
        extent = HalfExtentY
            + (other.HalfExtentX * MathF.Abs((other.Cos * -Sin) + (other.Sin * Cos)))
            + (other.HalfExtentY * MathF.Abs((other.Cos * Cos) + (other.Sin * Sin)))
            + margin;
        if (projected > extent)
        {
            return false;
        }

        projected = MathF.Abs((dx * other.Cos) + (dy * other.Sin));
        extent = other.HalfExtentX
            + (HalfExtentX * MathF.Abs((Cos * other.Cos) + (Sin * other.Sin)))
            + (HalfExtentY * MathF.Abs((Cos * -other.Sin) + (Sin * other.Cos)))
            + margin;
        if (projected > extent)
        {
            return false;
        }

        projected = MathF.Abs((dx * -other.Sin) + (dy * other.Cos));
        extent = other.HalfExtentY
            + (HalfExtentX * MathF.Abs((Cos * -other.Sin) + (Sin * other.Cos)))
            + (HalfExtentY * MathF.Abs((Cos * other.Cos) + (Sin * other.Sin)))
            + margin;
        return projected <= extent;
    }

    private bool CircleOverlapsRect(in PartFootprint rect, float margin)
    {
        float effectiveRadius = Radius + margin;
        if (effectiveRadius <= 0f)
        {
            return false;
        }

        float dx = PositionX - rect.PositionX;
        float dy = PositionY - rect.PositionY;
        float localX = (dx * rect.Cos) + (dy * rect.Sin);
        float localY = (dx * -rect.Sin) + (dy * rect.Cos);
        float clampedX = Math.Clamp(localX, -rect.HalfExtentX, rect.HalfExtentX);
        float clampedY = Math.Clamp(localY, -rect.HalfExtentY, rect.HalfExtentY);
        float offsetX = localX - clampedX;
        float offsetY = localY - clampedY;
        return (offsetX * offsetX) + (offsetY * offsetY) < (effectiveRadius * effectiveRadius);
    }

    private bool CircleOverlapsCircle(in PartFootprint other, float margin)
    {
        float effectiveRadius = Radius + other.Radius + margin;
        if (effectiveRadius <= 0f)
        {
            return false;
        }

        float dx = other.PositionX - PositionX;
        float dy = other.PositionY - PositionY;
        return (dx * dx) + (dy * dy) < (effectiveRadius * effectiveRadius);
    }
}
