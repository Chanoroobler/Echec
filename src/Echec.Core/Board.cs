namespace Echec.Core;

/// <summary>
/// Échiquier 8x8. Conteneur d'état pur : aucune règle de déplacement ici
/// (les règles viendront dans des services dédiés du domaine).
/// </summary>
public sealed class Board
{
    private readonly Piece?[,] _squares = new Piece?[8, 8];

    public Piece? this[Position position] => _squares[position.File, position.Rank];

    public void Set(Position position, Piece? piece) =>
        _squares[position.File, position.Rank] = piece;

    /// <summary>Construit l'échiquier dans sa position de départ standard.</summary>
    public static Board CreateInitial()
    {
        var board = new Board();

        for (var file = 0; file < 8; file++)
        {
            board.Set(new Position(file, 1), new Piece(PieceType.Pawn, PieceColor.White));
            board.Set(new Position(file, 6), new Piece(PieceType.Pawn, PieceColor.Black));
        }

        PieceType[] backRank =
        [
            PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
            PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook
        ];

        for (var file = 0; file < 8; file++)
        {
            board.Set(new Position(file, 0), new Piece(backRank[file], PieceColor.White));
            board.Set(new Position(file, 7), new Piece(backRank[file], PieceColor.Black));
        }

        return board;
    }
}
