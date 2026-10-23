using System.Collections.Concurrent;
using GameNet.Core;
using Xunit;

namespace GameNet.Tests;

internal sealed class FakePeer : IPeer
{
    private readonly ConcurrentQueue<InboundPacket> _inbound = new();
    public readonly List<(byte Channel, byte[] Payload)> Sent = new();
    private long _bytesSent;

    public FakePeer(int id) => Id = id;

    public int Id { get; }
    public string Remote => $"fake:{Id}";
    public bool Connected { get; private set; } = true;
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long BytesReceived { get; private set; }

    public bool Send(byte channel, ReadOnlySpan<byte> payload)
    {
        var copy = payload.ToArray();
        lock (Sent) Sent.Add((channel, copy));
        Interlocked.Add(ref _bytesSent, copy.Length);
        return true;
    }

    public void Close() => Connected = false;

    public bool TryDequeue(out InboundPacket packet) => _inbound.TryDequeue(out packet);

    public void Inject(byte channel, byte[] payload) => _inbound.Enqueue(new InboundPacket(channel, payload));

    public void ClearSent()
    {
        lock (Sent) Sent.Clear();
    }
}

internal sealed class FakeTransport : IServerTransport
{
    private readonly List<IPeer> _peers = new();

    public IReadOnlyCollection<IPeer> Peers => _peers;
    public event Action<IPeer>? PeerConnected;
    public event Action<IPeer>? PeerDisconnected;

    public FakePeer Connect()
    {
        var peer = new FakePeer(_peers.Count + 1);
        _peers.Add(peer);
        PeerConnected?.Invoke(peer);
        return peer;
    }

    public void Disconnect(FakePeer peer)
    {
        _peers.Remove(peer);
        PeerDisconnected?.Invoke(peer);
    }

    public void Start()
    {
    }

    public void Poll()
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}

public class ServerTests
{
    private static (GameServer server, FakeTransport transport, FakePeer peer) CreateServer(Action<ServerOptions>? configure = null)
    {
        var options = new ServerOptions();
        configure?.Invoke(options);
        var transport = new FakeTransport();
        var server = new GameServer(transport, options);
        server.Start();
        var peer = transport.Connect();
        return (server, transport, peer);
    }

    private static ClientWorld DrainSnapshots(FakePeer peer, ClientWorld world)
    {
        List<(byte Channel, byte[] Payload)> packets;
        lock (peer.Sent) packets = peer.Sent.ToList();
        foreach (var (channel, payload) in packets)
        {
            if (channel != 0) continue;
            if (SnapshotCodec.TryRead(payload, out var chunk)) world.OnChunk(chunk);
        }
        peer.ClearSent();
        return world;
    }

    [Fact]
    public void FirstTick_SendsFullSnapshot()
    {
        var (server, _, peer) = CreateServer(o => o.AoiRadius = 0);
        server.Entities.Spawn(EntityState.Create(1, EntityKind.Player, 10, 10));
        server.Entities.Spawn(EntityState.Create(2, EntityKind.Prop, 20, 20));

        server.Tick(0.033f);

        var world = DrainSnapshots(peer, new ClientWorld());
        Assert.Equal(2, world.EntityCount);
    }

    [Fact]
    public void Filter_HidesEntitiesPerSession()
    {
        var (server, _, peer) = CreateServer(o => o.AoiRadius = 0);
        server.Entities.Filter = (_, entity) => entity.Team == 0;
        server.Entities.Spawn(EntityState.Create(1, EntityKind.Player, 0, 0, team: 0));
        server.Entities.Spawn(EntityState.Create(2, EntityKind.Player, 0, 0, team: 1));

        server.Tick(0.033f);

        var world = DrainSnapshots(peer, new ClientWorld());
        Assert.Equal(1, world.EntityCount);
        Assert.True(world.Entities.ContainsKey(1));
    }

    [Fact]
    public void AoiRadius_LimitsVisibleEntities()
    {
        var (server, _, peer) = CreateServer(o => o.AoiRadius = 50);
        var session = server.Sessions[peer.Id];
        session.Avatar = EntityState.Create(100, EntityKind.Player, 0, 0);
        session.HasAvatar = true;

        server.Entities.Spawn(EntityState.Create(100, EntityKind.Player, 0, 0));
        server.Entities.Spawn(EntityState.Create(1, EntityKind.Prop, 10, 0));
        server.Entities.Spawn(EntityState.Create(2, EntityKind.Prop, 500, 500));

        server.Tick(0.033f);

        var world = DrainSnapshots(peer, new ClientWorld());
        Assert.Equal(2, world.EntityCount);
        Assert.True(world.Entities.ContainsKey(1));
        Assert.False(world.Entities.ContainsKey(2));
    }

