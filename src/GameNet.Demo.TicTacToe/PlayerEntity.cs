using GameNet.Core;
using GameNet.Persistence;

namespace GameNet.Demo.TicTacToe;

/// <summary>Per-session player state, replicated to that session only.</summary>
[Entity("player")]
public sealed partial class PlayerEntity
{
    public int Id { get; set; }
    [Key] public string Name { get; set; } = string.Empty;
    public bool Authenticated { get; set; }
}
