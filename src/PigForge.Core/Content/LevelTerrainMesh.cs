using PigForge.Physics.Abstractions;

namespace PigForge.Core.Content;

/// <summary>
/// Extrudes a level terrain's outline loops into the collision mesh the original builds.
/// <c>LevelLoader.CreateCollider</c> (<c>LevelLoader.cs:339-380</c>) duplicates each outline point at
/// <c>z = -depth/2</c> and <c>z = +depth/2</c> and joins consecutive pairs into a quad strip that
/// wraps back onto the first point (<c>array2[6i..6i+5] = {2i, 2i+1, 2i+2, 2i+2, 2i+1, 2i+3}</c>,
/// indices taken modulo <c>2 * points</c>). This is that construction, applied once per boundary
/// loop, as a <see cref="TriangleMeshShapeDefinition"/> whose triangles are doubled by the backends
/// (the original's non-convex <c>MeshCollider</c> collides from either face -- see ADR-032).
/// </summary>
public static class LevelTerrainMesh
{
    public static TriangleMeshShapeDefinition Build(LevelTerrainDefinition terrain)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        if (!float.IsFinite(terrain.Depth) || terrain.Depth <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(terrain), terrain.Depth, "A terrain depth must be finite and positive.");
        }

        int pointCount = 0;
        foreach (IReadOnlyList<PhysicsVector3> loop in terrain.Loops)
        {
            ArgumentNullException.ThrowIfNull(loop);
            if (loop.Count < 3)
            {
                throw new ArgumentException("A terrain outline loop needs at least three points.", nameof(terrain));
            }

            pointCount += loop.Count;
        }

        if (pointCount == 0)
        {
            throw new ArgumentException("A terrain needs at least one outline loop.", nameof(terrain));
        }

        float halfDepth = terrain.Depth * 0.5f;
        PhysicsVector3[] vertices = new PhysicsVector3[pointCount * 2];
        int[] triangles = new int[pointCount * 6];
        int vertexOffset = 0;
        int triangleOffset = 0;
        foreach (IReadOnlyList<PhysicsVector3> loop in terrain.Loops)
        {
            for (int index = 0; index < loop.Count; index++)
            {
                PhysicsVector3 point = loop[index];
                vertices[vertexOffset + (index * 2)] = new PhysicsVector3(point.X, point.Y, -halfDepth);
                vertices[vertexOffset + (index * 2) + 1] = new PhysicsVector3(point.X, point.Y, halfDepth);
            }

            for (int index = 0; index < loop.Count; index++)
            {
                int current = vertexOffset + (index * 2);
                int next = vertexOffset + (((index + 1) % loop.Count) * 2);
                triangles[triangleOffset++] = current;
                triangles[triangleOffset++] = current + 1;
                triangles[triangleOffset++] = next;
                triangles[triangleOffset++] = next;
                triangles[triangleOffset++] = current + 1;
                triangles[triangleOffset++] = next + 1;
            }

            vertexOffset += loop.Count * 2;
        }

        return new TriangleMeshShapeDefinition(vertices, triangles);
    }
}
