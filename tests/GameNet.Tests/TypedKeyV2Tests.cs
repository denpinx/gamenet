using GameNet.Core;
using GameNet.Persistence;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public readonly record struct Vec3I(int X, int Y, int Z);

[Entity("tile")]
public sealed partial class TileEntity
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Name { get; set; } = string.Empty;
}

[Entity("tile")]
public sealed partial record TileView(int X, int Y, string Name);

public sealed class Vec3Codec : ITypedKeyCodec<Vec3I>
{
    public string Id => "vec3i";
    public void Write(ByteWriter writer, Vec3I value)
    {
        writer.WriteVarInt(value.X);
        writer.WriteVarInt(value.Y);
        writer.WriteVarInt(value.Z);
    }
    public Vec3I Read(ref ByteReader reader) => new(reader.ReadVarInt(), reader.ReadVarInt(), reader.ReadVarInt());
}

public sealed class WrongVec3Codec : ITypedKeyCodec<Vec3I>
{
    public string Id => "vec3i-wrong";
    public void Write(ByteWriter writer, Vec3I value) => throw new NotSupportedException();
    public Vec3I Read(ref ByteReader reader) => throw new NotSupportedException();
}

public readonly record struct UnregisteredKey(int Value);

public class TypedKeyV2Tests
{
    static TypedKeyV2Tests()
    {
        TypedKeyCodecs.Register(new Vec3Codec());
    }

    [Fact]
    public void BuiltInKeys_RoundTripThroughSnapshot()
    {
        AssertKeyRoundTrip(42, TypedKeyCodecs.Get<int>());
        AssertKeyRoundTrip("alice", TypedKeyCodecs.Get<string>());
        var guid = Guid.NewGuid();
        AssertKeyRoundTrip(guid, TypedKeyCodecs.Get<Guid>());
    }

    [Fact]
    public void CustomKey_RoundTripsThroughSnapshot()
    {
        AssertKeyRoundTrip(new Vec3I(1, 2, 3), TypedKeyCodecs.Get<Vec3I>());
    }

