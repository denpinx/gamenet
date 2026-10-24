using System.Threading.Channels;
using GameNet.Core;

namespace GameNet.Demo.TicTacToe;

/// <summary>Interactive console client: REPL, queueing and one match loop.</summary>
public sealed class ClientConsole
{
    private readonly TttClient _client;

    private ClientConsole(TttClient client) => _client = client;

    public static async Task RunAsync(string host, int port)
    {
        using var client = new TttClient();
        if (!client.Connect(host, port))
        {
            Console.WriteLine("连接失败");
            return;
        }
        await new ClientConsole(client).ReplAsync();
    }

    private async Task ReplAsync()
    {
        Console.WriteLine("命令：register <user> <pass> | login <user> <pass> | queue | move <0-8> | state | quit");
        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null) return;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            switch (parts[0].ToLowerInvariant())
            {
                case "register":
                    if (parts.Length < 3) { Usage("register <user> <pass>"); break; }
                    Console.WriteLine((await _client.RegisterAsync(parts[1], parts[2])).Message);
                    break;

                case "login":
                    if (parts.Length < 3) { Usage("login <user> <pass>"); break; }
                    var login = await _client.LoginAsync(parts[1], parts[2]);
                    Console.WriteLine(login.Message);
                    if (login.Ok) await QueueAndPlayAsync();
                    break;

                case "queue":
                    await QueueAndPlayAsync();
                    break;

                case "move":
                    if (parts.Length < 2 || !int.TryParse(parts[1], out var cell)) { Usage("move 0..8"); break; }
                    Console.WriteLine((await _client.MoveAsync(cell)).Message);
                    break;

                case "state":
                    PrintView(await _client.StateAsync());
                    break;

                case "quit":
                    return;

                default:
                    Console.WriteLine("未知命令");
                    break;
            }
        }
    }

    private async Task QueueAndPlayAsync()
    {
        var queued = await _client.QueueAsync();
        Console.WriteLine(queued.Message);

        if (queued.Status == MatchStatus.Queued)
        {
            while (true)
            {
                await Task.Delay(500);
                MatchView view;
                try
                {
                    view = await _client.StateAsync();
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    if (!await TryReconnectAsync(ex)) return;
                    continue;
                }

                if (view.Status == MatchStatus.Playing)
                {
                    await PlayAsync();
                    return;
                }
            }
        }

        await PlayAsync();
    }

    private async Task PlayAsync()
    {
        var updates = Channel.CreateUnbounded<MatchBoard>();
        using var subscription = _client.Matches.OnChanged(e =>
        {
            foreach (var change in e.Changes)
                if (change.Kind != ChangeKind.Removed && change.Current is { } board)
                    updates.Writer.TryWrite(board);
        });

        var view = await _client.StateAsync();
        var mySymbol = view.MySymbol;
        var opponent = view.Opponent;
        Console.WriteLine($"对局 #{view.MatchId}：你={BoardText.Mark(mySymbol)} 对手={opponent}");

        MatchBoard? last = null;
        while (true)
        {
            MatchBoard board;
            try
            {
                board = await NextBoardAsync(updates.Reader, 3000);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (!await TryReconnectAsync(ex)) return;
                continue;
            }

            if (last is { } previous && SameBoard(previous, board)) continue;

            PrintBoard(board, mySymbol, opponent);
            last = board;

            if (board.Status != MatchStatus.Playing)
            {
                PrintFinalBoard(board, mySymbol);
                return;
            }

            if (board.Turn == mySymbol)
            {
                Console.Write("轮到你，落子 (0-8)：");
                var input = Console.ReadLine();
                if (!int.TryParse(input, out var cell)) continue;

                Ack result;
                try
                {
                    result = await _client.MoveAsync(cell);
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    if (!await TryReconnectAsync(ex)) return;
                    continue;
                }

                if (!result.Ok) Console.WriteLine(result.Message);
            }
        }
    }

    private async Task<MatchBoard> NextBoardAsync(ChannelReader<MatchBoard> reader, int timeoutMs)
    {
        var read = reader.ReadAsync().AsTask();
        var done = await Task.WhenAny(read, Task.Delay(timeoutMs));
        if (done == read) return await read;

        // A collection update was missed: recover the latest view over RPC.
        var view = await _client.StateAsync();
        return new MatchBoard(view.MatchId, 0, 0, view.Turn, view.Status, view.Winner, view.Cells);
    }

    private async Task<bool> TryReconnectAsync(Exception error)
    {
        Console.WriteLine($"[reconnect] {error.Message}");
        if (await _client.ReconnectAsync()) return true;
        Console.WriteLine("[reconnect] 失败");
        return false;
    }

    private static bool IsTransient(Exception ex) =>
        ex is TimeoutException or RpcException or InvalidOperationException;

    private static bool SameBoard(MatchBoard a, MatchBoard b) =>
        a.Turn == b.Turn && a.Status == b.Status && a.Winner == b.Winner &&
        a.Board.AsSpan().SequenceEqual(b.Board);

    private static void PrintBoard(MatchBoard board, byte mySymbol, string opponent)
    {
        Console.WriteLine();
        Console.WriteLine(BoardText.Render(board.Board));
        Console.WriteLine($"你={BoardText.Mark(mySymbol)} 对手={opponent} 轮到={BoardText.Mark(board.Turn)}");
    }

    private static void PrintFinalBoard(MatchBoard board, byte mySymbol)
    {
        Console.WriteLine(BoardText.Render(board.Board));
        Console.WriteLine(BoardText.Result(board.Status, board.Winner, mySymbol));
    }

    private static void PrintView(MatchView view)
    {
        Console.WriteLine();
        Console.WriteLine(BoardText.Render(view.Cells));
        Console.WriteLine($"你={BoardText.Mark(view.MySymbol)} 对手={view.Opponent} 轮到={BoardText.Mark(view.Turn)}");
    }

    private static void Usage(string text) => Console.WriteLine($"用法：{text}");
}
