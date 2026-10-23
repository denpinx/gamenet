using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

[RpcGroup("math")]
public sealed class MathApi
{
    [Rpc] public int Max(int a, int b) => Math.Max(a, b);
    [Rpc] public int Add(int a, int b) => a + b;
    [Rpc] public double Scale(double value, float factor) => value * factor;
    [Rpc] public string Greet(string name) => "hello " + name;
    [Rpc] public bool IsEven(long value) => value % 2 == 0;
    [Rpc("fail")] public int AlwaysThrows() => throw new InvalidOperationException("boom");
}

public sealed class GreetingService
{
    public int Calls;
    public string Greet(string name)
    {
        Calls++;
        return "hi " + name;
    }
}

[Packet]
public partial record struct Profile(int Id, string Name, float Score);

[RpcGroup("profile")]
public sealed class ProfileApi
{
    [Rpc] public Profile Echo(Profile profile) => profile;

    [Rpc] public Profile Get(int id) => new(id, "player" + id, id * 1.5f);
}

[RpcGroup("svc")]
public sealed class ServiceApi
{
    private readonly GreetingService _greeting;
    public ServiceApi(GreetingService greeting) => _greeting = greeting;

    [Rpc] public string Hello(string name) => _greeting.Greet(name);
    [Rpc] public int Calls() => _greeting.Calls;
}

[RpcGroup("counter")]
public sealed class CounterApi
{
    private int _value;
    [Rpc] public int Next() => ++_value;
    [Rpc] public int Value() => _value;
}

[RpcGroup("disposable")]
public sealed class DisposableApi : IDisposable
{
    public static int Disposed;
    [Rpc] public string Ping() => "pong";
    public void Dispose() => Disposed++;
}

[RpcGroup("chatapi")]
public sealed class ChatApi
{
    private readonly EntityCollection _chat;
    public ChatApi([FromCollection("chat")] EntityCollection chat) => _chat = chat;

    [Rpc] public int Count() => _chat.Count;
    [Rpc] public int Add(string text)
    {
        _chat.Spawn(EntityState.Create(_chat.Count + 1, EntityKind.Prop, 0, 0));
        return _chat.Count;
    }
}

