using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Physics.Jolt;

namespace PigForge.Physics.Tests;

/// <summary>
/// The original bakes every level's terrain into a static triangle mesh: an <c>e2dTerrain</c>'s fill
/// polygon is extruded along z into a <c>MeshCollider</c> (<c>LevelLoader.cs:339-380</c>, depth
/// <c>e2dConstants.COLLISION_MESH_Z_DEPTH</c> = 10). PigForge models it as
/// <see cref="TriangleMeshShapeDefinition"/>, and both backends build their own native mesh from the
/// same vertices and index triples, so these tests pin what the shape has to do: hold a body on top
/// of it, let a body slide down it, stay a static-only shape, and reproduce itself exactly.
/// </summary>
public sealed class TriangleMeshTests
{
    private static readonly FixedTimeStep Tick = FixedTimeStep.FromSeconds(1f / 60f);

    [Fact]
    public void ABodyDroppedOnATriangleMeshGroundRestsOnIt()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        PhysicsBodyId ground = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            mass: 0f,
            new ShapeDefinition[] { HorizontalGround(halfExtent: 8f) }));
        PhysicsBodyId box = DropBoxOn(world, height: 2f);

        Run(world, ticks: 240);
        PhysicsBodySnapshot resting = Snapshot(world, box);

        // A 1x1x1 box lands with its centre half a unit above the surface; the tolerance covers the
        // solver's penetration slop, not a mis-scaled mesh.
        Assert.InRange(resting.Position.Y, 0.47f, 0.53f);
        Assert.InRange(MathF.Abs(resting.LinearVelocity.Y), 0f, 0.05f);
        Assert.InRange(MathF.Abs(resting.Position.X), 0f, 0.05f);
        Assert.InRange(MathF.Abs(resting.Position.Z), 0f, 0.05f);

        world.DestroyBody(box);
        world.DestroyBody(ground);
    }

    [Fact]
    public void ABodySlidesDownATriangleMeshRamp()
    {
        const float degrees = 30f;
        float slope = MathF.Tan(degrees * MathF.PI / 180f);
        float startX = -2f;
        float SurfaceY(float x) => -x * slope;

        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        PhysicsBodyId ramp = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            mass: 0f,
            new ShapeDefinition[] { Ramp(degrees) }));
        PhysicsBodyId box = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(startX, SurfaceY(startX) + 0.55f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) },
            material: new PhysicsMaterial(Restitution: 0f, Friction: 0f)));

        Run(world, ticks: 90);
        PhysicsBodySnapshot sliding = Snapshot(world, box);

        // A 30 degree slope is steeper than any friction the pair can have with one side frictionless,
        // so the box travels downhill (+x) while riding the surface: its centre stays half a box
        // along the slope's normal above the plane, which is 0.5 * cos(30) = 0.43 in y.
        Assert.True(sliding.Position.X > startX + 1f, $"the box must slide downhill: x = {sliding.Position.X}");
        float gap = sliding.Position.Y - SurfaceY(sliding.Position.X);
        Assert.InRange(gap, 0.3f, 0.6f);

        world.DestroyBody(box);
        world.DestroyBody(ramp);
    }

    [Fact]
    public void ATriangleMeshRunIsDeterministic()
    {
        Assert.Equal(RunDroppedBox(), RunDroppedBox());
    }

    [Fact]
    public void ATriangleMeshBodyCannotBeDynamic()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        BodyDefinition dynamicMesh = new(
            PhysicsBodyMode.Dynamic,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { HorizontalGround(halfExtent: 4f) });

        Assert.Throws<NotSupportedException>(() => world.CreateBody(dynamicMesh));
    }

    /// <summary>
    /// A mesh is a surface, not a solid: the original's terrain collider is a non-convex Unity
    /// <c>MeshCollider</c>, and PhysX meshes generate contacts from either face (<c>eDOUBLE_SIDED</c>
    /// only affects raycasts and sweeps, per the PhysX API reference). Both native backends are
    /// one-sided and wind triangles opposite ways, so a body below a mesh must be caught exactly as
    /// one above it is.
    /// </summary>
    [Fact]
    public void ABodyUnderTheSameMeshRestsOnItsUnderside()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, 9.81f, 0f));
        _ = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            mass: 0f,
            new ShapeDefinition[] { HorizontalGround(halfExtent: 8f) }));
        PhysicsBodyId box = DropBoxOn(world, height: -2f);

        Run(world, ticks: 240);
        PhysicsBodySnapshot resting = Snapshot(world, box);

        Assert.InRange(resting.Position.Y, -0.53f, -0.47f);
        Assert.InRange(MathF.Abs(resting.LinearVelocity.Y), 0f, 0.05f);
    }

    [Fact]
    public void ATriangleMeshShapeRejectsMalformedIndexData()
    {
        PhysicsVector3[] vertices = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)];

        Assert.Throws<ArgumentException>(() => new TriangleMeshShapeDefinition(vertices, [0, 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriangleMeshShapeDefinition(vertices, [0, 1, 3]));
        Assert.Throws<ArgumentException>(() => new TriangleMeshShapeDefinition(vertices[..2], [0, 1, 2]));
    }

    /// <summary>
    /// A flat quad in the xz plane whose two triangles both face +y under the contract's convention
    /// (a triangle (A, B, C) faces <c>cross(B - A, C - A)</c>), so the same definition builds a
    /// collidable surface on both backends.
    /// </summary>
    internal static TriangleMeshShapeDefinition HorizontalGround(float halfExtent)
    {
        PhysicsVector3[] vertices =
        [
            new(-halfExtent, 0f, -halfExtent),
            new(halfExtent, 0f, -halfExtent),
            new(halfExtent, 0f, halfExtent),
            new(-halfExtent, 0f, halfExtent),
        ];

        return new TriangleMeshShapeDefinition(vertices, [0, 2, 1, 0, 3, 2]);
    }

    /// <summary>The same quad, tilted about z so the surface descends toward +x by the given angle.</summary>
    internal static TriangleMeshShapeDefinition Ramp(float degrees)
    {
        float radians = degrees * MathF.PI / 180f;
        float sine = MathF.Sin(-radians);
        float cosine = MathF.Cos(-radians);
        float halfExtent = 8f;

        PhysicsVector3[] flat =
        [
            new(-halfExtent, 0f, -halfExtent),
            new(halfExtent, 0f, -halfExtent),
            new(halfExtent, 0f, halfExtent),
            new(-halfExtent, 0f, halfExtent),
        ];

        PhysicsVector3[] tilted = new PhysicsVector3[flat.Length];
        for (int index = 0; index < flat.Length; index++)
        {
            tilted[index] = new PhysicsVector3(
                (flat[index].X * cosine) - (flat[index].Y * sine),
                (flat[index].X * sine) + (flat[index].Y * cosine),
                flat[index].Z);
        }

        return new TriangleMeshShapeDefinition(tilted, [0, 2, 1, 0, 3, 2]);
    }

    internal static PhysicsBodyId DropBoxOn(BepuPhysicsWorld world, float height) => world.CreateBody(new BodyDefinition(
        PhysicsBodyMode.Dynamic,
        new PhysicsVector3(0f, height, 0f),
        PhysicsQuaternion.Identity,
        1f,
        new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));

    private static long RunDroppedBox()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        _ = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            mass: 0f,
            new ShapeDefinition[] { HorizontalGround(halfExtent: 8f) }));
        _ = DropBoxOn(world, height: 2f);
        Run(world, ticks: 240);
        return HashSnapshots(world);
    }

    private static void Run(BepuPhysicsWorld world, int ticks)
    {
        PhysicsEvent[] events = new PhysicsEvent[8];
        for (int tick = 0; tick < ticks; tick++)
        {
            world.Step(Tick);
            _ = world.DrainEvents(events);
        }
    }

    internal static PhysicsBodySnapshot Snapshot(BepuPhysicsWorld world, PhysicsBodyId body)
    {
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[8];
        int count = world.CopySnapshots(snapshots);
        foreach (PhysicsBodySnapshot snapshot in snapshots[..count])
        {
            if (snapshot.Body == body)
            {
                return snapshot;
            }
        }

        throw new InvalidOperationException($"Body {body.Value} is not in the world's snapshots.");
    }

    private static long HashSnapshots(BepuPhysicsWorld world)
    {
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[8];
        int count = world.CopySnapshots(snapshots);
        long hash = 17;
        for (int index = 0; index < count; index++)
        {
            PhysicsBodySnapshot snapshot = snapshots[index];
            hash = unchecked((hash * 31) + snapshot.Body.Value);
            hash = Mix(hash, snapshot.Position);
            hash = Mix(hash, snapshot.Rotation);
            hash = Mix(hash, snapshot.AngularVelocity);
        }

        return hash;
    }

    private static long Mix(long hash, PhysicsVector3 value) => Mix(Mix(Mix(hash, value.X), value.Y), value.Z);

    private static long Mix(long hash, PhysicsQuaternion value) => Mix(Mix(Mix(Mix(hash, value.X), value.Y), value.Z), value.W);

    private static long Mix(long hash, float value) => unchecked((hash * 31) + BitConverter.SingleToInt32Bits(value));
}

