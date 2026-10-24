using GameNet.Core;

namespace GameNet.Demo.TicTacToe;

/// <summary>Interactive console client: REPL commands, then one event-driven match.</summary>
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
        using var session = new MatchSession(_client);
        var queued = await _client.QueueAsync();
        Console.WriteLine(queued.Message);
        if (queued.Status == MatchStatus.Idle) return;
        if (queued.Status == MatchStatus.Playing) await session.RefreshAsync();
        await session.Completion;
    }

    private static void PrintView(MatchView view)
    {
        Console.WriteLine();
        Console.WriteLine(BoardText.Render(view.Cells));
        Console.WriteLine($"你={BoardText.Mark(view.MySymbol)} 对手={view.Opponent} 轮到={BoardText.Mark(view.Turn)}");
    }

    private static void Usage(string text) => Console.WriteLine($"用法：{text}");
}
