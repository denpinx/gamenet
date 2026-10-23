using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GameNet.Core;

namespace GameNet.Transport;

/// <summary>Datagram impairment applied on send: loss, fixed latency, jitter and duplication.</summary>
public sealed class ImpairmentOptions
{
    public double Loss { get; set; }
    public int LatencyMs { get; set; }
    public int JitterMs { get; set; }
    public double DuplicateRate { get; set; }

    /// <summary>Send each state-channel datagram N times (FEC) to trade bandwidth for lower tail latency.</summary>
    public int StateRedundancy { get; set; } = 1;

    public bool HasDelay => LatencyMs > 0 || JitterMs > 0;
    public bool IsClean => Loss <= 0 && LatencyMs <= 0 && JitterMs <= 0 && DuplicateRate <= 0;
}

/// <summary>
/// Datagram frame: magic(4) type(1) peerId(4) channel(1) length(2) payload.
/// type 1 = hello, 2 = welcome, 3 = data.
/// </summary>
public sealed class UdpPeer : IPeer
{
    public const int FrameOverhead = 12;
    private const byte TypeHello = 1;
    private const byte TypeReject = 4;
    private const byte TypeWelcome = 2;
    private const byte TypeData = 3;
    private static readonly byte[] Magic = { (byte)'G', (byte)'N', (byte)'P', (byte)'1' };

    private readonly Action<byte[]> _sendDatagram;
    private readonly ConcurrentQueue<InboundPacket> _inbound = new();
    private readonly ReliableChannel _reliable0;
    private readonly ReliableChannel _reliable1;
    private long _bytesSent;
    private long _bytesReceived;
    private long _datagramsSent;
    private long _datagramsDropped;
    private readonly ImpairmentOptions _impairment;
    private readonly List<(long Due, byte[] Bytes)> _delayed = new();
    private readonly object _delayGate = new();
    private Thread? _delayThread;
    private volatile bool _connected = true;

    public UdpPeer(int id, Action<byte[]> sendDatagram, ImpairmentOptions? impairment = null, bool unorderedState = true)
    {
        Id = id;
        _sendDatagram = sendDatagram;
        _impairment = impairment ?? new ImpairmentOptions();
        _reliable0 = new ReliableChannel(0, SendReliable, Deliver, ordered: !unorderedState);
        _reliable1 = new ReliableChannel(1, SendReliable, Deliver, ordered: true);
    }

    public int Id { get; private set; }
    public string Remote { get; init; } = string.Empty;
    public bool Connected => _connected;
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public long Retransmits => _reliable0.Retransmits + _reliable1.Retransmits;
    public long WindowBlocked => _reliable0.WindowBlocked + _reliable1.WindowBlocked;
    public long DatagramsSent => Interlocked.Read(ref _datagramsSent);
    public long DatagramsDropped => Interlocked.Read(ref _datagramsDropped);
    public long FastRetransmits => _reliable0.FastRetransmits + _reliable1.FastRetransmits;
    public long LastSeenMs { get; private set; } = Environment.TickCount64;

    internal void AssignId(int id) => Id = id;

    private void SendReliable(byte channel, byte[] packet)
    {
        var frame = Frame(TypeData, Id, channel, packet);
        var copies = channel == 0 ? Math.Max(1, _impairment.StateRedundancy) : 1;
        for (var i = 0; i < copies; i++) SendRaw(frame);
    }

    private void SendRaw(byte[] bytes)
    {
        if (_impairment.Loss > 0 && Random.Shared.NextDouble() < _impairment.Loss)
        {
            Interlocked.Increment(ref _datagramsDropped);
            return;
        }

        var copies = _impairment.DuplicateRate > 0 && Random.Shared.NextDouble() < _impairment.DuplicateRate ? 2 : 1;
        for (var copy = 0; copy < copies; copy++)
        {
            var delay = _impairment.LatencyMs;
            if (_impairment.JitterMs > 0) delay += Random.Shared.Next(0, _impairment.JitterMs + 1);
            if (copy > 0) delay += 1;
            if (delay <= 0) SendNow(bytes);
            else EnqueueDelayed(bytes, delay);
        }
    }

    private void SendNow(byte[] bytes)
    {
        Interlocked.Increment(ref _datagramsSent);
        _sendDatagram(bytes);
    }

    private void EnqueueDelayed(byte[] bytes, int delayMs)
    {
        lock (_delayGate)
        {
            _delayed.Add((Environment.TickCount64 + delayMs, bytes));
            _delayThread ??= StartDelayThread();
        }
    }

