using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
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
/// A socket may name a session with <c>/play?session=&lt;id&gt;</c>: the first connection with an
/// id is a new player, and a later connection with the same id resumes that player's id and
/// ownership, so the client's 连接房间 button and a dropped socket no longer orphan the parts it
/// placed (see <see cref="PlaySessions"/>). The query string is transport only -- the PGFC/PGFS
/// frames are unchanged, and the wire playerId is still overridden from the connection.
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

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using GameRoom room = CreateSandboxRoom();
        using HttpListener listener = new();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        Console.WriteLine($"PigForge play host on {Prefix}play");

        ConcurrentDictionary<Guid, PlayClient> clients = new();
        SnapshotBroadcaster broadcaster = new();
        PlaySessions sessions = new(NextPlayerId);
        Task accept = AcceptAsync(listener, clients, room, broadcaster, sessions, cancellationToken);
        Task ticks = TickAsync(room, clients, broadcaster, cancellationToken);
        await Task.WhenAny(accept, ticks);
        listener.Stop();
        return 0;
    }

    public static GameRoom CreateSlopeRoom() => CreateLevelRoom("slope-v1.json");

    public static GameRoom CreateTerrainRoom() => CreateLevelRoom("terrain-v1.json");

    public static GameRoom CreateLevelRoom(string levelFile) => CreateRoom(levelFile, sandboxMode: false);

    /// <summary>
    /// Persistent sandbox room: the level world is materialized during setup, objectives
    /// are disabled, and the room runs at 60 Hz without ever returning to Building.
    /// </summary>
    public static GameRoom CreateSandboxRoom() => CreateRoom("terrain-v1.json", sandboxMode: true);

    /// <summary>Allocates the next process-unique player id (starts at 1, never reused).</summary>
    internal static uint NextPlayerId() => (uint)Interlocked.Increment(ref _nextPlayerId);

    /// <summary>Overrides the wire player id with the connection's server-assigned id.</summary>
    internal static ReplayCommand BindPlayer(ReplayCommand command, uint playerId) =>
        command with { PlayerId = playerId };

    private static GameRoom CreateRoom(string levelFile, bool sandboxMode)
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));
        LevelContentDocument level = LevelContentLibrary.Parse(File.ReadAllText(Path.Combine(root, "content", "levels", levelFile)));
        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 1200,
            ObjectivesEnabled: !sandboxMode);
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
        SnapshotBroadcaster broadcaster,
        PlaySessions sessions,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            if (!context.Request.IsWebSocketRequest || context.Request.Url?.AbsolutePath != "/play")
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            HttpListenerWebSocketContext socketContext = await context.AcceptWebSocketAsync(subProtocol: null);
            Guid id = Guid.NewGuid();
            string sessionId = context.Request.QueryString["session"] ?? string.Empty;
            uint playerId = sessions.Resolve(sessionId, out bool resumed);
            PlayClient client = new() { Socket = socketContext.WebSocket, PlayerId = playerId };
            clients[id] = client;
            Console.WriteLine($"play client -> playerId {playerId}{(resumed ? " (session resumed)" : string.Empty)}");
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
