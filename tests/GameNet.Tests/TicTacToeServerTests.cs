using GameNet.Demo.TicTacToe;
using Xunit;

namespace GameNet.Tests;

public class TicTacToeServerTests
{
    [Fact]
    public async Task RegisterLoginMatchAndPlay_EndToEnd()
    {
        var db = Path.Combine(Path.GetTempPath(), $"ttt-test-{Guid.NewGuid():N}.db");
        try
        {
            using var server = new TttServer(db);
            using var a = new TttClient();
            using var b = new TttClient();
            Assert.True(a.Connect("127.0.0.1", server.Port));
            Assert.True(b.Connect("127.0.0.1", server.Port));

            var suffix = Guid.NewGuid().ToString("N")[..8];
            var userA = "alice" + suffix;
            var userB = "bob" + suffix;

            Assert.True((await a.RegisterAsync(userA, "pw-a")).Ok);
            Assert.True((await b.RegisterAsync(userB, "pw-b")).Ok);
            Assert.True((await a.LoginAsync(userA, "pw-a")).Ok);
            Assert.True((await b.LoginAsync(userB, "pw-b")).Ok);

            var queueA = await a.QueueAsync();
            var queueB = await b.QueueAsync();
            Assert.Contains(queueA.Status, new[] { MatchStatus.Queued, MatchStatus.Playing });
            Assert.Equal(MatchStatus.Playing, queueB.Status);

            MatchView? last = null;
            for (var step = 0; step < 200; step++)
            {
                var va = await a.StateAsync();
                var vb = await b.StateAsync();
                last = va;
                if (va.Status is MatchStatus.Won or MatchStatus.Draw) break;

                if (va.IsMyTurn) await a.MoveAsync(Pick(va.Cells));
                else if (vb.IsMyTurn) await b.MoveAsync(Pick(vb.Cells));

                await Task.Delay(40);
            }

            Assert.NotNull(last);
            Assert.True(last!.Value.Status is MatchStatus.Won or MatchStatus.Draw, "game should finish");

            var finalA = await a.StateAsync();
            var finalB = await b.StateAsync();
            Assert.Equal(finalA.Cells, finalB.Cells);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    [Fact]
    public async Task DuplicateRegistration_IsRejected()
    {
        var db = Path.Combine(Path.GetTempPath(), $"ttt-test-{Guid.NewGuid():N}.db");
        try
        {
            using var server = new TttServer(db);
            using var client = new TttClient();
            Assert.True(client.Connect("127.0.0.1", server.Port));

            var name = "dup" + Guid.NewGuid().ToString("N")[..6];
            Assert.True((await client.RegisterAsync(name, "pw1")).Ok);
            var second = await client.RegisterAsync(name, "pw1");
            Assert.False(second.Ok);
            Assert.Contains("已存在", second.Message);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    [Fact]
    public async Task WrongPassword_IsRejected()
    {
        var db = Path.Combine(Path.GetTempPath(), $"ttt-test-{Guid.NewGuid():N}.db");
        try
        {
            using var server = new TttServer(db);
            using var client = new TttClient();
            Assert.True(client.Connect("127.0.0.1", server.Port));

            var name = "wrong" + Guid.NewGuid().ToString("N")[..6];
            Assert.True((await client.RegisterAsync(name, "right-pw")).Ok);
            var login = await client.LoginAsync(name, "wrong-pw");
            Assert.False(login.Ok);
            Assert.Contains("错误", login.Message);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    [Fact]
    public async Task MoveWithoutMatch_IsRejected()
    {
        var db = Path.Combine(Path.GetTempPath(), $"ttt-test-{Guid.NewGuid():N}.db");
        try
        {
            using var server = new TttServer(db);
            using var client = new TttClient();
            Assert.True(client.Connect("127.0.0.1", server.Port));

            var move = await client.MoveAsync(4);
            Assert.False(move.Ok);
            Assert.Contains("对局", move.Message);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }
    [Fact]
    public async Task MatchCollection_ShowsOnlyMyMatch()
    {
        var db = Path.Combine(Path.GetTempPath(), $"ttt-coll-{Guid.NewGuid():N}.db");
        try
        {
            using var server = new TttServer(db);
            var clients = new List<TttClient>();
            var names = new List<string>();
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    var client = new TttClient();
                    clients.Add(client);
                    var name = $"p{i}{Guid.NewGuid():N}"[..12];
                    names.Add(name);
                    Assert.True(client.Connect("127.0.0.1", server.Port));
                    Assert.True((await client.RegisterAsync(name, "pw1")).Ok);
                    Assert.True((await client.LoginAsync(name, "pw1")).Ok);
                }

                foreach (var client in clients) Assert.NotEqual(MatchStatus.Idle, (await client.QueueAsync()).Status);
                foreach (var client in clients) client.Subscribe("matches");

                var views = new MatchView[4];
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    var ready = true;
                    for (var i = 0; i < 4; i++)
                    {
                        views[i] = await clients[i].StateAsync();
                        if (views[i].Status != MatchStatus.Playing || clients[i].Matches.Count != 1) ready = false;
                    }
                    if (ready) break;
                    await Task.Delay(50);
                }

                for (var i = 0; i < 4; i++)
                {
                    var matches = clients[i].Matches.Entities;
                    Assert.Single(matches);
                    Assert.Equal(views[i].MatchId, matches.Keys.Single());
                    Assert.Equal(views[i].Cells, matches.Values.Single().Board);
                    Assert.Equal(MatchStatus.Playing, views[i].Status);
                }

                Assert.Equal(2, views.Select(v => v.MatchId).Distinct().Count());
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
            }
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }
    [Fact]
    public async Task PlayerCollection_ReplicatesOnlyOwnSession()
    {
        var db = Path.Combine(Path.GetTempPath(), $"ttt-test-{Guid.NewGuid():N}.db");
        try
        {
            using var server = new TttServer(db);
            using var a = new TttClient();
            using var b = new TttClient();
            Assert.True(a.Connect("127.0.0.1", server.Port));
            Assert.True(b.Connect("127.0.0.1", server.Port));

            var suffix = Guid.NewGuid().ToString("N")[..8];
            var userA = "alice" + suffix;
            var userB = "bob" + suffix;
            Assert.True((await a.RegisterAsync(userA, "pw-a")).Ok);
            Assert.True((await b.RegisterAsync(userB, "pw-b")).Ok);
            Assert.True((await a.LoginAsync(userA, "pw-a")).Ok);
            Assert.True((await b.LoginAsync(userB, "pw-b")).Ok);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && (a.Me?.Name != userA || b.Me?.Name != userB)) await Task.Delay(20);

            Assert.NotNull(a.Me);
            Assert.NotNull(b.Me);
            Assert.Equal(userA, a.Me!.Name);
            Assert.Equal(userB, b.Me!.Name);
            Assert.True(a.Me.Authenticated);
            Assert.Single(a.Players.Entities);
            Assert.Single(b.Players.Entities);
            Assert.NotEqual(a.Me.Id, b.Me.Id);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }
    private static int Pick(byte[] cells)
    {
        int[] order = { 4, 0, 2, 6, 8, 1, 3, 5, 7 };
        foreach (var cell in order)
            if (cells[cell] == 0) return cell;
        return 0;
    }
}
