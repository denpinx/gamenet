using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public class ClientLifecycleTests
{
    [Fact]
    public async Task Build_ManualTick_AppliesChunks_AndFreezesRegistration()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        var players = server.RegisterTypedCollection<int, PlayerEntity>("players");
        players.Spawn(1, new PlayerEntity { Id = 1, Name = "alice", X = 7 });
        server.Start();

        using var client = new RpcClientBuilder()
            .AddTypedCollection<int, PlayerView>("players")
            .Subscribe("players")
            .Build("127.0.0.1", transport.Port, manualTick: true);
        var world = client.Typed<int, PlayerView>("players");
        Assert.True(client.IsBuilt);
        Assert.True(client.ManualTick);

        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && world.Count == 0)
        {
            server.Tick(1f / 30f);
            client.Tick();
            await Task.Delay(5);
        }

        Assert.Equal(1, world.Count);
        Assert.Equal("alice", world.Entities[1].Name);

        // Build freezes the collection surface.
        Assert.Throws<InvalidOperationException>(() => client.RegisterTypedCollection<int, PlayerView>("other"));
        Assert.Throws<InvalidOperationException>(() => client.Build("127.0.0.1", transport.Port));
    }

    [Fact]
    public async Task Builder_ConfiguresCollectionsAndSubscriptions()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        var players = server.RegisterTypedCollection<int, PlayerEntity>("players");
        players.Spawn(1, new PlayerEntity { Id = 1, Name = "alice" });
        server.Start();

        using var client = new RpcClientBuilder()
            .AddTypedCollection<int, PlayerView>("players")
            .Subscribe("players")
            .Build("127.0.0.1", transport.Port, manualTick: true);

        var world = client.Typed<int, PlayerView>("players");
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && world.Count == 0)
        {
            server.Tick(1f / 30f);
            client.Tick();
            await Task.Delay(5);
        }

        Assert.Equal("alice", world.Entities[1].Name);
    }
    [Fact]
    public void Tick_OutsideManualMode_Throws()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        server.Start();

        using var client = new RpcClientBuilder().Build("127.0.0.1", transport.Port);
        Assert.False(client.ManualTick);
        Assert.Throws<InvalidOperationException>(() => client.Tick());
    }
}
