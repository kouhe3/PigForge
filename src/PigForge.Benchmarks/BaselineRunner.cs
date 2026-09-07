using System.Diagnostics;
using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Benchmarks;

public sealed record SceneReport(
    string Scene,
    int Ticks,
    int Bodies,
    int ConcurrentRooms,
    double TickP50Ms,
    double TickP95Ms,
    double TickP99Ms,
    double AllocP50Bytes,
    double AllocP95Bytes,
    double GcPauseMs,
    long WorkingSetBytes,
    long TotalCommittedBytes,
    int SnapshotBytesMax);

/// <summary>
/// Reproducible room-loop measurements. Timings are machine-local; the command and
/// metric set are the baseline contract. Allocation samples use
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>; pause delta uses
/// <see cref="GC.GetTotalPauseDuration"/>.
/// Sources: https://learn.microsoft.com/en-us/dotnet/api/system.gc.getallocatedbytesforcurrentthread
/// https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalpauseduration
/// https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.stopwatch.getelapsedtime
/// </summary>
public static class BaselineRunner
{
    private const uint TypicalTicks = 240;
    private const uint StressTicks = 120;
    private const int ConcurrentRoomCount = 8;
    private const uint ConcurrentTicks = 60;
    private static readonly PhysicsVector3 Gravity = new(0f, -9.81f, 0f);

    public static SceneReport RunTypical() => Measure("typical", CreateTypicalRoom, TypicalTicks, concurrentRooms: 1);

    public static SceneReport RunStress() => Measure("stress", CreateStressRoom, StressTicks, concurrentRooms: 1);

    public static SceneReport RunConcurrent() => Measure("concurrent-8", CreateTypicalRoom, ConcurrentTicks, ConcurrentRoomCount);

    private static SceneReport Measure(string scene, Func<GameRoom> factory, uint ticks, int concurrentRooms)
    {
        using GameRoom warmup = factory();
        warmup.RunTicks(Math.Min(ticks, 16));

        GameRoom[] rooms = new GameRoom[concurrentRooms];
        for (int index = 0; index < concurrentRooms; index++)
        {
            rooms[index] = factory();
        }

        try
        {
            int bodies = rooms[0].BodyCount;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            TimeSpan pauseBefore = GC.GetTotalPauseDuration();
            double[] tickMs = new double[ticks];
            double[] allocBytes = new double[ticks];
            int snapshotMax = 0;
            byte[] snapshotBuffer = new byte[SnapshotFrame.GetMaxByteCount(256)];

            for (uint tick = 0; tick < ticks; tick++)
            {
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int roomIndex = 0; roomIndex < rooms.Length; roomIndex++)
                {
                    rooms[roomIndex].Tick();
                    if (rooms[roomIndex].TryPublishSnapshot(snapshotBuffer, out int written))
                    {
                        snapshotMax = Math.Max(snapshotMax, written);
                    }
                }

                tickMs[tick] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                allocBytes[tick] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            }

            TimeSpan pauseAfter = GC.GetTotalPauseDuration();
            Array.Sort(tickMs);
            Array.Sort(allocBytes);
            GCMemoryInfo memory = GC.GetGCMemoryInfo();
            return new SceneReport(
                scene,
                (int)ticks,
                bodies,
                concurrentRooms,
                TickPercentiles.At(tickMs, 50),
                TickPercentiles.At(tickMs, 95),
                TickPercentiles.At(tickMs, 99),
                TickPercentiles.At(allocBytes, 50),
                TickPercentiles.At(allocBytes, 95),
                (pauseAfter - pauseBefore).TotalMilliseconds,
                Process.GetCurrentProcess().WorkingSet64,
                memory.TotalCommittedBytes,
                snapshotMax);
        }
        finally
        {
            for (int index = 0; index < rooms.Length; index++)
            {
                rooms[index].Dispose();
            }
        }
    }

    private static GameRoom CreateTypicalRoom()
    {
        GameRoom room = CreateRoom();
        room.Spawn(new RoomSpawnSpec(5, new PhysicsVector3(0f, -0.5f, 0f)));
        room.Spawn(new RoomSpawnSpec(2, new PhysicsVector3(7.5f, 1f, 0f), RoomActorRole.Pig));
        room.Spawn(new RoomSpawnSpec(3, new PhysicsVector3(8.9f, 1f, 0f), RoomActorRole.Tnt));
        room.Spawn(new RoomSpawnSpec(1, new PhysicsVector3(8.9f, 8f, 0f)));
        room.Start();
        return room;
    }

    private static GameRoom CreateStressRoom()
    {
        GameRoom room = CreateRoom();
        room.Spawn(new RoomSpawnSpec(5, new PhysicsVector3(0f, -0.5f, 0f)));
        room.Spawn(new RoomSpawnSpec(2, new PhysicsVector3(2f, 1f, 0f), RoomActorRole.Pig));
        for (int column = 0; column < 8; column++)
        {
            for (int row = 0; row < 8; row++)
            {
                room.Spawn(new RoomSpawnSpec(1, new PhysicsVector3(column * 1.2f, 6f + (row * 1.2f), 0f)));
            }
        }

        room.Start();
        return room;
    }

    private static GameRoom CreateRoom()
    {
        PartContentLibrary content = new(PartContentParser.Parse(PartContentJson));
        return new GameRoom(GameRoomOptions.Create(
            content,
            () => new BepuPhysicsWorld(Gravity),
            GameplayConfig.Default));
    }

    private const string PartContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "baseline-v1",
        "parts": [
            { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 2, "name": "pig", "mode": "dynamic", "mass": 1, "material": { "restitution": 0.2, "friction": 0.4 }, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 3, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 5, "name": "ground", "mode": "static", "mass": 0, "material": { "restitution": 0, "friction": 0.8 }, "shapes": [ { "kind": "box", "halfExtents": [40, 0.5, 10] } ] }
        ]
    }
    """;
}
