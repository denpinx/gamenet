namespace GameNet.Core;

public enum ChangeKind
{
    Added = 0,
    Updated = 1,
    Removed = 2,
}

public sealed class EntityChange
{
    public int Id { get; init; }
    public ChangeKind Kind { get; init; }
    public EntityState? Previous { get; init; }
    public EntityState? Current { get; init; }
}

public sealed class CollectionChanged
{
    public byte Collection { get; init; }
    public uint Version { get; init; }
    public IReadOnlyList<EntityChange> Changes { get; init; } = Array.Empty<EntityChange>();
}

public readonly struct TypedEntityChange<TKey, T>
{
    public TKey Id { get; init; }
    public ChangeKind Kind { get; init; }
    public T? Previous { get; init; }
    public T? Current { get; init; }
}

public sealed class TypedCollectionChanged<TKey, T>
{
    public byte Collection { get; init; }
    public uint Version { get; init; }
    public IReadOnlyList<TypedEntityChange<TKey, T>> Changes { get; init; } =
        Array.Empty<TypedEntityChange<TKey, T>>();
}

internal sealed class EventSubscription : IDisposable
{
    private Action? _unsubscribe;
    public EventSubscription(Action unsubscribe) => _unsubscribe = unsubscribe;
    public void Dispose()
    {
        var action = _unsubscribe;
        _unsubscribe = null;
        action?.Invoke();
    }
}