    [Fact]
    public void RegisteringSameTypeWithDifferentCodec_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => TypedKeyCodecs.Register(new WrongVec3Codec()));
    }

    [Fact]
    public void CollectionWithoutCodec_IsRejected()
    {
        using var transport = new UdpServerTransport(0);
        transport.Start();
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        Assert.Throws<InvalidOperationException>(() =>
            server.RegisterTypedCollection<UnregisteredKey, PlayerEntity>("unknown"));
    }

    [Fact]
    public async Task CodecMismatch_SetsRegistryError()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        server.RegisterTypedCollection<int, PlayerEntity>("players");
        server.Start();

        using var client = new RpcClientBuilder()
            .AddTypedCollection<string, PlayerView>("players")
            .Build("127.0.0.1", transport.Port);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && client.LastRegistryError is null) await Task.Delay(20);
        Assert.NotNull(client.LastRegistryError);
        Assert.Contains("key codec", client.LastRegistryError);
        server.Tick(1f / 30f);
    }

    [Fact]
    public async Task CustomKey_EventsCarryTypedKey()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        var tiles = server.RegisterTypedCollection<Vec3I, TileEntity>("tiles");
        var key = new Vec3I(3, 4, 5);
        tiles.Spawn(key, new TileEntity { X = 3, Y = 4, Name = "origin" });
        server.Start();

        using var client = new RpcClientBuilder()
            .AddTypedCollection<Vec3I, TileView>("tiles")
            .Subscribe("tiles")
            .Build("127.0.0.1", transport.Port);
        var world = client.Typed<Vec3I, TileView>("tiles");

        var events = new List<TypedCollectionChanged<Vec3I, TileView>>();
        using var subscription = world.OnChanged(events.Add);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && world.Count == 0)
        {
            server.Tick(1f / 30f);
            await Task.Delay(5);
        }

        Assert.Equal(1, world.Count);
        Assert.Equal("origin", world[key].Name);

        events.Clear();
        tiles.Spawn(key, new TileEntity { X = 3, Y = 4, Name = "updated" });
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && events.Count == 0)
        {
            server.Tick(1f / 30f);
            await Task.Delay(5);
        }

        Assert.NotEmpty(events);
        Assert.Equal(key, events[0].Changes[0].Id);
        Assert.Equal("updated", world[key].Name);
    }

    [Fact]
    public async Task MultipleCollections_WithDifferentKeyTypes_AreIndependent()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        var players = server.RegisterTypedCollection<int, PlayerEntity>("players");
        var tiles = server.RegisterTypedCollection<string, TileEntity>("tiles");
        players.Spawn(1, new PlayerEntity { Id = 1, Name = "alice" });
        tiles.Spawn("spawn", new TileEntity { Name = "spawn" });
        server.Start();

        using var client = new RpcClientBuilder()
            .AddTypedCollection<int, PlayerView>("players")
            .AddTypedCollection<string, TileView>("tiles")
            .Subscribe("players")
            .Subscribe("tiles")
            .Build("127.0.0.1", transport.Port);

        var playerWorld = client.Typed<int, PlayerView>("players");
        var tileWorld = client.Typed<string, TileView>("tiles");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && (playerWorld.Count == 0 || tileWorld.Count == 0))
        {
            server.Tick(1f / 30f);
            await Task.Delay(5);
        }

        Assert.Equal("alice", playerWorld[1].Name);
        Assert.Equal("spawn", tileWorld["spawn"].Name);
        Assert.Equal(1, playerWorld.Count);
        Assert.Equal(1, tileWorld.Count);
    }

    [Fact]
    public void CustomKey_PersistsQueryAndDeletes()
    {
        var db = Path.Combine(Path.GetTempPath(), $"v3-{Guid.NewGuid():N}.db");
        try
        {
            using var transport = new UdpServerTransport(0);
            transport.Start();
            var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
            var tiles = server.RegisterTypedCollection<Vec3I, TileEntity>("tiles");
            var store = tiles.UseSqlite($"Data Source={db}");

            tiles.Spawn(new Vec3I(0, 0, 0), new TileEntity { X = 0, Y = 0, Name = "origin" });
            tiles.Spawn(new Vec3I(5, 0, 0), new TileEntity { X = 5, Y = 0, Name = "far" });

            Assert.Equal("origin", store.Load(new Vec3I(0, 0, 0))!.Name);
            Assert.Equal(2, store.LoadAll().Count);
            var far = store.Query(tile => tile.X == 5);
            Assert.Single(far);
            Assert.Equal("far", far[0].Value.Name);

            store.Delete(new Vec3I(0, 0, 0));
            Assert.Null(store.Load(new Vec3I(0, 0, 0)));
            Assert.Single(store.LoadAll());
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    [Fact]
    public async Task StringAndCustomKeys_SurviveClientReconnect()
    {
        using var transport = new UdpServerTransport(0) { IdleTimeoutMs = 200 };
        var server = new GameServer(transport, new ServerOptions
        {
            AutoSubscribe = false, AoiRadius = 0, ResumeGraceSeconds = 5,
        });
        var tiles = server.RegisterTypedCollection<Vec3I, TileEntity>("tiles");
        tiles.Spawn(new Vec3I(7, 8, 9), new TileEntity { X = 7, Y = 8, Name = "kept" });
        server.Start(autoTick: true, tickRate: 60);

        try
        {
            using var client = new RpcClientBuilder()
                .AddTypedCollection<Vec3I, TileView>("tiles")
                .Subscribe("tiles")
                .Build("127.0.0.1", transport.Port);
            var world = client.Typed<Vec3I, TileView>("tiles");

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && world.Count == 0) await Task.Delay(20);
            Assert.Equal("kept", world[new Vec3I(7, 8, 9)].Name);

            Assert.True(await client.ReconnectAsync("127.0.0.1", transport.Port));
            Assert.True(await client.WaitForResumeAsync(8000));
            deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && world.Count == 0) await Task.Delay(20);
            Assert.Equal("kept", world[new Vec3I(7, 8, 9)].Name);
        }
        finally
        {
            server.StopAutoTick();
        }
    }

    private static void AssertKeyRoundTrip<TKey>(TKey key, ITypedKeyCodec<TKey> codec)
    {
        var entries = new List<TypedEntry<TKey>>
        {
            new() { Id = key, Op = DeltaOp.Add, Payload = new byte[] { 1, 2, 3 } },
        };
        var chunks = TypedSnapshotCodec.Build(codec, 1, 10, 0, entries);
        Assert.Single(chunks);
        Assert.True(TypedSnapshotCodec.TryRead(chunks[0], codec, out var parsed));
        Assert.Single(parsed.Entries);
        Assert.Equal(key, parsed.Entries[0].Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Entries[0].Payload);
    }
}
