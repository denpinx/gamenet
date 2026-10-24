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
    private readonly UdpClientTransport _transport;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<RpcReply>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _collections = new();
    private readonly ConcurrentDictionary<byte, ITypedWorld> _typedWorlds = new();
    private readonly List<(string Name, ITypedWorld World)> _pendingTyped = new();
    private readonly HashSet<string> _pendingSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private bool _built;
    private bool _manualTick;
    private Thread? _pump;
    private volatile bool _running;
    private uint _nextId;

    internal RpcClient(ImpairmentOptions? impairment = null) => _transport = new UdpClientTransport(impairment);

    public int SessionId => _transport.Id;
    public bool IsBuilt => _built;
    public bool ManualTick => _manualTick;

    /// <summary>Replicated world for channels subscribed via <see cref="Subscribe"/>.</summary>
    public ClientWorld World { get; } = new();

    public bool Subscribe(byte collection) => SendControl(MessageType.Subscribe, collection);

    public bool Unsubscribe(byte collection) => SendControl(MessageType.Unsubscribe, collection);

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
    internal TypedWorld<T> RegisterTypedCollection<T>(string name) where T : class, IEntityCodec<T>
    {
        if (_built) throw new InvalidOperationException("client is built; register collections before Build()");
        var world = new TypedWorld<T>(0);
        _pendingTyped.Add((name, world));
        return world;
    }

    public TypedWorld<T> Typed<T>(string name) where T : class, IEntityCodec<T>
    {
        if (_collections.TryGetValue(name, out var id) && _typedWorlds.TryGetValue(id, out var world))
            return (TypedWorld<T>)world;
        foreach (var pending in _pendingTyped)
        {
            if (string.Equals(pending.Name, name, StringComparison.OrdinalIgnoreCase))
                return (TypedWorld<T>)pending.World;
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
        _transport.Poll();
        Drain();
    }

    private void PumpLoop()
    {
        while (_running)
        {
            try
            {
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

    private void Drain()
    {
        while (_transport.TryDequeue(out var packet))
        {
            if (packet.Channel == 0)
            {
                if (packet.Payload.Length > 0 && packet.Payload[0] == (byte)MessageType.TypedSnapshot)
                {
                    if (TypedSnapshotCodec.TryRead(packet.Payload, out var typedChunk) &&
                        _typedWorlds.TryGetValue(typedChunk.Collection, out var typedWorld))
                        typedWorld.Apply(typedChunk);
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
                    _collections[name] = reader.ReadByte();
                }
                ResolvePending();
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
            var (name, world) = _pendingTyped[i];
            if (!_collections.TryGetValue(name, out var id)) continue;
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
