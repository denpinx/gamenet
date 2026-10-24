namespace GameNet.Core;

/// <summary>
/// Optional persistence backend for <see cref="TypedCollection{T}"/>. The collection never
/// references SQLite directly; GameNet.Persistence provides the SQLite implementation.
/// </summary>
public interface ITypedCollectionStore<T> where T : class
{
    void EnsureSchema();
    void Save(int id, T value);
    T? Load(int id);
    bool Delete(int id);
    IReadOnlyList<(int Id, T Value)> LoadAll();

    /// <summary>Returns matching rows (id + value). Implementations may filter in memory or SQL.</summary>
    IReadOnlyList<(int Id, T Value)> Query(Func<T, bool> predicate);
}
