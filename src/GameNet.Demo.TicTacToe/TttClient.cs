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
    private readonly RpcClient _rpc = new();

    public bool Connect(string host, int port) => _rpc.Connect(host, port);

    public ClientWorld World => _rpc.World;

    public bool Subscribe(string name) => _rpc.Subscribe(name);

    public IReadOnlyDictionary<int, EntityState>? EntitiesFor(string name) => _rpc.EntitiesFor(name);

    public Task<Ack> RegisterAsync(string username, string password) =>
        _rpc.CallAsync<RegisterRequest, Ack>(TttMethods.Register, new(username, password));

    public Task<Ack> LoginAsync(string username, string password) =>
        _rpc.CallAsync<RegisterRequest, Ack>(TttMethods.Login, new(username, password));

    public Task<QueueResult> QueueAsync() =>
        _rpc.CallAsync<Empty, QueueResult>(TttMethods.Queue, Empty.Value);

    public Task<MatchView> StateAsync() =>
        _rpc.CallAsync<Empty, MatchView>(TttMethods.State, Empty.Value);

    public Task<Ack> MoveAsync(int cell) =>
        _rpc.CallAsync<MoveRequest, Ack>(TttMethods.Move, new((byte)cell));

    public void Dispose() => _rpc.Dispose();
}