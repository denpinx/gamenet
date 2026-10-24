namespace GameNet.Core;

public struct TypedEntry
{
    public int Id;
    public DeltaOp Op;
    public ulong Mask;
    public byte[] Payload;
}

public sealed class TypedChunk
{
    public byte Collection;
    public uint Version;
    public uint ServerTimeMs;
    public int Index;
    public int Count;
    public List<TypedEntry> Entries = new();
}

/// <summary>Chunk format for typed entity collections: entries carry codec-generated payloads.</summary>
public static class TypedSnapshotCodec
{
    public const int MaxChunkPayload = 1100;
    private const int HeaderAllowance = 24;

    public static List<byte[]> Build(byte collection, uint version, uint serverTimeMs, List<TypedEntry> entries)
    {
        var encoded = new List<byte[]>(entries.Count);
        foreach (var entry in entries)
        {
            var writer = new ByteWriter(64);
            writer.WriteVarInt(entry.Id);
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

    public static bool TryRead(ReadOnlySpan<byte> payload, out TypedChunk chunk)
    {
        chunk = null!;
        var reader = new ByteReader(payload);
        if (reader.ReadByte() != (byte)MessageType.TypedSnapshot) return false;
        var result = new TypedChunk
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
            var id = reader.ReadVarInt();
            var op = (DeltaOp)reader.ReadByte();
            var entry = new TypedEntry { Id = id, Op = op };
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
    void Tick(double deltaSeconds);
    void Replicate(Session session, uint version, uint serverTimeMs, int maxBytesPerSecond);
    void RemoveSession(int sessionId);
}

/// <summary>Server-side store of typed entities with per-session delta baselines.</summary>
public sealed class TypedCollection<T> : ITypedCollection where T : class, IEntityCodec<T>
{
    private readonly Dictionary<int, T> _items = new();
    private readonly Dictionary<int, Dictionary<int, T>> _known = new();

    internal TypedCollection(byte id, string name)
    {
        Id = id;
        Name = name;
    }

    public byte Id { get; }
    public string Name { get; }
    public int Count => _items.Count;
    public IReadOnlyDictionary<int, T> Items => _items;

    public Func<Session, T, bool>? Filter { get; set; }
    public Action<double>? OnTick { get; set; }

    public void Spawn(int id, T value) => _items[id] = value;
    public bool Despawn(int id) => _items.Remove(id);
    public bool TryGet(int id, out T value) => _items.TryGetValue(id, out value!);

    public void Tick(double deltaSeconds) => OnTick?.Invoke(deltaSeconds);

    public void RemoveSession(int sessionId) => _known.Remove(sessionId);

    void ITypedCollection.Replicate(Session session, uint version, uint serverTimeMs, int maxBytesPerSecond)
    {
        if (!_known.TryGetValue(session.Id, out var known))
        {
            known = new Dictionary<int, T>();
            _known[session.Id] = known;
        }

        var visible = new List<(int Id, T Value)>(Math.Min(_items.Count, 512));
        var visibleIds = new HashSet<int>();
        foreach (var pair in _items)
        {
            if (Filter is not null && !Filter(session, pair.Value)) continue;
            visible.Add((pair.Key, pair.Value));
            visibleIds.Add(pair.Key);
        }

        var entries = new List<TypedEntry>(visible.Count + 8);
        foreach (var (id, value) in visible)
        {
            if (!known.TryGetValue(id, out var previous))
            {
                entries.Add(new TypedEntry { Id = id, Op = DeltaOp.Add, Payload = EncodeFull(value) });
                continue;
            }

            var mask = T.DiffMask(previous, value);
            if (mask != 0)
                entries.Add(new TypedEntry { Id = id, Op = DeltaOp.Update, Mask = mask, Payload = EncodeDelta(value, mask) });
        }

        foreach (var knownId in known.Keys)
        {
            if (!visibleIds.Contains(knownId))
                entries.Add(new TypedEntry { Id = knownId, Op = DeltaOp.Remove, Payload = Array.Empty<byte>() });
        }

        if (entries.Count == 0) return;

        var chunks = TypedSnapshotCodec.Build(Id, version, serverTimeMs, entries);
        var totalBytes = chunks.Sum(c => (long)c.Length);
        if (!session.TryConsume(totalBytes, maxBytesPerSecond, Environment.TickCount64)) return;

        var delivered = true;
        foreach (var chunk in chunks)
        {
            if (!session.Peer.Send(0, chunk)) delivered = false;
        }

        if (!delivered) return;

        known.Clear();
        // Snapshot by value: server entities are mutable classes, so keeping the same
        // reference would make an in-place edit look unchanged to the next diff.
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
    bool Apply(TypedChunk chunk);
}

/// <summary>Client-side view of a typed collection; T may be a record while the server uses a class.</summary>
public sealed class TypedWorld<T> : ITypedWorld where T : class, IEntityCodec<T>
{
    private readonly Dictionary<int, T> _entities = new();
    private readonly Dictionary<int, uint> _ticks = new();
    private uint _lastVersion;

    public TypedWorld(byte collection) => Collection = collection;

    public byte Collection { get; set; }
    public IReadOnlyDictionary<int, T> Entities => _entities;
    public int Count => _entities.Count;
    public long ChunksApplied { get; private set; }
    public bool NeedsResync { get; private set; }

    /// <summary>Raised after each applied chunk with the entries that changed.</summary>
    public event Action<TypedCollectionChanged<T>>? Changed;

    public IDisposable OnChanged(Action<TypedCollectionChanged<T>> handler)
    {
        Changed += handler;
        return new EventSubscription(() => Changed -= handler);
    }

    /// <summary>Last exception thrown by a Changed handler; handlers never break the receive pump.</summary>
    public Exception? LastHandlerError { get; private set; }

    public bool Apply(TypedChunk chunk)
    {
        var progressed = false;
        var changes = Changed is null ? null : new List<TypedEntityChange<T>>();
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
                    changes?.Add(new TypedEntityChange<T> { Id = entry.Id, Kind = ChangeKind.Added, Current = value });
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
                    changes?.Add(new TypedEntityChange<T> { Id = entry.Id, Kind = ChangeKind.Updated, Previous = previous, Current = updated });
                    progressed = true;
                    break;
                }
                case DeltaOp.Remove:
                    if (changes is not null && _entities.TryGetValue(entry.Id, out var removed))
                        changes.Add(new TypedEntityChange<T> { Id = entry.Id, Kind = ChangeKind.Removed, Previous = removed });
                    _entities.Remove(entry.Id);
                    _ticks[entry.Id] = chunk.Version;
                    progressed = true;
                    break;
            }
        }

        _lastVersion = Math.Max(_lastVersion, chunk.Version);
        ChunksApplied++;
        if (changes is { Count: > 0 })
            RaiseChanged(new TypedCollectionChanged<T> { Collection = Collection, Version = chunk.Version, Changes = changes });
        return progressed;
    }

    private void RaiseChanged(TypedCollectionChanged<T> context)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<TypedCollectionChanged<T>>)handler)(context); }
            catch (Exception ex) { LastHandlerError = ex; }
        }
    }

    public void Clear()
    {
        _entities.Clear();
        _ticks.Clear();
    }
}
