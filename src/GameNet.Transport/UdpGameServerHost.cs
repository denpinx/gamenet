using GameNet.Core;

namespace GameNet.Transport;

/// <summary>
/// Convenience host that owns a <see cref="UdpServerTransport"/> and the <see cref="GameServer"/> on
/// top of it. Use it for console apps/demos; keep the two objects separate when you want to inject a
/// different <see cref="IServerTransport"/> (tests, custom sockets, WebSocket, Steam, ...).
/// </summary>
public sealed class UdpGameServerHost : IDisposable
{
    public UdpGameServerHost(
        int port,
        ServerOptions? options = null,
        ImpairmentOptions? impairment = null,
        bool unorderedState = true,
        uint schemaHash = 0)
    {
        Transport = new UdpServerTransport(port, impairment, unorderedState, schemaHash);
        Server = new GameServer(Transport, options);
    }

    public UdpServerTransport Transport { get; }
    public GameServer Server { get; }
    public int Port => Transport.Port;

    public void Start(bool autoTick = false, double tickRate = 30) => Server.Start(autoTick, tickRate);

    public void Dispose()
    {
        Server.StopAutoTick();
        Transport.Dispose();
    }
}
