using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The impact rules (ADR-002: a charge ignites, an egg breaks, a detacher fires on a *strong* impact)
/// read the pair's approach speed, the same estimate the bounce gate uses (ADR-031, gap G116). They used
/// to read the pair's largest post-step velocity change, which misses every real landing: a measured
/// Bepu landing at 11.61 m/s produced its <c>ContactStarted</c> two steps after the last free tick and
/// carried 2.60 m/s -- the solver's soft recovery had already absorbed the rest -- so a charge dropped
/// onto a crate lit nothing at all.
/// <para>
/// The rig is a level with one crate-sized prop box at the origin (top plane y = 0.5) and a charge
/// placed above it: the same furniture a real level drives into.
/// </para>
/// </summary>
public sealed class ImpactRulesRoomTests
{
    private const uint PartTnt = 9;
    private const uint PartPlayer = 1;

    /// <summary>The charge's own fuse: what the impact arms, and what makes it leave the world.</summary>
    private const uint TntFuseTicks = 5;

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    private static readonly GameplayZone FarGoal = new(
        new PhysicsVector3(-100f, -500f, -2f),
        new PhysicsVector3(100f, -499f, 2f));

    /// <summary>One crate-sized prop at the origin: a 20 x 1 x 1 box whose top plane is y = 0.5.</summary>
    private static readonly LevelPropDefinition Crate = new("TNT_Box", X: 0f, Y: 0f, Z: 0f, Rotation: 0f, ScaleX: 1f, ScaleY: 1f)
    {
        Boxes = [new LevelPropBoxDefinition(X: 0f, Y: 0f, Z: 0f, SizeX: 20f, SizeY: 1f, SizeZ: 1f, Rotation: 0f)],
    };

    /// <summary>
    /// The same crate with the vanilla `TNT_Box` component's own numbers (schemaVersion 11,
    /// `docs/specs/level-props.md` §3.8): a level's own crate blows up when something hits it faster
    /// than 2 m/s, reaching 8 m with 25 N*s of impulse.
    /// </summary>
    private static readonly LevelPropDefinition ExplosiveCrate = Crate with
    {
        Explosive = new LevelPropExplosiveDefinition(TriggerSpeed: 2f, ExplosionRadius: 8f, ExplosionImpulse: 25f),
    };

    [Fact]
    public void AChargeThatLandsHardOnACrateIgnoresNothingAndBlows()
    {
        // 7.5 m of free fall, minus the original's own 0.2 drag: measured 10.70 m/s at the last free
        // tick and a rest pose about 0.47 m above the crate's top plane.
        using GameRoom room = StartRoom();
        uint tnt = Place(room, 0f, 8f);
        Assert.True(room.Submit(Start()).IsAccepted);

        float fastest = 0f;
        uint landed = 0;
        uint gone = 0;
        for (uint tick = 1; tick <= 240; tick++)
        {
            room.Tick();
            SnapshotEntity? entity = Find(room, tnt);
            if (entity is null)
            {
                gone = tick;
                break;
            }

            float speed = MathF.Abs(entity.Value.LinearVelocity.Y);
            if (entity.Value.Position.Y > 1.05f)
            {
                fastest = MathF.Max(fastest, speed);
                landed = tick;
            }
        }

        // The premise: this was a hard landing, above the room's own ignition speed (5 m/s), so an
        // ignition can only be the impact rule answering it.
        Assert.True(fastest > 5f, $"the fall must beat the ignition speed: {fastest} m/s");
        Assert.True(gone > landed, $"the charge must leave the world after it lands: gone {gone}, landed {landed}");
        // The fuse burns a handful of ticks after the landing tick; anything much later would mean the
        // charge was armed by something else entirely.
        Assert.InRange(gone - landed, 1u, TntFuseTicks + 4u);
    }

