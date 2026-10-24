namespace GameNet.Core;

public struct TypedEntry<TKey>
{
    public TKey Id;
    public DeltaOp Op;
    public ulong Mask;
    public byte[] Payload;
}

public sealed class TypedChunk<TKey>
{
    public byte Collection;
    public uint Version;
    public uint ServerTimeMs;
    public int Index;
    public int Count;
    public List<TypedEntry<TKey>> Entries = new();
}

/// <summary>Typed snapshot codec: keys are encoded by the collection's key codec.</summary>
public static class TypedSnapshotCodec
{
    public const int MaxChunkPayload = 1100;
    private const int HeaderAllowance = 24;

    public static List<byte[]> Build<TKey>(
        ITypedKeyCodec<TKey> keyCodec,
        byte collection,
        uint version,
        uint serverTimeMs,
        List<TypedEntry<TKey>> entries)
    {
        var encoded = new List<byte[]>(entries.Count);
        foreach (var entry in entries)
        {
            var writer = new ByteWriter(64);
            keyCodec.Write(writer, entry.Id);
            writer.WriteByte((byte)entry.Op);
            if (entry.Op == DeltaOp.Update) writer.WriteVarUInt((uint)entry.Mask);
            if (entry.Op != DeltaOp.Remove)
            {
                writer.WriteVarUInt((uint)entry.Payload.Length);
                writer.WriteBytes(entry.Payload);
            }
            encoded.Add(writer.ToArray());
        }

        var groups = new List<List<byte[]>>();
        var current = new List<byte[]>();
        var size = 0;
        foreach (var bytes in encoded)
        {
            if (current.Count > 0 && size + bytes.Length + HeaderAllowance > MaxChunkPayload)
            {
                groups.Add(current);
                current = new List<byte[]>();
                size = 0;
            }
            current.Add(bytes);
            size += bytes.Length;
        }
        if (current.Count > 0 || groups.Count == 0) groups.Add(current);

        var chunks = new List<byte[]>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
            chunks.Add(WriteChunk(collection, version, serverTimeMs, i, groups.Count, groups[i]));
        return chunks;
    }

    private static byte[] WriteChunk(byte collection, uint version, uint serverTimeMs, int index, int count, List<byte[]> entries)
    {
        var writer = new ByteWriter(256);
        writer.WriteByte((byte)MessageType.TypedSnapshot);
        writer.WriteByte(collection);
        writer.WriteVarUInt(version);
        writer.WriteUInt(serverTimeMs);
        writer.WriteByte((byte)index);
        writer.WriteByte((byte)count);
        writer.WriteVarUInt((uint)entries.Count);
        foreach (var entry in entries) writer.WriteBytes(entry);
        return writer.ToArray();
    }

    public static bool TryRead<TKey>(ReadOnlySpan<byte> payload, ITypedKeyCodec<TKey> keyCodec, out TypedChunk<TKey> chunk)
    {
        chunk = null!;
        var reader = new ByteReader(payload);
        if (reader.ReadByte() != (byte)MessageType.TypedSnapshot) return false;
        var result = new TypedChunk<TKey>
        {
            Collection = reader.ReadByte(),
            Version = reader.ReadVarUInt(),
            ServerTimeMs = reader.ReadUInt(),
            Index = reader.ReadByte(),
            Count = reader.ReadByte(),
        };
        var entryCount = (int)reader.ReadVarUInt();
        for (var i = 0; i < entryCount; i++)
        {
            var id = keyCodec.Read(ref reader);
            var op = (DeltaOp)reader.ReadByte();
            var entry = new TypedEntry<TKey> { Id = id, Op = op };
            if (op == DeltaOp.Update) entry.Mask = reader.ReadVarUInt();
            if (op != DeltaOp.Remove)
            {
                var length = (int)reader.ReadVarUInt();
                entry.Payload = reader.ReadBytes(length).ToArray();
            }
            else
            {
                entry.Payload = Array.Empty<byte>();
            }
            result.Entries.Add(entry);
        }
        chunk = result;
        return true;
    }
}

public interface ITypedCollection
{
    byte Id { get; }
    string Name { get; }
    string KeyCodecId { get; }
    void Tick(double deltaSeconds);
    void Replicate(Session session, uint version, uint serverTimeMs, int maxBytesPerSecond);
    void RemoveSession(int sessionId);
}

