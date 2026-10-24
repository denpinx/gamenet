namespace GameNet.Core;

/// <summary>
/// Client-side replicated world, keyed by collection id. Chunks are applied as soon as they arrive
/// (no whole-snapshot atomicity), so one delayed chunk does not stall the others. Per-entity tick
/// numbers prevent a late chunk from overwriting newer state.
/// </summary>
public sealed class ClientWorld
{
    private sealed class CollectionState
    {
        public readonly Dictionary<int, EntityState> Entities = new();
        public readonly Dictionary<int, uint> EntityTick = new();
    }

    private readonly Dictionary<byte, CollectionState> _collections = new();
    private CollectionState State(byte collection)
    {
        if (!_collections.TryGetValue(collection, out var state))
        {
            state = new CollectionState();
            _collections[collection] = state;
        }
        return state;
    }

    private uint _lastVersionSeen;
    private int _missingUpdates;

    /// <summary>Default collection (id 0) — kept for the single-collection API.</summary>
    public IReadOnlyDictionary<int, EntityState> Entities => State(0).Entities;
    public IReadOnlyDictionary<int, EntityState> EntitiesFor(byte collection) => State(collection).Entities;
    public int EntityCount => _collections.Values.Sum(c => c.Entities.Count);
    public int EntityCountFor(byte collection) => State(collection).Entities.Count;
    public long ChunksApplied { get; private set; }
    public bool NeedsResync { get; private set; }

    /// <summary>Raised after each applied chunk with the changes that chunk contained.</summary>
    public event Action<CollectionChanged>? Changed;

    public IDisposable OnChanged(Action<CollectionChanged> handler)
    {
        Changed += handler;
        return new EventSubscription(() => Changed -= handler);
    }

    /// <summary>Last exception thrown by a Changed handler; handlers never break the receive pump.</summary>
    public Exception? LastHandlerError { get; private set; }

    private readonly List<int> _lagSamples = new();
    public IReadOnlyList<int> LagSamples => _lagSamples;
    public int LastLagMs { get; private set; }

    public bool OnChunk(SnapshotChunk chunk)
    {
        var state = State(chunk.Collection);
        var progressed = false;
        var changes = Changed is null ? null : new List<EntityChange>();
        foreach (var entry in chunk.Entries)
        {
            var knownTick = state.EntityTick.TryGetValue(entry.Id, out var tick) ? tick : 0;
            if (chunk.Version < knownTick) continue; // stale chunk

            switch (entry.Op)
            {
                case DeltaOp.Add:
                    if (changes is not null)
                        changes.Add(new EntityChange { Id = entry.Id, Kind = ChangeKind.Added, Current = entry.State });
                    state.Entities[entry.Id] = entry.State;
                    state.EntityTick[entry.Id] = chunk.Version;
                    _missingUpdates = 0;
                    progressed = true;
                    break;

                case DeltaOp.Update:
                    if (state.Entities.TryGetValue(entry.Id, out var current))
                    {
                        var previous = current;
                        EntityCodec.ApplyFields(ref current, entry.State, entry.Mask);
                        current.Id = entry.Id;
                        state.Entities[entry.Id] = current;
                        state.EntityTick[entry.Id] = chunk.Version;
                        _missingUpdates = 0;
                        progressed = true;
                        if (changes is not null)
                            changes.Add(new EntityChange { Id = entry.Id, Kind = ChangeKind.Updated, Previous = previous, Current = current });
                    }
                    else
                    {
                        // We never saw this entity (join / lost add): ask for a fresh baseline.
                        if (++_missingUpdates > 32) NeedsResync = true;
                    }
                    break;

                case DeltaOp.Remove:
                    if (changes is not null && state.Entities.TryGetValue(entry.Id, out var removed))
                        changes.Add(new EntityChange { Id = entry.Id, Kind = ChangeKind.Removed, Previous = removed });
                    state.Entities.Remove(entry.Id);
                    state.EntityTick[entry.Id] = chunk.Version; // tombstone blocks older adds
                    progressed = true;
                    break;
            }
        }

        _lastVersionSeen = Math.Max(_lastVersionSeen, chunk.Version);
        ChunksApplied++;

        if (chunk.ServerTimeMs != 0)
        {
            var lag = (int)((uint)Environment.TickCount64 - chunk.ServerTimeMs);
            if (lag >= 0)
            {
                LastLagMs = lag;
                if (_lagSamples.Count < 8192) _lagSamples.Add(lag);
            }
        }

        TrimTombstones();
        if (changes is { Count: > 0 }) RaiseChanged(new CollectionChanged { Collection = chunk.Collection, Version = chunk.Version, Changes = changes });
        return progressed;
    }

    private void RaiseChanged(CollectionChanged context)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<CollectionChanged>)handler)(context); }
            catch (Exception ex) { LastHandlerError = ex; }
        }
    }

    private void TrimTombstones()
    {
        foreach (var state in _collections.Values)
        {
            if (state.EntityTick.Count < state.Entities.Count * 4 + 1024) continue;
            var cutoff = _lastVersionSeen > 300 ? _lastVersionSeen - 300 : 0;
            foreach (var id in state.EntityTick.Where(p => !state.Entities.ContainsKey(p.Key) && p.Value < cutoff)
                         .Select(p => p.Key).ToList())
            {
                state.EntityTick.Remove(id);
            }
        }
    }

    public void AcknowledgeResync() => NeedsResync = false;

    public void Clear()
    {
        _collections.Clear();
    }
}
