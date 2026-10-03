using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Protocol;
using PigForge.Physics.Abstractions;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The balloon's lift on the real content and the Bepu backend, against the original's numbers:
/// <c>Part_Balloon_01_SET.prefab</c> serializes <c>m_force: 11.5</c> and <c>Balloon.cs:181</c>
/// multiplies it by <c>BalloonForce</c> (<c>INSettingsBExp.json</c>, 2.0) for 23 N, applied every
/// <c>FixedUpdate</c> to the balloon's own body (<c>Balloon.cs:207-210</c>). <c>Balloon.cs:129</c>
/// rewrites that body to <c>mass 0.1f</c>, so the original's lift is 230 m/s².
/// PigForge applies one impulse per tick (<c>GameplayRules.RunBalloons</c>), so content carries
/// <c>force / 60</c>, and a free balloon must accelerate at exactly the original's force/mass.
/// </summary>
public sealed class BalloonLiftTests
{
    private const uint PartWoodenBlock = 1;
    private const uint PartBalloon = 10;

    private const float Gravity = 9.81f;
    private const float TickRate = 60f;

    /// <summary>11.5 N (prefab) x 2.0 (BalloonForce) = 23 N of lift on a 0.1 kg body.</summary>
    private const float OriginalForcePerBalloon = 23f;
    private const float OriginalBalloonMass = 0.1f;

    [Fact]
    public void FreeBalloonAcceleratesAtTheOriginalsForceOverMass()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        // No chassis within the original's 10-cell search radius, so no rope: pure lift.
        uint balloon = Place(room, ref sequence, player, PartBalloon, 0f, 5f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        // The first tick still runs with an empty command list (rules emit after the step), so
        // measure the steady rise once the lift is being applied every tick.
        room.RunTicks(4);
        float expectedAcceleration = (OriginalForcePerBalloon / OriginalBalloonMass) - Gravity;
        float previousY = BalloonY(room, balloon);
        float previousDelta = 0f;
        for (int tick = 0; tick < 6; tick++)
        {
            room.Tick();
            float y = BalloonY(room, balloon);
            float delta = y - previousY;
            previousY = y;
            if (tick == 0)
            {
                previousDelta = delta;
                continue;
            }

            // The published position advances one tick at a time, so the second difference of
            // the position is the acceleration in m/s^2.
            float acceleration = (delta - previousDelta) * TickRate * TickRate;
            previousDelta = delta;
            Assert.InRange(acceleration, expectedAcceleration - 0.5f, expectedAcceleration + 0.5f);
        }
    }

    private static float BalloonY(GameRoom room, uint entityId) =>
        PublishEntities(room).Single(entity => entity.EntityId == entityId).Position.Y;

    [Fact]
    public void BalloonLiftOnARigIsBounded()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint frame = Place(room, ref sequence, player, PartWoodenBlock, 0f, 2f);
        Place(room, ref sequence, player, PartBalloon, 0f, 3f);
        float buildY = PublishEntities(room).Single(entity => entity.EntityId == frame).Position.Y;
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        room.RunTicks(60);

        float lifted = PublishEntities(room).Single(entity => entity.EntityId == frame).Position.Y - buildY;
        // A single 1 kg frame under one 23 N balloon must climb, and must not be yanked: the
        // pre-fix 90 N equivalent threw it ~30 m in the same second.
        Assert.True(lifted > 0.2f, $"the balloon must lift the frame: {lifted} m");
        Assert.True(lifted < 4f, $"the lift must stay near the original's 23 N: {lifted} m in one second");
    }

    [Fact]
    public void SandbagFallsOntoTheTerrainInsteadOfPassingThrough()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        // 12 cells above the terrain: outside the original's 10-cell attachment search, so the
        // sandbag is loose cargo and must land on the ground slab (top at y = -3).
        uint sandbag = Place(room, ref sequence, player, 21, 0f, 9f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        room.RunTicks(240);

        SnapshotEntity entity = PublishEntities(room).Single(candidate => candidate.EntityId == sandbag);
        // The collider is the original's runtime SphereCollider: radius 0.13 centred 0.1 below
        // the part origin (Sandbag.cs:122-127). The level's slabs and ramps put the ground
        // somewhere between -3 and -0.6 at x = 0, so what matters is that it landed on solid
        // geometry instead of sinking into the ground slab it fell towards.
        Assert.True(entity.Position.Y > -2.94f, $"the sandbag must rest on the terrain: y={entity.Position.Y}");
        Assert.True(entity.Position.Y < 8.5f, $"the sandbag must fall from its placement: y={entity.Position.Y}");
        for (int tick = 0; tick < 30; tick++)
        {
            room.Tick();
            SnapshotEntity rolling = PublishEntities(room).Single(candidate => candidate.EntityId == sandbag);
            Assert.True(rolling.Position.Y > -2.94f, $"the sandbag must never sink into the ground slab: y={rolling.Position.Y}");
            Assert.Equal(0f, rolling.Position.Z);
        }

        Assert.Equal(0f, entity.Position.Z);
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
}
