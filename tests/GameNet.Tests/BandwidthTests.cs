using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public class BandwidthTests
{
    [Fact]
    public async Task PerSessionCap_Throttles_ButConvergesWhenLifted()
    {
        using var transport = new UdpServerTransport(0);
        var options = new ServerOptions
        {
            AutoSubscribe = true,
            AoiRadius = 0,
            MaxBytesPerSecondPerSession = 2000,
        };
        var server = new GameServer(transport, options);
        for (var i = 0; i < 300; i++)
            server.Entities.Spawn(EntityState.Create(i, EntityKind.Prop, i, 0, vx: 1));
        server.Start();

        using var client = new UdpClientTransport();
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        var world = new ClientWorld();

        for (var i = 0; i < 30; i++)
        {
            server.Tick(1f / 30f);
            client.Poll();
            while (client.TryDequeue(out var packet))
                if (packet.Channel == 0 && SnapshotCodec.TryRead(packet.Payload, out var chunk))
                    world.OnChunk(chunk);
            await Task.Delay(5);
        }

        var session = server.Sessions.Values.Single();
        Assert.True(session.ThrottledTicks > 0, "bandwidth cap should throttle at least one tick");

        options.MaxBytesPerSecondPerSession = 0;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && world.EntityCount < 300)
        {
            server.Tick(1f / 30f);
            client.Poll();
            while (client.TryDequeue(out var packet))
                if (packet.Channel == 0 && SnapshotCodec.TryRead(packet.Payload, out var chunk))
                    world.OnChunk(chunk);
            await Task.Delay(5);
        }

        Assert.Equal(300, world.EntityCount);
    }
}
