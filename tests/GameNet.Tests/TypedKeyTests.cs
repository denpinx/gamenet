using GameNet.Core;
using GameNet.Persistence;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public readonly record struct Vec2I(int X, int Y);

public class TypedKeyTests
{
    static TypedKeyTests()
    {
        TypedKeyCodecs.Register(new Vec2ICodec());
    }

    private sealed class Vec2ICodec : ITypedKeyCodec<Vec2I>
    {
        public string Id => "vec2i";
        public void Write(ByteWriter writer, Vec2I value)
        {
            writer.WriteVarInt(value.X);
            writer.WriteVarInt(value.Y);
        }
        public Vec2I Read(ref ByteReader reader) => new(reader.ReadVarInt(), reader.ReadVarInt());
    }

    [Fact]
    public void CustomStructKey_PersistsToSqlite()
    {
        var db = Path.Combine(Path.GetTempPath(), $"vec2i-{Guid.NewGuid():N}.db");
        try
        {
            using var transport = new UdpServerTransport(0);
            transport.Start();
            var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
            var tiles = server.RegisterTypedCollection<Vec2I, PlayerEntity>("tiles");
            var store = tiles.UseSqlite($"Data Source={db}");

            tiles.Spawn(new Vec2I(3, 4), new PlayerEntity { Id = 1, Name = "tile", X = 3, Y = 4 });
            var loaded = store.Load(new Vec2I(3, 4));
            Assert.NotNull(loaded);
            Assert.Equal("tile", loaded!.Name);

            tiles.Despawn(new Vec2I(3, 4));
            Assert.Null(store.Load(new Vec2I(3, 4)));
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }
    [Fact]
    public async Task CustomStructKey_ReplicatesAndIsQueryable()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        var players = server.RegisterTypedCollection<Vec2I, PlayerEntity>("players");
        players.Spawn(new Vec2I(3, 4), new PlayerEntity { Id = 1, Name = "tile", X = 3, Y = 4 });
        server.Start();

        using var client = new RpcClient();
        var world = client.RegisterTypedCollection<Vec2I, PlayerView>("players");
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        client.Subscribe("players");

        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && world.Count == 0)
        {
            server.Tick(1f / 30f);
            await Task.Delay(5);
        }

        Assert.Equal(1, world.Count);
        Assert.Equal("tile", world.Get(new Vec2I(3, 4)).Name);

        players.Despawn(new Vec2I(3, 4));
        deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && world.Count > 0)
        {
            server.Tick(1f / 30f);
            await Task.Delay(5);
        }
        Assert.Equal(0, world.Count);
    }
}
