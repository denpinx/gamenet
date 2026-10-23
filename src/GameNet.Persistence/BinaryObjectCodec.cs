using System.Reflection;
using GameNet.Core;

namespace GameNet.Persistence;

/// <summary>
/// Minimal reflection binary codec used by blob mode. Flat types only (scalars + string/Guid/DateTime);
/// nested decomposition is only supported in column mode.
/// </summary>
internal static class BinaryObjectCodec
{
    public static byte[] Write(object value)
    {
        var writer = new ByteWriter(256);
        WriteValue(ref writer, value, value.GetType());
        return writer.ToArray();
    }

    public static object? Read(byte[] payload, Type type)
    {
        var reader = new ByteReader(payload);
        return ReadValue(ref reader, type);
    }

    private static void WriteValue(ref ByteWriter writer, object? value, Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (value is null)
        {
            writer.WriteBool(false);
            return;
        }
        writer.WriteBool(true);

        if (IsList(t))
        {
            var items = ((System.Collections.IEnumerable)value).Cast<object?>().ToList();
            writer.WriteVarUInt((uint)items.Count);
            var elementType = t.GetGenericArguments()[0];
            foreach (var item in items) WriteValue(ref writer, item, elementType);
            return;
        }

        if (IsObject(t))
        {
            foreach (var property in Properties(t)) WriteValue(ref writer, property.GetValue(value), property.PropertyType);
            return;
        }

        if (t.IsEnum) writer.WriteVarInt(Convert.ToInt32(value));
        else if (t == typeof(int)) writer.WriteVarInt((int)value);
        else if (t == typeof(short)) writer.WriteVarInt((short)value);
        else if (t == typeof(byte)) writer.WriteByte((byte)value);
        else if (t == typeof(bool)) writer.WriteBool((bool)value);
        else if (t == typeof(long))
        {
            var l = (long)value;
            writer.WriteUInt((uint)(l >> 32));
            writer.WriteUInt((uint)l);
        }
        else if (t == typeof(float)) writer.WriteFloat((float)value);
        else if (t == typeof(double))
        {
            var bits = BitConverter.DoubleToInt64Bits((double)value);
            writer.WriteUInt((uint)(bits >> 32));
            writer.WriteUInt((uint)bits);
        }
        else if (t == typeof(string))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes((string)value);
            writer.WriteVarUInt((uint)bytes.Length);
            writer.WriteBytes(bytes);
        }
        else if (t == typeof(Guid)) writer.WriteBytes(((Guid)value).ToByteArray());
        else if (t == typeof(DateTime))
        {
            var ticks = ((DateTime)value).ToUniversalTime().Ticks;
            writer.WriteUInt((uint)(ticks >> 32));
            writer.WriteUInt((uint)ticks);
        }
        else
        {
            throw new NotSupportedException($"blob mode does not support field type {t.Name}; use column mode for nested types");
        }
    }

    private static object? ReadValue(ref ByteReader reader, Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        var present = reader.ReadBool();
        if (!present) return null;

        if (IsList(t))
        {
            var count = (int)reader.ReadVarUInt();
            var elementType = t.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            for (var i = 0; i < count; i++) list.Add(ReadValue(ref reader, elementType));
            return list;
        }

        if (IsObject(t))
        {
            var instance = Activator.CreateInstance(t) ?? throw new InvalidOperationException($"cannot construct {t.Name}");
            foreach (var property in Properties(t))
            {
                var value = ReadValue(ref reader, property.PropertyType);
                if (property.CanWrite) property.SetValue(instance, value);
            }
            return instance;
        }

        if (t.IsEnum) return Enum.ToObject(t, reader.ReadVarInt());
        if (t == typeof(int)) return reader.ReadVarInt();
        if (t == typeof(short)) return (short)reader.ReadVarInt();
        if (t == typeof(byte)) return reader.ReadByte();
        if (t == typeof(bool)) return reader.ReadBool();
        if (t == typeof(long)) return (long)(((ulong)reader.ReadUInt() << 32) | reader.ReadUInt());
        if (t == typeof(float)) return reader.ReadFloat();
        if (t == typeof(double)) return BitConverter.Int64BitsToDouble((long)(((ulong)reader.ReadUInt() << 32) | reader.ReadUInt()));
        if (t == typeof(string)) return System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)reader.ReadVarUInt()).ToArray());
        if (t == typeof(Guid)) return new Guid(reader.ReadBytes(16).ToArray());
        if (t == typeof(DateTime)) return new DateTime((long)(((ulong)reader.ReadUInt() << 32) | reader.ReadUInt()), DateTimeKind.Utc);

        throw new NotSupportedException($"blob mode does not support field type {t.Name}");
    }

    private static bool IsList(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);

    private static bool IsObject(Type t) =>
        !t.IsPrimitive && !t.IsEnum && t != typeof(string) && t != typeof(Guid) &&
        t != typeof(DateTime) && t != typeof(DateTimeOffset) && t != typeof(byte[]) && t != typeof(decimal) &&
        !IsList(t);

    private static IEnumerable<PropertyInfo> Properties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .OrderBy(p => p.Name, StringComparer.Ordinal);
}

public static class ObjectCodec
{
    public static object? Read(byte[] payload, Type type) => BinaryObjectCodec.Read(payload, type);
    public static byte[] Write(object value) => BinaryObjectCodec.Write(value);
}
