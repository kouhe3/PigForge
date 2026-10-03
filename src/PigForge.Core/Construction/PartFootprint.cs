using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Construction;

/// <summary>
/// Build-plane projection of a placed part, with two independent geometries:
/// <list type="bullet">
/// <item><description>
/// <c>_body</c> — the <b>occupied cells</b>, which are the original's declared build-grid cell box
/// (<c>BasePart.cs:197-200</c>, extracted per prefab by <c>tools/bple-grid</c>), one rectangle of
/// <c>gridBox</c> cells centred on the part's origin. The original's occupancy has never been the
/// collider union: a rotor's blades are 2.55 cells wide and it still occupies one cell, which is
/// exactly why it can stand beside a wooden frame. Colliders overhang neighbouring cells freely.
/// </description></item>
/// <item><description>
/// <c>_all</c> — the <b>collider union</b>: every shape the part carries, including the
/// build-time-only conditional brackets. Boxes become rotated rectangles, spheres become circles;
/// a wheel is its tire circle plus the support rectangle. Connection proximity uses this, because
/// a build welds along the geometry the player snapped to (ADR-017/ADR-018).
/// </description></item>
/// </list>
/// The build plane is Z = 0 and rotations project to their Z yaw; <c>scale</c> multiplies linear
/// dimensions, shape offsets and the cell box. The collider union rotates by the raw yaw, while the
/// cell box takes the quarter turn that yaw resolves to -- a cell map has no other orientations.
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
        List<FootprintShape> all = new(part.Shapes.Count);
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
        }

        // Occupancy is the original's build-grid cell box, not the collider union: a rotor's
        // blades are 2.55 cells wide yet the rotor occupies the single cell its prefab declares,
        // so it can be placed tight against a wooden frame. A part whose prefab declares the
        // default box carries no `gridBox` in content, and absence means that default (the
        // original's own 332-of-343 case), never a second source of truth.
        //
        // The original's occupancy is a cell map, not a geometry test, so an arbitrary planar pose
        // is first resolved to the grid coordinate it stands on. In the original that coordinate
        // *is* the serialized one and cell centres sit at whole units
        // (ConstructionUI.GridPositionToWorldPosition: contraption.position + right*x + up*y), so
        // the cell a part stands on is the one nearest its origin. PigForge places at arbitrary
        // poses, and this is what keeps a part the editor snapped flush against a neighbour inside
        // the neighbouring cell rather than half-inside the neighbour's: the editor puts the
        // connective edge on the cell line (a wooden glider wing lands at x = 1.7404 beside a frame
        // at x = 1.0, its bracket's left edge on 1.5, ADR-018), which is exactly one cell over.
        // Without it every flush-welded part is rejected by its own neighbour's cell (the bug
        // ADR-018 fixed), and a dragged part's occupancy would jitter with sub-cell pointer noise.
        float cellX = GridCoordinate(positionX);
        float cellY = GridCoordinate(positionY);
        GridCellBox box = part.GridBox ?? GridCellBox.Single;
        float boxCentreX = box.CentreX;
        float boxCentreY = box.CentreY;
        float boxHalfX = box.Width * 0.5f;
        float boxHalfY = box.Height * 0.5f;
        switch (QuarterTurns(angle))
        {
            case 1:
                (boxCentreX, boxCentreY, boxHalfX, boxHalfY) = (-boxCentreY, boxCentreX, boxHalfY, boxHalfX);
                break;
            case 2:
                (boxCentreX, boxCentreY) = (-boxCentreX, -boxCentreY);
                break;
            case 3:
                (boxCentreX, boxCentreY, boxHalfX, boxHalfY) = (boxCentreY, -boxCentreX, boxHalfY, boxHalfX);
                break;
            default:
                break;
        }

        FootprintShape body = new(
            PhysicsShapeKind.Box,
            boxHalfX * scale,
            boxHalfY * scale,
            0f,
            cellX + (boxCentreX * scale),
            cellY + (boxCentreY * scale),
            1f,
            0f);
        return new PartFootprint(new[] { body }, all.ToArray());
    }

    /// <summary>
    /// The build-grid coordinate a planar position stands on: cells are one unit wide with their
    /// centres at whole units (<see cref="ConstructionRules.CellSize"/>, and the editor's click
    /// placement snaps to those centres), so the nearest one is <c>position + 0.5</c> floored.
    /// Deliberately not <c>MathF.Round</c>, which rounds halves to even and would make the cell of
    /// a part dropped exactly on a cell boundary depend on which cell number it sits between.
    /// </summary>
    private static float GridCoordinate(float position) => MathF.Floor(position + 0.5f);

    /// <summary>
    /// The quarter turn a yaw resolves to for occupancy: the original's cell box is a cell map, so
    /// it has four orientations and no others, and quarter turns are the ones the editor already
    /// resolves build-time yaw to (<c>clients/web/src/editor/tools.ts</c>, <c>connectionEdges</c>:
    /// <c>Math.round(yaw / (Math.PI / 2)) % 4</c>). Rotating the box by the raw yaw instead would
    /// make every part built at 15/30/45 degrees overlap the box of the neighbour in the next cell
    /// -- an axis-aligned 1x1 cell turned a few degrees already has a 1.13-wide extent -- which the
    /// original's grid never does; the wooden cart is built at 0.9 rad and must stay placeable.
    /// Ties round up, so the result never depends on the sign of the angle that produced it.
    /// </summary>
    private static int QuarterTurns(float angle) =>
        ((((int)MathF.Floor((angle / (MathF.PI / 2f)) + 0.5f)) % 4) + 4) % 4;

    public static PartFootprint ForPart(PartDefinition part, PhysicsVector3 position, PhysicsQuaternion rotation, float scale)
    {
        // Physics poses may tumble; the build plane projects them to their Z yaw.
        float yaw = MathF.Atan2(
            2f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1f - (2f * ((rotation.Y * rotation.Y) + (rotation.Z * rotation.Z))));
        return ForPart(part, position.X, position.Y, yaw, scale);
    }

    /// <summary>
    /// AABB of both geometries in the build plane, expanded by <paramref name="margin"/>: the
    /// occupancy buckets and the footprint-area limit must cover everything either semantic can
    /// reach, and neither is a superset of the other (a KingPig's 3x2 cell box is wider than its
    /// collider; a rotor's collider is wider than its cell).
    /// </summary>
    public (float MinX, float MinY, float MaxX, float MaxY) Bounds(float margin = 0f)
    {
        bool first = true;
        float minX = 0f;
        float minY = 0f;
        float maxX = 0f;
        float maxY = 0f;
        Expand(_body, margin, ref first, ref minX, ref minY, ref maxX, ref maxY);
        Expand(_all, margin, ref first, ref minX, ref minY, ref maxX, ref maxY);
        return (minX, minY, maxX, maxY);
    }

    private static void Expand(
        FootprintShape[] shapes,
        float margin,
        ref bool first,
        ref float minX,
        ref float minY,
        ref float maxX,
        ref float maxY)
    {
        for (int index = 0; index < shapes.Length; index++)
        {
            (float shapeMinX, float shapeMinY, float shapeMaxX, float shapeMaxY) = shapes[index].Bounds(margin);
            if (first)
            {
                (minX, minY, maxX, maxY) = (shapeMinX, shapeMinY, shapeMaxX, shapeMaxY);
                first = false;
                continue;
            }

            minX = MathF.Min(minX, shapeMinX);
            minY = MathF.Min(minY, shapeMinY);
            maxX = MathF.Max(maxX, shapeMaxX);
            maxY = MathF.Max(maxY, shapeMaxY);
        }
    }

    /// <summary>
    /// Exact planar overlap of the occupied cells: the original's build-grid cell box of each
    /// part, which is all a cell can block. Colliders do not take part — a part's body may overhang
    /// a neighbour's cell without occupying it, as in the original. A negative
    /// <paramref name="margin"/> shrinks the boxes, so marginal penetrations count as legal touching.
    /// </summary>
    public bool Overlaps(in PartFootprint other, float margin = 0f)
        => Overlaps(_body, other._body, margin);

    /// <summary>
    /// Exact planar overlap over the collider union, conditional brackets included. Connection
    /// proximity uses this: a build welds along the geometry the player snapped to, exactly as the
    /// original does.
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
