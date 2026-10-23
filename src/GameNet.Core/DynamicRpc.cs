using System.Reflection;

namespace GameNet.Core;

/// <summary>One dynamically typed RPC argument or result (a small JSON-like value set).</summary>
public readonly struct RpcValue
{
    public const byte TagNull = 0;
    public const byte TagBool = 1;
    public const byte TagInt = 2;
    public const byte TagDouble = 3;
    public const byte TagString = 4;
    public const byte TagBytes = 5;
    public const byte TagPacket = 6;

    public readonly byte Tag;
    public readonly long Int;
    public readonly double Real;
    public readonly string? Text;
    public readonly byte[]? Data;

    private RpcValue(byte tag, long i = 0, double r = 0, string? s = null, byte[]? d = null)
    {
        Tag = tag;
        Int = i;
        Real = r;
        Text = s;
        Data = d;
    }

    public static RpcValue Null => default;
    public static RpcValue From(bool v) => new(TagBool, i: v ? 1 : 0);
    public static RpcValue From(long v) => new(TagInt, i: v);
    public static RpcValue From(double v) => new(TagDouble, r: v);
    public static RpcValue From(string v) => new(TagString, s: v);
    public static RpcValue From(byte[] v) => new(TagBytes, d: v);
    public static RpcValue Packet(byte[] v) => new(TagPacket, d: v);

    public static RpcValue From(object? value) => value switch
    {
        null => Null,
        bool b => From(b),
        byte or sbyte or short or ushort or int or uint or long or ulong => From(Convert.ToInt64(value)),
        float or double or decimal => From(Convert.ToDouble(value)),
        string s => From(s),
        byte[] bytes => From(bytes),
        _ => RpcPacketCodecs.TryWrite(value, out var packet)
            ? Packet(packet)
            : throw new InvalidOperationException($"dynamic RPC cannot send {value.GetType().Name}; mark it [Packet] or pass primitives"),
    };

    public object? To(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(bool)) return Int != 0;
        if (target == typeof(byte)) return (byte)Int;
        if (target == typeof(sbyte)) return (sbyte)Int;
        if (target == typeof(short)) return (short)Int;
        if (target == typeof(ushort)) return (ushort)Int;
        if (target == typeof(int)) return (int)Int;
        if (target == typeof(uint)) return (uint)Int;
        if (target == typeof(long)) return Int;
        if (target == typeof(ulong)) return (ulong)Int;
        if (target == typeof(float)) return (float)Real;
        if (target == typeof(double)) return Real;
        if (target == typeof(decimal)) return (decimal)Real;
        if (target == typeof(string)) return Text ?? string.Empty;
        if (target == typeof(byte[])) return Data ?? Array.Empty<byte>();
        if (target.IsEnum) return Enum.ToObject(target, Int);
        if (Tag == TagPacket && Data is not null)
        {
            if (RpcPacketCodecs.TryRead(target, Data, out var packet)) return packet;
            throw new InvalidOperationException($"dynamic RPC cannot decode {target.Name}: packet codec is not registered");
        }
        throw new InvalidOperationException($"dynamic RPC cannot convert value to {target.Name}");
    }

    public T As<T>()
    {
        var value = To(typeof(T));
        return value is T typed ? typed : (T)Convert.ChangeType(value!, typeof(T));
    }
}

public static class RpcValueCodec
{
    public static void Write(ref ByteWriter writer, in RpcValue value)
    {
        writer.WriteByte(value.Tag);
        switch (value.Tag)
        {
            case RpcValue.TagNull:
                break;
            case RpcValue.TagBool:
                writer.WriteBool(value.Int != 0);
                break;
            case RpcValue.TagInt:
                writer.WriteUInt((uint)(value.Int >> 32));
                writer.WriteUInt((uint)value.Int);
                break;
            case RpcValue.TagDouble:
                var bits = BitConverter.DoubleToInt64Bits(value.Real);
                writer.WriteUInt((uint)(bits >> 32));
                writer.WriteUInt((uint)bits);
                break;
            case RpcValue.TagString:
                var text = System.Text.Encoding.UTF8.GetBytes(value.Text ?? string.Empty);
                writer.WriteVarUInt((uint)text.Length);
                writer.WriteBytes(text);
                break;
            case RpcValue.TagBytes:
            case RpcValue.TagPacket:
                writer.WriteVarUInt((uint)(value.Data?.Length ?? 0));
                writer.WriteBytes(value.Data ?? Array.Empty<byte>());
                break;
        }
    }