/// <summary>Server-side store of typed entities with per-session delta baselines.</summary>
public sealed class TypedCollection<TKey, T> : ITypedCollection
    where TKey : notnull
    where T : class, IEntityCodec<T>
{
    private readonly Dictionary<TKey, T> _items = new();
    private readonly Dictionary<int, Dictionary<TKey, T>> _known = new();
    private readonly ITypedKeyCodec<TKey> _keyCodec;
    private bool _suppressPersist;

    internal TypedCollection(byte id, string name, ITypedKeyCodec<TKey> keyCodec)
    {
        Id = id;
        Name = name;
        _keyCodec = keyCodec;
    }

    public byte Id { get; }
    public string Name { get; }
    public string KeyCodecId => _keyCodec.Id;
    public int Count => _items.Count;
    public IReadOnlyDictionary<TKey, T> Items => _items;

    public Func<Session, T, bool>? Filter { get; set; }
    public Action<double>? OnTick { get; set; }

    public ITypedCollectionStore<TKey, T>? PersistenceStore { get; private set; }
    public bool AutoPersist { get; set; } = true;

    public void UseStore(ITypedCollectionStore<TKey, T> store, bool autoPersist = true)
    {
        PersistenceStore = store;
        AutoPersist = autoPersist;
        store.EnsureSchema();
    }

    public void Spawn(TKey id, T value)
    {
        _items[id] = value;
        if (AutoPersist && !_suppressPersist) PersistenceStore?.Save(id, value);
    }

    public bool Despawn(TKey id)
    {
        var removed = _items.Remove(id);
        if (removed && AutoPersist && !_suppressPersist) PersistenceStore?.Delete(id);
        return removed;
    }

    public bool TryGet(TKey id, out T value) => _items.TryGetValue(id, out value!);

    public void Save(TKey id)
    {
        if (PersistenceStore is null) throw new InvalidOperationException($"collection {Name} has no persistence store");
        if (!_items.TryGetValue(id, out var value)) throw new KeyNotFoundException($"entity {id} is not in collection {Name}");
        PersistenceStore.Save(id, value);
    }

    public void Save(TKey id, T value)
    {
        if (PersistenceStore is null) throw new InvalidOperationException($"collection {Name} has no persistence store");
        _items[id] = value;
        PersistenceStore.Save(id, value);
    }

    public T? Load(TKey id)
    {
        if (PersistenceStore is null) throw new InvalidOperationException($"collection {Name} has no persistence store");
        var value = PersistenceStore.Load(id);
        if (value is null) return null;
        _suppressPersist = true;
        try { Spawn(id, value); }
        finally { _suppressPersist = false; }
        return value;
    }

    public T LoadOrSpawn(TKey id, Func<T> factory)
    {
        if (PersistenceStore is null) throw new InvalidOperationException($"collection {Name} has no persistence store");
        if (_items.TryGetValue(id, out var current)) return current;

        var loaded = PersistenceStore.Load(id);
        if (loaded is not null)
        {
            _items[id] = loaded;
            return loaded;
        }

        var created = factory();
        _items[id] = created;
        PersistenceStore.Save(id, created);
        return created;
    }

    public IReadOnlyList<T> LoadWhere(Func<T, bool> predicate)
    {
        if (PersistenceStore is null) throw new InvalidOperationException($"collection {Name} has no persistence store");
        var rows = PersistenceStore.Query(predicate);
        _suppressPersist = true;
        try
        {
            foreach (var (id, value) in rows) Spawn(id, value);
        }
        finally { _suppressPersist = false; }
        return rows.Select(row => row.Value).ToList();
    }

    public int LoadAll()
    {
        if (PersistenceStore is null) throw new InvalidOperationException($"collection {Name} has no persistence store");
        var rows = PersistenceStore.LoadAll();
        _suppressPersist = true;
        try
        {
            foreach (var (id, value) in rows) Spawn(id, value);
        }
        finally { _suppressPersist = false; }
        return rows.Count;
    }

    public void Tick(double deltaSeconds) => OnTick?.Invoke(deltaSeconds);

    public void RemoveSession(int sessionId) => _known.Remove(sessionId);

    public void Replicate(Session session, uint version, uint serverTimeMs, int maxBytesPerSecond)
    {
        if (!_known.TryGetValue(session.Id, out var known))
        {
            known = new Dictionary<TKey, T>();
            _known[session.Id] = known;
        }

        var visible = new List<(TKey Id, T Value)>(Math.Min(_items.Count, 512));
        var visibleIds = new HashSet<TKey>();
        foreach (var pair in _items)
        {
            if (Filter is not null && !Filter(session, pair.Value)) continue;
            visible.Add((pair.Key, pair.Value));
            visibleIds.Add(pair.Key);
        }

        var entries = new List<TypedEntry<TKey>>(visible.Count + 8);
        foreach (var (id, value) in visible)
        {
            if (!known.TryGetValue(id, out var previous))
            {
                entries.Add(new TypedEntry<TKey> { Id = id, Op = DeltaOp.Add, Payload = EncodeFull(value) });
                continue;
            }

            var mask = T.DiffMask(previous, value);
            if (mask != 0)
                entries.Add(new TypedEntry<TKey> { Id = id, Op = DeltaOp.Update, Mask = mask, Payload = EncodeDelta(value, mask) });
        }

        foreach (var knownId in known.Keys)
        {
            if (!visibleIds.Contains(knownId))
                entries.Add(new TypedEntry<TKey> { Id = knownId, Op = DeltaOp.Remove, Payload = Array.Empty<byte>() });
        }

        if (entries.Count == 0) return;

        var chunks = TypedSnapshotCodec.Build(_keyCodec, Id, version, serverTimeMs, entries);
        var totalBytes = chunks.Sum(c => (long)c.Length);
        if (!session.TryConsume(totalBytes, maxBytesPerSecond, Environment.TickCount64)) return;

        var delivered = true;
        foreach (var chunk in chunks)
        {
            if (!session.Peer.Send(0, chunk)) delivered = false;
        }

        if (!delivered) return;

        known.Clear();
        foreach (var (id, value) in visible) known[id] = Snapshot(value);
    }

    private static T Snapshot(T value)
    {
        var reader = new ByteReader(EncodeFull(value));
        return T.Read(ref reader);
    }

    private static byte[] EncodeFull(T value)
    {
        var writer = new ByteWriter(64);
        T.Write(writer, value);
        return writer.ToArray();
    }

    private static byte[] EncodeDelta(T value, ulong mask)
    {
        var writer = new ByteWriter(64);
        T.WriteDelta(writer, value, mask);
        return writer.ToArray();
    }
}

