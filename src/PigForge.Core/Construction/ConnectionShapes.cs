using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Construction;

/// <summary>
/// The original's part-local side labels, in the <c>Direction</c> order the decompiled code walks
/// (<c>Right, Up, Left, Down</c>) followed by the four diagonals. A conditional shape names one of
/// these in its content <c>condition.side</c>.
/// </summary>
public enum LocalSide
{
    Right,
    Top,
    Left,
    Bottom,
    TopRight,
    TopLeft,
    BottomLeft,
    BottomRight
}

/// <summary>
/// Mirrors the original's <c>ChangeVisualConnections</c> connection state into physics geometry:
/// which of a part's conditional attachment colliders are solid (a hidden marker is a trigger,
/// <c>Rocket.cs:157-160</c>) and which of a wing's two root-collider forms is current
/// (<c>Wings.cs:62-71</c>). This is the server-side twin of the client's
/// <c>clients/web/src/renderer/connectionVisuals.ts</c>, which computes the same sides from the
/// published layout to decide which conditional sprites to draw; both derive "a side can connect"
/// from the original's weld rule (<c>Contraption.cs:690</c>).
/// <para>
/// The connection state is fixed at spawn: markers follow the build layout, and neither the build
/// graph nor a seam split changes it (the original's grid adjacency does not change either), so the
/// resolved shape set is a pure function of the layout and the same layout always yields the same
/// body.
/// </para>
/// </summary>
public static class ConnectionShapes
{
    /// <summary>
    /// How far two alignment boxes may sit apart and still count as touching, in cells. Mirrors
    /// the client's <c>CONTACT_TOLERANCE</c>: a bracket is made solid exactly when the renderer
    /// shows it, so the two halves of the same original rule stay in step.
    /// </summary>
    public const float ContactTolerance = 0.06f;

    /// <summary>A turn of 45 degrees, the step the eight-way conditional parts rotate on.</summary>
    private const float EighthTurn = MathF.PI / 4f;

    /// <summary>
    /// The four orthogonal sides in the order the renderer's yaw rotation walks them
    /// (<c>connectionVisuals.ts</c> <c>LOCAL_ORDER</c>); a quarter turn moves every entry one step.
    /// </summary>
    private static readonly LocalSide[] LocalOrder =
    {
        LocalSide.Top,
        LocalSide.Left,
        LocalSide.Bottom,
        LocalSide.Right
    };

    /// <summary>
    /// A wing's root collider in its two forms (<c>Wings.cs:62-71</c>): thin and high when only the
    /// top mount shows, thick and low otherwise. The prefab's serialized collider
    /// (<c>size.y 0.6122213</c>, <c>center.y -0.15</c>, <c>tools/bple-shapes</c>) is the pre-script
    /// value; <c>ChangeVisualConnections</c> always overwrites it to exactly these numbers, so the
    /// running game is <c>0.3</c>/<c>0.6</c> and <c>0.05</c>/<c>-0.15</c>.
    /// </summary>
    private const float WingThickHalfY = 0.3f;
    private const float WingThickOffsetY = -0.15f;
    private const float WingThinHalfY = 0.15f;
    private const float WingThinOffsetY = 0.05f;

