namespace GameNet.Core;

/// <summary>Stable key codec for a typed collection. Keys must be deterministic on both sides.</summary>
public interface ITypedKeyCodec<TKey>
{
    string Id { get; }
    void Write(ByteWriter writer, TKey key);
    TKey Read(ref ByteReader reader);
}

public sealed class IntKeyCodec : ITypedKeyCodec<int>
{
    public string Id => "int";
    public void Write(ByteWriter writer, int key) => writer.WriteVarInt(key);
    public int Read(ref ByteReader reader) => reader.ReadVarInt();
}

public sealed class StringKeyCodec : ITypedKeyCodec<string>
{
    public string Id => "string";
    public void Write(ByteWriter writer, string key)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        writer.WriteVarUInt((uint)bytes.Length);
        writer.WriteBytes(bytes);
    }
    public string Read(ref ByteReader reader) =>
        System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)reader.ReadVarUInt()).ToArray());
}

public sealed class GuidKeyCodec : ITypedKeyCodec<Guid>
{
    public string Id => "guid";
    public void Write(ByteWriter writer, Guid key) => writer.WriteBytes(key.ToByteArray());
    public Guid Read(ref ByteReader reader) => new(reader.ReadBytes(16));
}

/// <summary>Registry shared by server and client (e.g. Vec2I, Vec3I).</summary>
public static class TypedKeyCodecs
{
    private static readonly Dictionary<Type, object> Codecs = new();

    static TypedKeyCodecs()
    {
        Register(new IntKeyCodec());
        Register(new StringKeyCodec());
        Register(new GuidKeyCodec());
    }

    public static void Register<TKey>(ITypedKeyCodec<TKey> codec)
    {
        lock (Codecs)
        {
            if (Codecs.TryGetValue(typeof(TKey), out var existing) &&
                existing is ITypedKeyCodec<TKey> typed && typed.Id != codec.Id)
                throw new InvalidOperationException($"{typeof(TKey).Name} already has key codec {typed.Id}");
            Codecs[typeof(TKey)] = codec;
        }
    }

    public static ITypedKeyCodec<TKey> Get<TKey>()
    {
        lock (Codecs)
        {
            if (Codecs.TryGetValue(typeof(TKey), out var codec)) return (ITypedKeyCodec<TKey>)codec;
        }
        throw new InvalidOperationException(
            $"no key codec for {typeof(TKey).Name}; register one with TypedKeyCodecs.Register");
    }
}
