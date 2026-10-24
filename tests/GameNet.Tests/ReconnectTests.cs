using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public class ReconnectTests
{
    [Fact]
    public async Task Session_ResumesWithinGrace_AndKeepsSubscriptions()
    {
        using var transport = new UdpServerTransport(0) { IdleTimeoutMs = 200 };
        var server = new GameServer(transport, new ServerOptions
        {
            AutoSubscribe = false,
            AoiRadius = 0,
            ResumeGraceSeconds = 5,
        });
        var players = server.RegisterTypedCollection<int, PlayerEntity>("players");
        players.Spawn(1, new PlayerEntity { Id = 1, Name = "alice", X = 3 });
        server.Start(autoTick: true, tickRate: 60);
        try
        {
        using var first = new RpcClientBuilder()
            .AddTypedCollection<int, PlayerView>("players")
            .Subscribe("players")
            .Build("127.0.0.1", transport.Port);
        var firstWorld = first.Typed<int, PlayerView>("players");
        var token = await first.WaitForSessionTokenAsync();
        var initialSession = server.Sessions.Values.Single().Id;
        await PumpUntilAsync(server, () => firstWorld.Count == 1);

        first.Dispose();
        await PumpUntilAsync(server, () => server.Sessions.Count == 0); // peer gone, session suspended

        using var second = new RpcClientBuilder()
            .AddTypedCollection<int, PlayerView>("players")
            .Subscribe("players")
            .WithResumeToken(token)
            .Build("127.0.0.1", transport.Port);

        Assert.True(await second.WaitForResumeAsync(8000));
        var secondWorld = second.Typed<int, PlayerView>("players");
        await PumpUntilAsync(server, () => secondWorld.Count == 1);
        Assert.Equal("alice", secondWorld.Entities[1].Name);
        Assert.Equal(initialSession, server.Sessions.Values.Single().Id);
        }
        finally { server.StopAutoTick(); }
    }

    [Fact]
    public async Task Client_ReconnectsWithBackoff_AndResumes()
    {
        using var transport = new UdpServerTransport(0) { IdleTimeoutMs = 200 };
        var server = new GameServer(transport, new ServerOptions
        {
            AutoSubscribe = false,
            AoiRadius = 0,
            ResumeGraceSeconds = 5,
        });
        var players = server.RegisterTypedCollection<int, PlayerEntity>("players");
        players.Spawn(1, new PlayerEntity { Id = 1, Name = "alice", X = 4 });
        server.Start(autoTick: true, tickRate: 60);
        try
        {
            using var client = new RpcClientBuilder()
                .AddTypedCollection<int, PlayerView>("players")
                .Subscribe("players")
                .Build("127.0.0.1", transport.Port);

            var world = client.Typed<int, PlayerView>("players");
            var token = await client.WaitForSessionTokenAsync();
            var sessionId = server.Sessions.Values.Single().Id;
            await PumpUntilAsync(server, () => world.Count == 1);

            Assert.False(string.IsNullOrEmpty(token));
            Assert.True(server.Sessions.Values.Any(s => s.ResumeToken == token));

            Assert.True(await client.ReconnectAsync("127.0.0.1", transport.Port));
            Assert.True(await client.WaitForResumeAsync(8000));
            await PumpUntilAsync(server, () => world.Count == 1);
            Assert.Equal(sessionId, server.Sessions.Values.Single().Id);
            Assert.False(string.IsNullOrEmpty(token));
        }
        finally { server.StopAutoTick(); }
    }
    [Fact]
    public async Task InvalidToken_IsRejected()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        server.Start(autoTick: true, tickRate: 60);
        try
        {
        using var client = new RpcClientBuilder()
            .WithResumeToken("not-a-real-token")
            .Build("127.0.0.1", transport.Port);

        Assert.False(await client.WaitForResumeAsync(8000));
        }
        finally { server.StopAutoTick(); }
    }

    private static async Task PumpUntilAsync(GameServer server, Func<bool> condition, int timeoutMs = 6000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail("condition not met within timeout");
    }
}
