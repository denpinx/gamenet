using GameNet.Core;
using GameNet.Transport;

// Minimal end-to-end example: one typed collection ("players"), one RPC group ("hello").
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

    // Build mode: collection types are registered before Build, then frozen.
    var players = client.RegisterTypedCollection<PlayerView>("players");
    client.Subscribe("players");
    if (!client.Build(host, port))
    {
        Console.WriteLine("connect failed");
        return;
    }

    var arrival = new TaskCompletionSource<PlayerView>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subscription = players.OnChanged(e =>
    {
        foreach (var change in e.Changes)
        {
            if (change.Kind == ChangeKind.Added && change.Current is { } player)
                arrival.TrySetResult(player);
        }
    });

    var id = await client.Group("hello").Call<int>("spawn", "alice");
    var greeting = await client.Group("hello").Call<string>("greet", "alice");
    Console.WriteLine($"rpc greet -> {greeting}");
    Console.WriteLine($"rpc spawn -> player {id}");

    var me = await arrival.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Console.WriteLine($"[event] added player id={me.Id} name={me.Name} pos=({me.X},{me.Y})");
}

/// <summary>The whole server: one transport, one typed collection, one RPC group.</summary>
public sealed class MinimalServer : IDisposable
{
    private readonly UdpGameServerHost _host;

    public MinimalServer(int port)
    {
        _host = new UdpGameServerHost(port, new ServerOptions { AutoSubscribe = false, AoiRadius = 0 });
        var players = _host.Server.RegisterTypedCollection<Player>("players");
        _host.Server.RpcGroups.AddGroup(new HelloApi(players));
        _host.Start(autoTick: true, tickRate: 30);
    }

    public int Port => _host.Port;

    public void Dispose() => _host.Dispose();
}

/// <summary>Server-side authoritative entity: mutable class, generated codec.</summary>
[Entity("player")]
public sealed partial class Player
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
}

/// <summary>Client-side view: record, same field ids, generated codec.</summary>
[Entity("player")]
public sealed partial record PlayerView(int Id, string Name, float X, float Y);

[RpcGroup("hello")]
public sealed class HelloApi
{
    private readonly TypedCollection<Player> _players;
    private int _nextId;

    public HelloApi(TypedCollection<Player> players) => _players = players;

    [Rpc]
    public string Greet(string name) => $"hello {name}";

    [Rpc]
    public int Spawn(string name)
    {
        var id = Interlocked.Increment(ref _nextId);
        _players.Spawn(id, new Player
        {
            Id = id,
            Name = name,
            X = name.GetHashCode() & 0xFFFF,
            Y = 0,
        });
        return id;
    }
}
