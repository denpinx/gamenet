using GameNet.Demo.TicTacToe;

var mode = args.Contains("--server") ? "server"
    : args.Contains("--bot") ? "bot"
    : "client";

var host = Option("--host", "127.0.0.1");
var port = int.Parse(Option("--port", "0"));

if (mode == "server")
{
    var db = Option("--db", Path.Combine(Path.GetTempPath(), "gamenet-ttt.db"));
    await ServerHost.RunAsync(port, db);
    return;
}

if (mode == "bot")
{
    await BotRunner.RunAsync();
    return;
}

if (port == 0) port = 7777;
Console.WriteLine($"连接 {host}:{port}");
await ClientConsole.RunAsync(host, port);

string Option(string name, string fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
