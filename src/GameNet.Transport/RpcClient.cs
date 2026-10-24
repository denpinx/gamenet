using System.Collections.Concurrent;
using GameNet.Core;

namespace GameNet.Transport;

public readonly record struct RpcReply(RpcStatus Status, byte[] Payload);

/// <summary>Name-based RPC view: <c>client.Group("math").Call&lt;int&gt;("max", 1, 2)</c>.</summary>
public sealed class RpcGroupClient
{
    private readonly RpcClient _client;
    private readonly string _name;

    internal RpcGroupClient(RpcClient client, string name)
    {
        _client = client;
        _name = name;
    }

    public async Task<T> Call<T>(string method, params object?[] args)
    {
        var reply = await _client.CallDynamicAsync(_name, method, args, 5000, CancellationToken.None);
        if (reply.Status != RpcStatus.Ok)
            throw new RpcException(reply.Status, $"rpc {_name}.{method} failed: {reply.Status}");
        return Decode<T>(reply.Payload);
    }

    private static T Decode<T>(byte[] payload)
    {
        var reader = new ByteReader(payload);
        return RpcValueCodec.Read(ref reader).As<T>();
    }
}

/// <summary>Small request/response client over the reliable RPC channel (channel 1).</summary>
public sealed class RpcClient : IDisposable
{
    private UdpClientTransport _transport;
    private readonly ImpairmentOptions? _impairment;
    private readonly double _tickRate;
    private readonly HashSet<byte> _subscribedIds = new();
    private int _hostPort;
    private int _port;
    private int _timeoutMs = 5000;
    private bool _resubscribeOnRegistry;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<RpcReply>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _collections = new();
    private readonly ConcurrentDictionary<byte, ITypedWorld> _typedWorlds = new();
    private readonly List<(string Name, ITypedWorld World, string CodecId)> _pendingTyped = new();
    private readonly Dictionary<string, string> _collectionCodecs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private bool _built;
    private bool _manualTick;
    private TaskCompletionSource<string> _tokenTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _resumeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _sessionToken;
    private readonly TickClock _clock;
    private long _lastClockMs;
    private Thread? _pump;
    private volatile bool _running;
    private uint _nextId;

    internal RpcClient(ImpairmentOptions? impairment = null, double tickRate = 30)
    {
        _impairment = impairment;
        _tickRate = tickRate;
        _transport = new UdpClientTransport(impairment);
        _clock = new TickClock(tickRate);
        _lastClockMs = Environment.TickCount64;
    }

    public int SessionId => _transport.Id;
    public bool IsBuilt => _built;
    public bool ManualTick => _manualTick;
    public string? SessionToken => _sessionToken;
    public string? LastRegistryError { get; private set; }
    public TickClock Clock => _clock;

    /// <summary>Replicated world for channels subscribed via <see cref="Subscribe"/>.</summary>
    public ClientWorld World { get; } = new();

    public bool Subscribe(byte collection)
    {
        _subscribedIds.Add(collection);
        return SendControl(MessageType.Subscribe, collection);
    }

    public bool Unsubscribe(byte collection)
    {
        _subscribedIds.Remove(collection);
        return SendControl(MessageType.Unsubscribe, collection);
    }

    /// <summary>Subscribes by name. Before Build it is queued; after Build it resolves immediately.</summary>
    public bool Subscribe(string name, int timeoutMs = 2000)
    {
        if (!_built)
        {
            _pendingSubscriptions.Add(name);
            return true;
        }
        if (!TryResolve(name, timeoutMs, out var id)) return false;
        return Subscribe(id);
    }

    public bool Unsubscribe(string name)
    {
        if (!_collections.TryGetValue(name, out var id)) return false;
        return Unsubscribe(id);
    }

    public IReadOnlyDictionary<int, EntityState>? EntitiesFor(string name) =>
        _collections.TryGetValue(name, out var id) ? World.EntitiesFor(id) : null;

