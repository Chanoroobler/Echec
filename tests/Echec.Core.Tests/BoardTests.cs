using Echec.Core;
using Xunit;

namespace Echec.Core.Tests;

public class BoardTests
{
    [Fact]
    public void CreateInitial_PlaceExactly32Pieces()
    {
        var board = Board.CreateInitial();

        var count = 0;
        for (var file = 0; file < 8; file++)
            for (var rank = 0; rank < 8; rank++)
                if (board[new Position(file, rank)] is not null)
                    count++;

        Assert.Equal(32, count);
    }

    [Fact]
    public void CreateInitial_WhiteKingOnE1()
    {
        var board = Board.CreateInitial();

        Assert.Equal(new Piece(PieceType.King, PieceColor.White), board[new Position(4, 0)]);
    }

    [Fact]
    public void Position_ToString_UsesAlgebraicNotation()
    {
        Assert.Equal("e1", new Position(4, 0).ToString());
        Assert.Equal("a8", new Position(0, 7).ToString());
    }
}