    public static RpcValue Read(ref ByteReader reader)
    {
        return reader.ReadByte() switch
        {
            RpcValue.TagBool => RpcValue.From(reader.ReadBool()),
            RpcValue.TagInt => RpcValue.From((long)(((ulong)reader.ReadUInt() << 32) | reader.ReadUInt())),
            RpcValue.TagDouble => RpcValue.From(BitConverter.Int64BitsToDouble((long)(((ulong)reader.ReadUInt() << 32) | reader.ReadUInt()))),
            RpcValue.TagString => RpcValue.From(System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)reader.ReadVarUInt()).ToArray())),
            RpcValue.TagBytes => RpcValue.From(reader.ReadBytes((int)reader.ReadVarUInt()).ToArray()),
            RpcValue.TagPacket => RpcValue.Packet(reader.ReadBytes((int)reader.ReadVarUInt()).ToArray()),
            _ => RpcValue.Null,
        };
    }

    public static void WriteString(ref ByteWriter writer, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        writer.WriteVarUInt((uint)bytes.Length);
        writer.WriteBytes(bytes);
    }

    public static string ReadString(ref ByteReader reader) =>
        System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)reader.ReadVarUInt()).ToArray());
}

public enum RpcGroupLifetime
{
    /// <summary>One instance shared by every connection.</summary>
    Singleton,

    /// <summary>One instance per connection (session); disposed when the connection drops.</summary>
    PerConnection,
}

/// <summary>
/// Reflection-based RPC groups: [RpcGroup("math")] on a class, [Rpc] on methods.
/// Registration is once at startup; dispatch is a dictionary lookup plus one reflection invoke.
/// Groups can be singletons or per-connection, and can be built through constructor injection.
/// </summary>
public sealed class RpcGroupRegistry
{
    private sealed class Registration
    {
        public readonly Dictionary<string, MethodInfo> Methods = new(StringComparer.OrdinalIgnoreCase);
        public Func<object> Factory = null!;
        public RpcGroupLifetime Lifetime;
        public object? Singleton;
        public readonly Dictionary<int, object> PerConnection = new();

        public object InstanceFor(int sessionId)
        {
            if (Lifetime == RpcGroupLifetime.Singleton) return Singleton!;
            if (!PerConnection.TryGetValue(sessionId, out var instance))
            {
                instance = Factory();
                PerConnection[sessionId] = instance;
            }
            return instance;
        }
    }

    private readonly Dictionary<string, Registration> _groups = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> GroupNames => _groups.Keys;

    /// <summary>Resolves [FromCollection(name)] constructor parameters; wired by GameServer.</summary>
    public Func<string, object?>? CollectionResolver { get; set; }

    // ---- singleton registrations -------------------------------------------------------------

    public void AddGroup(object instance) =>
        Add(instance.GetType(), () => instance, RpcGroupLifetime.Singleton, instance);

    public void AddGroup<T>() where T : new() =>
        Add(typeof(T), () => new T(), RpcGroupLifetime.Singleton);

    public void AddGroup<T>(IServiceProvider services) where T : class =>
        Add(typeof(T), () => CreateWithServices(typeof(T), services), RpcGroupLifetime.Singleton);

    public void AddGroup<T>(Func<T> factory) where T : class =>
        Add(typeof(T), () => factory()!, RpcGroupLifetime.Singleton);

    // ---- per-connection registrations --------------------------------------------------------

