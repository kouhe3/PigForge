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
        Assert.Equal(CommandStatus.Accepted, room.Submit(new PlacePartCommand(0, 1, 1, 21, -5f, 5f, 0f, 1f)).Status);
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

    [Fact]
    public void RetryReturnsToBuildLayoutAndAllowsRestart()
    {
        using GameRoom room = PlayHost.CreateSlopeRoom();
        CommandOutcome pig = room.Submit(new PlacePartCommand(0, 1, 1, 4, -4.8f, 5.2f, 0f, 1f));
        Assert.Equal(CommandStatus.Accepted, pig.Status);
        Assert.Equal(CommandStatus.Accepted, room.Submit(new StartSimulationCommand(0, 2, 1)).Status);
        Assert.Equal(RoomMode.Running, room.Mode);

        for (int i = 0; i < 60; i++)
        {
            room.Tick();
        }

        // Retry returns to the pre-play build state; the player's pig is restored and
        // the room becomes buildable again (single-vehicle loop, not Keep/Clear).
        // A client whose last snapshot lags the authoritative tick by one frame must
        // still be able to retry (the sequence gate already orders the command).
        CommandOutcome retry = room.Submit(new RetryCommand(room.CurrentTick - 1, 3, 1));
        Assert.Equal(CommandStatus.Accepted, retry.Status);
        Assert.Equal(RoomMode.Building, room.Mode);
        Assert.Equal(GameplayPhase.Playing, room.Phase);

        // The pig entity survives the retry and is still placed on the ramp.
        Assert.Equal(CommandStatus.Accepted, room.Submit(new PlacePartCommand(0, 4, 1, 1, -3.0f, 5.0f, 0f, 1f)).Status);
        Assert.Equal(CommandStatus.Accepted, room.Submit(new StartSimulationCommand(0, 5, 1)).Status);
        Assert.Equal(RoomMode.Running, room.Mode);
    }

    [Fact]
    public void RetryAfterRuntimeDestroyedEntityDoesNotThrowStale()
    {
        using GameRoom room = PlayHost.CreateSlopeRoom();
        // TNT placed free, with a block above it: the falling block ignites the TNT,
        // the blast destroys the TNT entity during the run (a stale construction
        // entry is left behind). Retry must tolerate that without re-destroying it.
        Assert.Equal(CommandStatus.Accepted, room.Submit(new PlacePartCommand(0, 1, 1, 9, 0f, 3f, 0f, 1f)).Status); // tnt
        Assert.Equal(CommandStatus.Accepted, room.Submit(new PlacePartCommand(0, 2, 1, 1, 0f, 4.2f, 0f, 1f)).Status); // block above
        Assert.Equal(CommandStatus.Accepted, room.Submit(new StartSimulationCommand(0, 3, 1)).Status);
        for (int i = 0; i < 300; i++)
        {
            room.Tick();
        }

        CommandOutcome retry = room.Submit(new RetryCommand(room.CurrentTick, 4, 1));
        Assert.Equal(CommandStatus.Accepted, retry.Status);
        Assert.Equal(RoomMode.Building, room.Mode);
    }

    [Fact]
    public void PlacedPartAppearsInTerrainBuildingSnapshot()
    {
        using GameRoom room = PlayHost.CreateTerrainRoom();
        byte[] before = new byte[SnapshotFrame.GetMaxByteCount(16)];
        Assert.True(room.TryPublishSnapshot(before, out int beforeWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(before.AsSpan(0, beforeWritten), out SnapshotFrameHeader beforeHeader, out _));
        int beforeCount = (int)beforeHeader.EntityCount;

        Assert.Equal(CommandStatus.Accepted, room.Submit(new PlacePartCommand(0, 1, 1, 1, 5f, 5f, 0f, 1f)).Status);

        byte[] after = new byte[SnapshotFrame.GetMaxByteCount(beforeCount + 8)];
        Assert.True(room.TryPublishSnapshot(after, out int afterWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(after.AsSpan(0, afterWritten), out SnapshotFrameHeader afterHeader, out SnapshotFrameReader reader));
        Assert.Equal(beforeCount + 1, (int)afterHeader.EntityCount);

        bool sawPlaced = false;
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            if (entity.PartTypeId == 1u && entity.Position.X == 5f && entity.Position.Y == 5f)
            {
                sawPlaced = true;
                break;
            }
        }

        Assert.True(sawPlaced, "The placed wooden block must appear in the building snapshot.");
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
