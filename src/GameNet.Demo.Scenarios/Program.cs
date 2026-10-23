using System.Diagnostics;
using GameNet.Core;
using GameNet.Transport;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
if (mode is "chat" or "all") await Chat.RunAsync();
if (mode is "noise" or "all") await Noise.RunAsync();
if (mode is "delta" or "all") await Delta.RunAsync();

internal static class Harness
{
    public static GameServer StartServer(out UdpServerTransport transport, bool autoSubscribe = true, int idleTimeoutMs = 10_000)
    {
        transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = autoSubscribe, AoiRadius = 0 });
        transport.IdleTimeoutMs = idleTimeoutMs;
        server.Start();
        return server;
    }

    public static async Task<bool> ConnectAsync(UdpClientTransport client, int port)
    {
        var ok = client.Connect("127.0.0.1", port);
        await Task.Yield();
        return ok;
    }

    public static void Pump(UdpClientTransport client, ClientWorld world)
    {
        client.Poll();
        while (client.TryDequeue(out var packet))
        {
            if (packet.Channel == 0 && SnapshotCodec.TryRead(packet.Payload, out var chunk))
                world.OnChunk(chunk);
        }
    }

    public static void Send(UdpClientTransport client, MessageType type, byte? collection = null)
    {
        var writer = new ByteWriter(4);
        writer.WriteByte((byte)type);
        if (collection.HasValue) writer.WriteByte(collection.Value);
        client.Send(1, writer.Written);
    }

    public static void TickLoop(GameServer server, UdpClientTransport client, ClientWorld world, int ticks, int sleepMs = 5)
    {
        for (var i = 0; i < ticks; i++)
        {
            server.Tick(1f / 30f);
            Pump(client, world);
            Thread.Sleep(sleepMs);
        }
    }
}

internal static class Chat
{
    public static async Task RunAsync()
    {
        Console.WriteLine("== chat: short subscription + unsubscribe + disconnect ==");
        var server = Harness.StartServer(out var transport, autoSubscribe: false, idleTimeoutMs: 1200);
        var chat = server.RegisterCollection(7, new EntityCollection());
        using var clientA = new UdpClientTransport();
        using var clientB = new UdpClientTransport();
        if (!await Harness.ConnectAsync(clientA, transport.Port) || !await Harness.ConnectAsync(clientB, transport.Port))
        {
            Console.WriteLine("connect failed");
            return;
        }

        var worldA = new ClientWorld();
        var worldB = new ClientWorld();
        Harness.Send(clientA, MessageType.Subscribe, 7);
        Harness.Send(clientB, MessageType.Subscribe, 7);

        var nextId = 1;
        var seenA = new HashSet<int>();
        var seenB = new HashSet<int>();
        var messagesAtUnsubscribe = 0;
        string? phase = "before";
        var sentAfterUnsubscribe = 0;

        for (var step = 0; step < 120; step++)
        {
            if (step % 4 == 0)
            {
                var id = nextId++;
                chat.Spawn(EntityState.Create(id, EntityKind.Prop, id, step, team: 9));
                if (phase == "after") sentAfterUnsubscribe++;
            }

            server.Tick(1f / 30f);
            Harness.Pump(clientA, worldA);
            Harness.Pump(clientB, worldB);

            foreach (var id in worldA.EntitiesFor(7).Keys) seenA.Add(id);
            foreach (var id in worldB.EntitiesFor(7).Keys) seenB.Add(id);

            if (step == 30)
            {
                Harness.Send(clientA, MessageType.Unsubscribe, 7);
                messagesAtUnsubscribe = seenA.Count;
                phase = "after";
                Console.WriteLine($"A unsubscribed after {messagesAtUnsubscribe} messages");
            }
            if (step == 45)
            {
                phase = "before";
                Harness.Send(clientA, MessageType.Subscribe, 7);
                Console.WriteLine("A resubscribed");
            }
            if (step == 60)
            {
                clientB.Close();
                Console.WriteLine("B disconnected");
            }
            await Task.Delay(20);
        }

        // give the server a moment to notice B's idle timeout
        Harness.TickLoop(server, clientA, worldA, 100, 20);
        server.Tick(1f / 30f);
        Console.WriteLine($"A messages after resubscribe: {seenA.Count - messagesAtUnsubscribe} (sent while unsubscribed: {sentAfterUnsubscribe})");
        Console.WriteLine($"B messages before disconnect: {seenB.Count}");
        Console.WriteLine($"server sessions after B disconnect: {server.Sessions.Count}");
        transport.Dispose();
    }
}

