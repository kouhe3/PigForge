using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;

// Identity helpers stay internal; the server test project verifies them directly.
[assembly: InternalsVisibleTo("PigForge.Server.Tests")]

namespace PigForge.Server;

/// <summary>
/// Local play host. Every accepted /play socket owns a process-unique player id and the
/// server overrides each PGFC command's wire playerId before submitting it. --play hosts
/// the persistent sandbox room (Running from setup, objectives disabled); --demo-ws stays
/// the snapshot-only demo. Clients send PGFC and receive PGFA + PGFS.
///
/// A player's life is its socket: the connection closes, the player leaves the room and its
/// parts leave the world with it (<see cref="GameRoom.LeavePlayer"/>), so nothing -- a closed
/// tab, a reload, a dropped socket -- can strand a contraption nobody owns. A reconnect is a
/// new player with an empty building plane. The wire playerId is always overridden from the
/// connection, so a client cannot claim an identity.
/// </summary>
public static class PlayHost
{
    public const string Prefix = "http://127.0.0.1:5088/";

    private static int _nextPlayerId;

    private sealed class PlayClient
    {
        public required WebSocket Socket { get; init; }

        public required uint PlayerId { get; init; }

        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    /// <summary>
    /// Runs the play host. <paramref name="levelFile"/> names a level under <c>content/levels</c>
    /// (an original level converted by <c>tools/bple-levels/build-levels.mjs</c> lives under
    /// <c>original/</c>); without it the sandbox room keeps its own <c>terrain-v1.json</c>.
    /// </summary>
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default, string? levelFile = null)
    {
        using GameRoom room = CreateRoom(levelFile ?? "terrain-v1.json", sandboxMode: levelFile is null, out string levelJson);
        using HttpListener listener = new();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        Console.WriteLine($"PigForge play host on {Prefix}play");
        Console.WriteLine($"PigForge level on {Prefix}level");

        ConcurrentDictionary<Guid, PlayClient> clients = new();
        SnapshotBroadcaster broadcaster = new();
        Task accept = AcceptAsync(listener, clients, room, levelJson, broadcaster, cancellationToken);
        Task ticks = TickAsync(room, clients, broadcaster, cancellationToken);
        await Task.WhenAny(accept, ticks);
        listener.Stop();
        return 0;
    }

    public static GameRoom CreateSlopeRoom() => CreateLevelRoom("slope-v1.json");

    public static GameRoom CreateTerrainRoom() => CreateLevelRoom("terrain-v1.json");

    public static GameRoom CreateLevelRoom(string levelFile) => CreateRoom(levelFile, sandboxMode: false, out _);

    /// <summary>
    /// Persistent sandbox room: the level world is materialized during setup, objectives
    /// are disabled, and the room runs at 60 Hz without ever returning to Building.
    /// </summary>
    public static GameRoom CreateSandboxRoom() => CreateRoom("terrain-v1.json", sandboxMode: true, out _);

    /// <summary>Allocates the next process-unique player id (starts at 1, never reused).</summary>
    internal static uint NextPlayerId() => (uint)Interlocked.Increment(ref _nextPlayerId);

    /// <summary>Overrides the wire player id with the connection's server-assigned id.</summary>
    internal static ReplayCommand BindPlayer(ReplayCommand command, uint playerId) =>
        command with { PlayerId = playerId };

