using PigForge.Physics.Abstractions;

namespace PigForge.Core.Content;

/// <summary>
/// Immutable, startup-loaded view over a part-content document. Construction is the
/// startup gate: any invalid content fails here, before a server or replay can run.
/// </summary>
public sealed class PartContentLibrary
{
    private readonly Dictionary<uint, PartDefinition> _partsByTypeId;

    public PartContentLibrary(PartContentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        _partsByTypeId = document.Parts.ToDictionary(part => part.PartTypeId);
    }

    public static PartContentLibrary Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string json = File.ReadAllText(path);
        return new PartContentLibrary(PartContentParser.Parse(json));
    }

    public PartContentDocument Document { get; }

    /// <summary>
    /// The original's project-wide angular clamp every rigidbody inherits
    /// (<c>ProjectSettings/DynamicsManager.asset</c>, 7 rad/s; see
    /// <see cref="PartContentPhysics"/>). Content carries it once because it is a project default,
    /// not a per-part choice.
    /// </summary>
    public float MaximumAngularSpeed => Document.Physics.MaximumAngularSpeed;

    /// <summary>
    /// The damping the original gives this part's rigidbody: the class's own value where it
    /// overrides <c>BasePart.EnsureRigidbody</c> (<c>tools/bple-damping</c>), the document's
    /// default everywhere else, and none at all for a static part, which has no rigidbody.
    /// </summary>
    public PartDamping DampingOf(PartDefinition part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.Mode == PhysicsBodyMode.Dynamic ? part.Damping ?? Document.Physics.Damping : default;
    }

    public PartDefinition GetPart(uint partTypeId)
    {
        if (!_partsByTypeId.TryGetValue(partTypeId, out PartDefinition? part))
        {
            throw new KeyNotFoundException($"Part type {partTypeId} is not defined in content version '{Document.ContentVersion}'.");
        }

        return part;
    }

    /// <summary>
    /// One physics leaf shape of a part with its part-local offset (already scaled).
    /// Wheels use two: the tire sphere plus the support box that mounts the part.
    /// </summary>
    public readonly record struct ShapePlacement(ShapeDefinition Shape, PhysicsVector3 Offset);

    /// <summary>
    /// Enumerates a part's leaf shapes at a uniform <paramref name="scale"/>. Offsets are
    /// multiplied by the scale; shape kinds without a physics record fail here rather
    /// than silently degrading fidelity.
    /// </summary>
    public ShapePlacement[] EnumerateShapePlacements(uint partTypeId, float scale = 1f)
    {
        PartDefinition part = GetPart(partTypeId);
        ValidateScale(partTypeId, scale);
        // The static, connection-free body: a conditional shape is a joint attachment bracket the
        // original turns into a trigger while hidden (Rocket.cs:157-160), so only the spawn path --
        // which knows the connection state -- adds the visible ones back
        // (Construction.ConnectionShapes.SpawnShapes). Drag snapping and connection proximity read
        // them straight from the content instead.
        PartShapeDefinition[] bodyShapes = part.Shapes.Where(shape => shape.ConditionKind is null).ToArray();
        if (bodyShapes.Length == 0)
        {
            throw new InvalidOperationException($"Part type {partTypeId} has no body shape: every collider is conditional.");
        }

        return PlaceShapes(partTypeId, scale, bodyShapes);
    }

    /// <summary>
    /// Maps an explicit selection of a part's shapes to physics placements at a uniform
    /// <paramref name="scale"/>. The spawn path uses this: the connection state decides which
    /// conditional brackets are solid and which form a wing's body box takes
    /// (<see cref="Construction.ConnectionShapes.SpawnShapes"/>), and the resulting list then goes
    /// through exactly the same shape-to-physics mapping as the static body.
    /// </summary>
    public ShapePlacement[] PlaceShapes(uint partTypeId, float scale, IReadOnlyList<PartShapeDefinition> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ValidateScale(partTypeId, scale);
        ShapePlacement[] placements = new ShapePlacement[shapes.Count];
        for (int index = 0; index < shapes.Count; index++)
        {
            PartShapeDefinition shape = shapes[index];
            ShapeDefinition definition = shape.Kind switch
            {
                PhysicsShapeKind.Box when shape.BoxHalfExtents is { Length: 3 } halfExtents
                    => new BoxShapeDefinition(halfExtents[0] * scale, halfExtents[1] * scale, halfExtents[2] * scale),
                PhysicsShapeKind.Box => throw new InvalidOperationException($"Part type {partTypeId} has a box shape without half extents."),
                PhysicsShapeKind.Sphere when shape.Radius is float radius
                    => new SphereShapeDefinition(radius * scale),
                PhysicsShapeKind.Sphere => throw new InvalidOperationException($"Part type {partTypeId} has a sphere shape without radius."),
                _ => throw new NotSupportedException($"Part type {partTypeId} uses shape kind {shape.Kind}, which has no physics shape definition yet.")
            };
            PhysicsVector3 offset = shape.Offset is { Length: 3 } value
                ? new PhysicsVector3(value[0] * scale, value[1] * scale, value[2] * scale)
                : PhysicsVector3.Zero;
            placements[index] = new ShapePlacement(definition, offset);
        }

        return placements;
    }

    /// <summary>
    /// Volume-weighted centre of the part's leaf shapes in part-local space (scaled).
    /// Physics compounds are recentred onto this point, so callers pose the body there.
    /// </summary>
    public PhysicsVector3 ShapeCenterOfMass(uint partTypeId, float scale = 1f)
    {
        ShapePlacement[] placements = EnumerateShapePlacements(partTypeId, scale);
        float total = 0f;
        PhysicsVector3 weighted = PhysicsVector3.Zero;
        foreach (ShapePlacement placement in placements)
        {
            float volume = ShapeMetrics.Volume(placement.Shape);
            total += volume;
            weighted += placement.Offset * volume;
        }

        return total > 0f ? weighted * (1f / total) : PhysicsVector3.Zero;
    }

    /// <summary>
    /// One leaf shape of a hinged part: the shape, its part-local offset, and whether it spins
    /// with the wheel body.
    /// </summary>
    public readonly record struct WheelShape(ShapeDefinition Shape, PhysicsVector3 Offset, bool Spins);

    /// <summary>
    /// True for the only shape kind a spinning wheel body may carry: a sphere is invariant
    /// under rotation about its own centre, so a tire can roll. Any other collider is a fixed
    /// mount — the original keeps a wheel's support box on a non-rotating child, so a spinning
    /// body would sweep it into the chassis.
    /// </summary>
    public static bool IsTireShape(ShapeDefinition shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return shape.Kind == PhysicsShapeKind.Sphere;
    }

    /// <summary>
    /// Classifies a hinged part's shapes and reports the axle they turn about, in part-local
    /// space (scaled, Z = 0): the volume-weighted centre of its tires, with every other shape
    /// mounted on the parent body. Hinging anywhere but the tire centre makes the tire orbit
    /// the joint like a cam instead of rolling. A part with no tire shape has nothing that can
    /// roll, so all of its shapes spin about their own volume centre (the original's propeller).
    /// </summary>
    public (PhysicsVector3 Axle, WheelShape[] Shapes) DescribeWheel(uint partTypeId, float scale = 1f)
        => DescribeWheelFrom(EnumerateShapePlacements(partTypeId, scale));

    /// <summary>
    /// <see cref="DescribeWheel(uint, float)"/> over an explicit shape selection (the spawn path,
    /// where the connection state decides which conditional brackets the body carries).
    /// </summary>
    public (PhysicsVector3 Axle, WheelShape[] Shapes) DescribeWheel(uint partTypeId, float scale, IReadOnlyList<PartShapeDefinition> shapes)
        => DescribeWheelFrom(PlaceShapes(partTypeId, scale, shapes));

    private static (PhysicsVector3 Axle, WheelShape[] Shapes) DescribeWheelFrom(ShapePlacement[] placements)
    {
        bool hasTire = false;
        foreach (ShapePlacement placement in placements)
        {
            if (IsTireShape(placement.Shape))
            {
                hasTire = true;
                break;
            }
        }

        float total = 0f;
        PhysicsVector3 weighted = PhysicsVector3.Zero;
        foreach (ShapePlacement placement in placements)
        {
            if (hasTire && !IsTireShape(placement.Shape))
            {
                continue;
            }

            float volume = ShapeMetrics.Volume(placement.Shape);
            total += volume;
            weighted += placement.Offset * volume;
        }

        PhysicsVector3 axle = total > 0f ? weighted * (1f / total) : PhysicsVector3.Zero;
        WheelShape[] shapes = new WheelShape[placements.Length];
        for (int index = 0; index < placements.Length; index++)
        {
            shapes[index] = new WheelShape(
                placements[index].Shape,
                placements[index].Offset,
                !hasTire || IsTireShape(placements[index].Shape));
        }

        return (axle, shapes);
    }

    /// <summary>
    /// Maps a part definition to a physics body definition at the given pose. A uniform
    /// <paramref name="scale"/> multiplies linear shape dimensions and mass scales with
    /// volume (scale cubed). A part with several colliders or an offset collider becomes
    /// one compound shape; static parts cannot express that yet and fail here.
    /// </summary>
    public BodyDefinition CreateBodyDefinition(
        uint partTypeId,
        PhysicsVector3 position,
        PhysicsQuaternion rotation,
        float scale = 1f,
        PhysicsVector3 linearVelocity = default,
        PhysicsVector3 angularVelocity = default,
        PhysicsConstraintMask constraints = PhysicsConstraintMask.None)
    {
        PartDefinition part = GetPart(partTypeId);
        ValidateScale(partTypeId, scale);

        if (part.Mode == PhysicsBodyMode.Static && (linearVelocity != PhysicsVector3.Zero || angularVelocity != PhysicsVector3.Zero))
        {
            throw new ArgumentException("A static part cannot be placed with an initial velocity.", nameof(linearVelocity));
        }

        ShapePlacement[] placements = EnumerateShapePlacements(partTypeId, scale);
        ShapeDefinition[] shapes;
        if (placements.Length == 1 && placements[0].Offset == PhysicsVector3.Zero)
        {
            shapes = new[] { placements[0].Shape };
        }
        else
        {
            if (part.Mode == PhysicsBodyMode.Static)
            {
                throw new NotSupportedException($"Part type {partTypeId} is static and uses shape offsets, which the physics contract does not support yet.");
            }

            CompoundChild[] children = new CompoundChild[placements.Length];
            for (int index = 0; index < placements.Length; index++)
            {
                children[index] = new CompoundChild(placements[index].Shape, placements[index].Offset);
            }

            shapes = new ShapeDefinition[] { new CompoundShapeDefinition(children) };
        }

        PartDamping damping = DampingOf(part);
        return new BodyDefinition(
            part.Mode,
            position,
            rotation,
            part.Mass * (scale * scale * scale),
            shapes,
            linearVelocity,
            angularVelocity,
            new PhysicsMaterial(part.Restitution, part.Friction, part.FrictionCombine),
            constraints,
            damping.Linear,
            damping.Angular,
            MaximumAngularSpeed);
    }

    private static void ValidateScale(uint partTypeId, float scale)
    {
        if (!float.IsFinite(scale) || scale is <= 0f or > Construction.ConstructionRules.MaxScale)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, $"Part type {partTypeId} scale must be finite, positive and at most {Construction.ConstructionRules.MaxScale}.");
        }
    }
}
