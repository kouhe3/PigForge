using PigForge.Core;
using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The switch belongs to the part, not to the room: a part a player placed in a goal-based level
/// room answers <c>SetPartActive</c>/<c>SetPartTypeActive</c> exactly as in the sandbox (gap G115).
/// The original builds its button list out of <c>Contraption.Instance.Parts</c> in every game mode
/// (<c>UIPartButtonList.CreateButtons</c>, UIPartButtonList.cs:499-575) and its parts start switched
/// off -- <c>Rocket.Awake</c> clears <c>m_enabled</c> (Rocket.cs:123), <c>FanPropeller.cs:64-68</c>
/// leaves the motor off -- so a level room that registered no activation left the player's TNT,
/// bellows, glove and engines unreachable: every switch answered
/// <c>PartNotSwitchable</c> because <c>RegisterPlacedRole</c> only registered one under
/// <c>_sandboxMode</c>. A placed part therefore keeps waiting for its switch here instead of taking
/// the pre-switch automatic path the legacy rooms still use.
/// <para>
/// The rigs are buildings in the air over a level with no terrain: gravity is straight down, so the
/// only thing that can turn or push a rig is its own part, which keeps the readings independent of
/// a ground contact.
/// </para>
/// </summary>
public sealed class LevelSwitchRoomTests
{
    private const uint PartFrame = 1;
    private const uint PartEngine = 8;
    private const uint PartFan = 11;
    private const uint PartRocket = 13;
    private const uint PartPlayer = 1;

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    /// <summary>A goal zone far below the rigs: a fall never wins a run inside a test window.</summary>
    private static readonly GameplayZone FarGoal = new(
        new PhysicsVector3(-100f, -500f, -2f),
        new PhysicsVector3(100f, -499f, 2f));

    [Fact]
    public void ALevelRoomLeavesAPlacedFanOffUntilItsSwitch()
    {
        using GameRoom room = StartLevelRoom();
        uint sequence = 0;
        uint frame = Place(room, ref sequence, PartFrame, 0f, 20f);
        Place(room, ref sequence, PartEngine, 0f, 20f); // enclosed in the frame, so it supplies power
        uint fan = Place(room, ref sequence, PartFan, 0f, 21f); // one cell above the compound's centre of mass
        Assert.True(room.Submit(Start(++sequence)).IsAccepted);

        // FanPropeller.cs:64-68 leaves the motor off, and gravity pulls straight down through the
        // centre of mass, so the rig falls without turning at all.
        room.RunTicks(30);
        Assert.InRange(MathF.Abs(AngularVelocity(room, frame).Z), 0f, 0.05f);

        Assert.True(room.Submit(SetActive(++sequence, fan, true)).IsAccepted);
        room.RunTicks(30);

        // The fan's content axis is Left (-x) and its mount is above the centre of mass, so
        // r x F (r.y > 0, F.x < 0) turns the rig counter-clockwise about z: 6.35 rad/s measured after
        // 30 ticks (the sandbox fixture's 3.8 rad/s is the same rig resting on the terrain). Before
        // this gap was fixed the fan ran from tick zero (no activation entry = the pre-switch path),
        // so the rig was already turning when the assertion above ran.
        Assert.InRange(AngularVelocity(room, frame).Z, 3f, 10f);
    }

    [Fact]
    public void ALevelRoomLeavesAPlacedRocketOnItsButton()
    {
        using GameRoom room = StartLevelRoom();
        uint sequence = 0;
        uint frame = Place(room, ref sequence, PartFrame, -20f, 20f);
        uint rocket = Place(room, ref sequence, PartRocket, -19f, 20f);
        Assert.True(room.Submit(Start(++sequence)).IsAccepted);

        // Rocket.Awake clears m_enabled (Rocket.cs:123) and only OnTouch arms it
        // (Rocket.cs:570-581), so the rig coasts: nothing pushes it along x before the press.
        room.RunTicks(30);
        Assert.InRange(MathF.Abs(VelocityX(room, frame)), 0f, 0.05f);

        Assert.True(room.Submit(SetActive(++sequence, rocket, true)).IsAccepted);
        room.RunTicks(60);

        // A plain rocket (no `visualization`) thrusts from its first tick after ignition
        // (Rocket.cs:235-240 -> RunRockets), and its content axis is +x: 20.12 m/s measured after 60
        // ticks of burn.
        Assert.True(
            VelocityX(room, frame) > 5f,
            $"the pressed rocket must push the rig along +x: {VelocityX(room, frame)} m/s");
    }

