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
/// Headless demo: one authoritative Bepu room ticked at 60 Hz, broadcasting
/// binary snapshot frames. Clients may only receive; they cannot push poses.
/// </summary>
public static class DemoSnapshotHost
{
    public const string Prefix = "http://127.0.0.1:5088/";

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using GameRoom room = CreateTypicalRoom();
        using HttpListener listener = new();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        Console.WriteLine($"PigForge snapshot demo on {Prefix}snapshots");

        ConcurrentDictionary<Guid, WebSocket> clients = new();
        Task accept = AcceptAsync(listener, clients, cancellationToken);
        Task ticks = TickAsync(room, clients, cancellationToken);
        await Task.WhenAny(accept, ticks);
        listener.Stop();
        return 0;
    }

    private static async Task AcceptAsync(
        HttpListener listener,
        ConcurrentDictionary<Guid, WebSocket> clients,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            if (context.Request.IsWebSocketRequest && context.Request.Url?.AbsolutePath == "/snapshots")
            {
                HttpListenerWebSocketContext socketContext = await context.AcceptWebSocketAsync(subProtocol: null);
                clients.TryAdd(Guid.NewGuid(), socketContext.WebSocket);
            }
            else
            {
                byte[] body = "PigForge snapshot demo"u8.ToArray();
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, cancellationToken);
                context.Response.Close();
            }
        }
    }

    private static async Task TickAsync(
        GameRoom room,
        ConcurrentDictionary<Guid, WebSocket> clients,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(256)];
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1.0 / 60.0));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            room.Tick();
            if (!room.TryPublishSnapshot(buffer, out int written))
            {
                continue;
            }

            ArraySegment<byte> frame = new(buffer, 0, written);
            foreach (KeyValuePair<Guid, WebSocket> pair in clients)
            {
                if (pair.Value.State != WebSocketState.Open)
                {
                    clients.TryRemove(pair.Key, out _);
                    continue;
                }

                try
                {
                    await pair.Value.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
                }
                catch (WebSocketException)
                {
                    clients.TryRemove(pair.Key, out _);
                }
            }
        }
    }

    private static GameRoom CreateTypicalRoom()
    {
        PartContentLibrary content = new(PartContentParser.Parse(PartContentJson));
        GameRoom room = new(GameRoomOptions.Create(
            content,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            GameplayConfig.Default));
        room.Spawn(new RoomSpawnSpec(5, new PhysicsVector3(0f, -0.5f, 0f)));
        room.Spawn(new RoomSpawnSpec(2, new PhysicsVector3(7.5f, 1f, 0f), RoomActorRole.Pig));
        room.Spawn(new RoomSpawnSpec(3, new PhysicsVector3(8.9f, 1f, 0f), RoomActorRole.Tnt));
        room.Spawn(new RoomSpawnSpec(1, new PhysicsVector3(8.9f, 8f, 0f)));
        room.Start();
        return room;
    }

    private const string PartContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "demo-v1",
        "parts": [
            { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 2, "name": "pig", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 3, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 5, "name": "ground", "mode": "static", "mass": 0, "shapes": [ { "kind": "box", "halfExtents": [40, 0.5, 10] } ] }
        ]
    }
    """;
}
