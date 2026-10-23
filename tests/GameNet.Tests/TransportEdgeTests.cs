using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

public class TransportEdgeTests
{
    [Fact]
    public void ImpairmentOptions_FlagsReflectConfiguration()
    {
        var clean = new ImpairmentOptions();
        Assert.True(clean.IsClean);
        Assert.False(clean.HasDelay);

        var impaired = new ImpairmentOptions { Loss = 0.1, LatencyMs = 20, JitterMs = 5, DuplicateRate = 0.01 };
        Assert.False(impaired.IsClean);
        Assert.True(impaired.HasDelay);
    }

    [Fact]
    public void ReliableChannel_SendsSkip_AfterRetryLimit()
    {
        var now = 1000L;
        var sent = new List<byte[]>();
        var channel = new ReliableChannel(0, (_, packet) => sent.Add(packet), _ => { }, () => now)
        {
            RetransmitMs = 10,
            MaxRetries = 0,
        };

        Assert.True(channel.Send(new byte[8]));
        Assert.Single(sent);

        now += 100;
        channel.Tick(now);

        Assert.Equal(2, sent.Count);
        Assert.Equal(2, sent[1][16]); // FlagSkip
        Assert.Equal(25, sent[1].Length); // header only
    }

    [Fact]
    public void ReliableChannel_TickWithoutTraffic_DoesNotSendAcks()
    {
        var sent = 0;
        var channel = new ReliableChannel(0, (_, _) => sent++, _ => { }, () => 1000);
        channel.Tick(1000);
        Assert.Equal(0, sent);
    }

    [Fact]
    public void UdpClient_ConnectFails_WhenNoServerListens()
    {
        using var client = new UdpClientTransport();
        Assert.False(client.Connect("127.0.0.1", 9, 300));
    }

    [Fact]
    public void Handshake_RejectsSchemaMismatch()
    {
        using var server = new UdpServerTransport(0, schemaHash: 0xAAAA_1111);
        server.Start();
        using var client = new UdpClientTransport(schemaHash: 0xBBBB_2222);
        Assert.False(client.Connect("127.0.0.1", server.Port, 1000));
        Assert.Empty(server.Peers);
    }

    [Fact]
    public void Handshake_AcceptsMatchingSchema()
    {
        using var server = new UdpServerTransport(0, schemaHash: 0xAAAA_1111);
        server.Start();
        using var client = new UdpClientTransport(schemaHash: 0xAAAA_1111);
        Assert.True(client.Connect("127.0.0.1", server.Port, 2000));
        Assert.Single(server.Peers);
    }
    [Fact]
    public async Task RpcClient_TimesOut_WhenPeerDoesNotAnswer()
    {
        // Transport-level handshake works, but nothing drains the inbound queue, so no response.
        using var server = new UdpServerTransport(0);
        server.Start();
        using var rpc = new RpcClient();
        Assert.True(rpc.Connect("127.0.0.1", server.Port));

        await Assert.ThrowsAsync<TimeoutException>(() => rpc.CallAsync<Empty, Empty>(9999, Empty.Value, 300));
    }
}
