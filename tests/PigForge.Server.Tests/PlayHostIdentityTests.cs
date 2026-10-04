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
    public void AResetKeepsTheLayoutOwnedAndRemovableByItsPlayer()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint owner = PlayHost.NextPlayerId();
        CommandOutcome placed = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, 1, 0, PartTypeId: 1, PositionX: -8f, PositionY: 6f, Angle: 0f, Scale: 1f),
            owner));
        Assert.True(placed.IsAccepted, $"place: {placed.Status}/{placed.Error}");
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, 2, 0), owner)).IsAccepted);

        Assert.True(room.Submit(PlayHost.BindPlayer(new RetryCommand(0, 3, 0), owner)).IsAccepted);

        // RESET puts the layout back as previews instead of clearing it, and the parts stay this
        // player's: the same entity id is still removable by its owner...
        CommandOutcome ownerRemoval = room.Submit(PlayHost.BindPlayer(
            new RemovePartCommand(0, 4, 0, placed.EntityId),
            owner));
        Assert.True(ownerRemoval.IsAccepted, $"owner remove: {ownerRemoval.Status}/{ownerRemoval.Error}");
    }

    [Fact]
    public void AResetOnlyTouchesThePlayerThatAskedForIt()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint owner = PlayHost.NextPlayerId();
        uint other = PlayHost.NextPlayerId();
        CommandOutcome mine = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, 1, 0, PartTypeId: 1, PositionX: -8f, PositionY: 6f, Angle: 0f, Scale: 1f),
            owner));
        CommandOutcome theirs = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, 1, 0, PartTypeId: 1, PositionX: 12f, PositionY: 6f, Angle: 0f, Scale: 1f),
            other));
        Assert.True(mine.IsAccepted && theirs.IsAccepted, $"place: {mine.Status}/{theirs.Status}");
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, 2, 0), other)).IsAccepted);

        Assert.True(room.Submit(PlayHost.BindPlayer(new RetryCommand(0, 3, 0), owner)).IsAccepted);

        // The other player's part is still alive and still not this player's to delete.
        CommandOutcome refused = room.Submit(PlayHost.BindPlayer(
            new RemovePartCommand(0, 4, 0, theirs.EntityId),
            owner));
        Assert.Equal(CommandStatus.RuleRejected, refused.Status);
        Assert.Equal(ConstructionError.NotOwnedByPlayer, refused.Error);
    }

    [Fact]
    public void APlayerThatLeavesTakesItsPartsAndLeavesItsCells()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint owner = PlayHost.NextPlayerId();
        CommandOutcome placed = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, 1, 0, PartTypeId: 1, PositionX: -8f, PositionY: 6f, Angle: 0f, Scale: 1f),
            owner));
        Assert.True(placed.IsAccepted, $"place: {placed.Status}/{placed.Error}");
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, 2, 0), owner)).IsAccepted);

        // The socket closed: the player left the room and its part left the world with it.
        room.LeavePlayer(owner);

        CommandOutcome gone = room.Submit(PlayHost.BindPlayer(
            new RemovePartCommand(0, 3, 0, placed.EntityId),
            owner));
        Assert.Equal(ConstructionError.EntityNotFound, gone.Error);

        // The vacated build cell is free again for whoever connects next.
        CommandOutcome replanted = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, 1, 0, PartTypeId: 1, PositionX: -8f, PositionY: 6f, Angle: 0f, Scale: 1f),
            PlayHost.NextPlayerId()));
        Assert.True(replanted.IsAccepted, $"replant: {replanted.Status}/{replanted.Error}");
    }
}
