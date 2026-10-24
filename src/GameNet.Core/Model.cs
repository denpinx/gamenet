using System.Collections.Concurrent;

namespace GameNet.Core;

public readonly record struct InboundPacket(byte Channel, byte[] Payload);

public interface IPeer
{
    int Id { get; }
    string Remote { get; }
    bool Connected { get; }
    long BytesSent { get; }
    long BytesReceived { get; }
    bool Send(byte channel, ReadOnlySpan<byte> payload);
    void Close();
    bool TryDequeue(out InboundPacket packet);
}

public interface IServerTransport : IDisposable
{
    void Start();
    void Poll();
    void Stop();
    IReadOnlyCollection<IPeer> Peers { get; }
    event Action<IPeer>? PeerConnected;
    event Action<IPeer>? PeerDisconnected;
}

/// <summary>Server-side view of a connected client.</summary>
public sealed class Session
{
    public Session(int id, IPeer peer)
    {
        Id = id;
        Peer = peer;
    }

    public int Id { get; }
    public IPeer Peer { get; private set; }

    public string ResumeToken { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    public long SuspendedAtMs { get; set; }

    internal void Rebind(IPeer peer) => Peer = peer;
    public bool Subscribed => Subscriptions.Count > 0;

    /// <summary>Collections this session opted into.</summary>
    public readonly HashSet<byte> Subscriptions = new();

    public EntityState Avatar;
    public bool HasAvatar;
    public int VisibleCount { get; set; }
    public long SnapshotsSent { get; set; }
    public uint ClientTick { get; set; }
    public int TickDrift { get; set; }

    /// <summary>Per-collection delta baseline: collection id -> entity id -> last sent state.</summary>
    public readonly Dictionary<byte, Dictionary<int, EntityState>> Known = new();

    public Dictionary<int, EntityState> KnownFor(byte collection)
    {
        if (!Known.TryGetValue(collection, out var known))
        {
            known = new Dictionary<int, EntityState>();
            Known[collection] = known;
        }
        return known;
    }

    public void ClearKnown()
    {
        foreach (var known in Known.Values) known.Clear();
    }

    public bool TryDequeue(out InboundPacket packet) => Peer.TryDequeue(out packet);
}

/// <summary>Authoritative entity store. Filter decides visibility per session (interest management).</summary>
public sealed class EntityCollection
{
    private readonly Dictionary<int, EntityState> _items = new();
    private readonly List<int> _moving = new();
    private readonly HashSet<int> _movingSet = new();

    public Func<Session, EntityState, bool>? Filter { get; set; }

    /// <summary>Per-collection tick lambda (R8), called by GameServer once per tick.</summary>
    public Action<double>? OnTick { get; set; }

    public long Ticks { get; private set; }

    public void Tick(double deltaSeconds)
    {
        Ticks++;
        OnTick?.Invoke(deltaSeconds);
    }

    public int Count => _items.Count;
    public IReadOnlyDictionary<int, EntityState> Items => _items;

    public void Spawn(in EntityState state)
    {
        _items[state.Id] = state;
        TrackMovement(state);
    }

    private void TrackMovement(in EntityState state)
    {
        var moving = state.VX != 0f || state.VY != 0f;
        if (moving && _movingSet.Add(state.Id)) _moving.Add(state.Id);
        else if (!moving && _movingSet.Remove(state.Id)) { /* stays in list, skipped by velocity check */ }
    }

    public bool Despawn(int id)
    {
        _movingSet.Remove(id);
        return _items.Remove(id);
    }

    public bool TryGet(int id, out EntityState state) => _items.TryGetValue(id, out state);

    public void Update(int id, in EntityState state)
    {
        if (_items.ContainsKey(id))
        {
            _items[id] = state;
            TrackMovement(state);
        }
    }

    /// <summary>Simple server-authoritative integration; positions wrap inside the arena.</summary>
    public void Integrate(float dt, float arenaSize)
    {
        if (_items.Count == 0) return;
        foreach (var id in _moving)
        {
            if (!_items.TryGetValue(id, out var s)) continue;
            if (s.VX == 0f && s.VY == 0f) continue;
            s.X += s.VX * dt;
            s.Y += s.VY * dt;
            if (s.X < 0) s.X += arenaSize;
            else if (s.X >= arenaSize) s.X -= arenaSize;
            if (s.Y < 0) s.Y += arenaSize;
            else if (s.Y >= arenaSize) s.Y -= arenaSize;
            _items[id] = s;
        }
    }

    public bool IsVisible(Session session, in EntityState entity)
    {
        if (Filter is not null) return Filter(session, entity);
        return true;
    }
}