    private Thread StartDelayThread()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                List<byte[]> due = new();
                lock (_delayGate)
                {
                    var now = Environment.TickCount64;
                    for (var i = _delayed.Count - 1; i >= 0; i--)
                    {
                        if (_delayed[i].Due > now) continue;
                        due.Add(_delayed[i].Bytes);
                        _delayed.RemoveAt(i);
                    }
                    if (!_connected && _delayed.Count == 0) break;
                }
                foreach (var bytes in due) SendNow(bytes);
                Thread.Sleep(1);
            }
        })
        { IsBackground = true, Name = "udp-impairment" };
        thread.Start();
        return thread;
    }

    private void Deliver(ReadOnlyMemory<byte> message)
    {
        Interlocked.Add(ref _bytesReceived, message.Length);
        _inbound.Enqueue(new InboundPacket(LastChannel, message.ToArray()));
    }

    /// <summary>Set right before delivery by the reliable channel callback.</summary>
    internal byte LastChannel;

    public bool Send(byte channel, ReadOnlySpan<byte> payload)
    {
        Interlocked.Add(ref _bytesSent, payload.Length);
        if (channel == 0) return _reliable0.Send(payload);
        if (channel == 1) return _reliable1.Send(payload);
        SendRaw(Frame(TypeData, Id, channel, payload.ToArray()));
        return true;
    }

    public void OnDatagram(byte channel, ReadOnlySpan<byte> payload)
    {
        LastSeenMs = Environment.TickCount64;
        if (channel <= 1)
        {
            LastChannel = channel;
            if (channel == 0) _reliable0.OnPacket(payload);
            else _reliable1.OnPacket(payload);
        }
        else
        {
            Interlocked.Add(ref _bytesReceived, payload.Length);
            _inbound.Enqueue(new InboundPacket(channel, payload.ToArray()));
        }
    }

    public void Poll(long nowMs)
    {
        _reliable0.Tick(nowMs);
        _reliable1.Tick(nowMs);
    }

    public bool TryDequeue(out InboundPacket packet) => _inbound.TryDequeue(out packet);

    public void Close() => _connected = false;

    public static byte[] Frame(byte type, int peerId, byte channel, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[FrameOverhead + payload.Length];
        Magic.CopyTo(frame, 0);
        frame[4] = type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(5), peerId);
        frame[9] = channel;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(FrameOverhead));
        return frame;
    }

    public static bool TryParse(ReadOnlySpan<byte> frame, out byte type, out int peerId, out byte channel,
        out ReadOnlySpan<byte> payload)
    {
        type = 0;
        peerId = 0;
        channel = 0;
        payload = default;
        if (frame.Length < FrameOverhead) return false;
        if (frame[0] != Magic[0] || frame[1] != Magic[1] || frame[2] != Magic[2] || frame[3] != Magic[3]) return false;

        type = frame[4];
        peerId = BinaryPrimitives.ReadInt32LittleEndian(frame[5..9]);
        channel = frame[9];
        var length = BinaryPrimitives.ReadUInt16LittleEndian(frame[10..12]);
        if (frame.Length < FrameOverhead + length) return false;
        payload = frame.Slice(FrameOverhead, length);
        return true;
    }

    internal static byte[] HelloFrame(uint schemaHash) => Frame(TypeHello, 0, 0, BitConverter.GetBytes(schemaHash));
    internal static byte[] WelcomeFrame(int peerId, uint schemaHash) => Frame(TypeWelcome, peerId, 0, BitConverter.GetBytes(schemaHash));
    internal static byte[] RejectFrame(uint schemaHash) => Frame(TypeReject, 0, 0, BitConverter.GetBytes(schemaHash));
}

public sealed class UdpServerTransport : IServerTransport
{
    private readonly Socket _socket;
    private readonly ConcurrentDictionary<int, UdpPeer> _peers = new();
    private readonly Thread _receiveThread;
    private volatile bool _running = true;
    private int _nextPeerId;
    private readonly ImpairmentOptions _impairment;
    private readonly bool _unorderedState;
    private readonly uint _schemaHash;

