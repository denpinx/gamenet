using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public class CollectionTickTests
{
    [Fact]
    public void OnTick_RunsOncePerServerTick_WithDelta()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false });
        server.Start();
        var calls = 0;
        var total = 0.0;
        server.Entities.OnTick = dt =>
        {
            calls++;
            total += dt;
        };

        server.Tick(0.25f);
        server.Tick(0.25f);

        Assert.Equal(2, calls);
        Assert.Equal(0.5, total, 3);
        Assert.Equal(2, server.Entities.Ticks);
    }

    [Fact]
    public void OnTick_RunsForEveryRegisteredCollection()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false });
        server.Start();
        var worldTicks = 0;
        var chatTicks = 0;
        server.Entities.OnTick = _ => worldTicks++;
        var chat = server.RegisterCollection("chat", new EntityCollection());
        chat.OnTick = _ => chatTicks++;

        server.Tick(1f / 30f);
        server.Tick(1f / 30f);

        Assert.Equal(2, worldTicks);
        Assert.Equal(2, chatTicks);
        Assert.Equal(2, chat.Ticks);
    }

    [Fact]
    public void OnTick_RunsAfterIntegration()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, ArenaSize = 1000 });
        server.Start();
        server.Entities.Spawn(EntityState.Create(1, EntityKind.Prop, 0, 0, vx: 10, vy: 0));
        var seenX = -1f;
        server.Entities.OnTick = _ =>
        {
            server.Entities.TryGet(1, out var entity);
            seenX = entity.X;
        };

        server.Tick(0.5f);

        Assert.Equal(5f, seenX, 3); // integration moved it before the lambda ran
    }
}
