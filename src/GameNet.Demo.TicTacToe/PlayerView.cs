using GameNet.Core;

namespace GameNet.Demo.TicTacToe;

/// <summary>Client view of the player entity; field order must match PlayerEntity.</summary>
[Entity("player")]
public sealed partial record PlayerView(int Id, string Name, bool Authenticated);
