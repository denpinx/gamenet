using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

/// <summary>
/// Fault-injection matrix: loss × latency × jitter × duplication, each asserting that the
/// whole replication pipeline converges over real UDP.
/// </summary>
public class FaultMatrixTests
{
    public static IEnumerable<object[]> Conditions()
    {
        var networks = new (int Latency, int Jitter)[] { (0, 0), (40, 10), (80, 20) };
        foreach (var loss in new[] { 0.0, 0.03, 0.08 })
        foreach (var network in networks)
            yield return new object[] { loss, network.Latency, network.Jitter, 0.0 };

        yield return new object[] { 0.03, 0, 0, 0.05 };
        yield return new object[] { 0.03, 40, 10, 0.05 };
    }

    [Theory]
    [MemberData(nameof(Conditions))]
    public async Task Replication_Converges_UnderImpairment(double loss, int latency, int jitter, double duplicate)
    {
        var impairment = new ImpairmentOptions
        {
            Loss = loss,
            LatencyMs = latency,
            JitterMs = jitter,
            DuplicateRate = duplicate,
        };

        using var transport = new UdpServerTransport(0, impairment);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = true });
        for (var i = 0; i < 20; i++)
            server.Entities.Spawn(EntityState.Create(i, EntityKind.Prop, i, i, vx: 1, vy: 1));
        server.Start();

        using var client = new UdpClientTransport(impairment);
        Assert.True(client.Connect("127.0.0.1", transport.Port));

        var world = new ClientWorld();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && world.EntityCount < 20)
        {
            server.Tick(1f / 30f);
            client.Poll();
            while (client.TryDequeue(out var packet))
            {
                if (packet.Channel == 0 && SnapshotCodec.TryRead(packet.Payload, out var chunk))
                    world.OnChunk(chunk);
            }
            await Task.Delay(5);
        }

        Assert.Equal(20, world.EntityCount);
        Assert.True(client.BytesReceived > 0);
    }

    [Fact]
    public void Send_RejectsWhenWindowIsFull()
    {
        var channel = new ReliableChannel(0, (_, _) => { }, _ => { }, null, ordered: true)
        {
            WindowSize = 1,
        };

        Assert.True(channel.Send(new byte[16]));
        Assert.False(channel.Send(new byte[16]));
        Assert.True(channel.WindowBlocked > 0);
    }

    [Fact]
    public void ClientWorld_RequestsResync_AfterManyUnknownUpdates()
    {
        var world = new ClientWorld();
        var chunk = new SnapshotChunk { Version = 1, Index = 0, Count = 1 };
        chunk.Entries.Add(new ChunkEntry
        {
            Id = 99,
            Op = DeltaOp.Update,
            Mask = EntityCodec.FX,
            State = new EntityState { X = 1 },
        });

        for (var i = 0; i < 40; i++) world.OnChunk(chunk);

        Assert.True(world.NeedsResync);
        world.AcknowledgeResync();
        Assert.False(world.NeedsResync);
    }

    [Fact]
    public void ClientWorld_Tombstone_BlocksOlderAdd()
    {
        var world = new ClientWorld();

        var add = new SnapshotChunk { Version = 5, Index = 0, Count = 1 };
        add.Entries.Add(new ChunkEntry
        {
            Id = 1,
            Op = DeltaOp.Add,
            State = EntityState.Create(1, EntityKind.Prop, 1, 1),
        });
        world.OnChunk(add);
        Assert.True(world.Entities.ContainsKey(1));

        var remove = new SnapshotChunk { Version = 6, Index = 0, Count = 1 };
        remove.Entries.Add(new ChunkEntry { Id = 1, Op = DeltaOp.Remove });
        world.OnChunk(remove);
        Assert.False(world.Entities.ContainsKey(1));

        var staleAdd = new SnapshotChunk { Version = 4, Index = 0, Count = 1 };
        staleAdd.Entries.Add(new ChunkEntry
        {
            Id = 1,
            Op = DeltaOp.Add,
            State = EntityState.Create(1, EntityKind.Prop, 9, 9),
        });
        world.OnChunk(staleAdd);
        Assert.False(world.Entities.ContainsKey(1));
    }
}
