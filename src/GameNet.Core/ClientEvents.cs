namespace GameNet.Core;

public enum ChangeKind
{
    Added,
    Updated,
    Removed,
}

/// <summary>One applied change for the untyped EntityState world.</summary>
public readonly struct EntityChange
{
    public int Id { get; init; }
    public ChangeKind Kind { get; init; }
    public EntityState Previous { get; init; }
    public EntityState Current { get; init; }
}

public sealed class CollectionChanged
{
    public byte Collection { get; init; }
    public uint Version { get; init; }
    public IReadOnlyList<EntityChange> Changes { get; init; } = Array.Empty<EntityChange>();
}

/// <summary>One applied change for a typed world. Previous is the old value for record views.</summary>
public readonly struct TypedEntityChange<T>
{
    public int Id { get; init; }
    public ChangeKind Kind { get; init; }
    public T? Previous { get; init; }
    public T? Current { get; init; }
}

public sealed class TypedCollectionChanged<T>
{
    public byte Collection { get; init; }
    public uint Version { get; init; }
    public IReadOnlyList<TypedEntityChange<T>> Changes { get; init; } = Array.Empty<TypedEntityChange<T>>();
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
