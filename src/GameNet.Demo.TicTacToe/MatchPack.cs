using GameNet.Core;

namespace GameNet.Demo.TicTacToe;

public readonly record struct MatchSnapshot(MatchStatus Status, byte Turn, byte Winner, byte[] Cells)
{
    public bool IsMyTurn(byte symbol) => Status == MatchStatus.Playing && Turn == symbol;
}

/// <summary>
/// Packs a match into one replicated entity: 8 board cells in Health (2 bits each), the 9th cell +
/// turn + status in Team, winner in Flags. The collection filter hides other matches per session.
/// </summary>
public static class MatchPack
{
    public static EntityState Pack(Match match)
    {
        var game = match.Game;
        ushort board = 0;
        for (var i = 0; i < 8; i++) board |= (ushort)(game.Cells[i] << (i * 2));
        var team = (byte)(game.Cells[8] | (game.Turn << 2) | ((byte)game.Status << 4));
        return EntityState.Create(match.Id, EntityKind.Npc, 0, 0, team: team, health: (short)board, flags: game.Winner);
    }

    public static MatchSnapshot Unpack(EntityState entity)
    {
        var board = (ushort)entity.Health;
        var cells = new byte[9];
        for (var i = 0; i < 8; i++) cells[i] = (byte)((board >> (i * 2)) & 3);
        cells[8] = (byte)(entity.Team & 3);
        var turn = (byte)((entity.Team >> 2) & 3);
        var status = (MatchStatus)((entity.Team >> 4) & 0xF);
        return new MatchSnapshot(status, turn, entity.Flags, cells);
    }
}
