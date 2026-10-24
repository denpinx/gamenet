namespace GameNet.Core;

/// <summary>
/// Optional persistence backend for <see cref="TypedCollection{TKey,T}"/>. The collection never
/// references SQLite directly; GameNet.Persistence provides the SQLite implementation.
/// </summary>
public interface ITypedCollectionStore<TKey, T>
    where TKey : notnull
    where T : class
{
    void EnsureSchema();
    void Save(TKey id, T value);
    T? Load(TKey id);
    bool Delete(TKey id);
    IReadOnlyList<(TKey Id, T Value)> LoadAll();
    IReadOnlyList<(TKey Id, T Value)> Query(Func<T, bool> predicate);
}
