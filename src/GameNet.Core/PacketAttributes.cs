namespace GameNet.Core;

/// <summary>
/// Marks a type for compile-time serializer generation (source generator emits
/// Write/Read/WriteDelta/ApplyDelta for it).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class PacketAttribute : Attribute
{
}

/// <summary>Explicit wire field order. Required for [Packet] types; never renumber.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FieldAttribute : Attribute
{
    public FieldAttribute(int order) => Order = order;
    public int Order { get; }
}
