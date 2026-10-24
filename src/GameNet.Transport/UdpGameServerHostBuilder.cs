using GameNet.Core;

namespace GameNet.Transport;

/// <summary>
/// Fluent server setup: transport + GameServer + collections + RPC groups, then Start.
/// </summary>
public sealed class UdpGameServerHostBuilder
{
    private readonly int _port;
    private ServerOptions _options = new();
    private ImpairmentOptions? _impairment;
    private bool _unorderedState = true;
    private uint _schemaHash;
    private readonly List<Action<GameServer>> _configure = new();

    public UdpGameServerHostBuilder(int port) => _port = port;

    public UdpGameServerHostBuilder WithOptions(ServerOptions options)
    {
        _options = options;
        return this;
    }

    public UdpGameServerHostBuilder WithImpairment(ImpairmentOptions impairment)
    {
        _impairment = impairment;
        return this;
    }

    public UdpGameServerHostBuilder WithUnorderedState(bool unorderedState)
    {
        _unorderedState = unorderedState;
        return this;
    }

    public UdpGameServerHostBuilder WithSchemaHash(uint schemaHash)
    {
        _schemaHash = schemaHash;
        return this;
    }

    public UdpGameServerHostBuilder AddTypedCollection<TKey, T>(
        string name,
        Action<TypedCollection<TKey, T>>? configure = null)
        where TKey : notnull
        where T : class, IEntityCodec<T>
    {
        _configure.Add(server =>
        {
            var collection = server.RegisterTypedCollection<TKey, T>(name);
            configure?.Invoke(collection);
        });
        return this;
    }

    public UdpGameServerHostBuilder AddCollection(string name, EntityCollection collection)
    {
        _configure.Add(server => server.RegisterCollection(name, collection));
        return this;
    }

    public UdpGameServerHostBuilder AddRpcGroup(object instance)
    {
        _configure.Add(server => server.RpcGroups.AddGroup(instance));
        return this;
    }

    public UdpGameServerHostBuilder AddRpcGroup<T>() where T : class, new()
    {
        _configure.Add(server => server.RpcGroups.AddGroup<T>());
        return this;
    }

    public UdpGameServerHostBuilder AddRpcGroup(Func<GameServer, object> factory)
    {
        _configure.Add(server => server.RpcGroups.AddGroup(factory(server)));
        return this;
    }

    public UdpGameServerHost Build(bool autoTick = false, double tickRate = 30)
    {
        var host = new UdpGameServerHost(_port, _options, _impairment, _unorderedState, _schemaHash);
        foreach (var configure in _configure) configure(host.Server);
        if (autoTick) host.Start(true, tickRate);
        return host;
    }
}
