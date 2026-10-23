using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public class AoiTests
{
    private sealed class NullPeer : IPeer
    {
        public int Id => 1;
        public string Remote => "test";
        public bool Connected => true;
        public long BytesSent => 0;
        public long BytesReceived => 0;
        public bool Send(byte channel, ReadOnlySpan<byte> payload) => true;
        public void Close() { }
        public bool TryDequeue(out InboundPacket packet)
        {
            packet = default;
            return false;
        }
    }

    private static Session Viewer()
    {
        var session = new Session(1, new NullPeer());
        session.Avatar = EntityState.Create(1, EntityKind.Player, 0, 0);
        session.HasAvatar = true;
        return session;
    }

    [Fact]
    public void Circle_KeepsNearbyAndSelf_HidesFar()
    {
        var session = Viewer();
        var predicate = Aoi.Circle(10);

        Assert.True(predicate(session, EntityState.Create(1, EntityKind.Player, 5, 0)));   // self
        Assert.True(predicate(session, EntityState.Create(2, EntityKind.Prop, 9, 0)));     // near
        Assert.False(predicate(session, EntityState.Create(3, EntityKind.Prop, 50, 0)));   // far
    }

    [Fact]
    public void Circle_HidesEverything_UntilAvatarIsBound()
    {
        var session = new Session(1, new NullPeer());
        var predicate = Aoi.Circle(10);
        Assert.False(predicate(session, EntityState.Create(2, EntityKind.Prop, 1, 0)));
    }

    [Fact]
    public void All_ComposesFilters()
    {
        var session = Viewer();
        var visible = Aoi.All(
            Aoi.Circle(10),
            (_, entity) => entity.Team == 2);

        Assert.True(visible(session, EntityState.Create(2, EntityKind.Prop, 5, 0, team: 2)));
        Assert.False(visible(session, EntityState.Create(3, EntityKind.Prop, 5, 0, team: 1))); // wrong team
        Assert.False(visible(session, EntityState.Create(4, EntityKind.Prop, 50, 0, team: 2))); // too far
    }

    [Fact]
    public async Task CustomFilter_ReplacesTheDefaultCircle()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 60, AutoSubscribe = true });
        server.Entities.Spawn(EntityState.Create(1, EntityKind.Player, 0, 0));
        server.Entities.Spawn(EntityState.Create(2, EntityKind.Prop, 500, 0, team: 7)); // far outside the circle
        server.Entities.Filter = (_, entity) => entity.Team == 7 || entity.Id == 1;
        server.Start();

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        var bind = new ByteWriter(8);
        bind.WriteByte((byte)MessageType.BindRequest);
        bind.WriteVarInt(1);
        client.Send(1, bind.Written);

        var world = new ClientWorld();
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && world.EntityCount < 2)
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

        Assert.Equal(2, world.EntityCount); // the far entity is visible because the custom filter replaced AOI
    }

    [Fact]
    public void DefaultFilter_IsInstalled_WhenAoiRadiusIsPositive()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 25 });
        server.Start();
        Assert.NotNull(server.Entities.Filter);

        using var transport2 = new UdpServerTransport(0);
        var noAoi = new GameServer(transport2, new ServerOptions { AoiRadius = 0 });
        noAoi.Start();
        Assert.Null(noAoi.Entities.Filter);
    }
}