    [Fact]
    public void AChargeThatSettlesGentlyOnACrateStaysThere()
    {
        // A two-centimetre drop onto the same crate: a touch, not an impact.
        using GameRoom room = StartRoom();
        uint tnt = Place(room, 0f, 1.05f);
        Assert.True(room.Submit(Start()).IsAccepted);

        float fastest = 0f;
        for (uint tick = 1; tick <= 240; tick++)
        {
            room.Tick();
            SnapshotEntity? entity = Find(room, tnt);
            Assert.NotNull(entity);
            fastest = MathF.Max(fastest, MathF.Abs(entity.Value.LinearVelocity.Y));
        }

        // The premise: it really did touch down slowly, so "nothing happened" is the rule refusing the
        // impact rather than the charge never landing.
        Assert.InRange(fastest, 0f, 2f);
        Assert.NotNull(Find(room, tnt));
    }

    /// <summary>
    /// A level's own crate answers an impact itself: the vanilla `TNT_Box` is a static collider whose
    /// component explodes it above its own trigger speed (`TNTBox.cs:15-19`). The room builds the crate's
    /// static body out of the prop's `boxes` and reads the document's v11 `explosive` numbers
    /// (`docs/specs/level-props.md` §3.8); the blast takes the crate out of the world with its art, which
    /// is what the client's collected-prop hiding already does -- a level crate could be driven into and
    /// nothing happened before this.
    /// </summary>
    [Fact]
    public void AChargeDroppedOntoAnExplosiveCrateBlowsTheCrateUpWithIt()
    {
        using GameRoom room = StartRoom(ExplosiveCrate);
        Assert.Equal(1, room.PropBodyCount);
        uint tnt = Place(room, 0f, 8f);
        Assert.True(room.Submit(Start()).IsAccepted);

        // The crate leaves the world on its own impact tick; the charge it arms needs its fuse, so the
        // window runs past the explosion either way.
        uint exploded = 0;
        for (uint tick = 1; tick <= 240; tick++)
        {
            room.Tick();
            if (exploded == 0 && room.CollectedPropCount > 0)
            {
                exploded = tick;
            }
        }

        // The crate's own component answered the landing: the prop is collected (its art goes and its
        // static body with it), and the charge that landed on it blew with it.
        Assert.NotEqual(0u, exploded);
        Assert.Equal(1, room.CollectedPropCount);
        Assert.Equal(0, room.PropBodyCount);
        Assert.Null(Find(room, tnt));
    }

    private static GameRoom StartRoom(LevelPropDefinition? prop = null)
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));
        LevelContentDocument level = new(
            "impact-rules-room-test-v10",
            GoalZone: FarGoal,
            MapBounds: FarBounds,
            Spawns: [])
        {
            Props = [prop ?? Crate],
        };
        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 0,
            ObjectivesEnabled: true,
            ChallengeTriggers: LevelChallengeTriggers.Build(level));
        GameRoom room = new(GameRoomOptions.Create(
            parts,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config,
            sandboxMode: false));
        room.SetupFromLevel(level);
        return room;
    }

    private static uint Place(GameRoom room, float x, float y)
    {
        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, 1, PartPlayer, PartTnt, x, y, Angle: 0f, Scale: 1f),
            PartPlayer));
        Assert.True(outcome.IsAccepted, $"place the charge at ({x},{y}): {outcome.Status}/{outcome.Error}");
        return outcome.EntityId;
    }

    private static StartSimulationCommand Start() => new(0, 2, PartPlayer);

    private static SnapshotEntity? Find(GameRoom room, uint entityId)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(64)];
        Assert.True(room.TryPublishSnapshot(buffer, out int written));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, written), out SnapshotFrameHeader header, out SnapshotFrameReader reader));
        for (uint index = 0; index < header.EntityCount; index++)
        {
            Assert.True(reader.TryReadEntity(out SnapshotEntity entity));
            if (entity.EntityId == entityId)
            {
                return entity;
            }
        }

        return null;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "content", "parts.json")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException($"Could not locate content/parts.json above {AppContext.BaseDirectory}.");
        }

        return directory.FullName;
    }
}
