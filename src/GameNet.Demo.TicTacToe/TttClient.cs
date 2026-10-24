using GameNet.Core;
using GameNet.Transport;

namespace GameNet.Demo.TicTacToe;

[Packet]
public readonly partial record struct MatchView(
    MatchStatus Status,
    byte MySymbol,
    byte Turn,
    byte Winner,
    byte[] Cells,
    string Opponent,
    int MatchId)
{
    public bool IsMyTurn => Status == MatchStatus.Playing && Turn == MySymbol;
}

/// <summary>Thin typed wrapper — connection is one line, each call is one line.</summary>
public sealed class TttClient : IDisposable
{
    private RpcClient? _rpc;
    private string _host = "127.0.0.1";
    private int _port;

    public bool Connect(string host, int port)
    {
        _host = host;
        _port = port;
        try
        {
            _rpc = new RpcClientBuilder()
                .AddTypedCollection<MatchBoard>("matches")
                .Subscribe("matches")
                .Build(host, port);
            Matches = _rpc.Typed<MatchBoard>("matches");
            return true;
        }
        catch (InvalidOperationException)
        {
            _rpc = null;
            return false;
        }
    }

    public TypedWorld<MatchBoard> Matches { get; private set; } = null!;

    public Task<bool> ReconnectAsync() =>
        _rpc is null ? Task.FromResult(false) : _rpc.ReconnectAsync(_host, _port);

    public ClientWorld World => _rpc!.World;

    public bool Subscribe(string name) => _rpc!.Subscribe(name);

    public Task<Ack> RegisterAsync(string username, string password) =>
        _rpc!.Group("ttt").Call<Ack>("register", username, password);

    public Task<Ack> LoginAsync(string username, string password) =>
        _rpc!.Group("ttt").Call<Ack>("login", username, password);

    public Task<QueueResult> QueueAsync() =>
        _rpc!.Group("ttt").Call<QueueResult>("queue");

    public Task<MatchView> StateAsync() =>
        _rpc!.Group("ttt").Call<MatchView>("state");

    public Task<Ack> MoveAsync(int cell) =>
        _rpc!.Group("ttt").Call<Ack>("move", cell);

    public void Dispose() => _rpc?.Dispose();
}
