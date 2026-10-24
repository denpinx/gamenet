using GameNet.Core;
using GameNet.Generated.GameNet.Tests;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

[Entity("player")]
public sealed partial class PlayerEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
}

[Entity("player")]
public sealed partial record PlayerView(int Id, string Name, float X, float Y);

public class TypedCollectionTests
{
    [Fact]
    public async Task TypedCollection_ReplicatesClassToRecord_WithDeltaAndRemove()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        var players = server.RegisterTypedCollection<PlayerEntity>("players");
        players.Spawn(1, new PlayerEntity { Id = 1, Name = "alice", X = 10, Y = 20 });
        server.Start();

        using var client = new RpcClient();
        var world = client.RegisterTypedCollection<PlayerView>("players");
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        client.Subscribe("players");

        await PumpUntilAsync(server, () => world.Count == 1);
        Assert.Equal(1, world.Count);
        var view = world.Entities[1];
        Assert.Equal("alice", view.Name);
        Assert.Equal(10, view.X, 3);

        // Delta: only X changes.
        players.Spawn(1, new PlayerEntity { Id = 1, Name = "alice", X = 11, Y = 20 });
        await PumpUntilAsync(server, () => world.Entities[1].X == 11);
        Assert.Equal(20, world.Entities[1].Y, 3);

        players.Despawn(1);
        await PumpUntilAsync(server, () => world.Count == 0);
    }

    [Fact]
    public async Task TypedCollection_RespectsFilterPerSession()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        var players = server.RegisterTypedCollection<PlayerEntity>("players");
        players.Filter = (session, player) => player.Id == session.Id;
        players.Spawn(1, new PlayerEntity { Id = 1, Name = "one" });
        players.Spawn(2, new PlayerEntity { Id = 2, Name = "two" });
        server.Start();

        using var client = new RpcClient();
        var world = client.RegisterTypedCollection<PlayerView>("players");
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        client.Subscribe("players");

        await PumpUntilAsync(server, () => world.Count == 1);
        Assert.True(world.Entities.ContainsKey(1));
        Assert.False(world.Entities.ContainsKey(2));
    }

    [Fact]
    public void CodecsOfClassAndRecord_AreWireCompatible()
    {
        var before = new PlayerEntity { Id = 1, Name = "alice", X = 10, Y = 20 };
        var after = new PlayerEntity { Id = 1, Name = "alice", X = 11, Y = 20 };
        var mask = PlayerEntityPacketCodec.DiffMask(before, after);
        Assert.Equal(4UL, mask); // only X changed

        var writer = new ByteWriter(64);
        PlayerEntityPacketCodec.WriteDelta(ref writer, after, mask);
        Assert.Equal(4, writer.Length); // string field must not be written when unmasked

        var reader = new ByteReader(writer.Written);
        var view = PlayerViewPacketCodec.ApplyDelta(ref reader, new PlayerView(1, "alice", 10, 20), mask);
        Assert.Equal(11, view.X, 3);
        Assert.Equal(20, view.Y, 3);
        Assert.True(reader.End);
    }
    [Fact]
    public async Task TypedCollection_InPlaceMutation_Replicates()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        var players = server.RegisterTypedCollection<PlayerEntity>("players");
        var alice = new PlayerEntity { Id = 1, Name = "alice", X = 10, Y = 20 };
        players.Spawn(1, alice);
        server.Start();

        using var client = new RpcClient();
        var world = client.RegisterTypedCollection<PlayerView>("players");
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        client.Subscribe("players");
        await PumpUntilAsync(server, () => world.Count == 1);

        alice.Name = "bob";
        players.Spawn(1, alice);
        await PumpUntilAsync(server, () => world.Entities[1].Name == "bob");
        Assert.Equal("bob", world.Entities[1].Name);
    }
    private static async Task PumpUntilAsync(GameServer server, Func<bool> condition, int timeoutMs = 6000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            server.Tick(1f / 30f);
            await Task.Delay(5);
        }
        Assert.Fail("condition not met within timeout");
    }
}
