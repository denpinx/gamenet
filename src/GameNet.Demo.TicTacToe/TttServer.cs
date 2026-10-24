using System.Collections.Concurrent;
using System.Security.Cryptography;
using GameNet.Core;
using GameNet.Persistence;
using GameNet.Transport;

namespace GameNet.Demo.TicTacToe;

public sealed class UserRow
{
    [Key] public string Username { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed class Match
{
    public int Id { get; init; }
    public int PlayerA { get; init; }
    public int PlayerB { get; init; }
    public TicTacToeGame Game { get; } = new();

    public byte SymbolOf(int session) => session == PlayerA ? (byte)1 : (byte)2;
    public int OpponentOf(int session) => session == PlayerA ? PlayerB : PlayerA;
}

/// <summary>Turn-based demo server: SQLite accounts, matchmaking queue, RPC-only gameplay.</summary>
[RpcGroup("ttt")]
public sealed class TttServer : IDisposable
{
    private readonly UdpGameServerHost _host;
    private readonly GameServer _server;
    private readonly TypedCollection<MatchEntity> _matches;
    private readonly SqliteStore<UserRow> _users;
    private readonly TypedCollection<PlayerEntity> _players;
    private readonly Queue<int> _queue = new();
    private readonly ConcurrentDictionary<int, Match> _bySession = new();
    private int _nextMatchId = 1;

    public TttServer(string databasePath, int port = 0)
    {
        _host = new UdpGameServerHostBuilder(port)
            .WithOptions(new ServerOptions { AutoSubscribe = false, AoiRadius = 0 })
            .AddTypedCollection<MatchEntity>("matches", matches =>
                matches.Filter = (session, match) => match.PlayerA == session.Id || match.PlayerB == session.Id)
            .AddTypedCollection<PlayerEntity>("players", players =>
                players.Filter = (session, player) => player.Id == session.Id)
            .Build();
        _server = _host.Server;
        _matches = _server.GetTypedCollection<MatchEntity>("matches");
        _players = _server.GetTypedCollection<PlayerEntity>("players");
        _users = new SqliteStore<UserRow>($"Data Source={databasePath}");
        _users.EnsureSchema();

        _server.RpcGroups.AddGroup(this);

        _server.SessionJoined += session => _players.Spawn(session.Id, new PlayerEntity { Id = session.Id });
        _server.SessionLeft += OnDisconnected;
        _host.Start(autoTick: true, tickRate: 30);
    }

    private static MatchEntity ToEntity(Match match) => new()
    {
        Id = match.Id,
        PlayerA = match.PlayerA,
        PlayerB = match.PlayerB,
        Turn = match.Game.Turn,
        Status = match.Game.Status,
        Winner = match.Game.Winner,
        Board = (byte[])match.Game.Cells.Clone(),
    };
    public int Port => _host.Port;



    [Rpc] public Ack Register(string username, string password)
    {
        username = username.Trim();
        if (username.Length is < 2 or > 32) return new Ack(false, "用户名长度需在 2..32");
        if (password.Length < 3) return new Ack(false, "密码至少 3 位");
        if (_users.Load(username) is not null) return new Ack(false, "用户名已存在");

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        _users.Save(new UserRow
        {
            Username = username,
            Salt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(hash),
            CreatedAt = DateTime.UtcNow,
        });
        return new Ack(true, "注册成功");
    }

    [Rpc] public Ack Login(Session session, string username, string password)
    {
        username = username.Trim();
        var user = _users.Load(username);
        if (user is null || !Verify(password, user)) return new Ack(false, "用户名或密码错误");

        // Spawn a fresh object: the collection diffs against the last sent instance.
        _players.Spawn(session.Id, new PlayerEntity
        {
            Id = session.Id,
            Name = user.Username,
            Authenticated = true,
        });
        return new Ack(true, "登录成功");
    }

    private static bool Verify(string password, UserRow user)
    {
        try
        {
            var salt = Convert.FromBase64String(user.Salt);
            var expected = Convert.FromBase64String(user.PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    [Rpc] public QueueResult Queue(Session session)
    {
        if (!_players.TryGet(session.Id, out var player) || !player.Authenticated)
            return new QueueResult(MatchStatus.Idle, "请先登录");

        if (_bySession.TryGetValue(session.Id, out var existing))
            return new QueueResult(MatchStatus.Playing, $"对局 #{existing.Id}");

        if (!_queue.Contains(session.Id)) _queue.Enqueue(session.Id);

        var candidates = new List<int>();
        while (_queue.Count > 0 && candidates.Count < 2)
        {
            var id = _queue.Dequeue();
            if (_bySession.ContainsKey(id)) continue;
            if (!_players.TryGet(id, out var p) || !p.Authenticated) continue;
            candidates.Add(id);
        }

        if (candidates.Count < 2)
        {
            foreach (var id in candidates) _queue.Enqueue(id);
            return new QueueResult(MatchStatus.Queued, "等待对手…");
        }

        var match = new Match { Id = _nextMatchId++, PlayerA = candidates[0], PlayerB = candidates[1] };
        _bySession[candidates[0]] = match;
        _bySession[candidates[1]] = match;
        _matches.Spawn(match.Id, ToEntity(match));
        return new QueueResult(MatchStatus.Playing, $"匹配成功，对局 #{match.Id}");
    }

    [Rpc] public MatchView State(Session session)
    {
        if (!_bySession.TryGetValue(session.Id, out var match))
        {
            var status = _queue.Contains(session.Id) ? MatchStatus.Queued : MatchStatus.Idle;
            return new MatchView(status, 0, 0, 0, new byte[9], string.Empty, 0);
        }

        var game = match.Game;
        var opponentId = match.OpponentOf(session.Id);
        var opponent = _players.TryGet(opponentId, out var p) ? p.Name : $"player{opponentId}";
        return new MatchView(game.Status, match.SymbolOf(session.Id), game.Turn, game.Winner,
            (byte[])game.Cells.Clone(), opponent, match.Id);
    }

    [Rpc] public Ack Move(Session session, int cell)
    {
        if (cell is < 0 or > 8) return new Ack(false, "格子编号必须是 0..8");
        if (!_bySession.TryGetValue(session.Id, out var match)) return new Ack(false, "你不在对局中");

        var symbol = match.SymbolOf(session.Id);
        var ok = match.Game.TryMove(symbol, cell, out var error);
        if (ok) _matches.Spawn(match.Id, ToEntity(match));
        return new Ack(ok, ok ? "ok" : error);
    }
    private void OnDisconnected(Session session)
    {
        _players.Despawn(session.Id);
        if (_bySession.TryRemove(session.Id, out var match))
        {
            var opponent = match.OpponentOf(session.Id);
            match.Game.Forfeit(match.SymbolOf(session.Id));
            _matches.Spawn(match.Id, ToEntity(match));
            _bySession.TryRemove(opponent, out _);
        }
    }

    public void Dispose() => _host.Dispose();

}
