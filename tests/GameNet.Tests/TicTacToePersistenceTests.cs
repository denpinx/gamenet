using GameNet.Demo.TicTacToe;
using Xunit;

namespace GameNet.Tests;

public class TicTacToePersistenceTests
{
    [Fact]
    public async Task TurnTimeout_ForfeitsAndClearsActiveMatch()
    {
        var db = Path.Combine(Path.GetTempPath(), $"ttt-timeout-{Guid.NewGuid():N}.db");
        try
        {
            using var server = new TttServer(db, turnTimeoutSeconds: 0.2);
            using var a = new TttClient();
            using var b = new TttClient();
            Assert.True(a.Connect("127.0.0.1", server.Port));
            Assert.True(b.Connect("127.0.0.1", server.Port));

            var suffix = Guid.NewGuid().ToString("N")[..8];
            Assert.True((await a.RegisterAsync("a" + suffix, "pw-a")).Ok);
            Assert.True((await b.RegisterAsync("b" + suffix, "pw-b")).Ok);
            Assert.True((await a.LoginAsync("a" + suffix, "pw-a")).Ok);
            Assert.True((await b.LoginAsync("b" + suffix, "pw-b")).Ok);
            await a.QueueAsync();
            Assert.Equal(MatchStatus.Playing, (await b.QueueAsync()).Status);

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && a.Matches.Count == 0) await Task.Delay(20);
            Assert.True(a.Matches.Count > 0);

            deadline = DateTime.UtcNow.AddSeconds(5);
            MatchBoard? board = null;
            while (DateTime.UtcNow < deadline)
            {
                board = a.Matches.Entities.Values.FirstOrDefault();
                if (board is { Status: not MatchStatus.Playing }) break;
                await Task.Delay(20);
            }

            Assert.NotNull(board);
            Assert.Equal(MatchStatus.Won, board!.Status);
            Assert.Equal(MatchStatus.Won, (await a.StateAsync()).Status);
            Assert.Equal(MatchStatus.Queued, (await a.QueueAsync()).Status);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    [Fact]
    public async Task PlayersAndMatches_AreReloadedFromSqlite()
    {
        var db = Path.Combine(Path.GetTempPath(), $"ttt-persist-{Guid.NewGuid():N}.db");
        try
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            using (var server = new TttServer(db, turnTimeoutSeconds: 0.2))
            {
                using var a = new TttClient();
                using var b = new TttClient();
                Assert.True(a.Connect("127.0.0.1", server.Port));
                Assert.True(b.Connect("127.0.0.1", server.Port));
                Assert.True((await a.RegisterAsync("a" + suffix, "pw-a")).Ok);
                Assert.True((await b.RegisterAsync("b" + suffix, "pw-b")).Ok);
                Assert.True((await a.LoginAsync("a" + suffix, "pw-a")).Ok);
                Assert.True((await b.LoginAsync("b" + suffix, "pw-b")).Ok);
                await a.QueueAsync();
                await b.QueueAsync();

                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline && a.Matches.Count == 0) await Task.Delay(20);
                while (DateTime.UtcNow < deadline &&
                       a.Matches.Entities.Values.FirstOrDefault() is { Status: MatchStatus.Playing })
                    await Task.Delay(20);

                Assert.Equal(1, server.SavedMatchCount);
                Assert.Equal(2, server.SavedPlayerCount);
            }

            using var resumed = new TttServer(db);
            Assert.Equal(1, resumed.SavedMatchCount);
            Assert.Equal(2, resumed.SavedPlayerCount);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }
}