internal static class Noise
{
    public static async Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("== noise: 300 entities randomized every tick ==");
        var server = Harness.StartServer(out var transport);
        const int count = 300;
        for (var i = 0; i < count; i++) server.Entities.Spawn(EntityState.Create(i, EntityKind.Prop, i, i));
        using var client = new UdpClientTransport();
        if (!await Harness.ConnectAsync(client, transport.Port)) { Console.WriteLine("connect failed"); return; }
        var world = new ClientWorld();
        var random = new Random(3);

        Harness.TickLoop(server, client, world, 30);
        var beforeBytes = client.BytesReceived;
        var stopwatch = Stopwatch.StartNew();
        var ticks = 90;
        for (var tick = 0; tick < ticks; tick++)
        {
            foreach (var item in server.Entities.Items.Values)
                server.Entities.Update(item.Id, EntityState.Create(item.Id, EntityKind.Prop,
                    (float)random.NextDouble() * 500, (float)random.NextDouble() * 500,
                    vx: (float)random.NextDouble(), vy: (float)random.NextDouble()));
            var started = Stopwatch.GetTimestamp();
            server.Tick(1f / 30f);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Harness.Pump(client, world);
            await Task.Delay(1);
            if (tick == 0) Console.WriteLine($"tick with 300 changed entities: {elapsed:F2} ms");
        }
        stopwatch.Stop();

        var bytes = client.BytesReceived - beforeBytes;
        var seconds = stopwatch.Elapsed.TotalSeconds;
        var mismatch = server.Entities.Items.Values.Count(s =>
            !world.Entities.TryGetValue(s.Id, out var c) || Math.Abs(c.X - s.X) > 0.001f);

        Console.WriteLine($"ticks={ticks} seconds={seconds:F2}");
        Console.WriteLine($"downlink: {bytes / 1024.0 / seconds:F1} KB/s, per tick {bytes / (double)ticks:F0} B");
        Console.WriteLine($"client entities={world.EntityCount}/{count}, mismatched={mismatch}");
        transport.Dispose();
    }
}

internal static class Delta
{
    public static async Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("== delta: 1000 static entities, move 10 vs move all ==");
        var server = Harness.StartServer(out var transport);
        const int count = 1000;
        for (var i = 0; i < count; i++) server.Entities.Spawn(EntityState.Create(i, EntityKind.Prop, i, i));
        using var client = new UdpClientTransport();
        if (!await Harness.ConnectAsync(client, transport.Port)) { Console.WriteLine("connect failed"); return; }
        var world = new ClientWorld();
        Harness.TickLoop(server, client, world, 60);

        var before = client.BytesReceived;
        for (var tick = 0; tick < 30; tick++)
        {
            for (var i = 0; i < 10; i++)
            {
                server.Entities.TryGet(i, out var state);
                server.Entities.Update(i, state with { X = state.X + 1 });
            }
            server.Tick(1f / 30f);
            Harness.Pump(client, world);
            await Task.Delay(5);
        }
        var small = client.BytesReceived - before;

        before = client.BytesReceived;
        for (var tick = 0; tick < 10; tick++)
        {
            foreach (var item in server.Entities.Items.Values)
                server.Entities.Update(item.Id, item with { X = item.X + 1 });
            server.Tick(1f / 30f);
            Harness.Pump(client, world);
            await Task.Delay(5);
        }
        var large = client.BytesReceived - before;

        Console.WriteLine($"30 ticks moving 10 entities: {small} bytes");
        Console.WriteLine($"10 ticks moving 1000 entities: {large} bytes");
        Console.WriteLine($"ratio (per tick): {(large / 10.0) / Math.Max(1, small / 30.0):F1}x for 100x more changed entities");
        Console.WriteLine($"client entities={world.EntityCount}/{count}");
        transport.Dispose();
    }
}