    /// <summary>
    /// The local sides of <paramref name="entity"/> that carry a neighbour it would weld to: the
    /// side whose alignment boxes are flush (within <see cref="ContactTolerance"/> on both axes,
    /// the axis of the larger gap winning) and whose neighbour satisfies the weld rule. Empty for a
    /// part with no conditional shapes, which is the common case and costs nothing.
    /// </summary>
    public static IReadOnlySet<LocalSide> ConnectableSides(
        EntityId entity,
        ConstructionRules construction,
        PartContentLibrary content)
    {
        ArgumentNullException.ThrowIfNull(construction);
        ArgumentNullException.ThrowIfNull(content);
        HashSet<LocalSide> sides = new();
        if (!construction.TryGetPartTypeId(entity, out uint partTypeId)
            || !construction.TryGetTransform(entity, out EntityTransform transform))
        {
            return sides;
        }

        PartDefinition part = content.GetPart(partTypeId);
        if (!HasConditionalShapes(part))
        {
            return sides;
        }

        if (AlignmentBox(part, transform) is not Alignment self)
        {
            return sides;
        }

        float yaw = YawOf(transform.Rotation);
        foreach (uint neighbourValue in construction.PlacedEntities)
        {
            if (neighbourValue == entity.Value
                || !construction.TryGetPartTypeId(new EntityId(neighbourValue), out uint neighbourTypeId)
                || !construction.TryGetTransform(new EntityId(neighbourValue), out EntityTransform neighbourTransform))
            {
                continue;
            }

            PartDefinition neighbour = content.GetPart(neighbourTypeId);
            if (!CanWeld(part, neighbour) || AlignmentBox(neighbour, neighbourTransform) is not Alignment other)
            {
                continue;
            }

            float selfCentreX = transform.Position.X + self.OffsetX;
            float selfCentreY = transform.Position.Y + self.OffsetY;
            float neighbourCentreX = neighbourTransform.Position.X + other.OffsetX;
            float neighbourCentreY = neighbourTransform.Position.Y + other.OffsetY;
            float gapX = MathF.Abs(neighbourCentreX - selfCentreX) - (self.HalfX + other.HalfX);
            float gapY = MathF.Abs(neighbourCentreY - selfCentreY) - (self.HalfY + other.HalfY);
            if (gapX > ContactTolerance || gapY > ContactTolerance)
            {
                continue;
            }

            LocalSide world = gapX >= gapY
                ? (neighbourTransform.Position.X >= transform.Position.X ? LocalSide.Right : LocalSide.Left)
                : (neighbourTransform.Position.Y >= transform.Position.Y ? LocalSide.Top : LocalSide.Bottom);
            sides.Add(LocalSideOf(world, yaw));
        }

        return sides;
    }

