namespace GameNet.Core;

public enum RpcStatus : byte
{
    Ok = 0,
    NotFound = 1,
    Error = 2,
    Denied = 3,
}

public delegate byte[] RpcHandler(Session session, byte[] payload);

/// <summary>Method-id based RPC dispatch. No reflection, no string routing.</summary>
public sealed class RpcDispatcher
{
    private readonly Dictionary<ushort, RpcHandler> _handlers = new();

    public void Register(ushort methodId, RpcHandler handler) => _handlers[methodId] = handler;

    public byte[] Invoke(ushort methodId, Session session, ReadOnlySpan<byte> payload, out RpcStatus status)
    {
        if (!_handlers.TryGetValue(methodId, out var handler))
        {
            status = RpcStatus.NotFound;
            return Array.Empty<byte>();
        }

        try
        {
            var result = handler(session, payload.ToArray());
            status = RpcStatus.Ok;
            return result;
        }
        catch
        {
            status = RpcStatus.Error;
            return Array.Empty<byte>();
        }
    }
}

/// <summary>Well-known RPC method ids used by the samples and the benchmark.</summary>
public static class RpcMethods
{
    public const ushort CreateHero = 1;
    public const ushort Ping = 2;
}

/// <summary>Wire shape of the CreateHero RPC (record with explicit positional order).</summary>
public sealed record CreateHeroRequest(string Name, int TeamId)
{
    public void Write(ByteWriter writer)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Name);
        writer.WriteVarUInt((uint)bytes.Length);
        writer.WriteBytes(bytes);
        writer.WriteVarInt(TeamId);
    }

    public static CreateHeroRequest Read(ByteReader reader)
    {
        var length = (int)reader.ReadVarUInt();
        var name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
        var team = reader.ReadVarInt();
        return new CreateHeroRequest(name, team);
    }
}

public sealed record CreateHeroResponse(int EntityId, float X, float Y)
{
    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(EntityId);
        writer.WriteFloat(X);
        writer.WriteFloat(Y);
    }

    public static CreateHeroResponse Read(ByteReader reader) =>
        new(reader.ReadVarInt(), reader.ReadFloat(), reader.ReadFloat());
}
