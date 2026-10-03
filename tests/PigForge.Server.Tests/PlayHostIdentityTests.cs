using PigForge.Core;
using PigForge.Core.Construction;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

public sealed class PlayHostIdentityTests
{
    [Fact]
    public void BindPlayerReplacesWirePlayerIdWithConnectionId()
    {
        ReplayCommand wire = new PlacePartCommand(
            Tick: 0,
            Sequence: 1,
            PlayerId: 0,
            PartTypeId: 4,
            PositionX: -5.25f,
            PositionY: 1.5f,
            Angle: 0.5f,
            Scale: 1f);

        ReplayCommand bound = PlayHost.BindPlayer(wire, playerId: 7);

        Assert.Equal(7u, bound.PlayerId);
        PlacePartCommand place = Assert.IsType<PlacePartCommand>(bound);
        Assert.Equal(4u, place.PartTypeId);
        Assert.Equal(-5.25f, place.PositionX);
        Assert.Equal(1.5f, place.PositionY);
        Assert.Equal(0.5f, place.Angle);
        Assert.Equal(1f, place.Scale);
    }

    [Fact]
    public void BindPlayerPreservesCommandKindAndSequenceForEveryWireCommand()
    {
        ReplayCommand[] commands = new ReplayCommand[]
        {
            new PlacePartCommand(0, 1, 0, 4, 1f, 2f, 0f, 1f),
            new RemovePartCommand(0, 2, 0, 9),
            new RotatePartCommand(0, 3, 0, 9, 1f),
            new StartSimulationCommand(0, 4, 0),
            new EnterBuildModeCommand(0, 5, 0, BuildModePolicy.Keep),
            new RetryCommand(0, 6, 0),
            new MovePartCommand(0, 7, 0, 9, 1f, 2f),
            new ScalePartCommand(0, 8, 0, 9, 1.5f),
        };

        foreach (ReplayCommand command in commands)
        {
            ReplayCommand bound = PlayHost.BindPlayer(command, playerId: 3);

            Assert.Equal(command.GetType(), bound.GetType());
            Assert.Equal(command.Sequence, bound.Sequence);
            Assert.Equal(3u, bound.PlayerId);
        }
    }

    [Fact]
    public void EachConnectionAllocatesDistinctMonotonicPlayerIds()
    {
        uint first = PlayHost.NextPlayerId();
        uint second = PlayHost.NextPlayerId();

        Assert.NotEqual(0u, first);
        Assert.NotEqual(first, second);
        Assert.True(second > first, $"{second} should be greater than {first}");

        ReplayCommand firstConnection = PlayHost.BindPlayer(new StartSimulationCommand(0, 1, 0), first);
        ReplayCommand secondConnection = PlayHost.BindPlayer(new StartSimulationCommand(0, 1, 0), second);

        Assert.Equal(first, firstConnection.PlayerId);
        Assert.Equal(second, secondConnection.PlayerId);
        Assert.NotEqual(firstConnection.PlayerId, secondConnection.PlayerId);
    }

    [Fact]
    public void SandboxRoomStartsRunningWithObjectivesDisabled()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();

        Assert.Equal(RoomMode.Running, room.Mode);
        Assert.Equal(GameplayPhase.Playing, room.Phase);

        for (int i = 0; i < 5; i++)
        {
            room.Tick();
        }

