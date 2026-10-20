using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using PigForge.Core;
using PigForge.Core.Construction;
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
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default, string? levelFile = null, bool dev = false)
    {
        using HttpListener listener = new();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        Console.WriteLine($"PigForge play host on {Prefix}play");
        Console.WriteLine($"PigForge level on {Prefix}level");
        if (dev)
        {
            Console.WriteLine("PigForge dev mode: every connection gets its own room (?scenario=<id> / ?level=<path>)");
            return await RunDevAsync(listener, cancellationToken);
        }

        using GameRoom room = CreateRoom(levelFile ?? "terrain-v1.json", sandboxMode: levelFile is null, out string levelJson);
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
            CameraLimits: level.CameraLimits,
            ChallengeTriggers: LevelChallengeTriggers.Build(level));
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

            HttpListenerWebSocketContext socketContext;
            try
            {
                socketContext = await context.AcceptWebSocketAsync(subProtocol: null);
            }
            catch
            {
                room.Dispose();
                throw;
            }

            Guid id = Guid.NewGuid();
            uint playerId = NextPlayerId();
            PlayClient client = new() { Socket = socketContext.WebSocket, PlayerId = playerId };
            clients[id] = client;
            Console.WriteLine($"play client -> playerId {playerId}");
            _ = ReceiveAsync(id, client, room, clients, broadcaster, cancellationToken);
        }
    }

    // ------------------------------------------------------------------ dev mode
    // `--play --dev` gives every connection its own world (`docs/specs/dev-harness.md`). The world
    // is the websocket query (`?scenario=<id>` or `?level=<path>`), so one browser tab is one room
    // and a reload is the only "switch"; nothing is shared with another tester.

    private sealed class DevSession
    {
        public required Guid Id { get; init; }

        public required GameRoom Room { get; init; }

        public required PlayClient Client { get; init; }

        public required SnapshotBroadcaster Broadcaster { get; init; }

        public required ConcurrentDictionary<Guid, PlayClient> Clients { get; init; }

        public CancellationTokenSource Cancel { get; } = new();
    }

    private static async Task<int> RunDevAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        string root = FindRepositoryRoot();
        ConcurrentDictionary<Guid, DevSession> sessions = new();
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            string? path = context.Request.Url?.AbsolutePath;
            if (path == "/level")
            {
                await ServeDevLevelAsync(context, root, cancellationToken);
                continue;
            }

            if (path == "/dev/worlds")
            {
                await WriteJsonAsync(context.Response, DevWorlds.ListJson(root), cancellationToken);
                continue;
            }

            if (!context.Request.IsWebSocketRequest || path != "/play")
            {
                Reject(context.Response, "Expected a websocket upgrade on /play.");
                continue;
            }

            DevWorld world;
            GameRoom room;
            try
            {
                world = ResolveWorld(context.Request, root);
                room = CreateDevRoom(world);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or NotSupportedException)
            {
                Reject(context.Response, exception.Message);
                continue;
            }

            HttpListenerWebSocketContext socketContext = await context.AcceptWebSocketAsync(subProtocol: null);
            Guid id = Guid.NewGuid();
            uint playerId = NextPlayerId();
            PlayClient client = new() { Socket = socketContext.WebSocket, PlayerId = playerId };
            DevSession session = new()
            {
                Id = id,
                Room = room,
                Client = client,
                Broadcaster = new SnapshotBroadcaster(),
                Clients = new ConcurrentDictionary<Guid, PlayClient>(),
            };
            session.Clients[id] = client;
            sessions[id] = session;
            Console.WriteLine(
                $"dev session {id} player {playerId}: {world.ScenarioId ?? (world.Sandbox ? "sandbox" : world.LevelFile)}");
            _ = ReceiveDevAsync(session, sessions, cancellationToken);
            _ = TickDevAsync(session, cancellationToken);
        }

        return 0;
    }

    private static DevWorld ResolveWorld(HttpListenerRequest request, string root)
    {
        foreach (string? key in request.QueryString.AllKeys)
        {
            if (key is not null && key != "scenario" && key != "level")
            {
                throw new InvalidDataException($"unknown world query '{key}'");
            }
        }

        return DevWorlds.Resolve(root, request.QueryString["scenario"], request.QueryString["level"]);
    }

    /// <summary>Builds a dev room; a scenario's own parts are placed before any client connects.</summary>
    private static GameRoom CreateDevRoom(DevWorld world)
    {
        GameRoom room = CreateRoom(world.LevelFile, world.Sandbox, out _);
        try
        {
            if (world.Scenario is ScenarioDocument scenario)
            {
                for (int index = 0; index < scenario.Build.Count; index++)
                {
                    ScenarioPart part = scenario.Build[index];
                    ConstructionResult placed = room.SeedPart(part.PartTypeId, part.X, part.Y, part.Angle, part.Scale, part.Mirrored);
                    if (!placed.IsSuccess)
                    {
                        throw new InvalidDataException(
                            $"scenario {scenario.Id}: build[{index}] (part {part.PartTypeId}) was rejected: {placed.Error}.");
                    }
                }

                if (scenario.Start)
                {
                    room.Start();
                }
            }

            return room;
        }
        catch
        {
            room.Dispose();
            throw;
        }
    }

    private static async Task ServeDevLevelAsync(HttpListenerContext context, string root, CancellationToken cancellationToken)
    {
        DevWorld world;
        try
        {
            world = ResolveWorld(context.Request, root);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            Reject(context.Response, exception.Message);
            return;
        }

        await WriteJsonAsync(context.Response, Encoding.UTF8.GetBytes(world.LevelJson), cancellationToken);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, byte[] payload, CancellationToken cancellationToken)
    {
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload, cancellationToken);
        response.Close();
    }

    private static void Reject(HttpListenerResponse response, string message)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message);
        response.StatusCode = 400;
        response.ContentType = "text/plain; charset=utf-8";
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.ContentLength64 = payload.Length;
        response.OutputStream.Write(payload);
        response.Close();
    }

    private static async Task ReceiveDevAsync(
        DevSession session,
        ConcurrentDictionary<Guid, DevSession> sessions,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Cancel.Token);
        CancellationToken token = linked.Token;
        byte[] buffer = new byte[256];
        byte[] ack = new byte[CommandFrame.AckByteCount];
        try
        {
            while (session.Client.Socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await session.Client.Socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }

                lock (session.Room)
                {
                    if (!CommandFrame.TryDecode(buffer.AsSpan(0, result.Count), out ReplayCommand? command, out _)
                        || command is null)
                    {
                        CommandFrame.TryEncodeAck(ack, 0, (byte)CommandStatus.UnknownKind, 0, 0);
                    }
                    else
                    {
                        ReplayCommand bound = BindPlayer(command, session.Client.PlayerId);
                        CommandOutcome outcome = session.Room.Submit(bound);
                        CommandFrame.TryEncodeAck(ack, command.Sequence, (byte)outcome.Status, (byte)outcome.Error, outcome.EntityId);
                    }
                }

                await SendBinaryAsync(session.Client, ack, token);
                await session.Broadcaster.BroadcastAsync(session.Room, session.Clients, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException exception)
        {
            Console.Error.WriteLine($"dev socket failed: {exception.Message}");
        }
        catch (ObjectDisposedException)
        {
            // The tick loop disposed the room as the socket went away.
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"dev command failed: {exception}");
        }
        finally
        {
            session.Cancel.Cancel();
            sessions.TryRemove(session.Id, out _);
        }
    }

    private static async Task TickDevAsync(DevSession session, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Cancel.Token);
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1.0 / 60.0));
        try
        {
            while (await timer.WaitForNextTickAsync(linked.Token))
            {
                try
                {
                    lock (session.Room)
                    {
                        if (session.Room.Mode == RoomMode.Running && session.Room.Phase == GameplayPhase.Playing)
                        {
                            session.Room.Tick();
                        }
                    }

                    await session.Broadcaster.BroadcastAsync(session.Room, session.Clients, linked.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"dev tick failed: {exception}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (session.Room)
            {
                session.Room.Dispose();
            }
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
        /// <summary>The most challenge markers one frame can carry: a level's whole prop list, which is
        /// the bound the collected section is sized with (`docs/specs/level-challenges.md` §5).</summary>
        private const int MaxCollected = 1024;

        private byte[] _buffer = new byte[SnapshotFrame.GetMaxByteCount(256, MaxCollected)];

        public async Task BroadcastAsync(
            GameRoom room,
            ConcurrentDictionary<Guid, PlayClient> clients,
            CancellationToken cancellationToken)
        {
            int written;
            lock (room)
            {
                int required = SnapshotFrame.GetMaxByteCount(room.MaxSnapshotEntityCount, MaxCollected);
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