/// <summary>
/// The same shape on the Jolt backend. Jolt owns a process-wide native runtime, and two Jolt test
/// classes running concurrently have crashed the test host inside the native solver, so this class
/// shares the Jolt collection and its worlds stay serial.
/// </summary>
[Collection("Jolt")]
public sealed class JoltTriangleMeshTests
{
    private static readonly FixedTimeStep Tick = FixedTimeStep.FromSeconds(1f / 60f);

    [Fact]
    public void JoltBodyRestsOnATriangleMeshGroundWhereBepuDoes()
    {
        using JoltPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        _ = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            mass: 0f,
            new ShapeDefinition[] { TriangleMeshTests.HorizontalGround(halfExtent: 8f) }));
        PhysicsBodyId box = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 2f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));

        PhysicsEvent[] events = new PhysicsEvent[8];
        for (int tick = 0; tick < 240; tick++)
        {
            world.Step(Tick);
            _ = world.DrainEvents(events);
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[8];
        int count = world.CopySnapshots(snapshots);
        PhysicsBodySnapshot resting = snapshots[..count].Single(snapshot => snapshot.Body == box);

        // Same surface, same landed height as the Bepu case, so the two meshes are the same shape.
        Assert.InRange(resting.Position.Y, 0.47f, 0.53f);
        Assert.InRange(MathF.Abs(resting.LinearVelocity.Y), 0f, 0.05f);
    }

    [Fact]
    public void JoltBodyUnderTheSameMeshRestsOnItsUnderside()
    {
        using JoltPhysicsWorld world = new(new PhysicsVector3(0f, 9.81f, 0f));
        _ = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            mass: 0f,
            new ShapeDefinition[] { TriangleMeshTests.HorizontalGround(halfExtent: 8f) }));
        PhysicsBodyId box = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, -2f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));

        PhysicsEvent[] events = new PhysicsEvent[8];
        for (int tick = 0; tick < 240; tick++)
        {
            world.Step(Tick);
            _ = world.DrainEvents(events);
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[8];
        int count = world.CopySnapshots(snapshots);
        PhysicsBodySnapshot resting = snapshots[..count].Single(snapshot => snapshot.Body == box);

        Assert.InRange(resting.Position.Y, -0.53f, -0.47f);
        Assert.InRange(MathF.Abs(resting.LinearVelocity.Y), 0f, 0.05f);
    }

    [Fact]
    public void JoltTriangleMeshBodyCannotBeDynamic()
    {
        using JoltPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        BodyDefinition dynamicMesh = new(
            PhysicsBodyMode.Dynamic,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { TriangleMeshTests.HorizontalGround(halfExtent: 4f) });

        Assert.Throws<NotSupportedException>(() => world.CreateBody(dynamicMesh));
    }
}
