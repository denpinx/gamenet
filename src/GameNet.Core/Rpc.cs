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
