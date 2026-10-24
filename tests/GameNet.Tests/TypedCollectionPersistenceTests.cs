using GameNet.Core;
using GameNet.Persistence;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public class TypedCollectionPersistenceTests
{
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
