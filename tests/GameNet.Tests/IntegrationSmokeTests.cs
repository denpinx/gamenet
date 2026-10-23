using GameNet.Core;
using GameNet.Demo.TicTacToe;
using GameNet.Persistence;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

/// <summary>
/// Integration / smoke coverage: real UDP sockets, real ticks, real persistence.
/// These are slower than unit tests on purpose — they assert the whole pipeline.
/// </summary>
public class IntegrationSmokeTests
{
    [Fact]
    public async Task PerSessionFilter_IsAppliedOverUdp()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = true });
        server.Entities.Filter = (session, entity) => entity.Team == (byte)(session.Id % 2);
        for (var i = 0; i < 10; i++) server.Entities.Spawn(EntityState.Create(100 + i, EntityKind.Prop, i, 0, team: 0));
        for (var i = 0; i < 10; i++) server.Entities.Spawn(EntityState.Create(200 + i, EntityKind.Prop, i, 0, team: 1));
        server.Start();

        using var clientA = new UdpClientTransport();
        using var clientB = new UdpClientTransport();
        Assert.True(clientA.Connect("127.0.0.1", transport.Port));
        Assert.True(clientB.Connect("127.0.0.1", transport.Port));

        var worldA = new ClientWorld();
        var worldB = new ClientWorld();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && (worldA.EntityCount < 10 || worldB.EntityCount < 10))
        {
            server.Tick(1f / 30f);
            Pump(clientA, worldA);
            Pump(clientB, worldB);
            await Task.Delay(5);
        }

        Assert.Equal(10, worldA.EntityCount);
        Assert.Equal(10, worldB.EntityCount);
        // Session 1 sees team 1, session 2 sees team 0 (100/200 ranges).
        Assert.All(worldA.Entities.Keys, id => Assert.InRange(id, 200, 209));
        Assert.All(worldB.Entities.Keys, id => Assert.InRange(id, 100, 109));
    }

    [Fact]
    public async Task Replication_Converges_WithLossAndLatency()
    {
        var impairment = new ImpairmentOptions { Loss = 0.03, LatencyMs = 10, JitterMs = 5 };
        using var transport = new UdpServerTransport(0, impairment);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = true });
        for (var i = 0; i < 50; i++) server.Entities.Spawn(EntityState.Create(i, EntityKind.Prop, i, i, vx: 1, vy: 1));
        server.Start();

        using var client = new UdpClientTransport(impairment);
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        var world = new ClientWorld();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && world.EntityCount < 50)
        {
            server.Tick(1f / 30f);
            Pump(client, world);
            await Task.Delay(5);
        }

        Assert.Equal(50, world.EntityCount);
        var peer = server.Sessions.Values.Single().Peer;
        Assert.True(peer.BytesSent > 0);
    }

    [Fact]
    public async Task ChunkServer_LoadsInitialViewOverUdp()
    {
        using var transport = new UdpServerTransport(0);
        var server = new ChunkServer(transport, new ChunkOptions { WorldChunks = 16, ViewRadius = 1, ChunkPayloadBytes = 256 });
        server.Start();

        using var client = new ChunkClient();
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        client.SendSubscribe();

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && client.World.LoadedChunks < 9)
        {
            server.Tick(1f / 30f);
            client.Pump();
            await Task.Delay(5);
        }

        Assert.Equal(9, client.World.LoadedChunks); // 3x3 view
        Assert.True(client.BytesReceived > 0);
    }

    [Fact]
    public async Task Accounts_Persist_AcrossServerRestart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"ttt-persist-{Guid.NewGuid():N}.db");
        var name = "persist" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            using (var server = new TttServer(db))
            using (var client = new TttClient())
            {
                Assert.True(client.Connect("127.0.0.1", server.Port));
                Assert.True((await client.RegisterAsync(name, "pw1")).Ok);
            }

            using (var server = new TttServer(db))
            using (var client = new TttClient())
            {
                Assert.True(client.Connect("127.0.0.1", server.Port));
                var login = await client.LoginAsync(name, "pw1");
                Assert.True(login.Ok, login.Message);
            }
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    [Fact]
    public async Task QueueWithoutLogin_IsRejected()
    {
        var db = Path.Combine(Path.GetTempPath(), $"ttt-queue-{Guid.NewGuid():N}.db");
        try
        {
            using var server = new TttServer(db);
            using var client = new TttClient();
            Assert.True(client.Connect("127.0.0.1", server.Port));

            var result = await client.QueueAsync();
            Assert.Equal(MatchStatus.Idle, result.Status);
            Assert.Contains("登录", result.Message);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    [Fact]
    public async Task AoiRadius_HidesDistantEntities_OverUdp()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 30, AutoSubscribe = true });
        server.Entities.Spawn(EntityState.Create(1, EntityKind.Player, 0, 0));
        server.Entities.Spawn(EntityState.Create(2, EntityKind.Prop, 5, 0));
        server.Entities.Spawn(EntityState.Create(3, EntityKind.Prop, 500, 500));
        server.Start();

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port));

        var bind = new ByteWriter(8);
        bind.WriteByte((byte)MessageType.BindRequest);
        bind.WriteVarInt(1);
        client.Send(1, bind.Written);

        var world = new ClientWorld();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && world.EntityCount < 2)
        {
            server.Tick(1f / 30f);
            Pump(client, world);
            await Task.Delay(5);
        }

        Assert.True(world.Entities.ContainsKey(1));
        Assert.True(world.Entities.ContainsKey(2));
        Assert.False(world.Entities.ContainsKey(3));
    }

    [Fact]
    public async Task ChunkEdits_ReachClientAsDeltas_OverUdp()
    {
        using var transport = new UdpServerTransport(0);
        var server = new ChunkServer(transport, new ChunkOptions
        {
            WorldChunks = 16,
            ViewRadius = 1,
            ChunkPayloadBytes = 128,
            EditsPerSecond = 400,
        });
        server.Start();

        using var client = new ChunkClient();
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        client.SendSubscribe();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && client.World.LoadedChunks < 9)
        {
            server.Tick(1f / 30f);
            client.Pump();
            await Task.Delay(5);
        }
        Assert.Equal(9, client.World.LoadedChunks);

        var editDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < editDeadline && client.World.AppliedEdits == 0)
        {
            server.Tick(0.1f);
            client.Pump();
            await Task.Delay(5);
        }

        Assert.True(client.World.AppliedEdits > 0);
        Assert.True(client.World.DeltaChunks > 0);
    }
    [Fact]
    public async Task StartWithAutoTick_ReplicatesWithoutManualTick()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = true });
        for (var i = 0; i < 20; i++) server.Entities.Spawn(EntityState.Create(i, EntityKind.Prop, i, 0));
        server.Start(autoTick: true, tickRate: 60);
        Assert.True(server.AutoTickRunning);

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        var world = new ClientWorld();

        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && world.EntityCount < 20)
        {
            client.Poll();
            while (client.TryDequeue(out var packet))
            {
                if (packet.Channel == 0 && SnapshotCodec.TryRead(packet.Payload, out var chunk))
                    world.OnChunk(chunk);
            }
            await Task.Delay(10);
        }

        server.StopAutoTick();
        Assert.False(server.AutoTickRunning);
        Assert.Equal(20, world.EntityCount);
    }
    private static void Pump(UdpClientTransport transport, ClientWorld world)
    {
        transport.Poll();
        while (transport.TryDequeue(out var packet))
        {
            if (packet.Channel == 0 && SnapshotCodec.TryRead(packet.Payload, out var chunk))
                world.OnChunk(chunk);
        }
    }
}
