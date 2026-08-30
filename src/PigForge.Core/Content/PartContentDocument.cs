using PigForge.Physics.Abstractions;

namespace PigForge.Core.Content;

/// <summary>Engine-agnostic part and collision content, authored as JSON against part-content-v1.</summary>
public sealed record PartContentDocument(
    string ContentVersion,
    IReadOnlyList<PartDefinition> Parts)
{
    public const string Format = "pigforge.part-content";
    public const ushort SchemaVersion = 1;
}

public sealed record PartDefinition(
    uint PartTypeId,
    string Name,
    PhysicsBodyMode Mode,
    float Mass,
    float Restitution,
    float Friction,
    IReadOnlyList<PartShapeDefinition> Shapes);

public sealed record PartShapeDefinition(
    PhysicsShapeKind Kind,
    float[]? BoxHalfExtents,
    float? Radius,
    float? CylinderHalfHeight,
    float[][]? Vertices,
    uint[]? Triangles,
    float[]? Offset)
{
    public static PartShapeDefinition Box(float halfExtentX, float halfExtentY, float halfExtentZ) => new(
        PhysicsShapeKind.Box,
        new[] { halfExtentX, halfExtentY, halfExtentZ },
        Radius: null,
        CylinderHalfHeight: null,
        Vertices: null,
        Triangles: null,
        Offset: null);

    public static PartShapeDefinition Sphere(float radius) => new(
        PhysicsShapeKind.Sphere,
        BoxHalfExtents: null,
        radius,
        CylinderHalfHeight: null,
        Vertices: null,
        Triangles: null,
        Offset: null);
}
