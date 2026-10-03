using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Construction;

/// <summary>
/// Build-plane projection of a placed part's collision shapes: boxes become rotated
/// rectangles, spheres become circles. A part with several colliders projects to the
/// union of its shapes (a wheel is its tire circle plus the support rectangle), so
/// placement overlap and proximity connections see the original's real geometry.
/// The build plane is Z = 0 and rotations project to their Z yaw; <c>scale</c>
/// multiplies linear dimensions and shape offsets.
/// </summary>
public readonly record struct PartFootprint
{
    private readonly FootprintShape[] _body;
    private readonly FootprintShape[] _all;

    private PartFootprint(FootprintShape[] body, FootprintShape[] all)
    {
        _body = body;
        _all = all;
    }

    public static PartFootprint ForPart(PartDefinition part, float positionX, float positionY, float angle, float scale)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        List<FootprintShape> body = new(part.Shapes.Count);
        List<FootprintShape> all = new(part.Shapes.Count);
        List<FootprintShape> bracket = new(part.Shapes.Count);
        for (int index = 0; index < part.Shapes.Count; index++)
        {
            PartShapeDefinition shape = part.Shapes[index];
            (float offsetX, float offsetY) = shape.Offset is { Length: 3 } offset
                ? (((offset[0] * cos) - (offset[1] * sin)) * scale, ((offset[0] * sin) + (offset[1] * cos)) * scale)
                : (0f, 0f);
            float centreX = positionX + offsetX;
            float centreY = positionY + offsetY;
            FootprintShape projected = shape.Kind switch
            {
                PhysicsShapeKind.Box when shape.BoxHalfExtents is { Length: 3 } halfExtents
                    => new FootprintShape(PhysicsShapeKind.Box, halfExtents[0] * scale, halfExtents[1] * scale, 0f, centreX, centreY, cos, sin),
                PhysicsShapeKind.Sphere when shape.Radius is float radius
                    => new FootprintShape(PhysicsShapeKind.Sphere, 0f, 0f, radius * scale, centreX, centreY, cos, sin),
                _ => throw new NotSupportedException($"Part type {part.PartTypeId} has no build-plane footprint rule for shape kind {shape.Kind}.")
            };
            all.Add(projected);
            if (shape.ConditionKind is null)
            {
                body.Add(projected);
            }
            else if (shape.ConditionKind == "frame")
            {
                bracket.Add(projected);
            }
        }

        // A part with a frame is placed and occupancy-checked by that bracket, not by its body:
        // a glider wing's collider is the wing itself, which overhangs the neighbours it welds
        // to, so a body-based overlap test could never let it stand next to anything (ADR-018).
        return new PartFootprint(bracket.Count > 0 ? bracket.ToArray() : body.ToArray(), all.ToArray());
    }

    public static PartFootprint ForPart(PartDefinition part, PhysicsVector3 position, PhysicsQuaternion rotation, float scale)
    {
        // Physics poses may tumble; the build plane projects them to their Z yaw.
        float yaw = MathF.Atan2(
            2f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1f - (2f * ((rotation.Y * rotation.Y) + (rotation.Z * rotation.Z))));
        return ForPart(part, position.X, position.Y, yaw, scale);
    }

    /// <summary>AABB of the shape union in the build plane, expanded by <paramref name="margin"/>.</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) Bounds(float margin = 0f)
    {
        (float minX, float minY, float maxX, float maxY) = _all[0].Bounds(margin);
        for (int index = 1; index < _all.Length; index++)
        {
            (float shapeMinX, float shapeMinY, float shapeMaxX, float shapeMaxY) = _all[index].Bounds(margin);
            minX = MathF.Min(minX, shapeMinX);
            minY = MathF.Min(minY, shapeMinY);
            maxX = MathF.Max(maxX, shapeMaxX);
            maxY = MathF.Max(maxY, shapeMaxY);
        }

        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Exact planar overlap over the part's own body colliders only. Cell occupancy uses this:
    /// a hidden joint attachment bracket is a trigger in the original and never blocks a cell.
    /// A negative <paramref name="margin"/> shrinks every shape, so marginal penetrations count
    /// as legal touching.
    /// </summary>
    public bool Overlaps(in PartFootprint other, float margin = 0f)
        => Overlaps(_body, other._body, margin);

    /// <summary>
    /// Exact planar overlap including the conditional brackets. Connection proximity uses this:
    /// a build welds along the bracket the player snapped to, exactly as the original does.
    /// </summary>
    public bool Touches(in PartFootprint other, float margin = 0f)
        => Overlaps(_all, other._all, margin);

    private static bool Overlaps(FootprintShape[] leftShapes, FootprintShape[] rightShapes, float margin)
    {
        for (int left = 0; left < leftShapes.Length; left++)
        {
            for (int right = 0; right < rightShapes.Length; right++)
            {
                if (leftShapes[left].Overlaps(rightShapes[right], margin))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>One planar primitive of a part footprint.</summary>
    private readonly record struct FootprintShape(
        PhysicsShapeKind Kind,
        float HalfExtentX,
        float HalfExtentY,
        float Radius,
        float PositionX,
        float PositionY,
        float Cos,
        float Sin)
    {
        public (float MinX, float MinY, float MaxX, float MaxY) Bounds(float margin)
        {
            if (Kind == PhysicsShapeKind.Sphere)
            {
                return (PositionX - Radius - margin, PositionY - Radius - margin, PositionX + Radius + margin, PositionY + Radius + margin);
            }

            float extentX = (MathF.Abs(HalfExtentX * Cos) + MathF.Abs(HalfExtentY * Sin)) + margin;
            float extentY = (MathF.Abs(HalfExtentX * Sin) + MathF.Abs(HalfExtentY * Cos)) + margin;
            return (PositionX - extentX, PositionY - extentY, PositionX + extentX, PositionY + extentY);
        }

        public bool Overlaps(in FootprintShape other, float margin)
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

        private bool RectOverlapsRect(in FootprintShape other, float margin)
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

        private bool CircleOverlapsRect(in FootprintShape rect, float margin)
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

        private bool CircleOverlapsCircle(in FootprintShape other, float margin)
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
}