    private static GameRoom CreateRoom(string levelFile, bool sandboxMode, out string levelJson)
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));
        levelJson = File.ReadAllText(Path.Combine(root, "content", "levels", levelFile));
        LevelContentDocument level = LevelContentLibrary.Parse(levelJson);
        // No tick limit: a goal-based level room ends when the pig reaches the goal zone or leaves the
        // level's own camera rectangle, exactly like the original (`GameMode.NotifyGoalReached`,
        // `GameMode.OnPigOutOfBounds` -- `Pig.cs:396-403` sends `PigOutOfBounds` when the pig drops out
        // of the camera limits, and `GameMode.cs:384-387` answers by returning to the building state,
        // which is this room's `Retry`). The original has no run timer at all: `MaxTicks: 1200` used to
        // fail every official level 20 s after Start. A hand-made v1 level carries no camera limits and
        // keeps the coarse map box as its pig bound.
        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 0,
            ObjectivesEnabled: !sandboxMode,
            CameraLimits: level.CameraLimits);
        GameRoom room = new(GameRoomOptions.Create(
            parts,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config,
            sandboxMode: sandboxMode));
        room.SetupFromLevel(level);
        return room;
    }

    private static async Task AcceptAsync(
        HttpListener listener,
        ConcurrentDictionary<Guid, PlayClient> clients,
        GameRoom room,
        string levelJson,
        SnapshotBroadcaster broadcaster,
        CancellationToken cancellationToken)
    {
        byte[] levelPayload = Encoding.UTF8.GetBytes(levelJson);
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            if (context.Request.Url?.AbsolutePath == "/level")
            {
                // The client cannot infer level geometry from snapshots -- a terrain mesh is not a
                // part, and the goal zone and bounds are level data. It fetches this document
                // instead, which is the exact JSON the server parsed. CORS keeps the dev client on
                // its own origin (vite serves :5173, this host owns :5088).
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                context.Response.ContentLength64 = levelPayload.Length;
                await context.Response.OutputStream.WriteAsync(levelPayload, cancellationToken);
                context.Response.Close();
                continue;
            }

            if (!context.Request.IsWebSocketRequest || context.Request.Url?.AbsolutePath != "/play")
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            HttpListenerWebSocketContext socketContext = await context.AcceptWebSocketAsync(subProtocol: null);
            Guid id = Guid.NewGuid();
            uint playerId = NextPlayerId();
            PlayClient client = new() { Socket = socketContext.WebSocket, PlayerId = playerId };
            clients[id] = client;
            Console.WriteLine($"play client -> playerId {playerId}");
            _ = ReceiveAsync(id, client, room, clients, broadcaster, cancellationToken);
        }
    }

    private static async Task ReceiveAsync(
        Guid id,
        PlayClient client,
        GameRoom room,
        ConcurrentDictionary<Guid, PlayClient> clients,
        SnapshotBroadcaster broadcaster,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256];
        byte[] ack = new byte[CommandFrame.AckByteCount];
        try
        {
            while (client.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await client.Socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }

                lock (room)
                {
                    if (!CommandFrame.TryDecode(buffer.AsSpan(0, result.Count), out ReplayCommand? command, out _)
                        || command is null)
                    {
                        CommandFrame.TryEncodeAck(ack, 0, (byte)CommandStatus.UnknownKind, 0, 0);
                    }
                    else
                    {
                        ReplayCommand bound = BindPlayer(command, client.PlayerId);
                        CommandOutcome outcome = room.Submit(bound);
                        CommandFrame.TryEncodeAck(ack, command.Sequence, (byte)outcome.Status, (byte)outcome.Error, outcome.EntityId);
                    }
                }

                await SendBinaryAsync(client, ack, cancellationToken);
                await broadcaster.BroadcastAsync(room, clients, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException exception)
        {
            Console.Error.WriteLine($"Play socket receive failed: {exception.Message}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Play command handling failed: {exception}");
        }
        finally
        {
            clients.TryRemove(id, out _);
            // The socket is this player's whole life: when it goes, the player leaves the room
            // and its parts go with it. A reconnect is a new player with an empty plane.
            lock (room)
            {
                room.LeavePlayer(client.PlayerId);
            }
        }
    }

    private static async Task TickAsync(
        GameRoom room,
        ConcurrentDictionary<Guid, PlayClient> clients,
        SnapshotBroadcaster broadcaster,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1.0 / 60.0));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                lock (room)
                {
                    if (room.Mode == RoomMode.Running && room.Phase == GameplayPhase.Playing)
                    {
                        room.Tick();
                    }
                }

                await broadcaster.BroadcastAsync(room, clients, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Play tick failed: {exception}");
            }
        }
    }

    /// <summary>
    /// Reuses one snapshot buffer per host, growing it when the room's entity count rises:
    /// sandbox mixed frames include every player's previews, not just bound bodies, so the
    /// legacy fixed 256-entity capacity would silently drop frames.
    /// </summary>
    private sealed class SnapshotBroadcaster
    {
        private byte[] _buffer = new byte[SnapshotFrame.GetMaxByteCount(256)];

        public async Task BroadcastAsync(
            GameRoom room,
            ConcurrentDictionary<Guid, PlayClient> clients,
            CancellationToken cancellationToken)
        {
            int written;
            lock (room)
            {
                int required = SnapshotFrame.GetMaxByteCount(room.MaxSnapshotEntityCount);
                if (_buffer.Length < required)
                {
                    _buffer = new byte[required];
                }

                if (!room.TryPublishSnapshot(_buffer, out written))
                {
                    return;
                }
            }

            byte[] frame = _buffer.AsSpan(0, written).ToArray();
            foreach (KeyValuePair<Guid, PlayClient> pair in clients)
            {
                if (pair.Value.Socket.State != WebSocketState.Open)
                {
                    clients.TryRemove(pair.Key, out _);
                    continue;
                }

                try
                {
                    await SendBinaryAsync(pair.Value, frame, cancellationToken);
                }
                catch (WebSocketException)
                {
                    clients.TryRemove(pair.Key, out _);
                }
            }
        }
    }

    private static async Task SendBinaryAsync(PlayClient client, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await client.SendLock.WaitAsync(cancellationToken);
        try
        {
            if (client.Socket.State == WebSocketState.Open)
            {
                await client.Socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
            }
        }
        finally
        {
            client.SendLock.Release();
        }
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
            throw new InvalidOperationException("Could not locate content/parts.json.");
        }

        return directory.FullName;
    }
}
