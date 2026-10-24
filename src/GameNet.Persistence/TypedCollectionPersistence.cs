using System.Reflection;
using GameNet.Core;

namespace GameNet.Persistence;

/// <summary>SQLite implementation of <see cref="ITypedCollectionStore{T}"/>.</summary>
public sealed class SqliteTypedCollectionStore<T> : ITypedCollectionStore<T> where T : class, new()
{
    private readonly SqliteStore<T> _store;
    private readonly Func<T, int> _keyOf;

    public SqliteTypedCollectionStore(
        string connectionString,
        PersistenceOptions? options = null,
        Func<T, int>? keyOf = null)
    {
        _store = new SqliteStore<T>(connectionString, options);
        _keyOf = keyOf ?? DefaultKey;
    }

    public SqliteStore<T> Store => _store;

    public void EnsureSchema() => _store.EnsureSchema();

    public void Save(int id, T value) => _store.Save(value);

    public T? Load(int id) => _store.Load(id);

    public bool Delete(int id) => _store.Delete(id);

    public IReadOnlyList<(int Id, T Value)> LoadAll() =>
        _store.LoadAll().Select(value => (_keyOf(value), value)).ToList();

    private static int DefaultKey(T item)
    {
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var property = properties.FirstOrDefault(p => p.GetCustomAttribute<KeyAttribute>() is not null)
                       ?? properties.FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"{typeof(T).Name} needs a [Key] property or Id");

        var value = property.GetValue(item);
        return value is int id
            ? id
            : throw new InvalidOperationException($"{typeof(T).Name}.{property.Name} must be int for a typed collection store");
    }
}

public static class TypedCollectionPersistenceExtensions
{
    /// <summary>
    /// Enables SQLite persistence for a typed collection: schema is created now, and every
    /// Spawn/Despawn is upserted/deleted unless autoPersist is false.
    /// </summary>
    public static SqliteTypedCollectionStore<T> UseSqlite<T>(
        this TypedCollection<T> collection,
        string connectionString,
        PersistenceOptions? options = null,
        Func<T, int>? keyOf = null,
        bool autoPersist = true) where T : class, IEntityCodec<T>, new()
    {
        var store = new SqliteTypedCollectionStore<T>(connectionString, options, keyOf);
        collection.UseStore(store, autoPersist);
        return store;
    }
}
