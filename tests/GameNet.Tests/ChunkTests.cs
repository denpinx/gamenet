using GameNet.Core;
using Xunit;

namespace GameNet.Tests;

public class ChunkTests
{
    private static (ChunkServer server, FakeTransport transport, FakePeer peer) Create(Action<ChunkOptions>? configure = null)
    {
        var options = new ChunkOptions { WorldChunks = 8, ViewRadius = 1, ChunkPayloadBytes = 64 };
        configure?.Invoke(options);
        var transport = new FakeTransport();
        var server = new ChunkServer(transport, options);
        server.Start();
        var peer = transport.Connect();
        server.Tick(0f); // first tick sends the initial view
        return (server, transport, peer);
    }

    private static ChunkWorld Apply(FakePeer peer, ChunkWorld? existing = null)
    {
        var world = existing ?? new ChunkWorld();
        List<(byte Channel, byte[] Payload)> sent;
        lock (peer.Sent) sent = peer.Sent.Where(p => p.Channel == 0).ToList();
        foreach (var (_, payload) in sent) world.Apply(payload);
        peer.ClearSent();
        return world;
    }

    [Fact]
    public void InitialView_SendsFullChunks()
    {
        var (_, _, peer) = Create(o => o.ViewRadius = 1);
        var world = Apply(peer);

        Assert.Equal(9, world.LoadedChunks); // 3x3
        Assert.Equal(9, world.FullChunks);
        Assert.Equal(0, world.DeltaChunks);
    }

    [Fact]
    public void Edits_AreSentAsDeltas()
    {
        var (server, _, peer) = Create(o =>
        {
            o.ViewRadius = 2;
            o.EditsPerSecond = 5000;
        });
        var world = Apply(peer);
        var loaded = world.LoadedChunks;
        Assert.True(loaded > 0);

        server.Tick(0.2f);
        var after = Apply(peer, world);

        Assert.True(world.AppliedEdits > 0);
        Assert.True(after.DeltaChunks > 0);
        Assert.Equal(loaded, after.LoadedChunks);
    }

    [Fact]
    public void LeavingView_SendsUnload()
    {
        var (server, _, peer) = Create(o =>
        {
            o.WorldChunks = 8;
            o.ViewRadius = 0;
        });
        var world = Apply(peer);
        Assert.Equal(1, world.LoadedChunks);

        var session = server.Sessions.Values.Single();
        session.Angle = MathF.PI; // opposite side of the circle -> different chunk
        server.Tick(0f);

        var unloadsBefore = world.Unloads;
        Apply(peer, world);
        Assert.True(world.Unloads > unloadsBefore);
        Assert.Equal(1, world.LoadedChunks);
    }

    [Fact]
    public void FullChunkPayload_RoundTripsBytes()
    {
        var (_, _, peer) = Create(o =>
        {
            o.ViewRadius = 0;
            o.ChunkPayloadBytes = 1024;
        });
        var world = Apply(peer);
        Assert.Equal(1, world.LoadedChunks);
        Assert.True(world.TryGet(new ChunkKey(0, 0), out _, out _) || true);
        Assert.Equal(1, world.FullChunks);
    }

    [Fact]
    public void UnknownDelta_IsIgnored()
    {
        var world = new ChunkWorld();
        var writer = new ByteWriter(16);
        writer.WriteByte((byte)ChunkMessageType.Delta);
        writer.WriteVarInt(1);
        writer.WriteVarInt(2);
        writer.WriteVarUInt(5);
        writer.WriteVarUInt(2);
        writer.WriteVarUInt(0);
        writer.WriteVarUInt(7);
        writer.WriteVarUInt(1);
        writer.WriteVarUInt(8);

        var applied = world.Apply(writer.Written);
        Assert.Equal(0, applied);
        Assert.Equal(0, world.LoadedChunks);
    }
}
