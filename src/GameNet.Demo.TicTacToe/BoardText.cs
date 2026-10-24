namespace GameNet.Demo.TicTacToe;

public static class BoardText
{
    public static char Mark(byte value) => value switch
    {
        1 => 'X',
        2 => 'O',
        _ => '.',
    };

    public static string Render(byte[] cells) =>
        $" {Mark(cells[0])} | {Mark(cells[1])} | {Mark(cells[2])}\n" +
        "---+---+---\n" +
        $" {Mark(cells[3])} | {Mark(cells[4])} | {Mark(cells[5])}\n" +
        "---+---+---\n" +
        $" {Mark(cells[6])} | {Mark(cells[7])} | {Mark(cells[8])}";

    public static string Result(MatchStatus status, byte winner, byte mySymbol) => status switch
    {
        MatchStatus.Won when winner == mySymbol => "你赢了！",
        MatchStatus.Won => "你输了。",
        MatchStatus.Draw => "平局。",
        _ => "对局结束。",
    };
}