    public void AddGroupPerConnection<T>() where T : new() =>
        Add(typeof(T), () => new T(), RpcGroupLifetime.PerConnection);

    public void AddGroupPerConnection<T>(IServiceProvider services) where T : class =>
        Add(typeof(T), () => CreateWithServices(typeof(T), services), RpcGroupLifetime.PerConnection);

    public void AddGroupPerConnection<T>(Func<T> factory) where T : class =>
        Add(typeof(T), () => factory()!, RpcGroupLifetime.PerConnection);

    private void Add(Type type, Func<object> factory, RpcGroupLifetime lifetime, object? instance = null)
    {
        var name = type.GetCustomAttribute<RpcGroupAttribute>()?.Name
                   ?? throw new InvalidOperationException($"{type.Name} is missing [RpcGroup]");
        if (_groups.ContainsKey(name))
            throw new InvalidOperationException($"RPC group '{name}' is already registered");

        var registration = new Registration { Factory = factory, Lifetime = lifetime };
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = method.GetCustomAttribute<RpcAttribute>();
            if (attribute is null) continue;
            registration.Methods[attribute.Name ?? method.Name] = method;
        }

        if (lifetime == RpcGroupLifetime.Singleton) registration.Singleton = instance ?? factory();
        _groups[name] = registration;
    }

    /// <summary>Drops per-connection group instances for a disconnected session.</summary>
    public void RemoveSession(int sessionId)
    {
        foreach (var registration in _groups.Values)
        {
            if (registration.Lifetime != RpcGroupLifetime.PerConnection) continue;
            if (registration.PerConnection.Remove(sessionId, out var instance) && instance is IDisposable disposable)
                disposable.Dispose();
        }
    }

    public bool TryInvoke(int sessionId, string group, string method, RpcValue[] args, out RpcValue result, out RpcStatus status)
    {
        result = RpcValue.Null;
        if (!_groups.TryGetValue(group, out var registration) || !registration.Methods.TryGetValue(method, out var target))
        {
            status = RpcStatus.NotFound;
            return false;
        }

        var parameters = target.GetParameters();
        if (parameters.Length != args.Length)
        {
            status = RpcStatus.Error;
            result = RpcValue.From($"method {group}.{method} expects {parameters.Length} arguments, got {args.Length}");
            return false;
        }

        var converted = new object?[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            try
            {
                converted[i] = args[i].To(parameters[i].ParameterType);
            }
            catch (Exception ex)
            {
                status = RpcStatus.Error;
                result = RpcValue.From($"argument {parameters[i].Name}: {ex.Message}");
                return false;
            }
        }

        try
        {
            var instance = registration.InstanceFor(sessionId);
            var value = target.Invoke(instance, converted);
            result = RpcValue.From(value);
            status = RpcStatus.Ok;
            return true;
        }
        catch (TargetInvocationException ex)
        {
            status = RpcStatus.Error;
            result = RpcValue.From(ex.InnerException?.Message ?? ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            status = RpcStatus.Error;
            result = RpcValue.From(ex.Message);
            return false;
        }
    }

    private object CreateWithServices(Type type, IServiceProvider services)
    {
        foreach (var constructor in type.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
        {
            var parameters = constructor.GetParameters();
            var arguments = new object?[parameters.Length];
            var resolved = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                var collection = parameters[i].GetCustomAttribute<FromCollectionAttribute>();
                object? service;
                if (collection is not null)
                {
                    service = CollectionResolver?.Invoke(collection.Name);
                }
                else
                {
                    service = services.GetService(parameters[i].ParameterType);
                }

                if (service is null && parameters[i].HasDefaultValue) service = parameters[i].DefaultValue;
                if (service is null)
                {
                    resolved = false;
                    break;
                }
                arguments[i] = service;
            }

            if (resolved) return constructor.Invoke(arguments);
        }

        throw new InvalidOperationException(
            $"cannot construct RPC group {type.Name}: no constructor whose parameters are all registered in the service provider");
    }
}
