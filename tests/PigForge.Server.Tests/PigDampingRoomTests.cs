using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// G90 (docs/specs/body-defaults.md §6) on the real backend and the shipped content: the original's
/// <c>Pig.FixedUpdate</c> (<c>Pig.cs:249-262</c>) raises a pig's drag <b>and</b> angularDrag as it
/// slows — <c>0.2 + 2.5 * (1 - |v|)</c>, up to 2.7 below 1 m/s — so a pig that has run out of speed
/// parks instead of creeping on for metres. The control is the same document with the pig's
/// <c>capabilities.dampingRamp</c> stripped, so the two runs differ by that one key and nothing else.
/// <para>
/// The rig: the shipped pig (4) is dropped on a 12 m plank at 0.25 rad standing on the shipped
/// sandbox's three 20 m ground slabs, so it rolls ~11 m down the plank, crosses onto the flat floor
/// and decays there — which is where the ramp acts. Measured (this run, deterministic): both pigs are
/// still above the 1 m/s threshold at tick 540 (1.48 m/s), and once the ramped pig crosses it, its
/// speed collapses — 0.20 m/s at tick 780, 0.001 at tick 900 — while the control is still rolling at
/// 0.574 m/s at tick 900 and 0.262 m/s at tick 1200, 3.6 m further down the floor.
/// </para>
/// </summary>
public sealed class PigDampingRoomTests
{
    private const uint PartPig = 4;
    private const uint PartGroundSlab = 2;
    private const uint PartRampPlank = 6;
    private const uint PartPlayer = 1;
    private const uint SequencePlace = 1;
    private const uint SequenceStart = 2;

    private const int Ticks = 1200;
    private const int SampleTicks = 30;
    private const float RampThreshold = 1f;

    /// <summary>0.5 m above the plank's upper end (terrain-v1's own plank geometry: 12 m at 0.25 rad
    /// with its surface 0.242 m above its centre line).</summary>
    private static readonly PhysicsVector3 Drop = new(27f, 1.3f, 0f);

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    [Fact]
    public void ASlowingPigParksInsteadOfCreepingOn()
    {
        RollResult ramped = Roll(withRamp: true);
        RollResult control = Roll(withRamp: false);

        // The ramp's own branch is `|v| < 1`, so until the pig is that slow the two runs agree: both
        // read 1.48 m/s at tick 540 (the ramp has not been asked for anything yet).
        Assert.Equal(1.48f, ramped.SpeedAt540, precision: 2);
        Assert.Equal(1.48f, control.SpeedAt540, precision: 2);

        // Below the threshold both drags jump to 2.7 and the pig is parked within a few metres.
        Assert.True(
            ramped.RestTick > 0 && ramped.RestTick < 960,
            $"the ramp must park the pig: rest tick {ramped.RestTick}, |v| {ramped.Speed} at tick {Ticks}");
        Assert.True(ramped.Speed < 0.01f, $"ramped |v| {ramped.Speed} at tick {Ticks}");

        // Without it, the same pig is still rolling 20 s later and has covered 3.6 m more floor.
        Assert.Equal(0, control.RestTick);
        Assert.True(control.Speed > 0.2f, $"control |v| {control.Speed} at tick {Ticks}");
        Assert.True(
            control.Travel > ramped.Travel + 2f,
            $"the control must roll further: ramped {ramped.Travel} m, control {control.Travel} m");
    }

    [Fact]
    public void TheRampIsWhatParksThePig()
    {
        // Non-null: the same scenario without the pig's ramp never reaches rest, so an equal outcome
        // would mean the ramp never reached the physics world.
        Assert.NotEqual(Roll(withRamp: true).RestTick, Roll(withRamp: false).RestTick);
    }

    private readonly record struct RollResult(float Travel, float Speed, float SpeedAt540, int RestTick);

    /// <summary>Drops one pig on the plank and runs the real room, sampling the pig's own entity.</summary>
    private static RollResult Roll(bool withRamp)
    {
        using GameRoom room = CreateRoom(withRamp);
        CommandOutcome placed = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, SequencePlace, 0, PartPig, Drop.X, Drop.Y, Angle: 0f, Scale: 1f),
            PartPlayer));
        Assert.True(placed.IsAccepted, $"place: {placed.Status}/{placed.Error}");
        Assert.True(
            room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, SequenceStart, 0), PartPlayer)).IsAccepted);

        float travel = 0f;
        float speed = 0f;
        float speedAt540 = 0f;
        int restTick = 0;
        for (int tick = SampleTicks; tick <= Ticks; tick += SampleTicks)
        {
            room.RunTicks(SampleTicks);
            SnapshotEntity pig = Publish(room).Single(entity => entity.EntityId == placed.EntityId);
            Assert.NotEqual(0u, pig.PhysicsBodyId);
            speed = MathF.Sqrt(
                (pig.LinearVelocity.X * pig.LinearVelocity.X)
                + (pig.LinearVelocity.Y * pig.LinearVelocity.Y)
                + (pig.LinearVelocity.Z * pig.LinearVelocity.Z));
            travel = MathF.Abs(pig.Position.X - Drop.X);
            if (tick == 540)
            {
                speedAt540 = speed;
            }

            if (restTick == 0 && speed < 0.01f)
            {
                restTick = tick;
            }
        }

        return new RollResult(travel, speed, speedAt540, restTick);
    }

    private static GameRoom CreateRoom(bool withRamp)
    {
        string root = FindRepositoryRoot();
        PartContentDocument document = PartContentParser.Parse(
            File.ReadAllText(Path.Combine(root, "content", "parts.json")));
        if (!withRamp)
        {
            document = document with
            {
                Parts = document.Parts
                    .Select(part => part.PartTypeId == PartPig && part.Capabilities is not null
                        ? part with { Capabilities = part.Capabilities with { DampingRamp = null } }
                        : part)
                    .ToArray(),
            };
        }

        LevelContentDocument level = new(
            "pig-damping-test-v1",
            GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
            MapBounds: FarBounds,
            Spawns:
            [
                new LevelSpawnDefinition(PartGroundSlab, new PhysicsVector3(-20f, -3.5f, 0f)),
                new LevelSpawnDefinition(PartGroundSlab, new PhysicsVector3(0f, -3.5f, 0f)),
                new LevelSpawnDefinition(PartGroundSlab, new PhysicsVector3(20f, -3.5f, 0f)),
                new LevelSpawnDefinition(PartRampPlank, new PhysicsVector3(22f, -1.758f, 0f), Angle: 0.25f),
            ]);
        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 1200,
            ObjectivesEnabled: false);
        GameRoom room = new(GameRoomOptions.Create(
            new PartContentLibrary(document),
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config,
            sandboxMode: true));
        room.SetupFromLevel(level);
        return room;
    }

    private static List<SnapshotEntity> Publish(GameRoom room)
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
