using System.Collections.Concurrent;
using System.Security.Cryptography;
using GameNet.Core;
using GameNet.Generated.GameNet.Demo.TicTacToe;
using GameNet.Persistence;
using GameNet.Transport;

namespace GameNet.Demo.TicTacToe;

public static class TttMethods
{
    public const ushort Register = 100;
    public const ushort Login = 101;
    public const ushort Queue = 102;
    public const ushort State = 103;
    public const ushort Move = 104;
}

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
public sealed class TttServer : IDisposable
{
    private readonly UdpGameServerHost _host;
    private readonly GameServer _server;
    private readonly TypedCollection<MatchEntity> _matches;
    private readonly SqliteStore<UserRow> _users;
    private readonly ConcurrentDictionary<int, Player> _players = new();
    private readonly Queue<int> _queue = new();
    private readonly ConcurrentDictionary<int, Match> _bySession = new();
    private int _nextMatchId = 1;

    public TttServer(string databasePath)
    {
        _host = new UdpGameServerHostBuilder(0)
            .WithOptions(new ServerOptions { AutoSubscribe = false, AoiRadius = 0 })
            .AddTypedCollection<MatchEntity>("matches")
            .Build();
        _server = _host.Server;
        _matches = _server.GetTypedCollection<MatchEntity>("matches");
        _matches.Filter = (session, match) => match.PlayerA == session.Id || match.PlayerB == session.Id;
        _users = new SqliteStore<UserRow>($"Data Source={databasePath}");
        _users.EnsureSchema();

        _server.Rpc.Register(TttMethods.Register, HandleRegister);
        _server.Rpc.Register(TttMethods.Login, HandleLogin);
        _server.Rpc.Register(TttMethods.Queue, HandleQueue);
        _server.Rpc.Register(TttMethods.State, HandleState);
        _server.Rpc.Register(TttMethods.Move, HandleMove);

        _server.SessionJoined += session => _players[session.Id] = new Player();
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



    private static byte[] EncodeAck(bool ok, string message)
    {
        var writer = new ByteWriter(64);
        AckPacketCodec.Write(ref writer, new Ack(ok, message));
        return writer.ToArray();
    }

    private byte[] HandleRegister(Session session, byte[] payload)
    {
        var reader = new ByteReader(payload);
        var request = RegisterRequestPacketCodec.Read(ref reader);
        var username = request.Username.Trim();
        var password = request.Password;

        if (username.Length is < 2 or > 32) return EncodeAck(false, "用户名长度需在 2..32");
        if (password.Length < 3) return EncodeAck(false, "密码至少 3 位");
        if (_users.Load(username) is not null) return EncodeAck(false, "用户名已存在");

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        _users.Save(new UserRow
        {
            Username = username,
            Salt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(hash),
            CreatedAt = DateTime.UtcNow,
        });
        return EncodeAck(true, "注册成功");
    }

    private byte[] HandleLogin(Session session, byte[] payload)
    {
        var reader = new ByteReader(payload);
        var request = RegisterRequestPacketCodec.Read(ref reader);
        var username = request.Username.Trim();

        var user = _users.Load(username);
        if (user is null || !Verify(request.Password, user)) return EncodeAck(false, "用户名或密码错误");

        if (!_players.TryGetValue(session.Id, out var player))
        {
            player = new Player();
            _players[session.Id] = player;
        }
        player.Name = user.Username;
        player.Authenticated = true;
        return EncodeAck(true, "登录成功");
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

    private byte[] HandleQueue(Session session, byte[] payload)
    {
        if (!_players.TryGetValue(session.Id, out var player) || !player.Authenticated)
            return EncodeQueue(MatchStatus.Idle, "请先登录");

        if (_bySession.TryGetValue(session.Id, out var existing))
            return EncodeQueue(MatchStatus.Playing, $"对局 #{existing.Id}");

        if (!_queue.Contains(session.Id)) _queue.Enqueue(session.Id);

        var candidates = new List<int>();
        while (_queue.Count > 0 && candidates.Count < 2)
        {
            var id = _queue.Dequeue();
            if (_bySession.ContainsKey(id)) continue;
            if (!_players.TryGetValue(id, out var p) || !p.Authenticated) continue;
            candidates.Add(id);
        }

        if (candidates.Count < 2)
        {
            foreach (var id in candidates) _queue.Enqueue(id);
            return EncodeQueue(MatchStatus.Queued, "等待对手…");
        }

        var match = new Match { Id = _nextMatchId++, PlayerA = candidates[0], PlayerB = candidates[1] };
        _bySession[candidates[0]] = match;
        _bySession[candidates[1]] = match;
        _matches.Spawn(match.Id, ToEntity(match));
        return EncodeQueue(MatchStatus.Playing, $"匹配成功，对局 #{match.Id}");
    }

    private static byte[] EncodeQueue(MatchStatus status, string message)
    {
        var writer = new ByteWriter(64);
        QueueResultPacketCodec.Write(ref writer, new QueueResult(status, message));
        return writer.ToArray();
    }

    private byte[] HandleState(Session session, byte[] payload)
    {
        if (!_bySession.TryGetValue(session.Id, out var match))
        {
            var status = _queue.Contains(session.Id) ? MatchStatus.Queued : MatchStatus.Idle;
            return EncodeState(new MatchView(status, 0, 0, 0, new byte[9], string.Empty, 0));
        }

        var game = match.Game;
        var opponentId = match.OpponentOf(session.Id);
        var opponent = _players.TryGetValue(opponentId, out var p) ? p.Name : $"player{opponentId}";
        return EncodeState(new MatchView(game.Status, match.SymbolOf(session.Id), game.Turn, game.Winner,
            (byte[])game.Cells.Clone(), opponent, match.Id));
    }

    private static byte[] EncodeState(MatchView view)
    {
        var writer = new ByteWriter(64);
        MatchViewPacketCodec.Write(ref writer, view);
        return writer.ToArray();
    }

    private byte[] HandleMove(Session session, byte[] payload)
    {
        var reader = new ByteReader(payload);
        var request = MoveRequestPacketCodec.Read(ref reader);

        if (!_bySession.TryGetValue(session.Id, out var match)) return EncodeAck(false, "你不在对局中");

        var symbol = match.SymbolOf(session.Id);
        var ok = match.Game.TryMove(symbol, request.Cell, out var error);
        if (ok) _matches.Spawn(match.Id, ToEntity(match));
        return EncodeAck(ok, ok ? "ok" : error);
    }

    private void OnDisconnected(Session session)
    {
        _players.TryRemove(session.Id, out _);
        if (_bySession.TryRemove(session.Id, out var match))
        {
            var opponent = match.OpponentOf(session.Id);
            match.Game.Forfeit(match.SymbolOf(session.Id));
            _matches.Spawn(match.Id, ToEntity(match));
            _bySession.TryRemove(opponent, out _);
        }
    }

    public void Dispose() => _host.Dispose();

    private sealed class Player
    {
        public string Name = string.Empty;
        public bool Authenticated;
    }
}
