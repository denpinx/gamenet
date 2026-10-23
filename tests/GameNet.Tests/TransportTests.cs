using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

/// <summary>In-memory lossy datagram link for exercising the reliable layer deterministically.</summary>
internal sealed class LossyLink
{
    private readonly Random _random;
    private readonly double _lossRate;
    private readonly int _latencyMs;
    private readonly List<(long Due, bool ToB, byte[] Packet)> _inflight = new();

    public LossyLink(int seed, double lossRate, int latencyMs = 5)
    {
        _random = new Random(seed);
        _lossRate = lossRate;
        _latencyMs = latencyMs;
    }

    public long Now { get; set; }
    public int Dropped { get; private set; }
    public int Delivered { get; private set; }

    public void Send(bool toB, byte[] packet)
    {
        if (_random.NextDouble() < _lossRate)
        {
            Dropped++;
            return;
        }
        _inflight.Add((Now + _latencyMs, toB, packet));
    }

    public void Pump(Action<byte[]> toB, Action<byte[]> toA)
    {
        for (var i = _inflight.Count - 1; i >= 0; i--)
        {
            var item = _inflight[i];
            if (item.Due > Now) continue;
            _inflight.RemoveAt(i);
            Delivered++;
            if (item.ToB) toB(item.Packet);
            else toA(item.Packet);
        }
    }
}

public class ReliableChannelTests
{
    [Fact]
    public void DeliversAllMessagesInOrder_Under20PercentLoss()
    {
        var link = new LossyLink(seed: 7, lossRate: 0.2);
        var received = new List<byte[]>();

        ReliableChannel? a = null;
        ReliableChannel? b = null;
        a = new ReliableChannel(0, (_, packet) => link.Send(true, packet), _ => { }, () => link.Now);
        b = new ReliableChannel(0, (_, packet) => link.Send(false, packet), message => received.Add(message.ToArray()), () => link.Now);

        var start = Environment.TickCount64;
        var sent = new List<byte[]>();
        var nextSendAt = 0L;

        for (var elapsed = 0L; elapsed < 15_000; elapsed += 5)
        {
            link.Now = start + elapsed;
            if (elapsed >= nextSendAt && sent.Count < 40)
            {
                var payload = new byte[100 + sent.Count * 10];
                Random.Shared.NextBytes(payload);
                sent.Add(payload);
                a.Send(payload);
                nextSendAt = elapsed + 25;

                if (sent.Count == 1)
                {
                    var big = new byte[4096];
                    Random.Shared.NextBytes(big);
                    sent.Add(big);
                    a.Send(big); // fragmentation path
                }
            }

            a.Tick(link.Now);
            b.Tick(link.Now);
            link.Pump(p => b.OnPacket(p), p => a.OnPacket(p));

            if (received.Count == sent.Count) break;
        }

        Assert.True(link.Dropped > 0, "test should actually exercise loss");
        Assert.InRange(a.CongestionWindow, 16, 512);
        Assert.Equal(sent.Count, received.Count);
        for (var i = 0; i < sent.Count; i++) Assert.Equal(sent[i], received[i]);
    }

    [Fact]
    public void DuplicatePackets_AreIgnored()
    {
        var link = new LossyLink(seed: 1, lossRate: 0);
        var received = new List<byte[]>();
        ReliableChannel? a = null;
        ReliableChannel? b = null;
        a = new ReliableChannel(0, (_, packet) => link.Send(true, packet), _ => { }, () => link.Now);
        b = new ReliableChannel(0, (_, packet) => link.Send(false, packet), message => received.Add(message.ToArray()), () => link.Now);

        link.Now = 1000;
        a.Send(new byte[] { 1, 2, 3 });

        // deliver the same datagram twice
        var duplicate = new List<byte[]>();
        var capture = new ReliableChannel(0, (_, p) => duplicate.Add(p), _ => { }, () => link.Now);
        _ = capture;

        var packets = new List<byte[]>();
        var a2 = new ReliableChannel(0, (_, p) => packets.Add(p), _ => { }, () => link.Now);
        a2.Send(new byte[] { 9 });
        foreach (var p in packets)
        {
            b.OnPacket(p);
            b.OnPacket(p);
        }

        Assert.Single(received);
    }

    [Fact]
    public void FragmentReassembly_HandlesLargeMessages()
    {
        var link = new LossyLink(seed: 3, lossRate: 0);
        var received = new List<byte[]>();
        ReliableChannel? b = null;
        var a = new ReliableChannel(0, (_, packet) => link.Send(true, packet), _ => { }, () => link.Now);
        b = new ReliableChannel(0, (_, packet) => link.Send(false, packet), message => received.Add(message.ToArray()), () => link.Now);

        var payload = new byte[8000];
        Random.Shared.NextBytes(payload);

        var start = Environment.TickCount64;
        link.Now = start;
        a.Send(payload);

        for (var elapsed = 0L; elapsed < 2000 && received.Count == 0; elapsed += 5)
        {
            link.Now = start + elapsed;
            a.Tick(link.Now);
            b.Tick(link.Now);
            link.Pump(p => b.OnPacket(p), p => a.OnPacket(p));
        }

        Assert.Single(received);
        Assert.Equal(payload, received[0]);
    }
}