        Assert.True(room.CurrentTick > 0u);
        Assert.Equal(RoomMode.Running, room.Mode);
        Assert.Equal(GameplayPhase.Playing, room.Phase);
    }

    [Fact]
    public void LevelRoomFactoriesStillStartInBuilding()
    {
        using GameRoom slope = PlayHost.CreateSlopeRoom();
        using GameRoom terrain = PlayHost.CreateTerrainRoom();

        Assert.Equal(RoomMode.Building, slope.Mode);
        Assert.Equal(RoomMode.Building, terrain.Mode);
    }

    [Fact]
    public void SubmitLogsBoundConnectionIdInsteadOfWireValue()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint connectionId = PlayHost.NextPlayerId();

        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, 1, 0), connectionId));

        Assert.Equal(connectionId, outcome.Command.PlayerId);
        Assert.Equal(connectionId, room.OutcomeLog[^1].Command.PlayerId);
    }

    [Fact]
    public void APlaySessionResumesItsPlayerIdAndANewSessionDoesNot()
    {
        uint next = 0;
        PlaySessions sessions = new(() => ++next);
        const string Client = "8f14e45fceea167a9b3c5d7e1f2a4b6c";
        const string Other = "0d9c7b5a3f1e2d4c6b8a0f2e4d6c8b0a";

        uint firstConnect = sessions.Resolve(Client, out bool firstResumed);
        uint reconnect = sessions.Resolve(Client, out bool reconnectResumed);
        uint otherClient = sessions.Resolve(Other, out bool otherResumed);

        Assert.False(firstResumed);
        Assert.True(reconnectResumed);
        Assert.Equal(firstConnect, reconnect);
        Assert.False(otherResumed);
        Assert.NotEqual(firstConnect, otherClient);
        Assert.Equal(2, sessions.SessionCount);
    }

    [Fact]
    public void AnAbsentOrMalformedSessionIsAlwaysANewPlayer()
    {
        uint next = 0;
        PlaySessions sessions = new(() => ++next);

        uint absent = sessions.Resolve(null, out bool absentResumed);
        uint empty = sessions.Resolve(string.Empty, out _);
        // A bare player id is not a session id: the map is never indexed by a number the caller
        // picked, so a client cannot claim another connection's identity.
        uint bareId = sessions.Resolve("7", out _);
        uint malformed = sessions.Resolve("not a valid session id!", out _);

        Assert.False(absentResumed);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, new[] { absent, empty, bareId, malformed });
        Assert.Equal(0, sessions.SessionCount);
    }

    [Fact]
    public void AResumedSessionKeepsItsOwnPartsRemovable()
    {
        uint next = 0;
        PlaySessions sessions = new(() => ++next);
        using GameRoom room = PlayHost.CreateSandboxRoom();
        const string Client = "8f14e45fceea167a9b3c5d7e1f2a4b6c";

        uint owner = sessions.Resolve(Client, out _);
        CommandOutcome placed = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, 1, 0, PartTypeId: 1, PositionX: -8f, PositionY: 6f, Angle: 0f, Scale: 1f),
            owner));
        Assert.True(placed.IsAccepted, $"place: {placed.Status}/{placed.Error}");

        // The user clicks 连接房间: the new socket carries the same session id, so the host hands
        // back the same player id instead of orphaning the layout under a fresh one.
        uint reconnected = sessions.Resolve(Client, out bool resumed);
        Assert.True(resumed);
        Assert.Equal(owner, reconnected);

        CommandOutcome removed = room.Submit(PlayHost.BindPlayer(
            new RemovePartCommand(0, 2, 0, placed.EntityId),
            reconnected));
        Assert.True(removed.IsAccepted, $"remove after reconnect: {removed.Status}/{removed.Error}");
    }

    [Fact]
    public void AnotherSessionCannotRemoveThosePartsAndTheyStayInTheWorld()
    {
        uint next = 0;
        PlaySessions sessions = new(() => ++next);
        using GameRoom room = PlayHost.CreateSandboxRoom();
        const string Owner = "8f14e45fceea167a9b3c5d7e1f2a4b6c";

        uint owner = sessions.Resolve(Owner, out _);
        CommandOutcome placed = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, 1, 0, PartTypeId: 1, PositionX: -8f, PositionY: 6f, Angle: 0f, Scale: 1f),
            owner));
        Assert.True(placed.IsAccepted, $"place: {placed.Status}/{placed.Error}");

        // A genuinely new player (no session, or another one) is refused, not handed the parts.
        uint stranger = sessions.Resolve("0d9c7b5a3f1e2d4c6b8a0f2e4d6c8b0a", out bool strangerResumed);
        CommandOutcome refused = room.Submit(PlayHost.BindPlayer(
            new RemovePartCommand(0, 1, 0, placed.EntityId),
            stranger));
        Assert.False(strangerResumed);
        Assert.Equal(CommandStatus.RuleRejected, refused.Status);
        // NotOwnedByPlayer rather than EntityNotFound is what proves the part is still alive and
        // still owned by the absent player: a disconnected player's parts stay in the world.
        Assert.Equal(ConstructionError.NotOwnedByPlayer, refused.Error);

        CommandOutcome ownerRemoval = room.Submit(PlayHost.BindPlayer(
            new RemovePartCommand(0, 2, 0, placed.EntityId),
            owner));
        Assert.True(ownerRemoval.IsAccepted, $"owner remove: {ownerRemoval.Status}/{ownerRemoval.Error}");
    }
}
