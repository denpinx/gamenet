using System.Collections.Concurrent;

namespace GameNet.Core;

public delegate void PacketWriteDelegate(ByteWriter writer, object value);
public delegate object PacketReadDelegate(ref ByteReader reader);

/// <summary>
/// Registry of [Packet] codecs for dynamic RPC. Generated partial types register themselves via a
/// module initializer, so group methods can accept/return arbitrary packet types, not just primitives.
/// </summary>
public static class RpcPacketCodecs
{
    private readonly record struct Entry(PacketWriteDelegate Write, PacketReadDelegate Read);

    private static readonly ConcurrentDictionary<Type, Entry> Entries = new();

    public static void Register(Type type, PacketWriteDelegate write, PacketReadDelegate read) =>
        Entries[type] = new Entry(write, read);

    public static bool TryWrite(object value, out byte[] bytes)
    {
        if (!Entries.TryGetValue(value.GetType(), out var entry))
        {
            bytes = Array.Empty<byte>();
            return false;
        }
        var writer = new ByteWriter(64);
        entry.Write(writer, value);
        bytes = writer.ToArray();
        return true;
    }

    public static bool TryRead(Type type, byte[] payload, out object? value)
    {
        if (!Entries.TryGetValue(type, out var entry))
        {
            value = null;
            return false;
        }
        var reader = new ByteReader(payload);
        value = entry.Read(ref reader);
        return true;
    }
}
