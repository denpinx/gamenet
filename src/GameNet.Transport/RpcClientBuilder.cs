using GameNet.Core;

namespace GameNet.Transport;

/// <summary>
/// Fluent client setup. Collections and subscriptions are collected before <see cref="Build"/>;
/// Build connects, flushes queued subscriptions and returns a frozen <see cref="RpcClient"/>.
/// </summary>
public sealed class RpcClientBuilder
{
    private readonly RpcClient _client;
    private readonly List<string> _subscriptions = new();
    private bool _built;
    private string? _resumeToken;

    public RpcClientBuilder(ImpairmentOptions? impairment = null) => _client = new RpcClient(impairment);

    public RpcClientBuilder AddTypedCollection<T>(string name) where T : class, IEntityCodec<T>
    {
        _client.RegisterTypedCollection<T>(name);
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
        foreach (var name in _subscriptions) _client.Subscribe(name);
        if (!_client.Build(host, port, timeoutMs, manualTick))
        {
            _client.Dispose();
            throw new InvalidOperationException($"failed to connect to {host}:{port}");
        }
        _built = true;
        if (_resumeToken is not null) _client.SendResume(_resumeToken);
        return _client;
    }
}