    /// <summary>
    /// Registers a typed world before Build. After Build this throws: collection types are frozen,
    /// just like subscriptions are resolved from the server registry at handshake time.
    /// </summary>
    internal TypedWorld<TKey, T> RegisterTypedCollection<TKey, T>(string name)
        where TKey : notnull
        where T : class, IEntityCodec<T>
    {
        if (_built) throw new InvalidOperationException("client is built; register collections before Build()");
        var world = new TypedWorld<TKey, T>(0, TypedKeyCodecs.Get<TKey>());
        _pendingTyped.Add((name, world, world.KeyCodecId));
        return world;
    }

    public TypedWorld<TKey, T> Typed<TKey, T>(string name)
        where TKey : notnull
        where T : class, IEntityCodec<T>
    {
        if (_collections.TryGetValue(name, out var id) && _typedWorlds.TryGetValue(id, out var world))
            return (TypedWorld<TKey, T>)world;
        foreach (var pending in _pendingTyped)
        {
            if (string.Equals(pending.Name, name, StringComparison.OrdinalIgnoreCase))
                return (TypedWorld<TKey, T>)pending.World;
        }
        throw new KeyNotFoundException($"typed collection '{name}' is not registered");
    }

    public bool TryGetCollectionId(string name, out byte id) => _collections.TryGetValue(name, out id);

