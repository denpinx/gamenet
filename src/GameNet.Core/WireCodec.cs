namespace GameNet.Core;

/// <summary>
/// Compile-time wire contract. Implement on request/response types to get typed RPC calls
/// without hand-written byte juggling at every call site.
/// </summary>
public interface IWireCodec<T> where T : IWireCodec<T>
{
    static abstract void Write(ByteWriter writer, T value);
    static abstract T Read(ref ByteReader reader);
}

public readonly struct Empty : IWireCodec<Empty>
{
    public static readonly Empty Value = default;
    public static void Write(ByteWriter writer, Empty value) { }
    public static Empty Read(ref ByteReader reader) => default;
}

public sealed class RpcException : Exception
{
    public RpcException(RpcStatus status, string message) : base(message) => Status = status;
    public RpcStatus Status { get; }
}