    public UdpServerTransport(int port, ImpairmentOptions? impairment = null, bool unorderedState = true, uint schemaHash = 0)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 4 * 1024 * 1024,
            SendBufferSize = 4 * 1024 * 1024,
        };
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        _socket.ReceiveTimeout = 200;
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _impairment = impairment ?? new ImpairmentOptions();
        _unorderedState = unorderedState;
        _schemaHash = schemaHash;
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "udp-server-rx" };
    }

    public int Port { get; }
    public int IdleTimeoutMs { get; set; } = 10_000;
    public IReadOnlyCollection<IPeer> Peers => _peers.Values.Cast<IPeer>().ToList();

    public event Action<IPeer>? PeerConnected;
    public event Action<IPeer>? PeerDisconnected;

    public void Start() => _receiveThread.Start();

    private void ReceiveLoop()
    {
        var buffer = new byte[65536];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                var received = _socket.ReceiveFrom(buffer, ref remote);
                if (received <= 0) continue;
                var span = buffer.AsSpan(0, received);
                if (!UdpPeer.TryParse(span, out var type, out var peerId, out var channel, out var payload)) continue;

                if (type == 1) // hello
                {
                    var endpoint = new IPEndPoint(((IPEndPoint)remote).Address, ((IPEndPoint)remote).Port);
                    var clientHash = payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0u;
                    if (_schemaHash != 0 && clientHash != 0 && clientHash != _schemaHash)
                    {
                        SendTo(UdpPeer.RejectFrame(_schemaHash), endpoint);
                        continue;
                    }
                    var id = Interlocked.Increment(ref _nextPeerId);
                    var peer = new UdpPeer(id, bytes => SendTo(bytes, endpoint), _impairment, _unorderedState)
                    {
                        Remote = endpoint.ToString(),
                    };
                    _peers[id] = peer;
                    SendTo(UdpPeer.WelcomeFrame(id, _schemaHash), endpoint);
                    PeerConnected?.Invoke(peer);
                }
                else if (type == 3 && _peers.TryGetValue(peerId, out var peer))
                {
                    peer.OnDatagram(channel, payload);
                }
            }
            catch (SocketException)
            {
                // timeout / transient — keep looping
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private void SendTo(byte[] bytes, IPEndPoint endpoint)
    {
        try
        {
            _socket.SendTo(bytes, endpoint);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Poll()
    {
        var now = Environment.TickCount64;
        foreach (var peer in _peers.Values)
        {
            peer.Poll(now);
            if (IdleTimeoutMs > 0 && now - peer.LastSeenMs > IdleTimeoutMs)
            {
                if (_peers.TryRemove(peer.Id, out _))
                {
                    peer.Close();
                    PeerDisconnected?.Invoke(peer);
                }
            }
        }
    }

    public void Stop()
    {
        _running = false;
        _receiveThread.Join(500);
    }

    public void Dispose()
    {
        Stop();
        _socket.Dispose();
    }
}

/// <summary>Client side: one UDP socket, one peer, two reliable channels + one raw channel.</summary>
public sealed class UdpClientTransport : IPeer, IDisposable
{
    private readonly Socket _socket;
    private readonly UdpPeer _peer;
    private Thread? _receiveThread;
    private volatile bool _running;
    private readonly uint _schemaHash;

    public UdpClientTransport(ImpairmentOptions? impairment = null, bool unorderedState = true, uint schemaHash = 0)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 4 * 1024 * 1024,
            SendBufferSize = 4 * 1024 * 1024,
            ReceiveTimeout = 100,
        };
        _peer = new UdpPeer(0, SendToServer, impairment, unorderedState);
        _schemaHash = schemaHash;
    }

    public int Id => _peer.Id;
    public string Remote { get; private set; } = string.Empty;
    public bool Connected => _peer.Connected;
    public long BytesSent => _peer.BytesSent;
    public long BytesReceived => _peer.BytesReceived;
    public long Retransmits => _peer.Retransmits;

    public bool Connect(string host, int port, int timeoutMs = 5000)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(host), port);
        _socket.Connect(endpoint);
        Remote = endpoint.ToString();

        var buffer = new byte[2048];
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            SendToServer(UdpPeer.HelloFrame(_schemaHash));
            try
            {
                var received = _socket.Receive(buffer);
                if (received > 0 &&
                    UdpPeer.TryParse(buffer.AsSpan(0, received), out var type, out var peerId, out _, out _) &&
                    type == 4)
                {
                    return false; // schema mismatch, server rejected the handshake
                }
                if (received > 0 &&
                    UdpPeer.TryParse(buffer.AsSpan(0, received), out type, out peerId, out _, out _) &&
                    type == 2)
                {
                    _peer.AssignId(peerId);
                    _running = true;
                    _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "udp-client-rx" };
                    _receiveThread.Start();
                    return true;
                }
            }
            catch (SocketException)
            {
                // receive timeout, retry hello
            }
        }
        return false;
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[65536];
        while (_running)
        {
            try
            {
                var received = _socket.Receive(buffer);
                if (received <= 0) continue;
                if (UdpPeer.TryParse(buffer.AsSpan(0, received), out var type, out _, out var channel, out var payload) &&
                    type == 3)
                {
                    _peer.OnDatagram(channel, payload);
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private void SendToServer(byte[] bytes)
    {
        try
        {
            _socket.Send(bytes);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public bool Send(byte channel, ReadOnlySpan<byte> payload) => _peer.Send(channel, payload);

    public bool TryDequeue(out InboundPacket packet) => _peer.TryDequeue(out packet);

    public void Poll() => _peer.Poll(Environment.TickCount64);

    public void Close() => _peer.Close();

    public void Dispose()
    {
        _running = false;
        _socket.Dispose();
        _receiveThread?.Join(200);
    }
}