    private bool TryResolve(string name, int timeoutMs, out byte id)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!_collections.TryGetValue(name, out id))
        {
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(2);
        }
        return true;
    }

    private bool SendControl(MessageType type, byte collection)
    {
        var writer = new ByteWriter(4);
        writer.WriteByte((byte)type);
        writer.WriteByte(collection);
        return _transport.Send(1, writer.Written);
    }

    private bool SendTickReport(uint tick)
    {
        var writer = new ByteWriter(8);
        writer.WriteByte((byte)MessageType.TickReport);
        writer.WriteUInt(tick);
        return _transport.Send(1, writer.Written);
    }

    /// <summary>Reconnect with exponential backoff, re-sending the resume token and subscriptions.</summary>
    public async Task<bool> ReconnectAsync(string host, int port, int attempts = 5, int initialDelayMs = 200, int maxDelayMs = 2000)
    {
        if (!_built) throw new InvalidOperationException("client is not built");
        _running = false;
        try { _pump?.Join(300); } catch { }
        _transport.Dispose();

        var delay = initialDelayMs;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            _transport = new UdpClientTransport(_impairment);
            if (_transport.Connect(host, port, _timeoutMs))
            {
                _tokenTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _resumeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _resubscribeOnRegistry = true;
                if (!_manualTick)
                {
                    _running = true;
                    _pump = new Thread(PumpLoop) { IsBackground = true, Name = "rpc-client-pump" };
                    _pump.Start();
                }
                if (_sessionToken is not null) SendResume(_sessionToken);
                return true;
            }

            _transport.Dispose();
            await Task.Delay(delay);
            delay = Math.Min(maxDelayMs, delay * 2);
        }
        return false;
    }

    internal bool SendResume(string token)
    {
        var writer = new ByteWriter(64);
        writer.WriteByte((byte)MessageType.Resume);
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        writer.WriteVarUInt((uint)bytes.Length);
        writer.WriteBytes(bytes);
        return _transport.Send(1, writer.Written);
    }

    public async Task<string> WaitForSessionTokenAsync(int timeoutMs = 5000)
    {
        var done = await Task.WhenAny(_tokenTcs.Task, Task.Delay(timeoutMs));
        if (done != _tokenTcs.Task) throw new TimeoutException("session token was not received");
        return await _tokenTcs.Task;
    }

    public async Task<bool> WaitForResumeAsync(int timeoutMs = 5000)
    {
        var done = await Task.WhenAny(_resumeTcs.Task, Task.Delay(timeoutMs));
        if (done != _resumeTcs.Task) throw new TimeoutException("resume result was not received");
        return await _resumeTcs.Task;
    }

    /// <summary>
    /// Connects and freezes the client configuration. Register typed collections and subscriptions
    /// before calling Build; afterwards only Subscribe/Unsubscribe and calls are allowed.
    /// </summary>
    internal bool Build(string host, int port, int timeoutMs = 5000, bool manualTick = false)
    {
        if (_built) throw new InvalidOperationException("client is already built");
        if (!_transport.Connect(host, port, timeoutMs)) return false;
        _built = true;
        _manualTick = manualTick;
        _hostPort = host.GetHashCode() ^ port;
        _port = port;
        _timeoutMs = timeoutMs;
        _lastClockMs = Environment.TickCount64;
        if (!manualTick)
        {
            _running = true;
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "rpc-client-pump" };
            _pump.Start();
        }
        return true;
    }

    /// <summary>Compatibility wrapper around Build with the background pump enabled.</summary>
    internal bool Connect(string host, int port, int timeoutMs = 5000) => Build(host, port, timeoutMs);

    /// <summary>Manual mode only: poll the socket, dispatch responses and apply received chunks.</summary>
    public void Tick()
    {
        if (!_manualTick) throw new InvalidOperationException("client is not in manual-tick mode");
        AdvanceClock();
        _transport.Poll();
        Drain();
    }

    private void PumpLoop()
    {
        while (_running)
        {
            try
            {
                AdvanceClock();
                _transport.Poll();
                Drain();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            Thread.Sleep(2);
        }
    }

    private void AdvanceClock()
    {
        var now = Environment.TickCount64;
        _clock.Advance((now - _lastClockMs) / 1000.0);
        _lastClockMs = now;
    }

    private void Drain()
    {
        while (_transport.TryDequeue(out var packet))
        {
            if (packet.Channel == 0)
            {
                if (packet.Payload.Length > 0 && packet.Payload[0] == (byte)MessageType.TypedSnapshot)
                {
                    var collectionId = packet.Payload[1];
                    if (_typedWorlds.TryGetValue(collectionId, out var typedWorld))
                        typedWorld.Apply(packet.Payload);
                }
                else if (SnapshotCodec.TryRead(packet.Payload, out var chunk))
                {
                    World.OnChunk(chunk);
                }
                continue;
            }
            if (packet.Channel != 1) continue;
            var reader = new ByteReader(packet.Payload);
            var messageType = (MessageType)reader.ReadByte();
            if (messageType == MessageType.CollectionRegistry)
            {
                var count = (int)reader.ReadVarUInt();
                for (var i = 0; i < count; i++)
                {
                    var name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)reader.ReadVarUInt()).ToArray());
                    var collectionId = reader.ReadByte();
                    var codecId = System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)reader.ReadVarUInt()).ToArray());
                    _collections[name] = collectionId;
                    _collectionCodecs[name] = codecId;
                }
                ResolvePending();
                continue;
            }
            if (messageType == MessageType.TickSync)
            {
                var serverTick = reader.ReadUInt();
                var tolerance = reader.ReadUShort();
                _ = reader.ReadUInt();
                var local = _clock.CurrentTick;
                _clock.Synchronize(serverTick, tolerance);
                SendTickReport(local);
                continue;
            }
            if (messageType == MessageType.SessionToken)
            {
                var tokenLength = (int)reader.ReadVarUInt();
                var token = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(tokenLength).ToArray());
                _sessionToken = token;
                _tokenTcs.TrySetResult(token);
                continue;
            }
            if (messageType == MessageType.ResumeResult)
            {
                var resumed = reader.ReadBool();
                _ = reader.ReadVarInt();
                _resumeTcs.TrySetResult(resumed);
                continue;
            }
            if (messageType == MessageType.RpcCallResponse)
            {
                var callId = reader.ReadUInt();
                var callStatus = (RpcStatus)reader.ReadByte();
                var valueLength = (int)reader.ReadVarUInt();
                var valuePayload = reader.ReadBytes(valueLength).ToArray();
                if (_pending.TryRemove(callId, out var callTcs)) callTcs.TrySetResult(new RpcReply(callStatus, valuePayload));
                continue;
            }
            if (messageType != MessageType.RpcResponse) continue;
            var id = reader.ReadUInt();
            var status = (RpcStatus)reader.ReadByte();
            var length = (int)reader.ReadVarUInt();
            var payload = reader.ReadBytes(length).ToArray();
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(new RpcReply(status, payload));
        }
    }

    private void ResolvePending()
    {
        for (var i = _pendingTyped.Count - 1; i >= 0; i--)
        {
            var (name, world, codecId) = _pendingTyped[i];
            if (!_collections.TryGetValue(name, out var id)) continue;
            if (_collectionCodecs.TryGetValue(name, out var serverCodec) &&
                !string.Equals(serverCodec, codecId, StringComparison.Ordinal))
            {
                LastRegistryError = $"collection {name} uses key codec {serverCodec}, client registered {codecId}";
                _pendingTyped.RemoveAt(i);
                continue;
            }
            world.Collection = id;
            _typedWorlds[id] = world;
            _pendingTyped.RemoveAt(i);
        }

        foreach (var name in _pendingSubscriptions.ToList())
        {
            if (!_collections.TryGetValue(name, out var id)) continue;
            SendControl(MessageType.Subscribe, id);
            _pendingSubscriptions.Remove(name);
        }

        if (_resubscribeOnRegistry)
        {
            foreach (var id in _subscribedIds) SendControl(MessageType.Subscribe, id);
            _resubscribeOnRegistry = false;
        }
    }

    public async Task<TResponse> CallAsync<TRequest, TResponse>(ushort methodId, TRequest request, int timeoutMs = 5000, CancellationToken ct = default)
        where TRequest : IWireCodec<TRequest>
        where TResponse : IWireCodec<TResponse>
    {
        var writer = new ByteWriter(64);
        TRequest.Write(writer, request);
        var reply = await CallAsync(methodId, writer.ToArray(), timeoutMs, ct);
        if (reply.Status != RpcStatus.Ok)
            throw new RpcException(reply.Status, $"rpc {methodId} failed: {reply.Status}");
        return Decode<TResponse>(reply.Payload);
    }

    private static TResponse Decode<TResponse>(byte[] payload) where TResponse : IWireCodec<TResponse>
    {
        var reader = new ByteReader(payload);
        return TResponse.Read(ref reader);
    }

    internal async Task<RpcReply> CallAsync(ushort methodId, byte[] payload, int timeoutMs = 5000, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var writer = new ByteWriter(32 + payload.Length);
        writer.WriteByte((byte)MessageType.RpcRequest);
        writer.WriteUInt(id);
        writer.WriteUShort(methodId);
        writer.WriteBytes(payload);

        var tcs = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        if (!_transport.Send(1, writer.Written))
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException("rpc send rejected (window full)");
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct));
        if (completed != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"rpc {methodId} timed out after {timeoutMs}ms");
        }
        return await tcs.Task;
    }

    public RpcGroupClient Group(string name) => new(this, name);

    internal async Task<RpcReply> CallDynamicAsync(string group, string method, object?[] args, int timeoutMs, CancellationToken ct)
    {
        var payload = new ByteWriter(128);
        RpcValueCodec.WriteString(ref payload, group);
        RpcValueCodec.WriteString(ref payload, method);
        payload.WriteVarUInt((uint)args.Length);
        foreach (var arg in args) RpcValueCodec.Write(ref payload, RpcValue.From(arg));

        var id = Interlocked.Increment(ref _nextId);
        var writer = new ByteWriter(32 + payload.Length);
        writer.WriteByte((byte)MessageType.RpcCall);
        writer.WriteUInt(id);
        writer.WriteBytes(payload.Written);

        var tcs = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        if (!_transport.Send(1, writer.Written))
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException("rpc send rejected (window full)");
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct));
        if (completed != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"rpc {group}.{method} timed out after {timeoutMs}ms");
        }
        return await tcs.Task;
    }

    public void Dispose()
    {
        _running = false;
        try { _pump?.Join(200); } catch { }
        _transport.Dispose();
    }
}
