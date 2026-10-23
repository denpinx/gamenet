namespace GameNet.Demo.TicTacToe;

public enum MatchStatus : byte
{
    Idle = 0,
    Queued = 1,
    Playing = 2,
    Won = 3,
    Draw = 4,
}

public sealed class TicTacToeGame
{
    public static readonly int[][] Lines =
    {
        new[] { 0, 1, 2 }, new[] { 3, 4, 5 }, new[] { 6, 7, 8 },
        new[] { 0, 3, 6 }, new[] { 1, 4, 7 }, new[] { 2, 5, 8 },
        new[] { 0, 4, 8 }, new[] { 2, 4, 6 },
    };

    public byte[] Cells { get; } = new byte[9];
    public byte Turn { get; private set; } = 1;
    public MatchStatus Status { get; private set; } = MatchStatus.Playing;
    public byte Winner { get; private set; }

    public bool TryMove(byte player, int cell, out string error)
    {
        error = string.Empty;
        if (Status != MatchStatus.Playing) { error = "对局已结束"; return false; }
        if (player != Turn) { error = "还没轮到你"; return false; }
        if (cell is < 0 or > 8) { error = "格子编号必须是 0..8"; return false; }
        if (Cells[cell] != 0) { error = "这个格子已经有棋子了"; return false; }

        Cells[cell] = player;
        if (CheckWin(player))
        {
            Status = MatchStatus.Won;
            Winner = player;
            return true;
        }
        if (Array.TrueForAll(Cells, c => c != 0))
        {
            Status = MatchStatus.Draw;
            return true;
        }
        Turn = player == 1 ? (byte)2 : (byte)1;
        return true;
    }

    public bool CheckWin(byte player)
    {
        foreach (var line in Lines)
        {
            if (Cells[line[0]] == player && Cells[line[1]] == player && Cells[line[2]] == player)
                return true;
        }
        return false;
    }

    public void Forfeit(byte loser)
    {
        if (Status != MatchStatus.Playing) return;
        Status = MatchStatus.Won;
        Winner = loser == 1 ? (byte)2 : (byte)1;
    }

    public string Render()
    {
        static char Mark(byte v) => v switch { 1 => 'X', 2 => 'O', _ => '.' };
        var c = Cells;
        return
            $" {Mark(c[0])} | {Mark(c[1])} | {Mark(c[2])}\n" +
            "---+---+---\n" +
            $" {Mark(c[3])} | {Mark(c[4])} | {Mark(c[5])}\n" +
            "---+---+---\n" +
            $" {Mark(c[6])} | {Mark(c[7])} | {Mark(c[8])}";
    }
}
