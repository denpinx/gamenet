using System.Threading.Channels;
using GameNet.Core;
using GameNet.Demo.TicTacToe;

var isServer = args.Contains("--server");
var isBot = args.Contains("--bot");
var host = Option("--host", "127.0.0.1");
var port = int.Parse(Option("--port", "0"));

if (isServer)
{
    var db = Option("--db", Path.Combine(Path.GetTempPath(), "gamenet-ttt.db"));
    using var server = new TttServer(db);
    Console.WriteLine($"READY {server.Port}");
    Console.Out.Flush();
    await Task.Delay(Timeout.Infinite);
    return;
}

if (isBot)
{
    await RunBotAsync();
    return;
}

if (port == 0) port = 7777;
Console.WriteLine($"连接 {host}:{port}");
using var client = new TttClient();
if (!client.Connect(host, port))
{
    Console.WriteLine("连接失败");
    return;
}
await RunInteractiveAsync(client);

static async Task RunInteractiveAsync(TttClient client)
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
                if (parts.Length < 3) { Console.WriteLine("用法：register <user> <pass>"); break; }
                var ack = await client.RegisterAsync(parts[1], parts[2]);
                Console.WriteLine(ack.Message);
                break;

            case "login":
                if (parts.Length < 3) { Console.WriteLine("用法：login <user> <pass>"); break; }
                var login = await client.LoginAsync(parts[1], parts[2]);
                Console.WriteLine(login.Message);
                if (login.Ok) await PlayAsync(client);
                break;

            case "queue":
                await QueueAndPlayAsync(client);
                break;

            case "move":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var cell)) { Console.WriteLine("用法：move 0..8"); break; }
                var move = await client.MoveAsync(cell);
                Console.WriteLine(move.Message);
                break;

            case "state":
                Print(await client.StateAsync());
                break;

            case "quit":
                return;

            default:
                Console.WriteLine("未知命令");
                break;
        }
    }
}

static async Task QueueAndPlayAsync(TttClient client)
{
    var queued = await client.QueueAsync();
    Console.WriteLine(queued.Message);
    if (queued.Status == MatchStatus.Queued)
    {
        while (true)
        {
            await Task.Delay(500);
            var view = await client.StateAsync();
            if (view.Status == MatchStatus.Playing)
            {
                await PlayAsync(client);
                return;
            }
        }
    }
    await PlayAsync(client);
}

static async Task PlayAsync(TttClient client)
{
    var updates = Channel.CreateUnbounded<MatchBoard>();
    using var subscription = client.Matches.OnChanged(e =>
    {
        foreach (var change in e.Changes)
            if (change.Kind != ChangeKind.Removed && change.Current is { } board)
                updates.Writer.TryWrite(board);
    });

    var view = await client.StateAsync();
    var mySymbol = view.MySymbol;
    var opponent = view.Opponent;
    Console.WriteLine($"对局 #{view.MatchId}：你={Mark(mySymbol)} 对手={opponent}");

    MatchBoard? last = null;
    while (true)
    {
        var board = await NextBoardAsync(client, updates.Reader, 3000);
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
            var moveResult = await client.MoveAsync(cell);
            if (!moveResult.Ok) Console.WriteLine(moveResult.Message);
        }
    }
}

static async Task<MatchBoard> NextBoardAsync(TttClient client, ChannelReader<MatchBoard> reader, int timeoutMs)
{
    var read = reader.ReadAsync().AsTask();
    var done = await Task.WhenAny(read, Task.Delay(timeoutMs));
    if (done == read) return await read;

    // Fallback: a collection update was missed; recover the latest view over RPC.
    var view = await client.StateAsync();
    return new MatchBoard(view.MatchId, 0, 0, view.Turn, view.Status, view.Winner, view.Cells);
}

static bool SameBoard(MatchBoard a, MatchBoard b) =>
    a.Turn == b.Turn && a.Status == b.Status && a.Winner == b.Winner && a.Board.AsSpan().SequenceEqual(b.Board);

static void PrintBoard(MatchBoard board, byte mySymbol, string opponent)
{
    Console.WriteLine();
    Console.WriteLine(RenderBoard(board.Board));
    Console.WriteLine($"你={Mark(mySymbol)} 对手={opponent} 轮到={Mark(board.Turn)}");
}

static void PrintFinalBoard(MatchBoard board, byte mySymbol)
{
    Console.WriteLine(RenderBoard(board.Board));
    Console.WriteLine(board.Status switch
    {
        MatchStatus.Won when board.Winner == mySymbol => "你赢了！",
        MatchStatus.Won => "你输了。",
        MatchStatus.Draw => "平局。",
        _ => "对局结束。",
    });
}

static void Print(MatchView view)
{
    Console.WriteLine();
    Console.WriteLine(RenderBoard(view.Cells));
    Console.WriteLine($"你={Mark(view.MySymbol)} 对手={view.Opponent} 轮到={Mark(view.Turn)}");
}

static string RenderBoard(byte[] cells)
{
    static char Mark(byte v) => v switch { 1 => 'X', 2 => 'O', _ => '.' };
    return
        $" {Mark(cells[0])} | {Mark(cells[1])} | {Mark(cells[2])}\n" +
        "---+---+---\n" +
        $" {Mark(cells[3])} | {Mark(cells[4])} | {Mark(cells[5])}\n" +
        "---+---+---\n" +
        $" {Mark(cells[6])} | {Mark(cells[7])} | {Mark(cells[8])}";
}

static async Task RunBotAsync()
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
            Console.WriteLine(RenderBoard(va.Cells));
            Console.WriteLine($"A({Mark(va.MySymbol)}) result={(va.Winner == va.MySymbol ? "win" : va.Status == MatchStatus.Draw ? "draw" : "lose")}");
            Console.WriteLine($"B({Mark(vb.MySymbol)}) result={(vb.Winner == vb.MySymbol ? "win" : vb.Status == MatchStatus.Draw ? "draw" : "lose")}");
            return;
        }

        if (va.IsMyTurn)
        {
            var cell = PickCell(va);
            var ackA = await a.MoveAsync(cell);
            Console.WriteLine($"A move {cell}: {ackA.Message}");
        }
        else if (vb.IsMyTurn)
        {
            var cell = PickCell(vb);
            var ackB = await b.MoveAsync(cell);
            Console.WriteLine($"B move {cell}: {ackB.Message}");
        }
        else if (va.Status == MatchStatus.Queued)
        {
            await Task.Delay(200);
        }

        last = va;
        await Task.Delay(100);
    }

    Console.WriteLine("bot game timed out");
    if (last is not null) Console.WriteLine(RenderBoard(last.Value.Cells));
}

static int PickCell(MatchView view)
{
    // center, then corners, then first free — enough to produce a legal full game.
    int[] order = { 4, 0, 2, 6, 8, 1, 3, 5, 7 };
    foreach (var cell in order)
    {
        if (view.Cells[cell] == 0) return cell;
    }
    return 0;
}

static char Mark(byte v) => v switch { 1 => 'X', 2 => 'O', _ => '.' };

string Option(string name, string fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