    [Fact]
    public void SecondTick_SendsOnlyDeltas()
    {
        var (server, _, peer) = CreateServer(o => o.AoiRadius = 0);
        server.Entities.Spawn(EntityState.Create(1, EntityKind.Prop, 10, 10));
        server.Tick(0.033f);
        var world = DrainSnapshots(peer, new ClientWorld());

        var moved = EntityState.Create(1, EntityKind.Prop, 11, 10);
        server.Entities.Spawn(moved);
        server.Tick(0.033f);

        world = DrainSnapshots(peer, world);
        Assert.Single(world.Entities);
        Assert.Equal(11, world.Entities[1].X);
    }

    [Fact]
    public void UnchangedTick_SendsNothing()
    {
        var (server, _, peer) = CreateServer(o => o.AoiRadius = 0);
        server.Entities.Spawn(EntityState.Create(1, EntityKind.Prop, 10, 10, vx: 0, vy: 0));
        server.Tick(0.033f);
        DrainSnapshots(peer, new ClientWorld());

        server.Tick(0.033f);

        lock (peer.Sent) Assert.DoesNotContain(peer.Sent, p => p.Channel == 0); // registry is sent, snapshots are not
    }

    [Fact]
    public void Subscribe_OptInSessionGetsSnapshots()
    {
        var (server, _, peer) = CreateServer(o => { o.AutoSubscribe = false; o.AoiRadius = 0; });
        server.Entities.Spawn(EntityState.Create(1, EntityKind.Prop, 10, 10));
        server.Tick(0.033f);
        lock (peer.Sent) Assert.DoesNotContain(peer.Sent, p => p.Channel == 0); // registry is sent, snapshots are not

        var subscribe = new ByteWriter(1);
        subscribe.WriteByte((byte)MessageType.Subscribe);
        peer.Inject(0, subscribe.ToArray());

        server.Tick(0.033f);
        var world = DrainSnapshots(peer, new ClientWorld());
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void TickHooks_RunEveryTick()
    {
        var (server, _, _) = CreateServer();
        var ticks = 0;
        var lastDelta = 0d;
        server.TickHooks.Add(dt => { ticks++; lastDelta = dt; });

        server.Tick(1f / 30f);
        server.Tick(1f / 30f);

        Assert.Equal(2, ticks);
        Assert.Equal(1f / 30f, lastDelta, 5);
    }

    [Fact]
    public void Rpc_DispatchesAndResponds()
    {
        var (server, _, peer) = CreateServer();
        server.Rpc.Register(RpcMethods.Ping, (_, payload) => payload);

        var request = new ByteWriter(32);
        request.WriteByte((byte)MessageType.RpcRequest);
        request.WriteUInt(7);
        request.WriteUShort(RpcMethods.Ping);
        request.WriteBytes(new byte[] { 1, 2, 3 });
        peer.Inject(1, request.ToArray());

        server.Tick(0.033f);

        (byte Channel, byte[] Payload) response;
        lock (peer.Sent) response = peer.Sent.Single(p => p.Channel == 1 && p.Payload.Length > 0 && p.Payload[0] == (byte)MessageType.RpcResponse);

        var reader = new ByteReader(response.Payload);
        Assert.Equal((byte)MessageType.RpcResponse, reader.ReadByte());
        Assert.Equal(7u, reader.ReadUInt());
        Assert.Equal((byte)RpcStatus.Ok, reader.ReadByte());
        var length = (int)reader.ReadVarUInt();
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadBytes(length).ToArray());
    }

    [Fact]
    public void Admission_RejectsBind()
    {
        var (server, _, peer) = CreateServer();
        server.Admission = (_, _) => false;
        server.Entities.Spawn(EntityState.Create(1, EntityKind.Player, 0, 0));

        var bind = new ByteWriter(8);
        bind.WriteByte((byte)MessageType.BindRequest);
        bind.WriteVarInt(1);
        peer.Inject(1, bind.ToArray());

        server.Tick(0.033f);

        lock (peer.Sent)
        {
            var response = peer.Sent.Single(p => p.Channel == 1 && p.Payload[0] == (byte)MessageType.BindResponse);
            var reader = new ByteReader(response.Payload);
            Assert.Equal((byte)MessageType.BindResponse, reader.ReadByte());
            Assert.False(reader.ReadBool());
        }
        Assert.False(server.Sessions[peer.Id].HasAvatar);
    }
}
