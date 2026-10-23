using GameNet.Demo.TicTacToe;
using Xunit;

namespace GameNet.Tests;

public class TicTacToeGameTests
{
    [Fact]
    public void HorizontalWin_IsDetected()
    {
        var game = new TicTacToeGame();
        Assert.True(game.TryMove(1, 0, out _));
        Assert.True(game.TryMove(2, 3, out _));
        Assert.True(game.TryMove(1, 1, out _));
        Assert.True(game.TryMove(2, 4, out _));
        Assert.True(game.TryMove(1, 2, out _));

        Assert.Equal(MatchStatus.Won, game.Status);
        Assert.Equal(1, game.Winner);
    }

    [Fact]
    public void OutOfTurnMove_IsRejected()
    {
        var game = new TicTacToeGame();
        Assert.False(game.TryMove(2, 0, out var error));
        Assert.Contains("轮到", error);
        Assert.Equal(0, game.Cells[0]);
    }

    [Fact]
    public void OccupiedCell_IsRejected()
    {
        var game = new TicTacToeGame();
        Assert.True(game.TryMove(1, 4, out _));
        Assert.False(game.TryMove(2, 4, out var error));
        Assert.Contains("棋子", error);
    }

    [Fact]
    public void FullBoardWithoutWinner_IsDraw()
    {
        var game = new TicTacToeGame();
        int[] moves = { 0, 1, 2, 4, 3, 5, 7, 6, 8 };
        byte player = 1;
        foreach (var cell in moves)
        {
            Assert.True(game.TryMove(player, cell, out var error), error);
            player = player == 1 ? (byte)2 : (byte)1;
        }

        Assert.Equal(MatchStatus.Draw, game.Status);
        Assert.Equal(0, game.Winner);
    }

    [Fact]
    public void Forfeit_GivesWinToOpponent()
    {
        var game = new TicTacToeGame();
        game.Forfeit(1);
        Assert.Equal(MatchStatus.Won, game.Status);
        Assert.Equal(2, game.Winner);
    }
}