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

    public PartDefinition GetPart(uint partTypeId)
    {
        if (!_partsByTypeId.TryGetValue(partTypeId, out PartDefinition? part))
        {
            throw new KeyNotFoundException($"Part type {partTypeId} is not defined in content version '{Document.ContentVersion}'.");
        }

        return part;
    }

    /// <summary>
    /// Maps a part definition to a physics body definition at the given pose. A uniform
    /// <paramref name="scale"/> multiplies linear shape dimensions and mass scales with
    /// volume (scale cubed). Shape kinds without a physics abstraction record yet
    /// (sphere, capsule, meshes) fail here rather than silently degrading fidelity.
    /// </summary>
    public BodyDefinition CreateBodyDefinition(
        uint partTypeId,
        PhysicsVector3 position,
        PhysicsQuaternion rotation,
        float scale = 1f,
        PhysicsVector3 linearVelocity = default,
        PhysicsVector3 angularVelocity = default)
    {
        PartDefinition part = GetPart(partTypeId);
        if (!float.IsFinite(scale) || scale is <= 0f or > Construction.ConstructionRules.MaxScale)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, $"A part scale must be finite, positive and at most {Construction.ConstructionRules.MaxScale}.");
        }

        if (part.Mode == PhysicsBodyMode.Static && (linearVelocity != PhysicsVector3.Zero || angularVelocity != PhysicsVector3.Zero))
        {
            throw new ArgumentException("A static part cannot be placed with an initial velocity.", nameof(linearVelocity));
        }

        if (part.Shapes.Any(shape => shape.Offset is not null))
        {
            throw new NotSupportedException($"Part type {partTypeId} uses shape offsets, which the physics contract does not support yet.");
        }

        ShapeDefinition[] shapes = part.Shapes.Select(shape => shape.Kind switch
        {
            PhysicsShapeKind.Box when shape.BoxHalfExtents is { Length: 3 } halfExtents
                => new BoxShapeDefinition(halfExtents[0] * scale, halfExtents[1] * scale, halfExtents[2] * scale),
            PhysicsShapeKind.Box => throw new InvalidOperationException($"Part type {partTypeId} has a box shape without half extents."),
            _ => throw new NotSupportedException($"Part type {partTypeId} uses shape kind {shape.Kind}, which has no physics shape definition yet.")
        }).ToArray();

        return new BodyDefinition(
            part.Mode,
            position,
            rotation,
            part.Mass * (scale * scale * scale),
            shapes,
            linearVelocity,
            angularVelocity,
            new PhysicsMaterial(part.Restitution, part.Friction));
    }
}
