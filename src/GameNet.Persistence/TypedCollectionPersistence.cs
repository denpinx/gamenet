using System.Reflection;
using GameNet.Core;

namespace GameNet.Persistence;

/// <summary>SQLite implementation of <see cref="ITypedCollectionStore{T}"/>.</summary>
public sealed class SqliteTypedCollectionStore<T> : ITypedCollectionStore<T> where T : class, new()
{
    private readonly SqliteStore<T> _store;
    private readonly Func<T, TypedKey> _keyOf;

    public SqliteTypedCollectionStore(
        string connectionString,
        PersistenceOptions? options = null,
        Func<T, TypedKey>? keyOf = null)
    {
        _store = new SqliteStore<T>(connectionString, options);
        _keyOf = keyOf ?? DefaultKey;
    }

    public SqliteStore<T> Store => _store;

    public void EnsureSchema() => _store.EnsureSchema();

    public void Save(TypedKey id, T value) => _store.Save(value);

    public T? Load(TypedKey id) => _store.Load(ToObject(id));

    public bool Delete(TypedKey id) => _store.Delete(ToObject(id));

    public IReadOnlyList<(TypedKey Id, T Value)> LoadAll() =>
        _store.LoadAll().Select(value => (_keyOf(value), value)).ToList();

    public IReadOnlyList<(TypedKey Id, T Value)> Query(Func<T, bool> predicate) =>
        _store.LoadAll().Where(predicate).Select(value => (_keyOf(value), value)).ToList();

    private static object ToObject(TypedKey key) => key.Kind switch
    {
        TypedKeyKind.String => key.StringValue ?? string.Empty,
        TypedKeyKind.Guid => key.GuidValue,
        _ => key.IntValue,
    };

    private static TypedKey DefaultKey(T item)
    {
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var property = properties.FirstOrDefault(p => p.GetCustomAttribute<KeyAttribute>() is not null)
                       ?? properties.FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"{typeof(T).Name} needs a [Key] property or Id");

        var value = property.GetValue(item);
        return value switch
        {
            int id => id,
            string text => text,
            Guid guid => guid,
            _ => throw new InvalidOperationException($"{typeof(T).Name}.{property.Name} must be int, string or Guid"),
        };
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
        Func<T, TypedKey>? keyOf = null,
        bool autoPersist = true) where T : class, IEntityCodec<T>, new()
    {
        var store = new SqliteTypedCollectionStore<T>(connectionString, options, keyOf);
        collection.UseStore(store, autoPersist);
        return store;
    }
}
