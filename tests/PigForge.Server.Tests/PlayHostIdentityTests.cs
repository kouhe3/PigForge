using PigForge.Core;
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
}
