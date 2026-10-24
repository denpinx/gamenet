namespace GameNet.Demo.TicTacToe;

/// <summary>Starts the authoritative server and keeps the process alive (for `--server`).</summary>
public static class ServerHost
{
    public static async Task RunAsync(int port, string databasePath)
    {
        using var server = new TttServer(databasePath, port);
        Console.WriteLine($"READY {server.Port}");
        Console.Out.Flush();
        await Task.Delay(Timeout.Infinite);
    }
}
