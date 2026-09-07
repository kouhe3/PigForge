using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;
using Xunit.Sdk;

namespace PigForge.Server.Tests;

public sealed class SlopePlayTests
{
    [Fact]
    public void BuildingSnapshotIncludesAngledRampWithoutStart()
    {
        using GameRoom room = PlayHost.CreateSlopeRoom();
        Assert.Equal(RoomMode.Building, room.Mode);
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(8)];
        Assert.True(room.TryPublishSnapshot(buffer, out int written));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, written), out SnapshotFrameHeader header, out SnapshotFrameReader reader));
        Assert.Equal(SnapshotFrame.BuildingPhase, header.Phase);
        Assert.Equal(0u, header.Tick);
        Assert.True(header.EntityCount >= 1);
        Assert.True(reader.TryReadEntity(out SnapshotEntity entity));
        Assert.Equal(0u, entity.PhysicsBodyId);
        Assert.Equal(6u, entity.PartTypeId);
        Assert.NotEqual(0f, entity.Rotation.Z);
    }

    [Fact]
    public void PlaceThenStartWrongModeRejectsSecondPlace()
    {
        using GameRoom room = PlayHost.CreateSlopeRoom();
        CommandOutcome placed = room.Submit(new PlacePartCommand(0, 1, 1, 4, -5f, 5f, 0f, 1f));
        Assert.Equal(CommandStatus.Accepted, placed.Status);
        Assert.NotEqual(0u, placed.EntityId);
        Assert.Equal(CommandStatus.Accepted, room.Submit(new StartSimulationCommand(0, 2, 1)).Status);
        Assert.Equal(RoomMode.Running, room.Mode);
        Assert.Equal(CommandStatus.WrongMode, room.Submit(new PlacePartCommand(0, 3, 1, 1, 0f, 0f, 0f, 1f)).Status);
    }

    [Fact]
    public void PlacingSphereThenStartDoesNotThrow()
    {
        using GameRoom room = PlayHost.CreateSlopeRoom();
        Assert.Equal(CommandStatus.Accepted, room.Submit(new PlacePartCommand(0, 1, 1, 3, -5f, 5f, 0f, 1f)).Status);
        Assert.Equal(CommandStatus.Accepted, room.Submit(new StartSimulationCommand(0, 2, 1)).Status);
        Assert.Equal(RoomMode.Running, room.Mode);
    }


    [Fact]
    public void ScriptedPigReachesGoalZone()
    {
        long first = RunUntilTerminal();
        long second = RunUntilTerminal();
        Assert.Equal(first, second);
    }

    [Fact]
    public void OutOfBoundsPigFails()
    {
        using GameRoom room = PlayHost.CreateSlopeRoom();
        Assert.Equal(CommandStatus.Accepted, room.Submit(new PlacePartCommand(0, 1, 1, 4, 0f, 30f, 0f, 1f)).Status);
        Assert.Equal(CommandStatus.Accepted, room.Submit(new StartSimulationCommand(0, 2, 1)).Status);
        for (int i = 0; i < 240; i++)
        {
            room.Tick();
            if (room.Phase == GameplayPhase.Failed)
            {
                return;
            }
        }

        throw new XunitException($"Expected Failed, was {room.Phase}.");
    }

    private static long RunUntilTerminal()
    {
        using GameRoom room = PlayHost.CreateSlopeRoom();
        Assert.Equal(CommandStatus.Accepted, room.Submit(new PlacePartCommand(0, 1, 1, 4, -4.8f, 5.2f, 0f, 1f)).Status);
        Assert.Equal(CommandStatus.Accepted, room.Submit(new StartSimulationCommand(0, 2, 1)).Status);
        for (int i = 0; i < 1200; i++)
        {
            room.Tick();
            if (room.Phase is GameplayPhase.Won or GameplayPhase.Failed)
            {
                break;
            }
        }

        if (room.Phase != GameplayPhase.Won)
        {
            throw new XunitException($"Expected Won, was {room.Phase} at tick {room.CurrentTick}.");
        }

        return room.ComputeStateHash();
    }
}
