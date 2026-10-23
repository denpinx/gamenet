using GameNet.Core;

namespace GameNet.Demo.TicTacToe;

/// <summary>Server-side replicated match: a mutable class with business fields, no bit packing.</summary>
[Entity("match")]
public sealed partial class MatchEntity
{
    public int Id { get; set; }
    public int PlayerA { get; set; }
    public int PlayerB { get; set; }
    public byte Turn { get; set; }
    public MatchStatus Status { get; set; }
    public byte Winner { get; set; }
    public byte[] Board { get; set; } = new byte[9];
}

/// <summary>Client-side view with the same field ids; records are fine for read-only views.</summary>
[Entity("match")]
public sealed partial record MatchBoard(int Id, int PlayerA, int PlayerB, byte Turn, MatchStatus Status, byte Winner, byte[] Board);
