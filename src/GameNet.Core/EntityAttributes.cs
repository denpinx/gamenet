namespace GameNet.Core;

/// <summary>
/// Marks a type as a replicated typed entity. The generator emits an <see cref="IEntityCodec{T}"/>
/// implementation (Write/Read/DiffMask/WriteDelta/ApplyDelta) so it can live in a TypedCollection.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EntityAttribute : Attribute
{
    public EntityAttribute(string name) => Name = name;
    public string Name { get; }
}

/// <summary>Wire + delta contract for typed entity replication.</summary>
public interface IEntityCodec<T> where T : IEntityCodec<T>
{
    static abstract void Write(ByteWriter writer, T value);
    static abstract T Read(ref ByteReader reader);
    static abstract ulong DiffMask(T a, T b);
    static abstract void WriteDelta(ByteWriter writer, T value, ulong mask);
    static abstract T ApplyDelta(ref ByteReader reader, T target, ulong mask);
}