    /// <summary>
    /// Whether one conditional attachment marker is solid under <paramref name="visual"/>, mirroring
    /// the client's <c>conditionalSpriteVisible</c> and therefore the original's per-script rule.
    /// </summary>
    public static bool ConditionVisible(
        PartShapeDefinition shape,
        IReadOnlySet<LocalSide>? sides,
        ConnectionVisualKind visual,
        float yaw)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!TryParseSide(shape.ConditionSide, out LocalSide side))
        {
            return false;
        }

        bool Has(LocalSide candidate) => sides?.Contains(candidate) == true;
        switch (visual)
        {
            case ConnectionVisualKind.AttachmentFallback:
                // Rocket.cs:153-156: each marker follows its own side, and the bottom marker is also
                // the ghost shown when no other side can connect.
                return side == LocalSide.Bottom
                    ? Has(LocalSide.Bottom) || !(Has(LocalSide.Top) || Has(LocalSide.Left) || Has(LocalSide.Right))
                    : Has(side);

            case ConnectionVisualKind.AttachmentPlain:
                // TNT.cs:94-106: each marker follows its own side only, with no fallback.
                return Has(side);

            case ConnectionVisualKind.AttachmentEight:
            {
                // SpotLight.cs:92-100 / GrapplingHook.cs:241-249: the diagonals show only while the
                // part sits on a 45-degree turn, the orthogonal ones otherwise, with the bottom
                // marker as the "nothing connects" ghost.
                bool diagonal = side is LocalSide.TopLeft or LocalSide.TopRight or LocalSide.BottomLeft or LocalSide.BottomRight;
                bool eighthTurn = OnDiagonalRotation(yaw);
                if (diagonal)
                {
                    return Has(side) && eighthTurn;
                }

                if (side == LocalSide.Bottom)
                {
                    return (Has(LocalSide.Bottom) && !eighthTurn) || !AnySide(sides);
                }

                return Has(side) && !eighthTurn;
            }

            default:
                // A frame mount is a sprite pair, not a collider: the wing's root collider carries
                // its state instead (see SpawnShapes).
                return false;
        }
    }

    /// <summary>
    /// The shapes a part's spawned body carries: its body shapes plus the conditional attachment
    /// markers the connection state shows, with a wing's root box in its two-state form. A
    /// <c>frame</c> bracket is never body geometry (it is the art-derived build alignment box,
    /// ADR-018); a conditional marker whose part declares no rule stays out, the pre-ADR-017
    /// behaviour.
    /// </summary>
    public static IReadOnlyList<PartShapeDefinition> SpawnShapes(
        EntityId entity,
        ConstructionRules construction,
        PartContentLibrary content)
    {
        ArgumentNullException.ThrowIfNull(construction);
        ArgumentNullException.ThrowIfNull(content);
        if (!construction.TryGetPartTypeId(entity, out uint partTypeId)
            || !construction.TryGetTransform(entity, out EntityTransform transform))
        {
            return Array.Empty<PartShapeDefinition>();
        }

        PartDefinition part = content.GetPart(partTypeId);
        if (!HasConditionalShapes(part))
        {
            return part.Shapes;
        }

        return Resolve(part, ConnectableSides(entity, construction, content), YawOf(transform.Rotation));
    }

    private static IReadOnlyList<PartShapeDefinition> Resolve(
        PartDefinition part,
        IReadOnlySet<LocalSide> sides,
        float yaw)
    {
        bool wing = part.ConnectionVisual == ConnectionVisualKind.Frame;
        bool thickWing = wing && WingBottomVisible(sides);
        List<PartShapeDefinition> shapes = new(part.Shapes.Count);
        foreach (PartShapeDefinition shape in part.Shapes)
        {
            if (shape.ConditionKind is null)
            {
                shapes.Add(wing ? WingShape(shape, thickWing) : shape);
                continue;
            }

            if (shape.ConditionKind == "attachment"
                && part.ConnectionVisual is ConnectionVisualKind visual
                && ConditionVisible(shape, sides, visual, yaw))
            {
                shapes.Add(shape);
            }
        }

        return shapes;
    }

    /// <summary>
    /// The original's wing mount state: <c>flag3 = flag2 || !flag</c> with
    /// <c>flag = Up|Left|Right</c> and <c>flag2 = Down|Left|Right</c> in the part's own frame
    /// (<c>Wings.cs:43-45</c>). True means the bottom mount (and the thick collider) is current.
    /// </summary>
    private static bool WingBottomVisible(IReadOnlySet<LocalSide> sides)
    {
        bool top = sides.Contains(LocalSide.Right) || sides.Contains(LocalSide.Top) || sides.Contains(LocalSide.Left);
        bool bottom = sides.Contains(LocalSide.Left) || sides.Contains(LocalSide.Bottom) || sides.Contains(LocalSide.Right);
        return bottom || !top;
    }

    /// <summary>
    /// A wing's root box in the state <c>ChangeVisualConnections</c> leaves it in. Only the Y centre
    /// and height change; X, Z and the half extents along them are the prefab's.
    /// </summary>
    private static PartShapeDefinition WingShape(PartShapeDefinition shape, bool thick)
    {
        if (shape.Kind != PhysicsShapeKind.Box || shape.BoxHalfExtents is not { Length: 3 } halfExtents)
        {
            return shape;
        }

        float halfY = thick ? WingThickHalfY : WingThinHalfY;
        float offsetY = thick ? WingThickOffsetY : WingThinOffsetY;
        float[] offset = shape.Offset is { Length: 3 } value
            ? new[] { value[0], offsetY, value[2] }
            : new[] { 0f, offsetY, 0f };
        return shape with
        {
            BoxHalfExtents = new[] { halfExtents[0], halfY, halfExtents[2] },
            Offset = offset
        };
    }

    /// <summary>The original's weld predicate (<c>Contraption.cs:690</c>): neither end <c>none</c>
    /// and at least one <c>source</c>. The same rule the client uses to decide a side can connect.</summary>
    private static bool CanWeld(PartDefinition left, PartDefinition right)
    {
        JointConnectionType leftType = left.Capabilities?.JointConnectionType ?? JointConnectionType.None;
        JointConnectionType rightType = right.Capabilities?.JointConnectionType ?? JointConnectionType.None;
        return leftType != JointConnectionType.None
            && rightType != JointConnectionType.None
            && (leftType == JointConnectionType.Source || rightType == JointConnectionType.Source);
    }

    private static bool HasConditionalShapes(PartDefinition part)
    {
        for (int index = 0; index < part.Shapes.Count; index++)
        {
            if (part.Shapes[index].ConditionKind is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A part's build alignment box in the entity's frame: the union AABB of every shape, except
    /// that a part carrying a <c>frame</c> bracket is measured by that bracket alone -- a glider
    /// wing's collider is the wing, which overhangs the neighbour it welds to, while the bracket is
    /// the single cell it is placed by (<c>connectionVisuals.ts</c> <c>snapBoxOf</c>, ADR-018).
    /// Null when the part projects no shape onto the build plane.
    /// </summary>
    private static Alignment? AlignmentBox(PartDefinition part, EntityTransform transform)
    {
        float yaw = YawOf(transform.Rotation);
        float cos = MathF.Cos(yaw);
        float sin = MathF.Sin(yaw);
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        float frameMinX = float.PositiveInfinity;
        float frameMinY = float.PositiveInfinity;
        float frameMaxX = float.NegativeInfinity;
        float frameMaxY = float.NegativeInfinity;
        for (int index = 0; index < part.Shapes.Count; index++)
        {
            PartShapeDefinition shape = part.Shapes[index];
            (float offsetX, float offsetY) = shape.Offset is { Length: 3 } offset
                ? (offset[0], offset[1])
                : (0f, 0f);
            float centreX = ((offsetX * cos) - (offsetY * sin)) * transform.Scale;
            float centreY = ((offsetX * sin) + (offsetY * cos)) * transform.Scale;
            float extentX;
            float extentY;
            if (shape.Kind == PhysicsShapeKind.Sphere && shape.Radius is float radius)
            {
                extentX = radius * transform.Scale;
                extentY = extentX;
            }
            else if (shape.BoxHalfExtents is { Length: 3 } halfExtents)
            {
                float halfX = halfExtents[0] * transform.Scale;
                float halfY = halfExtents[1] * transform.Scale;
                extentX = MathF.Abs(halfX * cos) + MathF.Abs(halfY * sin);
                extentY = MathF.Abs(halfX * sin) + MathF.Abs(halfY * cos);
            }
            else
            {
                continue;
            }

            minX = MathF.Min(minX, centreX - extentX);
            maxX = MathF.Max(maxX, centreX + extentX);
            minY = MathF.Min(minY, centreY - extentY);
            maxY = MathF.Max(maxY, centreY + extentY);
            if (shape.ConditionKind == "frame")
            {
                frameMinX = MathF.Min(frameMinX, centreX - extentX);
                frameMaxX = MathF.Max(frameMaxX, centreX + extentX);
                frameMinY = MathF.Min(frameMinY, centreY - extentY);
                frameMaxY = MathF.Max(frameMaxY, centreY + extentY);
            }
        }

        if (minX > maxX)
        {
            return null;
        }

        return frameMinX <= frameMaxX
            ? new Alignment((frameMaxX - frameMinX) / 2f, (frameMaxY - frameMinY) / 2f, (frameMinX + frameMaxX) / 2f, (frameMinY + frameMaxY) / 2f)
            : new Alignment((maxX - minX) / 2f, (maxY - minY) / 2f, (minX + maxX) / 2f, (minY + maxY) / 2f);
    }

    /// <summary>A world side back in the entity's own frame: the inverse of the renderer's yaw
    /// rotation, with the same quarter-turn rounding the editor uses.</summary>
    private static LocalSide LocalSideOf(LocalSide world, float yaw)
    {
        int quarter = ((((int)MathF.Floor((yaw / (MathF.PI / 2f)) + 0.5f)) % 4) + 4) % 4;
        int index = Array.IndexOf(LocalOrder, world);
        return LocalOrder[(((index - quarter) % 4) + 4) % 4];
    }

    private static bool OnDiagonalRotation(float yaw)
    {
        int eighth = (int)MathF.Floor((yaw / EighthTurn) + 0.5f);
        return (((eighth % 8) + 8) % 8) % 2 == 1;
    }

    private static bool AnySide(IReadOnlySet<LocalSide>? sides) =>
        sides is not null
        && (sides.Contains(LocalSide.Right)
            || sides.Contains(LocalSide.Top)
            || sides.Contains(LocalSide.Left)
            || sides.Contains(LocalSide.Bottom)
            || sides.Contains(LocalSide.TopRight)
            || sides.Contains(LocalSide.TopLeft)
            || sides.Contains(LocalSide.BottomLeft)
            || sides.Contains(LocalSide.BottomRight));

    private static bool TryParseSide(string? value, out LocalSide side)
    {
        switch (value)
        {
            case "right": side = LocalSide.Right; return true;
            case "top": side = LocalSide.Top; return true;
            case "left": side = LocalSide.Left; return true;
            case "bottom": side = LocalSide.Bottom; return true;
            case "topRight": side = LocalSide.TopRight; return true;
            case "topLeft": side = LocalSide.TopLeft; return true;
            case "bottomRight": side = LocalSide.BottomRight; return true;
            case "bottomLeft": side = LocalSide.BottomLeft; return true;
            default: side = default; return false;
        }
    }

    /// <summary>The build-plane yaw of a stored pose; the same projection the footprint uses.</summary>
    private static float YawOf(PhysicsQuaternion rotation) =>
        MathF.Atan2(
            2f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1f - (2f * ((rotation.Y * rotation.Y) + (rotation.Z * rotation.Z))));

    /// <summary>
    /// A part's build alignment box in the entity's frame: the half extents of its AABB and that
    /// AABB's centre relative to the entity origin (already rotated into the world by the yaw).
    /// </summary>
    private readonly record struct Alignment(float HalfX, float HalfY, float OffsetX, float OffsetY);
}