    [Fact]
    public void ALevelRoomSwitchesEveryPartOfAType()
    {
        using GameRoom room = StartLevelRoom();
        uint sequence = 0;
        Place(room, ref sequence, PartFrame, 0f, 20f);
        uint first = Place(room, ref sequence, PartFan, 0f, 21f);
        uint second = Place(room, ref sequence, PartFan, 2f, 19f);
        Assert.True(room.Submit(Start(++sequence)).IsAccepted);

        // The switch bar's own command for a group (PGFC kind 9): every placed part of that type
        // this owner has, ascending -- the level room places under owner 0 (one shared contraption,
        // the original's single-player `Contraption.GetPartCount`).
        Assert.True(room.Submit(SetTypeActive(++sequence, PartFan, true)).IsAccepted);

        List<SnapshotEntity> entities = PublishEntities(room);
        Assert.Equal((byte)1, entities.Single(entity => entity.EntityId == first).Flags);
        Assert.Equal((byte)1, entities.Single(entity => entity.EntityId == second).Flags);
    }

    [Fact]
    public void ALevelRoomRejectsSwitchesWhileBuildingAndOnNonSwitchableParts()
    {
        using GameRoom room = StartLevelRoom();
        uint sequence = 0;
        uint block = Place(room, ref sequence, PartFrame, 0f, 20f); // no activation in content
        uint fan = Place(room, ref sequence, PartFan, 0f, 21f);

        // The original's parts only answer while its run state is Running
        // (BasePart.ProcessTouch, BasePart.cs:924-935), so the phase matrix is the room kind's.
        CommandOutcome building = room.Submit(SetActive(++sequence, fan, true));
        Assert.Equal(CommandStatus.WrongMode, building.Status);

        Assert.True(room.Submit(Start(++sequence)).IsAccepted);
        CommandOutcome notSwitchable = room.Submit(SetActive(++sequence, block, true));
        Assert.Equal(CommandStatus.RuleRejected, notSwitchable.Status);
        Assert.Equal(ConstructionError.PartNotSwitchable, notSwitchable.Error);
    }

    private static GameRoom StartLevelRoom()
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));
        LevelContentDocument level = new(
            "level-switch-room-test-v10",
            GoalZone: FarGoal,
            MapBounds: FarBounds,
            Spawns: []);
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

    private static uint Place(GameRoom room, ref uint sequence, uint partTypeId, float x, float y)
    {
        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, ++sequence, PartPlayer, partTypeId, x, y, Angle: 0f, Scale: 1f),
            PartPlayer));
        Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
        return outcome.EntityId;
    }

    private static StartSimulationCommand Start(uint sequence) => new(0, sequence, PartPlayer);

    private static SetPartActiveCommand SetActive(uint sequence, uint entityId, bool active) =>
        new(0, sequence, PartPlayer, entityId, active);

    private static SetPartTypeActiveCommand SetTypeActive(uint sequence, uint partTypeId, bool active) =>
        new(0, sequence, PartPlayer, partTypeId, active);

    private static ReplayVector3 AngularVelocity(GameRoom room, uint entityId) =>
        PublishEntities(room).Single(entity => entity.EntityId == entityId).AngularVelocity;

    private static float VelocityX(GameRoom room, uint entityId) =>
        PublishEntities(room).Single(entity => entity.EntityId == entityId).LinearVelocity.X;

    private static List<SnapshotEntity> PublishEntities(GameRoom room)
    {
        List<SnapshotEntity> entities = new();
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(64)];
        Assert.True(room.TryPublishSnapshot(buffer, out int written));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, written), out SnapshotFrameHeader header, out SnapshotFrameReader reader));
        for (uint index = 0; index < header.EntityCount; index++)
        {
            Assert.True(reader.TryReadEntity(out SnapshotEntity entity));
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

        if (directory is null)
        {
            throw new InvalidOperationException($"Could not locate content/parts.json above {AppContext.BaseDirectory}.");
        }

        return directory.FullName;
    }
}
