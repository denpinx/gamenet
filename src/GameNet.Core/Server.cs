namespace GameNet.Core;

public sealed class ServerOptions
{
    public int TickRate { get; set; } = 30;
    public float AoiRadius { get; set; } = 60f;
    public float ArenaSize { get; set; } = 200f;
    public bool AutoSubscribe { get; set; } = true;
    public int MaxChunkPayload { get; set; } = SnapshotCodec.MaxChunkPayload;
}

public sealed class TickMetrics
{
    private readonly List<double> _samples = new();
    public long Ticks { get; private set; }
    public long BytesSent { get; private set; }

    public void Record(double milliseconds, long bytesSent)
    {
        Ticks++;
        BytesSent += bytesSent;
        _samples.Add(milliseconds);
    }

    public IReadOnlyList<double> Samples => _samples;

    public (double Avg, double P50, double P95, double P99, double Max) Summarize()
    {
        if (_samples.Count == 0) return (0, 0, 0, 0, 0);
        var sorted = _samples.OrderBy(x => x).ToArray();
        double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        return (sorted.Average(), At(0.50), At(0.95), At(0.99), sorted[^1]);
    }

    public void Reset()
    {
        _samples.Clear();
        Ticks = 0;
        BytesSent = 0;
    }
}

public sealed class GameServer
{
    private readonly IServerTransport _transport;
    private readonly Dictionary<int, Session> _sessions = new();
    private readonly Dictionary<byte, EntityCollection> _collections = new();
    private readonly Dictionary<string, byte> _collectionIds = new();
    private readonly Dictionary<string, EntityCollection> _collectionsByName = new(StringComparer.OrdinalIgnoreCase);
    private byte _nextCollectionId = 1;
    private uint _version;

    public GameServer(IServerTransport transport, ServerOptions? options = null)
    {
        _transport = transport;
        Options = options ?? new ServerOptions();
        Rpc = new RpcDispatcher();
        RpcGroups.CollectionResolver = ResolveCollection;
        _collections[0] = Entities;
    }

    public ServerOptions Options { get; }
    public EntityCollection Entities { get; } = new();
    public IReadOnlyDictionary<byte, EntityCollection> Collections => _collections;

    /// <summary>Registers a collection under a name; the server assigns the wire id and tells clients.</summary>
    public EntityCollection RegisterCollection(string name, EntityCollection collection)
    {
        if (_collectionIds.ContainsKey(name)) throw new InvalidOperationException($"collection '{name}' is already registered");
        var id = _nextCollectionId++;
        _collectionIds[name] = id;
        _collections[id] = collection;
        _collectionsByName[name] = collection;
        return collection;
    }

    /// <summary>Registers a collection created from the service provider (singleton lifetime).</summary>
    public EntityCollection RegisterCollection(string name, IServiceProvider services, Func<IServiceProvider, EntityCollection> factory)
        => RegisterCollection(name, factory(services));

    public EntityCollection GetCollection(string name) =>
        _collectionsByName.TryGetValue(name, out var collection)
            ? collection
            : throw new KeyNotFoundException($"collection '{name}' is not registered");

    public bool TryGetCollection(string name, out EntityCollection collection) =>
        _collectionsByName.TryGetValue(name, out collection!);

    private EntityCollection? ResolveCollection(string name) =>
        _collectionsByName.TryGetValue(name, out var collection) ? collection : null;

    /// <summary>Registers a collection under an explicit numeric id (tests and legacy callers).</summary>
    public EntityCollection RegisterCollection(byte id, EntityCollection collection)
    {
        _collections[id] = collection;
        return collection;
    }

    public IReadOnlyDictionary<string, byte> CollectionIds => _collectionIds;
    public TickMetrics Metrics { get; } = new();
    public RpcDispatcher Rpc { get; }
    public RpcGroupRegistry RpcGroups { get; } = new();
    public IReadOnlyDictionary<int, Session> Sessions => _sessions;

    /// <summary>Approval lambda for client-bound objects (requirement R2). Default: allow.</summary>
    public Func<Session, int, bool> Admission { get; set; } = (_, _) => true;

    public readonly List<Action<double>> TickHooks = new();

    public event Action<Session>? SessionJoined;
    public event Action<Session>? SessionLeft;

    private Thread? _autoTickThread;
    private volatile bool _autoTickRunning;

    public bool AutoTickRunning => _autoTickRunning;

    /// <summary>Raised when an auto-tick iteration throws; the loop keeps running.</summary>
    public event Action<Exception>? TickFailed;

    /// <summary>
    /// Starts the transport, and optionally a background fixed-rate tick loop.
    /// Default stays manual so engines/tests can drive <see cref="Tick"/> themselves.
    /// </summary>
    public void Start(bool autoTick = false, double tickRate = 30)
    {
        _transport.PeerConnected += OnPeerConnected;
        _transport.PeerDisconnected += OnPeerDisconnected;
        _transport.Start();
        if (autoTick) StartAutoTick(tickRate);
    }

