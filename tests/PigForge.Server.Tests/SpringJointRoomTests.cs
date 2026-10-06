using PigForge.Core;
using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// Spring seams in a real room (docs/specs/spring-joint.md §3/§4): two placed parts held by a soft,
/// calibrated distance link instead of one merged body; the link breaks past 3 m of anchor separation
/// and hands over to a runtime sub-entity (the original's <c>SpringEndpoint</c> body, ADR-027). Real
/// Bepu, real content, real sandbox room: the numbers below are what the shipped parts do.
/// </summary>
public sealed class SpringJointRoomTests
{
    private const uint PartFrame = 1;
    private const uint PartTnt = 9;
    private const uint PartSpring = 12;
    private const uint PartHeavy = 18;
    private const uint PartTarget = 25;

    /// <summary>The vanilla probe cell <c>limit_auto_mass0p6</c> (tasks/spring-probe.json,
    /// Unity 2021.3.45f2): a 0.6 kg body — this content's own spring mass — hanging under the
    /// original's y soft limit reads 0.123418 m of sag at a 5.863 N joint force. The acceptance
    /// band is ±25% (docs/specs/spring-joint.md §6).</summary>
    private const float ProbeSag = 0.123418f;

    private const float Tolerance = 0.25f;

    private const float Gravity = 9.81f;

    private static readonly FixedTimeStep Step = FixedTimeStep.FromSeconds(1f / 60f);

    private const PhysicsConstraintMask AnchorLock =
        PhysicsConstraintMask.LockPositionX | PhysicsConstraintMask.LockPositionY | PhysicsConstraintMask.LockPositionZ
        | PhysicsConstraintMask.LockRotationX | PhysicsConstraintMask.LockRotationY | PhysicsConstraintMask.LockRotationZ;

    [Fact]
    public void ASpringSeamMaterialisesTwoBodiesHeldByOneSpringJoint()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint spring = Place(room, ref sequence, player, PartSpring, 0f, 4f);
        uint frame = Place(room, ref sequence, player, PartFrame, 1f, 4f);

        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(1, room.SpringJointCount);

