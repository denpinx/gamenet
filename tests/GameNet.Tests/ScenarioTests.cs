using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

/// <summary>Scenario-level coverage: short subscriptions, noisy collections, delta-only traffic.</summary>
public class ScenarioTests
{
    [Fact]
    public async Task Unsubscribe_StopsReplication_ResubscribeResendsFullState()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        for (var i = 0; i < 3; i++) server.Entities.Spawn(EntityState.Create(i, EntityKind.Prop, i, 0));
        server.Start();

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        var world = new ClientWorld();

        Send(client, MessageType.Subscribe);
        await WaitUntilAsync(() => { server.Tick(1f / 30f); Pump(client, world); return world.EntityCount >= 3; });

        Send(client, MessageType.Unsubscribe);
        for (var i = 0; i < 5; i++)
        {
            server.Tick(1f / 30f);
            Pump(client, world);
            await Task.Delay(10);
        }
        for (var i = 0; i < 20; i++) server.Entities.Spawn(EntityState.Create(100 + i, EntityKind.Prop, i, 0));
        for (var i = 0; i < 20; i++)
        {
            server.Tick(1f / 30f);
            Pump(client, world);
            await Task.Delay(10);
        }
        Assert.Equal(3, world.EntityCount); // unsubscribed: new entities are not replicated

        Send(client, MessageType.Subscribe);
        await WaitUntilAsync(() => { server.Tick(1f / 30f); Pump(client, world); return world.EntityCount >= 23; });
        Assert.Equal(23, world.EntityCount); // resubscribe resends a full snapshot
    }

    [Fact]
    public async Task Despawn_RemovesEntityFromClient()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = true });
        for (var i = 0; i < 5; i++) server.Entities.Spawn(EntityState.Create(i, EntityKind.Prop, i, 0));
        server.Start();

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        var world = new ClientWorld();
        await WaitUntilAsync(() => { server.Tick(1f / 30f); Pump(client, world); return world.EntityCount >= 5; });

        server.Entities.Despawn(2);
        await WaitUntilAsync(() => { server.Tick(1f / 30f); Pump(client, world); return !world.Entities.ContainsKey(2); });
        Assert.Equal(4, world.EntityCount);
    }

    [Fact]
    public async Task NoisyCollection_ConvergesToLatestValues()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = true });
        var random = new Random(7);
        for (var i = 0; i < 40; i++) server.Entities.Spawn(EntityState.Create(i, EntityKind.Prop, i, i));
        server.Start();

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        var world = new ClientWorld();

        for (var tick = 0; tick < 30; tick++)
        {
            foreach (var item in server.Entities.Items.Values)
                server.Entities.Update(item.Id, EntityState.Create(item.Id, EntityKind.Prop,
                    (float)random.NextDouble() * 100, (float)random.NextDouble() * 100,
                    vx: (float)random.NextDouble(), vy: (float)random.NextDouble()));
            server.Tick(1f / 30f);
            Pump(client, world);
            await Task.Delay(5);
        }

        await WaitUntilAsync(() =>
        {
            server.Tick(1f / 30f);
            Pump(client, world);
            return world.EntityCount == 40 && server.Entities.Items.Values.All(s =>
                world.Entities.TryGetValue(s.Id, out var c) && Math.Abs(c.X - s.X) < 0.001f && Math.Abs(c.Y - s.Y) < 0.001f);
        });
    }

    [Fact]
    public async Task DeltaTraffic_ScalesWithChangedEntityCount()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = true });
        for (var i = 0; i < 200; i++) server.Entities.Spawn(EntityState.Create(i, EntityKind.Prop, i, i));
        server.Start();

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        var world = new ClientWorld();
        await WaitUntilAsync(() => { server.Tick(1f / 30f); Pump(client, world); return world.EntityCount >= 200; });

        var before = client.BytesReceived;
        for (var tick = 0; tick < 20; tick++)
        {
            for (var i = 0; i < 5; i++)
            {
                server.Entities.TryGet(i, out var state);
                server.Entities.Update(i, state with { X = state.X + 1 });
            }
            server.Tick(1f / 30f);
            Pump(client, world);
            await Task.Delay(5);
        }
        var small = client.BytesReceived - before;

        before = client.BytesReceived;
        for (var tick = 0; tick < 10; tick++)
        {
            foreach (var item in server.Entities.Items.Values)
                server.Entities.Update(item.Id, item with { X = item.X + 1 });
            server.Tick(1f / 30f);
            Pump(client, world);
            await Task.Delay(5);
        }
        var large = client.BytesReceived - before;

        Assert.True(large > small * 4, $"expected delta traffic to scale with changes: small={small} large={large}");
    }

    [Fact]
    public async Task SecondCollection_SubscribesIndependently()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        var chat = server.RegisterCollection(7, new EntityCollection());
        for (var i = 0; i < 3; i++) server.Entities.Spawn(EntityState.Create(i, EntityKind.Prop, i, 0));
        for (var i = 0; i < 4; i++) chat.Spawn(EntityState.Create(100 + i, EntityKind.Prop, i, 0));
        server.Start();

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        var world = new ClientWorld();

        Send(client, MessageType.Subscribe, 0);
        await WaitUntilAsync(() => { server.Tick(1f / 30f); Pump(client, world); return world.EntityCountFor(0) >= 3; });
        Assert.Equal(0, world.EntityCountFor(7)); // not subscribed to chat yet

        Send(client, MessageType.Subscribe, 7);
        await WaitUntilAsync(() => { server.Tick(1f / 30f); Pump(client, world); return world.EntityCountFor(7) >= 4; });
        Assert.Equal(3, world.EntityCountFor(0));
        Assert.Equal(7, world.EntityCount);

        // Unsubscribe only from chat: default collection keeps flowing.
        Send(client, MessageType.Unsubscribe, 7);
        for (var i = 0; i < 5; i++)
        {
            server.Tick(1f / 30f);
            Pump(client, world);
            await Task.Delay(10);
        }
        chat.Spawn(EntityState.Create(200, EntityKind.Prop, 9, 9));
        for (var i = 0; i < 10; i++)
        {
            server.Tick(1f / 30f);
            Pump(client, world);
            await Task.Delay(10);
        }
        Assert.Equal(3, world.EntityCountFor(0));
        Assert.Equal(4, world.EntityCountFor(7)); // new chat entity not delivered
    }
    private static void Send(UdpClientTransport client, MessageType type, byte? collection = null)
    {
        var writer = new ByteWriter(4);
        writer.WriteByte((byte)type);
        if (collection.HasValue) writer.WriteByte(collection.Value);
        client.Send(1, writer.Written);
    }

    private static void Pump(UdpClientTransport client, ClientWorld world)
    {
        client.Poll();
        while (client.TryDequeue(out var packet))
        {
            if (packet.Channel == 0 && SnapshotCodec.TryRead(packet.Payload, out var chunk))
                world.OnChunk(chunk);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(5);
        }
        Assert.Fail("condition not met within timeout");
    }
}
