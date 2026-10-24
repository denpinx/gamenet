using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public class TickSyncTests
{
    [Fact]
    public async Task ClientClock_SnapsToServerTick()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions
        {
            AutoSubscribe = false,
            AoiRadius = 0,
            TickSyncIntervalTicks = 5,
            TickSyncTolerance = 2,
        });
        server.Start(autoTick: true, tickRate: 30);
        try
        {
            using var client = new RpcClientBuilder(tickRate: 30).Build("127.0.0.1", transport.Port);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && client.Clock.SyncCount < 2) await Task.Delay(20);

            Assert.True(client.Clock.SyncCount >= 2);
            Assert.True(Math.Abs((int)(client.Clock.CurrentTick - server.CurrentTick)) <= 5,
                $"client={client.Clock.CurrentTick} server={server.CurrentTick}");
        }
        finally { server.StopAutoTick(); }
    }

    [Fact]
    public async Task DriftBeyondTolerance_IsCorrected()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions
        {
            AutoSubscribe = false,
            AoiRadius = 0,
            TickSyncIntervalTicks = 5,
            TickSyncTolerance = 2,
        });
        server.Start(autoTick: true, tickRate: 30);
        try
        {
            using var client = new RpcClientBuilder(tickRate: 30).Build("127.0.0.1", transport.Port);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && client.Clock.SyncCount < 1) await Task.Delay(20);
            Assert.True(client.Clock.SyncCount >= 1);

            client.Clock.Advance(1.0); // +30 ticks of artificial drift
            var syncs = client.Clock.SyncCount;
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && client.Clock.SyncCount <= syncs) await Task.Delay(20);

            Assert.True(client.Clock.SyncCount > syncs);
            Assert.True(Math.Abs((int)(client.Clock.CurrentTick - server.CurrentTick)) <= 5,
                $"client={client.Clock.CurrentTick} server={server.CurrentTick}");
        }
        finally { server.StopAutoTick(); }
    }
}
