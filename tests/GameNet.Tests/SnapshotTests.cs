using GameNet.Core;
using Xunit;

namespace GameNet.Tests;

public class SnapshotTests
{
    private static EntityDelta Add(int id, float x, float y) => new()
    {
        Id = id,
        Op = DeltaOp.Add,
        State = EntityState.Create(id, EntityKind.Prop, x, y),
    };

    [Fact]
    public void Build_SplitsIntoChunks_WhenExceedingMtu()
    {
        var deltas = new List<EntityDelta>();
        for (var i = 0; i < 500; i++) deltas.Add(Add(i, i % 10, i / 10));

        var chunks = SnapshotCodec.Build(1, deltas);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= SnapshotCodec.MaxChunkPayload));
    }

    [Fact]
    public void Chunks_ReassembleIntoWorld()
    {
        var deltas = new List<EntityDelta>();
        for (var i = 0; i < 300; i++) deltas.Add(Add(i, i, i));

        var chunks = SnapshotCodec.Build(7, deltas);
        var world = new ClientWorld();

        foreach (var payload in chunks)
        {
            Assert.True(SnapshotCodec.TryRead(payload, out var chunk));
            Assert.Equal(7u, chunk.Version);
            Assert.Equal(chunks.Count, chunk.Count);
            world.OnChunk(chunk);
        }

        Assert.Equal(300, world.EntityCount);
        Assert.Equal(chunks.Count, world.ChunksApplied);
    }

    [Fact]
    public void SnapshotTimestamp_RoundTrips()
    {
        var deltas = new List<EntityDelta> { Add(1, 0, 0) };
        var chunks = SnapshotCodec.Build(1, deltas, 123456);
        Assert.True(SnapshotCodec.TryRead(chunks[0], out var chunk));
        Assert.Equal(123456u, chunk.ServerTimeMs);
    }

    [Fact]
    public void Client_RecordsLagFromServerTimestamp()
    {
        var deltas = new List<EntityDelta> { Add(1, 0, 0) };
        var serverTime = (uint)(Environment.TickCount64 - 25);
        var chunks = SnapshotCodec.Build(1, deltas, serverTime);

        var world = new ClientWorld();
        SnapshotCodec.TryRead(chunks[0], out var chunk);
        world.OnChunk(chunk);

        Assert.Single(world.LagSamples);
        Assert.InRange(world.LastLagMs, 20, 500);
    }
    [Fact]
    public void UpdatesApplyOnlyMaskedFields()
    {
        var world = new ClientWorld();
        var spawn = new List<EntityDelta> { Add(1, 0, 0) };
        foreach (var chunk in SnapshotCodec.Build(1, spawn))
        {
            SnapshotCodec.TryRead(chunk, out var parsed);
            world.OnChunk(parsed);
        }

        var current = world.Entities[1];
        var next = current;
        next.X = 42;
        var mask = EntityCodec.DiffMask(current, next);

        var update = new List<EntityDelta>
        {
            new() { Id = 1, Op = DeltaOp.Update, State = next, Mask = mask },
        };
        foreach (var chunk in SnapshotCodec.Build(2, update))
        {
            SnapshotCodec.TryRead(chunk, out var parsed);
            world.OnChunk(parsed);
        }

        Assert.Equal(42, world.Entities[1].X);
        Assert.Equal(0, world.Entities[1].Y);
    }

    [Fact]
    public void Remove_DeletesEntity()
    {
        var world = new ClientWorld();
        var spawn = new List<EntityDelta> { Add(5, 0, 0) };
        foreach (var chunk in SnapshotCodec.Build(1, spawn))
        {
            SnapshotCodec.TryRead(chunk, out var parsed);
            world.OnChunk(parsed);
        }

        var remove = new List<EntityDelta> { new() { Id = 5, Op = DeltaOp.Remove } };
        foreach (var chunk in SnapshotCodec.Build(2, remove))
        {
            SnapshotCodec.TryRead(chunk, out var parsed);
            world.OnChunk(parsed);
        }

        Assert.Empty(world.Entities);
    }

    [Fact]
    public void PartialChunks_ApplyImmediately()
    {
        var deltas = new List<EntityDelta>();
        for (var i = 0; i < 400; i++) deltas.Add(Add(i, i, i));
        var chunks = SnapshotCodec.Build(3, deltas);
        Assert.True(chunks.Count > 1);

        var world = new ClientWorld();
        SnapshotCodec.TryRead(chunks[0], out var first);
        Assert.True(world.OnChunk(first));
        Assert.True(world.EntityCount > 0);
        Assert.True(world.EntityCount < 400);
    }

    [Fact]
    public void StaleEntityVersion_IsIgnored()
    {
        var world = new ClientWorld();

        var newer = new List<EntityDelta> { Add(1, 10, 10) };
        SnapshotCodec.TryRead(SnapshotCodec.Build(5, newer)[0], out var newChunk);
        world.OnChunk(newChunk);

        var stale = new List<EntityDelta> { Add(1, 99, 99) };
        SnapshotCodec.TryRead(SnapshotCodec.Build(3, stale)[0], out var oldChunk);
        world.OnChunk(oldChunk);

        Assert.Equal(10, world.Entities[1].X);
    }
}
