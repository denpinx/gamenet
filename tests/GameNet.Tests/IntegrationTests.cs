using GameNet.Core;
using GameNet.Persistence;
using Xunit;

namespace GameNet.Tests;

/// <summary>Closes the R8 loop: server tick → TickHooks → PersistableSet.Tick → auto-save.</summary>
public class IntegrationTests : IDisposable
{
    private readonly string _path;
    private readonly string _connectionString;

    public IntegrationTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"gamenet-int-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_path}";
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void ServerTick_DrivesPersistableSet_AutoSave()
    {
        var transport = new FakeTransport();
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0 });
        server.Start();

        var store = new SqliteStore<FlatRow>(_connectionString);
        store.EnsureSchema();
        var set = new PersistableSet<FlatRow>(store) { AutoSaveInterval = TimeSpan.Zero };
        var ticks = 0;
        set.OnTick = (_, _) => ticks++;
        server.TickHooks.Add(delta => set.Tick(delta));

        var row = new FlatRow { Name = "tick-driven", Level = 3 };
        set.Add(row);

        server.Tick(1f / 30f);

        Assert.Equal(1, ticks);
        Assert.Equal(0, set.DirtyCount);
        Assert.Equal("tick-driven", store.Load(row.Id)!.Name);
    }

    [Fact]
    public void ServerTick_BindingAndReplication_WorkTogether()
    {
        var transport = new FakeTransport();
        var server = new GameServer(transport, new ServerOptions { AoiRadius = 0 });
        server.Start();
        var peer = transport.Connect();
        server.Entities.Spawn(EntityState.Create(1, EntityKind.Player, 10, 10));

        var bind = new ByteWriter(8);
        bind.WriteByte((byte)MessageType.BindRequest);
        bind.WriteVarInt(1);
        peer.Inject(1, bind.ToArray());

        server.Tick(1f / 30f);

        Assert.True(server.Sessions[peer.Id].HasAvatar);
        lock (peer.Sent) Assert.Contains(peer.Sent, p => p.Channel == 0);
    }
}
