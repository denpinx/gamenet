using GameNet.Core;
using GameNet.Persistence;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

[Entity("named")]
public sealed partial class NamedEntity
{
    [Key] public string Name { get; set; } = string.Empty;
    public int X { get; set; }
}
public class TypedCollectionPersistenceTests
{
    [Fact]
    public void TypedCollection_StringKey_SavesAndLoads()
    {
        var db = Path.Combine(Path.GetTempPath(), $"typed-{Guid.NewGuid():N}.db");
        try
        {
            using var transport = new UdpServerTransport(0);
            transport.Start();
            var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
            var named = server.RegisterTypedCollection<NamedEntity>("named");
            var store = named.UseSqlite($"Data Source={db}");

            named.Spawn("alice", new NamedEntity { Name = "alice", X = 1 });
            Assert.Equal(1, store.Load("alice")!.X);

            named.Save("bob", new NamedEntity { Name = "bob", X = 2 });
            Assert.Equal(2, store.Load("bob")!.X);
            Assert.True(named.TryGet("bob", out var bob));
            Assert.Equal("bob", bob.Name);

            named.Despawn("alice");
            Assert.Null(store.Load("alice"));
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }
    [Fact]
    public void TypedCollection_LoadOrSpawn_LoadsOrCreates()
    {
        var db = Path.Combine(Path.GetTempPath(), $"typed-{Guid.NewGuid():N}.db");
        try
        {
            using var transport = new UdpServerTransport(0);
            transport.Start();
            var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
            var named = server.RegisterTypedCollection<NamedEntity>("named");
            var store = named.UseSqlite($"Data Source={db}", autoPersist: false);

            store.Store.Save(new NamedEntity { Name = "alice", X = 1 });

            var alice = named.LoadOrSpawn("alice", () => new NamedEntity { Name = "alice", X = 99 });
            Assert.Equal(1, alice.X); // loaded from the database, factory not used

            var bob = named.LoadOrSpawn("bob", () => new NamedEntity { Name = "bob", X = 2 });
            Assert.Equal(2, bob.X);
            Assert.Equal(2, store.Load("bob")!.X); // created row was saved

            var bobAgain = named.LoadOrSpawn("bob", () => new NamedEntity { Name = "bob", X = 3 });
            Assert.Same(bob, bobAgain);
            Assert.Equal(2, bobAgain.X);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }
    [Fact]
    public void TypedCollection_SaveAtSpecificId()
    {
        var db = Path.Combine(Path.GetTempPath(), $"typed-{Guid.NewGuid():N}.db");
        try
        {
            using var transport = new UdpServerTransport(0);
            transport.Start();
            var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
            var players = server.RegisterTypedCollection<PlayerEntity>("players");
            var store = players.UseSqlite($"Data Source={db}", autoPersist: false);

            players.Save(3, new PlayerEntity { Id = 3, Name = "dave", X = 7 });

            Assert.True(players.TryGet(3, out var dave));
            Assert.Equal("dave", dave.Name);
            Assert.Equal("dave", store.Load(3)!.Name);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }
    [Fact]
    public void TypedCollection_LoadWhere_ByProperty()
    {
        var db = Path.Combine(Path.GetTempPath(), $"typed-{Guid.NewGuid():N}.db");
        try
        {
            using var transport = new UdpServerTransport(0);
            transport.Start();
            var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
            var players = server.RegisterTypedCollection<PlayerEntity>("players");
            var store = players.UseSqlite($"Data Source={db}");

            store.Store.Save(new PlayerEntity { Id = 1, Name = "alice", X = 1 });
            store.Store.Save(new PlayerEntity { Id = 2, Name = "bob", X = 2 });

            var loaded = players.LoadWhere(player => player.Name == "alice");
            Assert.Single(loaded);
            Assert.Equal("alice", loaded[0].Name);
            Assert.Equal(1, players.Count);
            Assert.True(players.TryGet(1, out _));
            Assert.False(players.TryGet(2, out _));

            Assert.Equal(2, players.LoadAll());
            Assert.Equal(2, players.Count);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }
    [Fact]
    public void TypedCollection_AutoPersistsSpawnAndDespawn()
    {
        var db = Path.Combine(Path.GetTempPath(), $"typed-{Guid.NewGuid():N}.db");
        try
        {
            using var transport = new UdpServerTransport(0);
            transport.Start();
            var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
            var players = server.RegisterTypedCollection<PlayerEntity>("players");
            var store = players.UseSqlite($"Data Source={db}");

            players.Spawn(1, new PlayerEntity { Id = 1, Name = "alice", X = 1 });
            Assert.Equal("alice", store.Load(1)!.Name);

            players.Spawn(1, new PlayerEntity { Id = 1, Name = "bob", X = 2 });
            Assert.Equal("bob", store.Load(1)!.Name);

            store.Store.Save(new PlayerEntity { Id = 2, Name = "carol" });
            Assert.Equal(2, players.LoadAll());
            Assert.True(players.TryGet(2, out var carol));
            Assert.Equal("carol", carol.Name);

            players.Despawn(1);
            Assert.Null(store.Load(1));
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }
}
