using GameNet.Core;
using GameNet.Transport;

// Minimal end-to-end example: one collection ("players"), one RPC group ("hello").
// Run `dotnet run --project src/GameNet.Demo.Minimal -c Release` for the in-process demo,
// or `-- --server --port 7777` / `-- --client --port 7777` for two processes.

var mode = args.FirstOrDefault() ?? "--demo";
var port = int.Parse(Get("--port", "7777"));

switch (mode)
{
    case "--server":
        using (var server = new MinimalServer(port))
        {
            Console.WriteLine($"READY {server.Port}");
            await Task.Delay(Timeout.Infinite);
        }
        break;

    case "--client":
        await RunClientAsync("127.0.0.1", port);
        break;

    default:
        await RunDemoAsync();
        break;
}

string Get(string name, string fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

static async Task RunDemoAsync()
{
    using var server = new MinimalServer(0);
    Console.WriteLine($"server listening on udp {server.Port}");
    await RunClientAsync("127.0.0.1", server.Port);
}

static async Task RunClientAsync(string host, int port)
{
    using var client = new RpcClient();
    if (!client.Connect(host, port))
    {
        Console.WriteLine("connect failed");
        return;
    }

    client.Subscribe("players");

    var id = await client.Group("hello").Call<int>("spawn", "alice");
    var greeting = await client.Group("hello").Call<string>("greet", "alice");
    Console.WriteLine($"rpc greet -> {greeting}");
    Console.WriteLine($"rpc spawn -> entity {id}");

    for (var i = 0; i < 100 && (client.EntitiesFor("players")?.Count ?? 0) == 0; i++)
        await Task.Delay(10);

    var players = client.EntitiesFor("players");
    Console.WriteLine($"subscribed collection 'players': {players?.Count ?? 0} entity/entities");
    if (players is not null && players.TryGetValue(id, out var state))
    {
        var name = await client.Group("hello").Call<string>("nameOf", id); // names are not in EntityState
        var me = Player.FromState(state, name);
        Console.WriteLine($"my player: id={me.Id} name={me.Name} pos=({me.X},{me.Y})");
    }
}

/// <summary>The whole server: one transport, one collection, one RPC group.</summary>
public sealed class MinimalServer : IDisposable
{
    private readonly UdpServerTransport _transport;
    private readonly GameServer _server;

    public MinimalServer(int port)
    {
        _transport = new UdpServerTransport(port);
        _server = new GameServer(_transport, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        _server.RegisterCollection("players", new EntityCollection());
        _server.RpcGroups.AddGroup<HelloApi>(new EmptyProvider());
        _server.Start(autoTick: true, tickRate: 30);
    }

    public int Port => _transport.Port;

    public void Dispose()
    {
        _server.StopAutoTick();
        _transport.Dispose();
    }

    /// <summary>Groups resolve dependencies from this; collections come from [FromCollection].</summary>
    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>
/// Domain type. The framework replicates fixed EntityState fields, so Player is the game-side
/// shape and ToState/FromState is the mapping. Name is server-only (EntityState has no string).
/// </summary>
public sealed record Player(int Id, string Name, float X, float Y)
{
    public EntityState ToState() => EntityState.Create(Id, EntityKind.Player, X, Y);

    public static Player FromState(EntityState state, string name = "") =>
        new(state.Id, name, state.X, state.Y);
}

[RpcGroup("hello")]
public sealed class HelloApi
{
    private readonly EntityCollection _players;
    private readonly Dictionary<int, Player> _roster = new();
    private int _nextId;

    public HelloApi([FromCollection("players")] EntityCollection players) => _players = players;

    [Rpc]
    public string Greet(string name) => $"hello {name}";

    [Rpc]
    public int Spawn(string name)
    {
        var id = Interlocked.Increment(ref _nextId);
        var player = new Player(id, name, (float)(name.GetHashCode() & 0xFFFF) / 100f, 0);
        _roster[id] = player;
        _players.Spawn(player.ToState());
        return id;
    }

    [Rpc]
    public string NameOf(int id) => _roster.TryGetValue(id, out var player) ? player.Name : "";
}
