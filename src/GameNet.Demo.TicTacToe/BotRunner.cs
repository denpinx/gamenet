using GameNet.Core;

namespace GameNet.Demo.TicTacToe;

/// <summary>In-process scripted match: starts a server, connects two clients and plays to the end.</summary>
public static class BotRunner
{
    public static async Task RunAsync()
    {
        var db = Path.Combine(Path.GetTempPath(), $"gamenet-ttt-{Guid.NewGuid():N}.db");
        using var server = new TttServer(db);
        Console.WriteLine($"server ready on port {server.Port}");

        using var a = new TttClient();
        using var b = new TttClient();
        if (!a.Connect("127.0.0.1", server.Port) || !b.Connect("127.0.0.1", server.Port))
        {
            Console.WriteLine("bot connect failed");
            return;
        }

        var suffix = DateTime.UtcNow.Ticks.ToString()[^6..];
        var userA = "alice" + suffix;
        var userB = "bob" + suffix;
        Console.WriteLine((await a.RegisterAsync(userA, "pw-a")).Message);
        Console.WriteLine((await b.RegisterAsync(userB, "pw-b")).Message);
        Console.WriteLine((await a.LoginAsync(userA, "pw-a")).Message);
        Console.WriteLine((await b.LoginAsync(userB, "pw-b")).Message);

        Console.WriteLine((await a.QueueAsync()).Message);
        Console.WriteLine((await b.QueueAsync()).Message);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        MatchView? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var va = await a.StateAsync();
            var vb = await b.StateAsync();
            if (va.Status is MatchStatus.Won or MatchStatus.Draw)
            {
                Console.WriteLine($"collection matches: A sees {a.Matches.Count} match, B sees {b.Matches.Count}");
                Console.WriteLine("final:");
                Console.WriteLine(BoardText.Render(va.Cells));
                Console.WriteLine($"A({BoardText.Mark(va.MySymbol)}) result={Result(va)}");
                Console.WriteLine($"B({BoardText.Mark(vb.MySymbol)}) result={Result(vb)}");
                return;
            }

            if (va.IsMyTurn)
            {
                var cell = PickCell(va);
                Console.WriteLine($"A move {cell}: {(await a.MoveAsync(cell)).Message}");
            }
            else if (vb.IsMyTurn)
            {
                var cell = PickCell(vb);
                Console.WriteLine($"B move {cell}: {(await b.MoveAsync(cell)).Message}");
            }
            else if (va.Status == MatchStatus.Queued)
            {
                await Task.Delay(200);
            }

            last = va;
            await Task.Delay(100);
        }

        Console.WriteLine("bot game timed out");
        if (last is not null) Console.WriteLine(BoardText.Render(last.Value.Cells));
    }

    private static string Result(MatchView view) =>
        view.Winner == view.MySymbol ? "win" : view.Status == MatchStatus.Draw ? "draw" : "lose";

    private static int PickCell(MatchView view)
    {
        // center, then corners, then first free — enough to produce a legal full game.
        int[] order = { 4, 0, 2, 6, 8, 1, 3, 5, 7 };
        foreach (var cell in order)
        {
            if (view.Cells[cell] == 0) return cell;
        }
        return 0;
    }
}
