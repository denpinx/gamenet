using System.Threading.Channels;
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

        var aUpdates = Subscribe(a);
        var bUpdates = Subscribe(b);

        var suffix = DateTime.UtcNow.Ticks.ToString()[^6..];
        var userA = "alice" + suffix;
        var userB = "bob" + suffix;
        Console.WriteLine((await a.RegisterAsync(userA, "pw-a")).Message);
        Console.WriteLine((await b.RegisterAsync(userB, "pw-b")).Message);
        Console.WriteLine((await a.LoginAsync(userA, "pw-a")).Message);
        Console.WriteLine((await b.LoginAsync(userB, "pw-b")).Message);

        Console.WriteLine((await a.QueueAsync()).Message);
        Console.WriteLine((await b.QueueAsync()).Message);

        var viewA = await a.StateAsync();
        var viewB = await b.StateAsync();

        // Both clients receive the same match creation; wait for the first board on each.
        var pendingA = aUpdates.Reader.ReadAsync().AsTask();
        var pendingB = bUpdates.Reader.ReadAsync().AsTask();
        var done = await Task.WhenAny(pendingA, pendingB);
        var board = done == pendingA ? await pendingA : await pendingB;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            Console.WriteLine(BoardText.Render(board.Board));
            if (board.Status is MatchStatus.Won or MatchStatus.Draw)
            {
                Console.WriteLine($"collection matches: A sees {a.Matches.Count} match, B sees {b.Matches.Count}");
                Console.WriteLine($"result: {board.Status}, winner={(board.Winner == 0 ? "-" : BoardText.Mark(board.Winner).ToString())}");
                return;
            }

            if (board.Turn == viewA.MySymbol)
            {
                var cell = PickCell(board.Board);
                Console.WriteLine($"A move {cell}: {(await a.MoveAsync(cell)).Message}");
            }
            else if (board.Turn == viewB.MySymbol)
            {
                var cell = PickCell(board.Board);
                Console.WriteLine($"B move {cell}: {(await b.MoveAsync(cell)).Message}");
            }

            // Wait for the next match update; no polling.
            if (pendingA.IsCompleted) pendingA = aUpdates.Reader.ReadAsync().AsTask();
            if (pendingB.IsCompleted) pendingB = bUpdates.Reader.ReadAsync().AsTask();
            done = await Task.WhenAny(pendingA, pendingB);
            board = done == pendingA ? await pendingA : await pendingB;
        }

        Console.WriteLine("bot game timed out");
        Console.WriteLine(BoardText.Render(board.Board));
    }

    private static Channel<MatchBoard> Subscribe(TttClient client)
    {
        var channel = Channel.CreateUnbounded<MatchBoard>();
        client.Matches.OnChanged(e =>
        {
            foreach (var change in e.Changes)
                if (change.Kind != ChangeKind.Removed && change.Current is { } board)
                    channel.Writer.TryWrite(board);
        });
        return channel;
    }

    private static int PickCell(byte[] cells)
    {
        // center, then corners, then first free — enough to produce a legal full game.
        int[] order = { 4, 0, 2, 6, 8, 1, 3, 5, 7 };
        foreach (var cell in order)
        {
            if (cells[cell] == 0) return cell;
        }
        return 0;
    }
}
