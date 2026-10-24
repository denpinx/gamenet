namespace GameNet.Core;

public enum TypedKeyKind : byte
{
    Int = 0,
    String = 1,
    Guid = 2,
}

/// <summary>
/// Stable wire key for a typed collection: int, string or Guid. Arbitrary hash codes are not
/// valid keys because they collide and are process-local.
/// </summary>
public readonly struct TypedKey : IEquatable<TypedKey>
{
    public TypedKeyKind Kind { get; }
    public int IntValue { get; }
    public string? StringValue { get; }
    public Guid GuidValue { get; }

    private TypedKey(TypedKeyKind kind, int intValue, string? stringValue, Guid guidValue)
    {
        Kind = kind;
        IntValue = intValue;
        StringValue = stringValue;
        GuidValue = guidValue;
    }

    public static implicit operator TypedKey(int value) => new(TypedKeyKind.Int, value, null, default);
    public static implicit operator TypedKey(string value) => new(TypedKeyKind.String, 0, value, default);
    public static implicit operator TypedKey(Guid value) => new(TypedKeyKind.Guid, 0, null, value);

    public bool Equals(TypedKey other) => Kind == other.Kind
        && IntValue == other.IntValue
        && GuidValue == other.GuidValue
        && string.Equals(StringValue, other.StringValue, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TypedKey other && Equals(other);

    public override int GetHashCode() => Kind switch
    {
        TypedKeyKind.String => StringComparer.Ordinal.GetHashCode(StringValue ?? string.Empty),
        TypedKeyKind.Guid => GuidValue.GetHashCode(),
        _ => IntValue,
    };

    public static bool operator ==(TypedKey left, TypedKey right) => left.Equals(right);
    public static bool operator !=(TypedKey left, TypedKey right) => !left.Equals(right);

    public override string ToString() => Kind switch
    {
        TypedKeyKind.String => StringValue ?? string.Empty,
        TypedKeyKind.Guid => GuidValue.ToString(),
        _ => IntValue.ToString(),
    };
}
