using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;

namespace PigForge.Server;

/// <summary>
/// Local play host: one Bepu room stays in Building until StartSimulation, then ticks
/// at 60 Hz. Clients send PGFC and receive PGFA + PGFS. No keep / EnterBuildMode.
/// </summary>
public static class PlayHost
{
    public const string Prefix = "http://127.0.0.1:5088/";
    public const uint PlayerId = 1;

    private sealed class PlayClient
    {
        public required WebSocket Socket { get; init; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using GameRoom room = CreateLevelRoom("terrain-v1.json");
        using HttpListener listener = new();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        Console.WriteLine($"PigForge play host on {Prefix}play");

        ConcurrentDictionary<Guid, PlayClient> clients = new();
        Task accept = AcceptAsync(listener, clients, room, cancellationToken);
        Task ticks = TickAsync(room, clients, cancellationToken);
        await Task.WhenAny(accept, ticks);
        listener.Stop();
        return 0;
    }

    public static GameRoom CreateSlopeRoom() => CreateLevelRoom("slope-v1.json");

    public static GameRoom CreateTerrainRoom() => CreateLevelRoom("terrain-v1.json");

    public static GameRoom CreateLevelRoom(string levelFile)
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
            MaxTicks: 1200);
        GameRoom room = new(GameRoomOptions.Create(
            parts,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config));
        room.SetupFromLevel(level);
        return room;
    }

    private static async Task AcceptAsync(
        HttpListener listener,
        ConcurrentDictionary<Guid, PlayClient> clients,
        GameRoom room,
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
            PlayClient client = new() { Socket = socketContext.WebSocket };
            clients[id] = client;
            _ = ReceiveAsync(id, client, room, clients, cancellationToken);
        }
    }

    private static async Task ReceiveAsync(
        Guid id,
        PlayClient client,
        GameRoom room,
        ConcurrentDictionary<Guid, PlayClient> clients,
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
                        || command is null
                        || command.PlayerId != PlayerId)
                    {
                        CommandFrame.TryEncodeAck(ack, 0, (byte)CommandStatus.UnknownKind, 0, 0);
                    }
                    else
                    {
                        CommandOutcome outcome = room.Submit(command);
                        CommandFrame.TryEncodeAck(ack, command.Sequence, (byte)outcome.Status, (byte)outcome.Error, outcome.EntityId);
                    }
                }

                await SendBinaryAsync(client, ack, cancellationToken);
                await BroadcastSnapshotAsync(room, clients, cancellationToken);
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

                await BroadcastSnapshotAsync(room, clients, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Play tick failed: {exception}");
            }
        }
    }

    private static async Task BroadcastSnapshotAsync(
        GameRoom room,
        ConcurrentDictionary<Guid, PlayClient> clients,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(256)];
        int written;
        lock (room)
        {
            if (!room.TryPublishSnapshot(buffer, out written))
            {
                return;
            }
        }

        byte[] frame = buffer.AsSpan(0, written).ToArray();
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
