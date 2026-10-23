using GameNet.Core;

namespace GameNet.Transport;

/// <summary>Client helper for the chunk benchmark: owns a UDP transport + a ChunkWorld.</summary>
public sealed class ChunkClient : IDisposable
{
    private readonly UdpClientTransport _transport = new();

    public ChunkWorld World { get; } = new();
    public long BytesReceived => _transport.BytesReceived;
    public int Id => _transport.Id;

    public bool Connect(string host, int port) => _transport.Connect(host, port, 5000);

    public void SendSubscribe()
    {
        var writer = new ByteWriter(2);
        writer.WriteByte((byte)MessageType.Subscribe);
        _transport.Send(0, writer.Written);
    }

    public void Pump()
    {
        _transport.Poll();
        while (_transport.TryDequeue(out var packet))
        {
            if (packet.Channel == 0) World.Apply(packet.Payload);
        }
    }

    public void Dispose() => _transport.Dispose();
}
