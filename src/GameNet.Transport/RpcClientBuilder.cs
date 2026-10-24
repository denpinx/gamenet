using GameNet.Core;

namespace GameNet.Transport;

/// <summary>
/// Fluent client setup. Collections and subscriptions are collected before <see cref="Build"/>;
/// Build connects, flushes queued subscriptions and returns a frozen <see cref="RpcClient"/>.
/// </summary>
public sealed class RpcClientBuilder
{
    private readonly ImpairmentOptions? _impairment;
    private readonly List<string> _subscriptions = new();
    private RpcClient? _client;
    private string? _resumeToken;
    private double _tickRate;
    private bool _built;

    public RpcClientBuilder(ImpairmentOptions? impairment = null, double tickRate = 30)
    {
        _impairment = impairment;
        _tickRate = tickRate;
    }

    public RpcClientBuilder WithTickRate(double tickRate)
    {
        if (_client is not null) throw new InvalidOperationException("configure the tick rate before adding collections");
        _tickRate = tickRate;
        return this;
    }

    private RpcClient Client => _client ??= new RpcClient(_impairment, _tickRate);

    public RpcClientBuilder AddTypedCollection<TKey, T>(string name)
        where TKey : notnull
        where T : class, IEntityCodec<T>
    {
        Client.RegisterTypedCollection<TKey, T>(name);
        return this;
    }

    public RpcClientBuilder WithResumeToken(string token)
    {
        _resumeToken = token;
        return this;
    }

    public RpcClientBuilder Subscribe(string name)
    {
        _subscriptions.Add(name);
        return this;
    }

    public RpcClient Build(string host, int port, int timeoutMs = 5000, bool manualTick = false)
    {
        if (_built) throw new InvalidOperationException("builder was already built");
        var client = Client;
        foreach (var name in _subscriptions) client.Subscribe(name);
        if (!client.Build(host, port, timeoutMs, manualTick))
        {
            client.Dispose();
            throw new InvalidOperationException($"failed to connect to {host}:{port}");
        }
        _built = true;
        if (_resumeToken is not null) client.SendResume(_resumeToken);
        return client;
    }
}
