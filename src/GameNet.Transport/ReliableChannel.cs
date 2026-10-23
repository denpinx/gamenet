using System.Buffers.Binary;

namespace GameNet.Transport;

/// <summary>
/// Minimal reliable channel over datagrams: seq/ACK bitmap, retransmit, fragmentation and
/// optional in-order delivery. State replication uses <c>ordered: false</c> so one lost datagram
/// does not stall every later snapshot (no head-of-line blocking); RPC keeps ordering.
/// </summary>
public sealed class ReliableChannel
{
    private const int Mss = 1024;
    private const int HeaderSize = 25; // seq4 ack4 bits8 flags1 msgId4 frag2 count2
    private const byte FlagAckOnly = 1;
    private const byte FlagSkip = 2;
    private const byte FlagNack = 4;

    private readonly byte _channel;
    private readonly bool _ordered;
    private readonly Action<byte, byte[]> _send;
    private readonly Action<ReadOnlyMemory<byte>> _deliver;
    private readonly Func<long> _clock;
    private readonly object _gate = new();

    private readonly Dictionary<uint, PendingPacket> _pending = new();
    private readonly SortedDictionary<uint, Fragment> _reorder = new();
    private readonly Dictionary<uint, Assembly> _assembling = new();
    private readonly HashSet<uint> _received = new();

    private uint _nextSeq;
    private uint _nextMsgId;
    private uint _expected;
    private uint _maxAck;
    private uint _cumAck = uint.MaxValue;
    private uint _cumNext;
    private bool _hasReceived;
    private uint _lastAck = uint.MaxValue;
    private int _dupAcks;
    private long _lastAckMs;
    private long _lastNackMs;
    private uint _lastNackSeq = uint.MaxValue;
    private int _cwnd = 128;
    private int _sinceAck;

    private double _srtt;
    private double _rttvar;
    private bool _hasRtt;

    public ReliableChannel(byte channel, Action<byte, byte[]> send, Action<ReadOnlyMemory<byte>> deliver,
        Func<long>? clock = null, bool ordered = true)
    {
        _channel = channel;
        _send = send;
        _deliver = deliver;
        _clock = clock ?? (() => Environment.TickCount64);
        _ordered = ordered;
    }

    public int WindowSize { get; set; } = 1024;
    public int RetransmitMs { get; set; } = 150;
    public int MinRtoMs { get; set; } = 50;
    public int MaxRtoMs { get; set; } = 1000;
    public int MaxRetries { get; set; } = 20;
    public bool Ordered => _ordered;
    public long Retransmits { get; private set; }
    public long WindowBlocked { get; private set; }
    public long FastRetransmits { get; private set; }
    public long Delivered { get; private set; }
    public int CongestionWindow => _cwnd;

    public int CurrentRtoMs => _hasRtt
        ? (int)Math.Clamp(_srtt + 4 * _rttvar, MinRtoMs, MaxRtoMs)
        : RetransmitMs;

    public bool Send(ReadOnlySpan<byte> payload)
    {
        lock (_gate) return SendCore(payload);
    }

    private bool SendCore(ReadOnlySpan<byte> payload)
    {
        if (_pending.Count >= Math.Min(WindowSize, _cwnd))
        {
            WindowBlocked++;
            return false;
        }

        var msgId = _nextMsgId++;
        var fragCount = Math.Max(1, (payload.Length + Mss - 1) / Mss);
        for (var i = 0; i < fragCount; i++)
        {
            var offset = i * Mss;
            var length = Math.Min(Mss, payload.Length - offset);
            var seq = _nextSeq++;
            var packet = BuildPacket(seq, msgId, (ushort)i, (ushort)fragCount, payload.Slice(offset, length), false);
            var now = _clock();
            _pending[seq] = new PendingPacket(packet, now, 0, now);
            _send(_channel, packet);
        }
        return true;
    }

    public void OnPacket(ReadOnlySpan<byte> packet)
    {
        lock (_gate) OnPacketCore(packet);
    }