    /// <summary>Runs <see cref="Tick"/> on a background thread at the given rate. Do not also call Tick manually.</summary>
    public void StartAutoTick(double tickRate = 30)
    {
        if (tickRate <= 0) throw new ArgumentOutOfRangeException(nameof(tickRate));
        if (_autoTickThread is not null) throw new InvalidOperationException("auto tick is already running");
        _autoTickRunning = true;
        var interval = TimeSpan.FromSeconds(1.0 / tickRate);
        _autoTickThread = new Thread(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var last = clock.Elapsed;
            while (_autoTickRunning)
            {
                var now = clock.Elapsed;
                var delta = (float)(now - last).TotalSeconds;
                last = now;
                try
                {
                    Tick(delta);
                }
                catch (Exception ex)
                {
                    TickFailed?.Invoke(ex);
                }

                var remaining = interval - (clock.Elapsed - now);
                if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
            }
        })
        {
            IsBackground = true,
            Name = "gamenet-autotick",
        };
        _autoTickThread.Start();
    }

    public void StopAutoTick()
    {
        _autoTickRunning = false;
        var thread = _autoTickThread;
        _autoTickThread = null;
        try { thread?.Join(1000); } catch { }
    }

    private void OnPeerConnected(IPeer peer)
    {
        var session = new Session(peer.Id, peer);
        lock (_sessions) _sessions[peer.Id] = session;
        if (Options.AutoSubscribe)
        {
            foreach (var id in _collections.Keys) session.Subscriptions.Add(id);
        }
        SendCollectionRegistry(peer);
        SessionJoined?.Invoke(session);
    }

    private void SendCollectionRegistry(IPeer peer)
    {
        var writer = new ByteWriter(64);
        writer.WriteByte((byte)MessageType.CollectionRegistry);
        writer.WriteVarUInt((uint)_collectionIds.Count);
        foreach (var pair in _collectionIds)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(pair.Key);
            writer.WriteVarUInt((uint)bytes.Length);
            writer.WriteBytes(bytes);
            writer.WriteByte(pair.Value);
        }
        peer.Send(1, writer.Written);
    }

    private void OnPeerDisconnected(IPeer peer)
    {
        Session? session;
        lock (_sessions) _sessions.Remove(peer.Id, out session);
        RpcGroups.RemoveSession(peer.Id);
        if (session is not null) SessionLeft?.Invoke(session);
    }

    /// <summary>One authoritative tick: drain input, integrate, replicate per-session deltas.</summary>
    public void Tick(float deltaSeconds)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _transport.Poll();

        List<Session> sessions;
        lock (_sessions) sessions = _sessions.Values.ToList();

        foreach (var session in sessions) ProcessInbound(session);

        Entities.Integrate(deltaSeconds, Options.ArenaSize);
        foreach (var collection in _collections.Values)
        {
            if (!ReferenceEquals(collection, Entities)) collection.Tick(deltaSeconds);
        }
        Entities.Tick(deltaSeconds);

        _version++;
        long bytes = 0;
        foreach (var session in sessions)
        {
            if (!session.Subscribed) continue;
            bytes += Replicate(session);
        }

        foreach (var hook in TickHooks) hook(deltaSeconds);

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Metrics.Record(elapsed, bytes);
    }

    private void ProcessInbound(Session session)
    {
        while (session.TryDequeue(out var packet))
        {
            var reader = new ByteReader(packet.Payload);
            if (reader.End) continue;
            var type = (MessageType)reader.ReadByte();
            switch (type)
            {
                case MessageType.Subscribe:
                    session.Subscriptions.Add(reader.End ? (byte)0 : reader.ReadByte());
                    break;
                case MessageType.Unsubscribe:
                    if (reader.End) session.Subscriptions.Clear();
                    else session.Subscriptions.Remove(reader.ReadByte());
                    session.ClearKnown();
                    break;

                case MessageType.Resync:
                    // Client lost its baseline (loss/reordering); drop Known so the next tick is a full snapshot.
                    session.ClearKnown();
                    break;

                case MessageType.Input:
                {
                    var vx = reader.ReadFloat();
                    var vy = reader.ReadFloat();
                    if (session.HasAvatar && Entities.TryGet(session.Avatar.Id, out var avatar))
                    {
                        avatar.VX = vx;
                        avatar.VY = vy;
                        Entities.Update(avatar.Id, avatar);
                        session.Avatar = avatar;
                    }
                    break;
                }

                case MessageType.BindRequest:
                {
                    var entityId = reader.ReadVarInt();
                    var approved = Admission(session, entityId);
                    var response = new ByteWriter(8);
                    response.WriteByte((byte)MessageType.BindResponse);
                    response.WriteBool(approved);
                    if (approved && Entities.TryGet(entityId, out var bound))
                    {
                        session.Avatar = bound;
                        session.HasAvatar = true;
                    }
                    session.Peer.Send(1, response.Written);
                    break;
                }

                case MessageType.RpcRequest:
                    HandleRpc(session, reader);
                    break;

                case MessageType.RpcCall:
                    HandleDynamicRpc(session, reader);
                    break;
            }
        }
    }

    private void HandleDynamicRpc(Session session, ByteReader reader)
    {
        var requestId = reader.ReadUInt();
        var group = RpcValueCodec.ReadString(ref reader);
        var method = RpcValueCodec.ReadString(ref reader);
        var count = (int)reader.ReadVarUInt();
        var args = new RpcValue[count];
        for (var i = 0; i < count; i++) args[i] = RpcValueCodec.Read(ref reader);

        RpcGroups.TryInvoke(session.Id, group, method, args, out var result, out var status);

        var valueWriter = new ByteWriter(64);
        RpcValueCodec.Write(ref valueWriter, result);

        var writer = new ByteWriter(96);
        writer.WriteByte((byte)MessageType.RpcCallResponse);
        writer.WriteUInt(requestId);
        writer.WriteByte((byte)status);
        writer.WriteVarUInt((uint)valueWriter.Length);
        writer.WriteBytes(valueWriter.Written);
        session.Peer.Send(1, writer.Written);
    }

    private void HandleRpc(Session session, ByteReader reader)
    {
        var requestId = reader.ReadUInt();
        var methodId = reader.ReadUShort();
        var payload = reader.ReadRemaining();
        var result = Rpc.Invoke(methodId, session, payload, out var status);

        var writer = new ByteWriter(64);
        writer.WriteByte((byte)MessageType.RpcResponse);
        writer.WriteUInt(requestId);
        writer.WriteByte((byte)status);
        writer.WriteVarUInt((uint)result.Length);
        writer.WriteBytes(result);
        session.Peer.Send(1, writer.Written);
    }

    private long Replicate(Session session)
    {
        long bytes = 0;
        foreach (var collectionId in session.Subscriptions.ToList())
        {
            if (_collections.TryGetValue(collectionId, out var collection))
                bytes += ReplicateCollection(session, collectionId, collection);
        }
        return bytes;
    }

    private long ReplicateCollection(Session session, byte collectionId, EntityCollection collection)
    {
        // Collection 0 carries avatars: with AOI enabled, wait for a bound avatar so the first
        // snapshot does not push the whole world. Other collections replicate immediately.
        if (collectionId == 0 && Options.AoiRadius > 0 && !session.HasAvatar) return 0;

        var viewer = session.Avatar;
        if (session.HasAvatar && Entities.TryGet(viewer.Id, out var current))
        {
            viewer = current;
            session.Avatar = current; // keep the AOI centre in sync with the simulated entity
        }

        var visible = new List<EntityState>(Math.Min(collection.Count, 512));
        var visibleIds = new HashSet<int>();

        foreach (var entity in collection.Items.Values)
        {
            if (!collection.IsVisible(session, entity)) continue;
            if (collectionId == 0 && session.HasAvatar && entity.Id == viewer.Id)
            {
                visible.Add(entity);
                visibleIds.Add(entity.Id);
                continue;
            }
            if (collectionId == 0 && Options.AoiRadius > 0 && session.HasAvatar)
            {
                var dx = entity.X - viewer.X;
                var dy = entity.Y - viewer.Y;
                if (dx * dx + dy * dy > Options.AoiRadius * Options.AoiRadius) continue;
            }
            visible.Add(entity);
            visibleIds.Add(entity.Id);
        }

        var known = session.KnownFor(collectionId);
        var deltas = new List<EntityDelta>(visible.Count + 8);
        foreach (var entity in visible)
        {
            if (!known.TryGetValue(entity.Id, out var previous))
            {
                deltas.Add(new EntityDelta { Id = entity.Id, Op = DeltaOp.Add, State = entity });
                continue;
            }

            var mask = EntityCodec.DiffMask(previous, entity);
            if (mask != 0)
                deltas.Add(new EntityDelta { Id = entity.Id, Op = DeltaOp.Update, State = entity, Mask = mask });
        }

        foreach (var knownId in known.Keys)
        {
            if (!visibleIds.Contains(knownId))
                deltas.Add(new EntityDelta { Id = knownId, Op = DeltaOp.Remove });
        }

        if (collectionId == 0) session.VisibleCount = visible.Count;

        if (deltas.Count == 0) return 0;

        var chunks = SnapshotCodec.Build(_version, deltas, (uint)Environment.TickCount64, collectionId);
        var delivered = true;
        foreach (var chunk in chunks)
        {
            if (!session.Peer.Send(0, chunk)) delivered = false;
        }

        // Only advance the baseline when every chunk was accepted by the transport. Otherwise the
        // next tick rebuilds the same delta from the old baseline instead of silently losing it.
        if (delivered)
        {
            known.Clear();
            foreach (var entity in visible) known[entity.Id] = entity;
            session.SnapshotsSent++;
        }

        return delivered ? chunks.Sum(c => (long)c.Length) : 0;
    }
}
