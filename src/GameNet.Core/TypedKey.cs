namespace GameNet.Core;

public enum TypedKeyKind : byte
{
    Int = 0,
    String = 1,
    Guid = 2,
    Custom = 3,
}

public delegate void TypedKeyWriter<in T>(ByteWriter writer, T value);
public delegate T TypedKeyReader<T>(ref ByteReader reader);
public delegate object TypedKeyObjectReader(ref ByteReader reader);

/// <summary>
/// Stable wire key for a typed collection. Built-ins are int/string/Guid; any other struct or
/// class can be registered with <see cref="TypedKeyRegistry"/> and used as a custom key.
/// Hash codes are deliberately not used as keys.
/// </summary>
public readonly struct TypedKey : IEquatable<TypedKey>
{
    public TypedKeyKind Kind { get; }
    public int IntValue { get; }
    public string? StringValue { get; }
    public Guid GuidValue { get; }
    public string? CodecId { get; }
    public byte[]? Data { get; }

    private TypedKey(TypedKeyKind kind, int intValue, string? stringValue, Guid guidValue, string? codecId = null, byte[]? data = null)
    {
        Kind = kind;
        IntValue = intValue;
        StringValue = stringValue;
        GuidValue = guidValue;
        CodecId = codecId;
        Data = data;
    }

    public static implicit operator TypedKey(int value) => new(TypedKeyKind.Int, value, null, default);
    public static implicit operator TypedKey(string value) => new(TypedKeyKind.String, 0, value, default);
    public static implicit operator TypedKey(Guid value) => new(TypedKeyKind.Guid, 0, null, value);

    /// <summary>Creates a key from a registered custom type (Vector2I, Vector3I, ...).</summary>
    public static TypedKey From<T>(T value)
    {
        if (value is int intValue) return intValue;
        if (value is string stringValue) return stringValue;
        if (value is Guid guidValue) return guidValue;

        var codec = TypedKeyRegistry.ForType(typeof(T));
        var writer = new ByteWriter(32);
        codec.Write(writer, value!);
        return new TypedKey(TypedKeyKind.Custom, 0, null, default, codec.Id, writer.ToArray());
    }

    internal static TypedKey Custom(string codecId, byte[] data) =>
        new(TypedKeyKind.Custom, 0, null, default, codecId, data);

    /// <summary>Decodes a custom key back to its original type; built-ins are not supported here.</summary>
    public T To<T>()
    {
        if (Kind != TypedKeyKind.Custom) throw new InvalidOperationException($"key is {Kind}, not custom");
        var codec = TypedKeyRegistry.ForId(CodecId!);
        var reader = new ByteReader(Data!);
        return (T)codec.Read(ref reader);
    }

    public bool Equals(TypedKey other)
    {
        if (Kind != other.Kind) return false;
        return Kind switch
        {
            TypedKeyKind.String => string.Equals(StringValue, other.StringValue, StringComparison.Ordinal),
            TypedKeyKind.Guid => GuidValue == other.GuidValue,
            TypedKeyKind.Custom => string.Equals(CodecId, other.CodecId, StringComparison.Ordinal)
                                   && (Data ?? Array.Empty<byte>()).AsSpan().SequenceEqual(other.Data ?? Array.Empty<byte>()),
            _ => IntValue == other.IntValue,
        };
    }

    public override bool Equals(object? obj) => obj is TypedKey other && Equals(other);

    public override int GetHashCode()
    {
        if (Kind == TypedKeyKind.Custom)
        {
            var hash = new HashCode();
            hash.Add(CodecId, StringComparer.Ordinal);
            hash.AddBytes(Data ?? Array.Empty<byte>());
            return hash.ToHashCode();
        }
        return Kind switch
        {
            TypedKeyKind.String => StringComparer.Ordinal.GetHashCode(StringValue ?? string.Empty),
            TypedKeyKind.Guid => GuidValue.GetHashCode(),
            _ => IntValue,
        };
    }

    public static bool operator ==(TypedKey left, TypedKey right) => left.Equals(right);
    public static bool operator !=(TypedKey left, TypedKey right) => !left.Equals(right);

    public override string ToString() => Kind switch
    {
        TypedKeyKind.String => StringValue ?? string.Empty,
        TypedKeyKind.Guid => GuidValue.ToString(),
        TypedKeyKind.Custom => $"{CodecId}:{Convert.ToHexString(Data ?? Array.Empty<byte>())}",
        _ => IntValue.ToString(),
    };
}

/// <summary>Registry of stable key codecs shared by server and client.</summary>
public static class TypedKeyRegistry
{
    public sealed class Codec
    {
        internal Codec(string id, Type type, Action<ByteWriter, object> write,
            TypedKeyObjectReader read)
        {
            Id = id;
            Type = type;
            Write = write;
            Read = read;
        }

        public string Id { get; }
        public Type Type { get; }
        internal Action<ByteWriter, object> Write { get; }
        internal TypedKeyObjectReader Read { get; }
    }

    private static readonly Dictionary<Type, Codec> ByType = new();
    private static readonly Dictionary<string, Codec> ById = new(StringComparer.Ordinal);

    public static void Register<T>(string id, TypedKeyWriter<T> write, TypedKeyReader<T> read)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("codec id is required", nameof(id));
        var codec = new Codec(
            id,
            typeof(T),
            (writer, value) => write(writer, (T)value),
            delegate(ref ByteReader reader) { return (object)read(ref reader)!; });
        lock (ByType)
        {
            if (ByType.TryGetValue(typeof(T), out var existing) && existing.Id != id)
                throw new InvalidOperationException($"key type {typeof(T).Name} is already registered as {existing.Id}");
            if (ById.TryGetValue(id, out var byId) && byId.Type != typeof(T))
                throw new InvalidOperationException($"key codec {id} is already registered for {byId.Type.Name}");
            ByType[typeof(T)] = codec;
            ById[id] = codec;
        }
    }

    internal static Codec ForType(Type type)
    {
        lock (ByType)
            return ByType.TryGetValue(type, out var codec)
                ? codec
                : throw new InvalidOperationException($"key type {type.Name} is not registered; call TypedKeyRegistry.Register first");
    }

    internal static Codec ForId(string id)
    {
        lock (ByType)
            return ById.TryGetValue(id, out var codec)
                ? codec
                : throw new InvalidOperationException($"key codec {id} is not registered on this side");
    }
}