    private void OnPacketCore(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize) return;
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        var ack = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4));
        var ackBits = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(8));
        var flags = packet[16];

        Acknowledge(ack, ackBits, _clock());

        if ((flags & FlagAckOnly) != 0) return;

        if ((flags & FlagSkip) != 0)
        {
            // The peer gave up on this sequence; treat it as received so the cumulative ACK can move on.
            if (_received.Add(seq)) AdvanceCumulative();
            return;
        }

        if ((flags & FlagNack) != 0)
        {
            if (_pending.TryGetValue(seq, out var missing))
            {
                _send(_channel, missing.Packet);
                missing.LastSentMs = _clock();
                missing.Attempts++;
                FastRetransmits++;
            }
            return;
        }

        var msgId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(17));
        var fragIndex = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(21));
        var fragCount = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(23));
        var payload = packet[HeaderSize..].ToArray();

        if (_received.Contains(seq) || (_ordered && seq < _expected)) return;

        _received.Add(seq);
        if (!_hasReceived || seq > _maxAck)
        {
            _maxAck = seq;
            _hasReceived = true;
        }
        AdvanceCumulative();

        if (_ordered)
        {
            _reorder[seq] = new Fragment(payload, msgId, fragIndex, fragCount);
            while (_reorder.TryGetValue(_expected, out var fragment))
            {
                _reorder.Remove(_expected);
                _expected++;
                AcceptFragment(fragment.MsgId, fragment.Index, fragment.Count, fragment.Data);
            }

            // A gap appeared: ask for the missing sequence immediately instead of waiting for RTO.
            if (_reorder.Count > 0 && !_reorder.ContainsKey(_expected))
                MaybeNack(_expected);
        }
        else
        {
            AcceptFragment(msgId, fragIndex, fragCount, payload);
        }

        TrimReceived();

        var now = _clock();
        _sinceAck++;
        if (_sinceAck >= 8 || now - _lastAckMs >= 20)
        {
            _sinceAck = 0;
            SendAck(now);
        }
    }

    private void AdvanceCumulative()
    {
        while (_received.Contains(_cumNext))
        {
            _cumAck = _cumNext;
            _cumNext++;
        }
    }

    private void AcceptFragment(uint msgId, ushort index, ushort count, byte[] data)
    {
        if (!_assembling.TryGetValue(msgId, out var assembly))
        {
            if (_assembling.Count > 4096) _assembling.Clear();
            assembly = new Assembly(count);
            _assembling[msgId] = assembly;
        }

        if (assembly.Parts.Length != count || index >= count) return;
        if (assembly.Parts[index] != null) return;

        assembly.Parts[index] = data;
        assembly.Filled++;
        if (assembly.Filled < assembly.Count) return;

        var total = 0;
        foreach (var part in assembly.Parts) total += part.Length;
        var message = new byte[total];
        var offset = 0;
        foreach (var part in assembly.Parts)
        {
            part.CopyTo(message, offset);
            offset += part.Length;
        }

        _assembling.Remove(msgId);
        Delivered++;
        _deliver(message);
    }

    private void Acknowledge(uint ack, ulong bits, long nowMs)
    {
        if (ack == uint.MaxValue) return;

        List<uint>? acked = null;
        foreach (var seq in _pending.Keys)
        {
            if (seq <= ack) (acked ??= new List<uint>()).Add(seq);
        }
        if (acked is not null)
            foreach (var seq in acked) RemoveAcked(seq, nowMs);

        for (var i = 0; i < 64; i++)
        {
            if ((bits & (1UL << i)) == 0) continue;
            RemoveAcked(ack + 1 + (uint)i, nowMs);
        }

        if (ack == _lastAck)
        {
            _dupAcks++;
            if (_dupAcks == 3) FastRetransmitOldest(nowMs);
        }
        else
        {
            _lastAck = ack;
            _dupAcks = 0;
        }
    }

    private void FastRetransmitOldest(long nowMs)
    {
        if (_pending.Count == 0) return;
        var seq = _pending.Keys.Min();
        var pending = _pending[seq];
        _send(_channel, pending.Packet);
        pending.LastSentMs = nowMs;
        pending.Attempts++;
        FastRetransmits++;
    }

    private void RemoveAcked(uint seq, long nowMs)
    {
        if (!_pending.TryGetValue(seq, out var pending)) return;
        _pending.Remove(seq);
        if (Volatile.Read(ref _cwnd) < WindowSize) Interlocked.Increment(ref _cwnd);
        if (pending.Attempts != 0) return;

        var sample = nowMs - pending.FirstSentMs;
        if (sample <= 0 || sample > 5000) return;
        if (!_hasRtt)
        {
            _srtt = sample;
            _rttvar = sample / 2.0;
            _hasRtt = true;
        }
        else
        {
            _rttvar = 0.75 * _rttvar + 0.25 * Math.Abs(_srtt - sample);
            _srtt = 0.875 * _srtt + 0.125 * sample;
        }
    }

    private void TrimReceived()
    {
        if (_received.Count < 8192) return;
        var cutoff = _maxAck > 4096 ? _maxAck - 4096 : 0;
        _received.RemoveWhere(s => s < cutoff);
    }

    public void Tick(long nowMs)
    {
        lock (_gate) TickCore(nowMs);
    }

    private void TickCore(long nowMs)
    {
        List<uint>? expired = null;
        foreach (var (seq, pending) in _pending)
        {
            if (nowMs - pending.LastSentMs < CurrentRtoMs) continue;
            if (pending.Attempts >= MaxRetries)
            {
                (expired ??= new List<uint>()).Add(seq);
                continue;
            }
            _send(_channel, pending.Packet);
            pending.LastSentMs = nowMs;
            pending.Attempts++;
            Retransmits++;
            Interlocked.Exchange(ref _cwnd, Math.Max(32, _cwnd / 2));
        }

        if (expired is not null)
        {
            foreach (var seq in expired)
            {
                _send(_channel, BuildSkip(seq));
                _pending.Remove(seq);
            }
        }

        SendAck(nowMs);
    }

    private void MaybeNack(uint seq)
    {
        var now = _clock();
        if (seq == _lastNackSeq && now - _lastNackMs < 10) return;
        _lastNackSeq = seq;
        _lastNackMs = now;
        _send(_channel, BuildNack(seq));
    }

    private byte[] BuildNack(uint seq)
    {
        var packet = BuildPacket(seq, 0, 0, 0, ReadOnlySpan<byte>.Empty, false);
        packet[16] = FlagNack;
        return packet;
    }

    private byte[] BuildSkip(uint seq)
    {
        var packet = BuildPacket(seq, 0, 0, 0, ReadOnlySpan<byte>.Empty, false);
        packet[16] = FlagSkip;
        return packet;
    }

    private void SendAck(long nowMs)
    {
        if (!_hasReceived) return;
        _send(_channel, BuildPacket(0, 0, 0, 0, ReadOnlySpan<byte>.Empty, true));
        _lastAckMs = nowMs;
    }

    private byte[] BuildPacket(uint seq, uint msgId, ushort fragIndex, ushort fragCount, ReadOnlySpan<byte> payload, bool ackOnly)
    {
        var packet = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, seq);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), AckValue());
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), AckBits());
        packet[16] = ackOnly ? FlagAckOnly : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(17), msgId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(21), fragIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(23), fragCount);
        payload.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    // Ordered channels report the contiguous prefix; unordered ones acknowledge the highest
    // sequence seen so a permanently lost datagram cannot stall the sender forever.
    private uint AckValue() => _cumAck;

    private ulong AckBits()
    {
        var ack = AckValue();
        if (ack == uint.MaxValue) return 0;
        ulong bits = 0;
        for (var i = 0; i < 64; i++)
        {
            if (_received.Contains(ack + 1 + (uint)i)) bits |= 1UL << i;
        }
        return bits;
    }

    private readonly struct Fragment
    {
        public Fragment(byte[] data, uint msgId, ushort index, ushort count)
        {
            Data = data; MsgId = msgId; Index = index; Count = count;
        }

        public readonly byte[] Data;
        public readonly uint MsgId;
        public readonly ushort Index;
        public readonly ushort Count;
    }

    private sealed class Assembly
    {
        public Assembly(ushort count)
        {
            Count = count;
            Parts = new byte[count][];
        }

        public readonly ushort Count;
        public readonly byte[][] Parts;
        public int Filled;
    }

    private sealed class PendingPacket
    {
        public PendingPacket(byte[] packet, long lastSentMs, int attempts, long firstSentMs)
        {
            Packet = packet;
            LastSentMs = lastSentMs;
            Attempts = attempts;
            FirstSentMs = firstSentMs;
        }

        public byte[] Packet;
        public long LastSentMs;
        public int Attempts;
        public long FirstSentMs;
    }
}
