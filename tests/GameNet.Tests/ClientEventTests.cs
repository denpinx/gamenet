using GameNet.Core;
using GameNet.Generated.GameNet.Tests;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public class ClientEventTests
{
    [Fact]
    public void UntypedWorld_ReportsAddedUpdatedRemoved()
    {
        var world = new ClientWorld();
        var events = new List<CollectionChanged>();
        using var subscription = world.OnChanged(events.Add);

        var add = new SnapshotChunk { Version = 1, Index = 0, Count = 1 };
        add.Entries.Add(new ChunkEntry
        {
            Id = 1,
            Op = DeltaOp.Add,
            State = EntityState.Create(1, EntityKind.Player, 1, 0),
        });
        world.OnChunk(add);

        var update = new SnapshotChunk { Version = 2, Index = 0, Count = 1 };
        update.Entries.Add(new ChunkEntry
        {
            Id = 1,
            Op = DeltaOp.Update,
            Mask = EntityCodec.FX,
            State = new EntityState { X = 2 },
        });
        world.OnChunk(update);

        var remove = new SnapshotChunk { Version = 3, Index = 0, Count = 1 };
        remove.Entries.Add(new ChunkEntry { Id = 1, Op = DeltaOp.Remove });
        world.OnChunk(remove);

        Assert.Equal(3, events.Count);
        Assert.Equal(ChangeKind.Added, events[0].Changes[0].Kind);
        Assert.Equal(1, events[0].Changes[0].Current!.Value.X);
        Assert.Equal(ChangeKind.Updated, events[1].Changes[0].Kind);
        Assert.Equal(1, events[1].Changes[0].Previous!.Value.X);
        Assert.Equal(2, events[1].Changes[0].Current!.Value.X);
        Assert.Equal(ChangeKind.Removed, events[2].Changes[0].Kind);
        Assert.Equal(2, events[2].Changes[0].Previous!.Value.X);
    }

    [Fact]
    public void TypedWorld_ReportsAddedUpdatedRemoved()
    {
        var world = new TypedWorld<int, PlayerView>(7, TypedKeyCodecs.Get<int>());
        var events = new List<TypedCollectionChanged<int, PlayerView>>();
        using var subscription = world.OnChanged(events.Add);

        var add = new ByteWriter();
        PlayerViewPacketCodec.Write(ref add, new PlayerView(1, "alice", 1, 0));
        var addChunk = new TypedChunk<int> { Collection = 7, Version = 1, Index = 0, Count = 1 };
        addChunk.Entries.Add(new TypedEntry<int> { Id = 1, Op = DeltaOp.Add, Payload = add.ToArray() });
        world.Apply(addChunk);

        var before = new PlayerView(1, "alice", 1, 0);
        var after = new PlayerView(1, "alice", 2, 0);
        var mask = PlayerViewPacketCodec.DiffMask(before, after);
        var delta = new ByteWriter();
        PlayerViewPacketCodec.WriteDelta(ref delta, after, mask);
        var updateChunk = new TypedChunk<int> { Collection = 7, Version = 2, Index = 0, Count = 1 };
        updateChunk.Entries.Add(new TypedEntry<int> { Id = 1, Op = DeltaOp.Update, Mask = mask, Payload = delta.ToArray() });
        world.Apply(updateChunk);

        var removeChunk = new TypedChunk<int> { Collection = 7, Version = 3, Index = 0, Count = 1 };
        removeChunk.Entries.Add(new TypedEntry<int> { Id = 1, Op = DeltaOp.Remove, Payload = Array.Empty<byte>() });
        world.Apply(removeChunk);

        Assert.Equal(3, events.Count);
        Assert.Equal(ChangeKind.Added, events[0].Changes[0].Kind);
        Assert.Equal(1, events[0].Changes[0].Current!.X);
        Assert.Equal(ChangeKind.Updated, events[1].Changes[0].Kind);
        Assert.Equal(1, events[1].Changes[0].Previous!.X);
        Assert.Equal(2, events[1].Changes[0].Current!.X);
        Assert.Equal(ChangeKind.Removed, events[2].Changes[0].Kind);
        Assert.Equal(2, events[2].Changes[0].Previous!.X);
    }

    [Fact]
    public async Task TypedWorld_EventFiresWhenServerUpdatesCollection()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        var players = server.RegisterTypedCollection<int, PlayerEntity>("players");
        players.Spawn(1, new PlayerEntity { Id = 1, Name = "alice", X = 1 });
        server.Start();

        using var client = new RpcClient();
        var world = client.RegisterTypedCollection<int, PlayerView>("players");
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        client.Subscribe("players");

        var events = new List<TypedCollectionChanged<int, PlayerView>>();
        using var subscription = world.OnChanged(events.Add);

        await PumpUntilAsync(server, () => events.Count > 0);
        Assert.Equal(ChangeKind.Added, events[0].Changes[0].Kind);
        Assert.Equal("alice", events[0].Changes[0].Current!.Name);

        players.Spawn(1, new PlayerEntity { Id = 1, Name = "alice", X = 2 });
        await PumpUntilAsync(server, () => events.Count > 1);
        var update = events[^1].Changes[0];
        Assert.Equal(ChangeKind.Updated, update.Kind);
        Assert.Equal(1, update.Previous!.X, 3);
        Assert.Equal(2, update.Current!.X, 3);
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