public class DynamicRpcTests
{
    [Fact]
    public async Task GroupCall_ByNames_WorksOverUdp()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = false });
        server.RpcGroups.AddGroup<MathApi>();
        server.Start();
        using var ticker = StartTicker(server);

        using var client = new RpcClient();
        Assert.True(client.Connect("127.0.0.1", transport.Port));

        Assert.Equal(7, await client.Group("math").Call<int>("max", 3, 7));
        Assert.Equal(10, await client.Group("math").Call<int>("Add", 4, 6)); // names are case-insensitive
        Assert.Equal(3.5, await client.Group("math").Call<double>("scale", 1.75, 2f), 5);
        Assert.Equal("hello bob", await client.Group("math").Call<string>("greet", "bob"));
        Assert.True(await client.Group("math").Call<bool>("isEven", 42L));
    }

    [Fact]
    public void RegisteredCollection_IsInjectedByName()
    {
        var server = new GameServer(new FakeTransport(), new ServerOptions());
        var chat = server.RegisterCollection("chat", new EntityCollection());
        server.RpcGroups.AddGroup<ChatApi>(new MapProvider());

        var args = new[] { RpcValue.From("hello") };
        Assert.True(server.RpcGroups.TryInvoke(1, "chatapi", "add", args, out var added, out _));
        Assert.Equal(1, added.As<int>());
        Assert.Equal(1, chat.Count);
        Assert.Same(chat, server.GetCollection("chat"));
    }

    [Fact]
    public void Collection_CanBeCreatedFromTheServiceProvider()
    {
        var server = new GameServer(new FakeTransport(), new ServerOptions());
        var chat = new EntityCollection();
        var provider = new MapProvider((typeof(EntityCollection), chat));

        var registered = server.RegisterCollection("chat", provider,
            services => (EntityCollection)services.GetService(typeof(EntityCollection))!);

        Assert.Same(chat, registered);
        Assert.Same(chat, server.GetCollection("chat"));
    }

    [Fact]
    public void Group_WithMissingCollection_Throws()
    {
        var server = new GameServer(new FakeTransport(), new ServerOptions());
        var error = Assert.Throws<InvalidOperationException>(() => server.RpcGroups.AddGroup<ChatApi>(new MapProvider()));
        Assert.Contains("service provider", error.Message);
    }
    [Fact]
    public void PerConnectionGroup_GetsOneInstancePerSession()
    {
        var registry = new RpcGroupRegistry();
        registry.AddGroupPerConnection<CounterApi>();
        var noArgs = Array.Empty<RpcValue>();

        Assert.True(registry.TryInvoke(1, "counter", "next", noArgs, out var first, out _));
        Assert.Equal(1, first.As<int>());
        Assert.True(registry.TryInvoke(1, "counter", "next", noArgs, out var second, out _));
        Assert.Equal(2, second.As<int>());

        Assert.True(registry.TryInvoke(2, "counter", "next", noArgs, out var other, out _));
        Assert.Equal(1, other.As<int>()); // separate instance for session 2

        registry.RemoveSession(1);
        Assert.True(registry.TryInvoke(1, "counter", "next", noArgs, out var fresh, out _));
        Assert.Equal(1, fresh.As<int>()); // reset after disconnect
    }

    [Fact]
    public void SingletonGroup_SharesStateAcrossSessions()
    {
        var registry = new RpcGroupRegistry();
        registry.AddGroup<CounterApi>();
        var noArgs = Array.Empty<RpcValue>();

        Assert.True(registry.TryInvoke(1, "counter", "next", noArgs, out var a, out _));
        Assert.True(registry.TryInvoke(2, "counter", "next", noArgs, out var b, out _));
        Assert.Equal(2, b.As<int>());
        Assert.Equal(1, a.As<int>());
    }

    [Fact]
    public void PerConnectionGroup_IsDisposedWhenSessionEnds()
    {
        DisposableApi.Disposed = 0;
        var registry = new RpcGroupRegistry();
        registry.AddGroupPerConnection<DisposableApi>();
        var noArgs = Array.Empty<RpcValue>();

        Assert.True(registry.TryInvoke(5, "disposable", "ping", noArgs, out _, out _));
        registry.RemoveSession(5);
        Assert.Equal(1, DisposableApi.Disposed);
    }

    [Fact]
    public async Task PerConnectionGroup_WithDependencyInjection_IsPerSession()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = false });
        var greeting = new GreetingService();
        server.RpcGroups.AddGroupPerConnection<ServiceApi>(new MapProvider((typeof(GreetingService), greeting)));
        server.Start();
        using var ticker = StartTicker(server);

        using var client = new RpcClient();
        Assert.True(client.Connect("127.0.0.1", transport.Port));
        Assert.Equal("hi bob", await client.Group("svc").Call<string>("hello", "bob"));
        Assert.Equal("hi eve", await client.Group("svc").Call<string>("hello", "eve"));
        Assert.Equal(2, await client.Group("svc").Call<int>("calls")); // both calls hit one session instance
        Assert.Equal(2, greeting.Calls);
    }
    [Fact]
    public async Task Group_CanPassAndReturnPacketTypes()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = false });
        server.RpcGroups.AddGroup<ProfileApi>();
        server.Start();
        using var ticker = StartTicker(server);

        using var client = new RpcClient();
        Assert.True(client.Connect("127.0.0.1", transport.Port));

        var fetched = await client.Group("profile").Call<Profile>("get", 7);
        Assert.Equal(7, fetched.Id);
        Assert.Equal("player7", fetched.Name);
        Assert.Equal(10.5f, fetched.Score, 3);

        var echoed = await client.Group("profile").Call<Profile>("echo", new Profile(3, "alice", 1.25f));
        Assert.Equal(new Profile(3, "alice", 1.25f), echoed);
    }
    [Fact]
    public async Task Group_IsConstructedThroughDependencyInjection()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = false });
        var greeting = new GreetingService();
        server.RpcGroups.AddGroup<ServiceApi>(new MapProvider((typeof(GreetingService), greeting)));
        server.Start();
        using var ticker = StartTicker(server);

        using var client = new RpcClient();
        Assert.True(client.Connect("127.0.0.1", transport.Port));

        Assert.Equal("hi bob", await client.Group("svc").Call<string>("hello", "bob"));
        Assert.Equal("hi eve", await client.Group("svc").Call<string>("hello", "eve"));
        Assert.Equal(2, await client.Group("svc").Call<int>("calls")); // singleton state shared
        Assert.Equal(2, greeting.Calls);
    }

    [Fact]
    public void Group_WithUnregisteredDependency_Throws()
    {
        var server = new GameServer(new FakeTransport(), new ServerOptions());
        var error = Assert.Throws<InvalidOperationException>(() =>
            server.RpcGroups.AddGroup<ServiceApi>(new MapProvider()));
        Assert.Contains("service provider", error.Message);
    }

    private sealed class MapProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();
        public MapProvider(params (Type Type, object Value)[] services)
        {
            foreach (var (type, value) in services) _services[type] = value;
        }
        public object? GetService(Type serviceType) => _services.TryGetValue(serviceType, out var value) ? value : null;
    }
    private static IDisposable StartTicker(GameServer server)
    {
        var cts = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                server.Tick(1f / 30f);
                Thread.Sleep(10);
            }
        }) { IsBackground = true };
        thread.Start();
        return new Stopper(cts);
    }

    private sealed class Stopper : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        public Stopper(CancellationTokenSource cts) => _cts = cts;
        public void Dispose() => _cts.Cancel();
    }

    [Fact]
    public async Task UnknownGroupOrMethod_ThrowsNotFound()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = false });
        server.RpcGroups.AddGroup<MathApi>();
        server.Start();
        using var ticker = StartTicker(server);

        using var client = new RpcClient();
        Assert.True(client.Connect("127.0.0.1", transport.Port));

        var missingGroup = await Assert.ThrowsAsync<RpcException>(() => client.Group("nope").Call<int>("max", 1, 2));
        Assert.Equal(RpcStatus.NotFound, missingGroup.Status);

        var missingMethod = await Assert.ThrowsAsync<RpcException>(() => client.Group("math").Call<int>("nope", 1, 2));
        Assert.Equal(RpcStatus.NotFound, missingMethod.Status);
    }

    [Fact]
    public async Task ThrowingMethod_ReturnsErrorStatus()
    {
        using var transport = new UdpServerTransport(0);
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0, AutoSubscribe = false });
        server.RpcGroups.AddGroup<MathApi>();
        server.Start();
        using var ticker = StartTicker(server);

        using var client = new RpcClient();
        Assert.True(client.Connect("127.0.0.1", transport.Port));

        var error = await Assert.ThrowsAsync<RpcException>(() => client.Group("math").Call<int>("fail"));
        Assert.Equal(RpcStatus.Error, error.Status);
    }
}
