using System.Diagnostics;
using System.Reflection;

namespace GameNet.Persistence;

/// <summary>
/// In-memory set backed by a store, with a per-set tick lambda and optional auto-save.
/// This is the R8 shape: `OnTick` is your per-frame hook; Save/Load are one-call single-record ops.
/// </summary>
public sealed class PersistableSet<T> where T : class, new()
{
    private readonly SqliteStore<T> _store;
    private readonly PropertyInfo _keyProperty;
    private readonly Dictionary<object, T> _items = new();
    private readonly Dictionary<object, HashSet<string>> _dirty = new();
    private long _lastFlushTimestamp;

    public PersistableSet(SqliteStore<T> store)
    {
        _store = store;
        _keyProperty = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                           .FirstOrDefault(p => p.GetCustomAttribute<KeyAttribute>() is not null)
                       ?? typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                           .FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"{typeof(T).Name} needs a key property");
        _lastFlushTimestamp = Stopwatch.GetTimestamp();
    }

    public Action<PersistableSet<T>, double>? OnTick { get; set; }
    public bool AutoSave { get; set; } = true;
    public TimeSpan AutoSaveInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public IReadOnlyCollection<T> Items => _items.Values;
    public int Count => _items.Count;
    public int DirtyCount => _dirty.Count;

    public void Add(T item)
    {
        var key = KeyOf(item);
        _items[key] = item;
        _dirty[key] = new HashSet<string>(StringComparer.Ordinal); // empty set = full row
    }

    public T? Load(object key)
    {
        var item = _store.Load(key);
        if (item is not null) _items[key] = item;
        return item;
    }

    /// <summary>Loads the whole table (startup restore) and returns what was loaded.</summary>
    public IReadOnlyList<T> LoadAll()
    {
        var items = _store.LoadAll();
        foreach (var item in items) _items[KeyOf(item)] = item;
        return items;
    }

    /// <summary>Marks a record dirty. Pass property names to persist only those columns.</summary>
    public void MarkDirty(T item, params string[] changedProperties)
    {
        var key = KeyOf(item);
        _items[key] = item;
        if (changedProperties.Length == 0)
        {
            _dirty[key] = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        if (!_dirty.TryGetValue(key, out var columns) || columns.Count == 0)
        {
            // already scheduled as a full row — keep it that way
            if (_dirty.ContainsKey(key) && _dirty[key].Count == 0) return;
            _dirty[key] = new HashSet<string>(changedProperties, StringComparer.Ordinal);
            return;
        }

        foreach (var property in changedProperties) columns.Add(property);
    }

    public void Save(T item)
    {
        _store.Save(item);
        var key = KeyOf(item);
        _items[key] = item;
        _dirty.Remove(key);
    }

    public bool Delete(object key)
    {
        _dirty.Remove(key);
        _items.Remove(key);
        return _store.Delete(key);
    }

    /// <summary>Persists every dirty record. Returns how many were written.</summary>
    public int Flush()
    {
        if (_dirty.Count == 0) return 0;
        var entries = _dirty.ToArray();
        var written = 0;
        foreach (var (key, columns) in entries)
        {
            if (!_items.TryGetValue(key, out var item)) continue;
            if (columns.Count > 0) _store.SavePartial(item, columns.ToArray());
            else _store.Save(item);
            _dirty.Remove(key);
            written++;
        }
        _lastFlushTimestamp = Stopwatch.GetTimestamp();
        return written;
    }

    /// <summary>Per-tick entry point: runs the user lambda, then auto-saves on the configured interval.</summary>
    public void Tick(double deltaSeconds)
    {
        OnTick?.Invoke(this, deltaSeconds);
        if (!AutoSave) return;
        var elapsed = Stopwatch.GetElapsedTime(_lastFlushTimestamp);
        if (elapsed >= AutoSaveInterval) Flush();
    }

    private object KeyOf(T item)
    {
        var value = _keyProperty.GetValue(item);
        if (value is Guid guid && guid == Guid.Empty)
        {
            guid = Guid.NewGuid();
            if (_keyProperty.CanWrite) _keyProperty.SetValue(item, guid);
            value = guid;
        }
        return value ?? throw new InvalidOperationException($"{typeof(T).Name}.{_keyProperty.Name} is null");
    }
}