public interface ITypedWorld
{
    byte Collection { get; set; }
    string KeyCodecId { get; }
    bool Apply(ReadOnlySpan<byte> payload);
}

/// <summary>Client-side view of a typed collection; T may be a record while the server uses a class.</summary>
public sealed class TypedWorld<TKey, T> : ITypedWorld
    where TKey : notnull
    where T : class, IEntityCodec<T>
{
    private readonly Dictionary<TKey, T> _entities = new();
    private readonly Dictionary<TKey, uint> _ticks = new();
    private readonly ITypedKeyCodec<TKey> _keyCodec;

    public TypedWorld(byte collection, ITypedKeyCodec<TKey> keyCodec)
    {
        Collection = collection;
        _keyCodec = keyCodec;
    }

    public byte Collection { get; set; }
    public string KeyCodecId => _keyCodec.Id;
    public IReadOnlyDictionary<TKey, T> Entities => _entities;
    public int Count => _entities.Count;
    public long ChunksApplied { get; private set; }
    public bool NeedsResync { get; private set; }

    public T this[TKey key] => _entities[key];
    public T Get(TKey key) => _entities[key];
    public bool TryGet(TKey key, out T value) => _entities.TryGetValue(key, out value!);

    public event Action<TypedCollectionChanged<TKey, T>>? Changed;

    public IDisposable OnChanged(Action<TypedCollectionChanged<TKey, T>> handler)
    {
        Changed += handler;
        return new EventSubscription(() => Changed -= handler);
    }

    public Exception? LastHandlerError { get; private set; }

    public bool Apply(ReadOnlySpan<byte> payload)
    {
        if (!TypedSnapshotCodec.TryRead(payload, _keyCodec, out var chunk)) return false;
        return Apply(chunk);
    }

    public bool Apply(TypedChunk<TKey> chunk)
    {
        var progressed = false;
        var changes = Changed is null ? null : new List<TypedEntityChange<TKey, T>>();
        foreach (var entry in chunk.Entries)
        {
            var tick = _ticks.TryGetValue(entry.Id, out var known) ? known : 0;
            if (chunk.Version < tick) continue;

            switch (entry.Op)
            {
                case DeltaOp.Add:
                {
                    var reader = new ByteReader(entry.Payload);
                    var value = T.Read(ref reader);
                    _entities[entry.Id] = value;
                    _ticks[entry.Id] = chunk.Version;
                    changes?.Add(new TypedEntityChange<TKey, T> { Id = entry.Id, Kind = ChangeKind.Added, Current = value });
                    progressed = true;
                    break;
                }
                case DeltaOp.Update:
                {
                    if (!_entities.TryGetValue(entry.Id, out var current)) break;
                    var previous = current;
                    var reader = new ByteReader(entry.Payload);
                    var updated = T.ApplyDelta(ref reader, current, entry.Mask);
                    _entities[entry.Id] = updated;
                    _ticks[entry.Id] = chunk.Version;
                    changes?.Add(new TypedEntityChange<TKey, T>
                    {
                        Id = entry.Id, Kind = ChangeKind.Updated, Previous = previous, Current = updated,
                    });
                    progressed = true;
                    break;
                }
                case DeltaOp.Remove:
                {
                    if (!_entities.TryGetValue(entry.Id, out var current)) break;
                    _entities.Remove(entry.Id);
                    _ticks.Remove(entry.Id);
                    changes?.Add(new TypedEntityChange<TKey, T> { Id = entry.Id, Kind = ChangeKind.Removed, Previous = current });
                    progressed = true;
                    break;
                }
            }
        }

        ChunksApplied++;
        if (changes is { Count: > 0 })
        {
            try { Changed?.Invoke(new TypedCollectionChanged<TKey, T> { Collection = chunk.Collection, Version = chunk.Version, Changes = changes }); }
            catch (Exception ex) { LastHandlerError = ex; }
        }
        return progressed;
    }

    public void Clear()
    {
        _entities.Clear();
        _ticks.Clear();
        NeedsResync = true;
    }
}
