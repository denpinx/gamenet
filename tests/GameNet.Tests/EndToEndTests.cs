using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public class EndToEndTests
{
    [Fact]
    public async Task UdpLoopback_SnapshotReachesClient()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0 });
        server.Start();

        server.Entities.Spawn(EntityState.Create(1, EntityKind.Player, 1, 2));
        server.Entities.Spawn(EntityState.Create(2, EntityKind.Prop, 3, 4));

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port, 5000));

        var world = new ClientWorld();
        var deadline = DateTime.UtcNow.AddSeconds(8);
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

        Assert.Equal(2, world.EntityCount);
        Assert.Equal(1, world.Entities[1].X);
        Assert.True(client.BytesReceived > 0);
        Assert.True(client.Id > 0);
    }

    [Fact]
    public async Task UdpLoopback_RpcRoundTrip()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport);
        server.Rpc.Register(RpcMethods.CreateHero, (_, payload) =>
        {
            var request = CreateHeroRequest.Read(new ByteReader(payload));
            var response = new ByteWriter(32);
            new CreateHeroResponse(42, 1, 2).Write(response);
            return response.ToArray();
        });
        server.Start();

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port, 5000));

        var requestWriter = new ByteWriter(64);
        requestWriter.WriteByte((byte)MessageType.RpcRequest);
        requestWriter.WriteUInt(99);
        requestWriter.WriteUShort(RpcMethods.CreateHero);
        var body = new ByteWriter(64);
        new CreateHeroRequest("mage", 1).Write(body);
        requestWriter.WriteBytes(body.Written);
        client.Send(1, requestWriter.Written);

        CreateHeroResponse? result = null;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && result is null)
        {
            server.Tick(1f / 30f);
            client.Poll();
            while (client.TryDequeue(out var packet))
            {
                if (packet.Channel != 1) continue;
                if (!TryParseRpcResponse(packet.Payload, out var requestId, out var status, out var bodyBytes)) continue;
                if (requestId == 99 && status == 0)
                    result = CreateHeroResponse.Read(new ByteReader(bodyBytes));
            }
            await Task.Delay(5);
        }

        Assert.NotNull(result);
        Assert.Equal(42, result!.EntityId);
    }

    [Fact]
    public async Task UdpLoopback_IdlePeer_IsDisconnected()
    {
        using var transport = new UdpServerTransport(0) { IdleTimeoutMs = 60 };
        var disconnected = false;
        transport.PeerDisconnected += _ => disconnected = true;
        transport.Start();

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port, 5000));

        await Task.Delay(150);
        transport.Poll();

        Assert.True(disconnected);
        Assert.Empty(transport.Peers);
    }

    [Fact]
    public void ReliableChannel_Rto_AdaptsAfterAcks()
    {
        var link = new LossyLink(seed: 11, lossRate: 0);
        var received = new List<byte[]>();
        ReliableChannel? a = null;
        ReliableChannel? b = null;
        a = new ReliableChannel(0, (_, packet) => link.Send(true, packet), _ => { }, () => link.Now);
        b = new ReliableChannel(0, (_, packet) => link.Send(false, packet), message => received.Add(message.ToArray()), () => link.Now);

        var start = Environment.TickCount64;
        link.Now = start;
        a.Send(new byte[] { 1, 2, 3 });

        for (var elapsed = 0L; elapsed < 200; elapsed += 5)
        {
            link.Now = start + elapsed;
            a.Tick(link.Now);
            b.Tick(link.Now);
            link.Pump(p => b.OnPacket(p), p => a.OnPacket(p));
        }

        Assert.Single(received);
        Assert.True(a.CurrentRtoMs < a.RetransmitMs, $"rto should adapt below the default: {a.CurrentRtoMs}");
    }

    private static bool TryParseRpcResponse(byte[] payload, out uint requestId, out byte status, out byte[] bodyBytes)
    {
        requestId = 0;
        status = 0;
        bodyBytes = Array.Empty<byte>();
        var reader = new ByteReader(payload);
        if (reader.ReadByte() != (byte)MessageType.RpcResponse) return false;
        requestId = reader.ReadUInt();
        status = reader.ReadByte();
        var length = (int)reader.ReadVarUInt();
        bodyBytes = reader.ReadBytes(length).ToArray();
        return true;
    }
}