        List<SnapshotEntity> entities = PublishEntities(room);
        uint springBody = entities.Single(entity => entity.EntityId == spring).PhysicsBodyId;
        uint frameBody = entities.Single(entity => entity.EntityId == frame).PhysicsBodyId;
        Assert.NotEqual(0u, springBody);
        Assert.NotEqual(0u, frameBody);
        Assert.NotEqual(springBody, frameBody);
    }

    [Fact]
    public void AnUnpulledSpringHoldsAndSpawnsNothing()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        Place(room, ref sequence, player, PartSpring, 0f, 4f);
        Place(room, ref sequence, player, PartFrame, 1f, 4f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        room.RunTicks(120);

        // A falling pair never stretches the link, so nothing snaps and no endpoint is spawned.
        Assert.Equal(1, room.SpringJointCount);
        Assert.Equal(0, room.SubEntityCount);
    }

    /// <summary>
    /// The original tears a spring down once its anchors are more than 3 m apart and spawns the
    /// <c>SpringEndpoint</c> body to continue the link (<c>Spring.cs:78-92,131-176</c>). PigForge
    /// pulls one apart for real: a metal box is the far end, a charge beside the pair throws the 1 kg
    /// spring part away from it, and the anchors cross 3 m within a few ticks — at a joint reaction of
    /// ~450 N, far under the declared 1200 N break force, so the geometric check is what fires.
    /// <para>
    /// The rig's room is built here with a test-grade <see cref="GameplayConfig"/> because the shipped
    /// blast (25 N·s) cannot push a free pair 3 m apart: the spring needs ~675 J of relative energy,
    /// i.e. more than 45 m/s. The original never runs its own 3 m branch either —
    /// <c>StrongSpringConnection = true</c> short-circuits <c>FixedUpdate</c> (<c>Spring.cs:80</c>) —
    /// but PigForge implements it per spec, so the test uses a stronger blast to reach it.
    /// </para>
    /// </summary>
    [Fact]
    public void APullPastThreeMetresBreaksTheSpringAndSpawnsTheEndpointBody()
    {
        using GameRoom room = CreatePullRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint spring = PullRig(room, ref sequence, player);
        Assert.True(room.SpringJointCount >= 1);

        for (int tick = 0; tick < 60 && room.SubEntityCount == 0; tick++)
        {
            room.Tick();
        }

        Assert.True(room.SubEntityCount >= 1, "the 3 m pull must spawn the endpoint body");
        Assert.Equal(0, room.SpringJointCount);

        List<SnapshotEntity> entities = PublishEntities(room);
        string dump = string.Join(" ", entities.Select(entity => $"{entity.EntityId}:{entity.PartTypeId}@b{entity.PhysicsBodyId}"));
        SnapshotEntity endpoint = entities.First(entity => entity.PartTypeId == PartSpring && entity.EntityId != spring);
        Assert.True(endpoint.PhysicsBodyId != 0, $"the endpoint sub-entity must have a live body: {dump}");
        Assert.NotEqual(entities.Single(entity => entity.EntityId == spring).PhysicsBodyId, endpoint.PhysicsBodyId);
        // The sub-entity is in the frame that was just published, and the room's own bound covers
        // every entity it carries (ADR-027: a spawned body must not push a frame past the buffer).
        Assert.True(room.MaxSnapshotEntityCount >= entities.Count);
    }

    /// <summary>
    /// Forgetting a body's joints before destroying it is the ADR-023/ADR-024 discipline, and a split
    /// destroys the compound's body: the spring seam that body carried has to be forgotten and rebound
    /// onto the piece that keeps the part, or a split anywhere beside a spring would silently tear the
    /// spring off.
    /// </summary>
    [Fact]
    public void ASplitRebindsTheSpringJointItDestroyed()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        // A wooden frame enclosing a soda bottle is one merged compound whose internal seam is the
        // unmodified threshold (both ends are Normal strength), so the shipped blast can split it.
        uint spring = Place(room, ref sequence, player, PartSpring, 0f, 4f);
        uint near = Place(room, ref sequence, player, PartFrame, 1f, 4f);
        uint far = Place(room, ref sequence, player, PartTarget, 1f, 4f);
        uint charge = Place(room, ref sequence, player, PartTnt, 1f, 6f);

        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(1, room.SpringJointCount);
        List<SnapshotEntity> before = PublishEntities(room);
        uint mergedBody = before.Single(entity => entity.EntityId == near).PhysicsBodyId;
        Assert.Equal(mergedBody, before.Single(entity => entity.EntityId == far).PhysicsBodyId);

        // The shipped blast (25 N*s) clears the detachers' own seam threshold without stretching the
        // spring anywhere near its 3 m pull break.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, charge, true), player)).IsAccepted);
        room.RunTicks(12);

        Assert.Equal(1, room.SpringJointCount);
        List<SnapshotEntity> after = PublishEntities(room);
        uint nearBody = after.Single(entity => entity.EntityId == near).PhysicsBodyId;
        Assert.True(after.Any(entity => entity.EntityId == spring), "the spring part must survive");
        Assert.NotEqual(nearBody, after.Single(entity => entity.EntityId == far).PhysicsBodyId);
        Assert.NotEqual(mergedBody, nearBody);
    }

    [Fact]
    public void TheSpringRoomIsDeterministicAcrossRuns()
    {
        Assert.Equal(RunSpringRig(), RunSpringRig());
    }

    /// <summary>
    /// A runtime sub-entity's life is its host's (ADR-027 decision 3): the player's RESET destroys the
    /// bodies and the endpoint goes with them, and a fresh Start rebuilds the seam with no endpoint
    /// left over.
    /// </summary>
    [Fact]
    public void APlayerResetTakesTheEndpointSubEntityWithIt()
    {
        using GameRoom room = CreatePullRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        PullRig(room, ref sequence, player);
        for (int tick = 0; tick < 60 && room.SubEntityCount == 0; tick++)
        {
            room.Tick();
        }

        Assert.True(room.SubEntityCount >= 1);

        Assert.True(room.Submit(PlayHost.BindPlayer(new RetryCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(0, room.SubEntityCount);
        Assert.Equal(0, room.SpringJointCount);

        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.True(room.SpringJointCount >= 1);
        Assert.Equal(0, room.SubEntityCount);
    }

    /// <summary>
    /// The calibration acceptance (docs/specs/spring-joint.md §6): a 0.6 kg load hangs under a fixed end
    /// through the slackless distance link, and the room's own numbers — the declared content stiffness
    /// and damper scaled by <see cref="CompoundAssembler.SpringEffectiveStiffnessScale"/> /
    /// <see cref="CompoundAssembler.SpringEffectiveDampingScale"/> and converted by
    /// <see cref="GameRoom.TrySpringResponse"/> — put the sag inside ±25% of the original's measured
    /// 0.123418 m at that same load. This is a real Bepu world rather than a room body because the physics contract has
    /// no static↔dynamic joint: the original's kinematic anchor is a dynamic body with every degree of
    /// freedom locked, the same stand-in WeldComplianceTests uses.
    /// </summary>
    [Fact]
    public void TheCalibratedSpringHangsTheLoadAtTheOriginalsSag()
    {
        const float load = 0.6f;
        const float anchorMass = 1000f;
        const float restDistance = 1f;
        CompoundSpring spring = new(default, default, default, default, 250f, 20f, 0.1f, 1f, 250f);
        float stiffness = CompoundAssembler.EffectiveStiffness(spring);

        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        PhysicsBodyId anchor = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 4f, 0f),
            PhysicsQuaternion.Identity,
            anchorMass,
            new ShapeDefinition[] { new SphereShapeDefinition(0.1f) },
            constraints: AnchorLock));
        PhysicsBodyId hanging = world.CreateBody(new BodyDefinition(
            new PhysicsVector3(0f, 4f - restDistance, 0f),
            PhysicsQuaternion.Identity,
            load,
            new ShapeDefinition[] { new SphereShapeDefinition(0.1f) }));
        Assert.True(GameRoom.TrySpringResponse(
            anchorMass,
            load,
            stiffness,
            CompoundAssembler.EffectiveDamping(spring),
            out float frequency,
            out float dampingRatio));
        world.CreateJoint(new JointDefinition(
            PhysicsJointKind.Distance,
            anchor,
            hanging,
            PhysicsConstraintMask.None,
            breakForce: 0f,
            breakTorque: 0f,
            minimumDistance: restDistance,
            maximumDistance: restDistance,
            springFrequency: frequency,
            springDampingRatio: dampingRatio));

        PhysicsEvent[] events = new PhysicsEvent[8];
        for (int tick = 0; tick < 300; tick++)
        {
            world.Step(Step);
            _ = world.DrainEvents(events);
        }

        float sag = Distance(world, anchor, hanging) - restDistance;
        // The acceptance is the deviation from the original's own reading: the calibrated rate puts
        // Bepu's slackless link ~0.4% above the probe's 0.123418 m (0.123896 m = 5.886 N / 47.5077 N/m).
        Assert.InRange((sag - ProbeSag) / ProbeSag, -Tolerance, Tolerance);
    }

    private static long RunSpringRig()
    {
        using GameRoom room = CreatePullRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        PullRig(room, ref sequence, player);
        room.RunTicks(90);
        return room.ComputeStateHash();
    }

    /// <summary>
    /// The pull rig: a spring part at (0,4) whose far end is the metal box at (1,4) — the box's
    /// collider reaches the spring's 0.7 x 0.2 plate within the connection proximity, so the seam is
    /// registered — plus one charge at (1,6), clear of both so it stays a free body. Materialises the
    /// player's layout, then lights the charge.
    /// </summary>
    private static uint PullRig(GameRoom room, ref uint sequence, uint player)
    {
        uint spring = Place(room, ref sequence, player, PartSpring, 0f, 4f);
        Place(room, ref sequence, player, PartHeavy, 1f, 4f);
        uint charge = Place(room, ref sequence, player, PartTnt, 1f, 6f);

        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, charge, true), player)).IsAccepted);
        return spring;
    }

    /// <summary>The sandbox room with the test-grade blast the pull rig needs (see the pull test).</summary>
    private static GameRoom CreatePullRoom()
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));
        LevelContentDocument level = LevelContentLibrary.Parse(File.ReadAllText(Path.Combine(root, "content", "levels", "terrain-v1.json")));
        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 200f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 1200,
            ObjectivesEnabled: false);
        GameRoom room = new(GameRoomOptions.Create(
            parts,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -Gravity, 0f)),
            config,
            sandboxMode: true));
        room.SetupFromLevel(level);
        return room;
    }

    private static float Distance(IPhysicsWorld world, PhysicsBodyId first, PhysicsBodyId second)
    {
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[4];
        int count = world.CopySnapshots(snapshots);
        PhysicsVector3 position = default;
        bool found = false;
        for (int index = 0; index < count; index++)
        {
            if (snapshots[index].Body == first)
            {
                position = snapshots[index].Position;
                found = true;
                break;
            }
        }

        Assert.True(found, $"body {first.Value} must be in the world");
        for (int index = 0; index < count; index++)
        {
            if (snapshots[index].Body == second)
            {
                return PhysicsVector3.Distance(position, snapshots[index].Position);
            }
        }

        throw new InvalidOperationException($"Body {second.Value} is not in the world's snapshots.");
    }

    private static uint Place(GameRoom room, ref uint sequence, uint player, uint partTypeId, float x, float y)
    {
        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, ++sequence, player, partTypeId, x, y, 0f, 1f),
            player));
        Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
        return outcome.EntityId;
    }

    private static List<SnapshotEntity> PublishEntities(GameRoom room)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(64)];
        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out _, out SnapshotFrameReader reader));
        List<SnapshotEntity> entities = new();
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            entities.Add(entity);
        }

        return entities;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "content", "parts.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
