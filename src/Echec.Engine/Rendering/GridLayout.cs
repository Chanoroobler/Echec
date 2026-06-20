using Microsoft.Xna.Framework;

namespace Echec.Engine.Rendering;

/// <summary>
/// Conversion case logique ↔ pixels pour une grille droite avec épaisseur.
///
/// - <see cref="TileSize"/> (64) = zone de jeu d'une case ET pas de la grille.
/// - Le sprite mesure <see cref="SpriteWidth"/> × <see cref="SpriteHeight"/> (64×74) :
///   les 10 px supplémentaires (<see cref="Thickness"/>) sont l'épaisseur, dessinée
///   sous la surface. On dessine de l'arrière vers l'avant (rangée 0 → N) pour que
///   les cases de devant recouvrent correctement l'épaisseur de celles de derrière.
/// </summary>
public sealed class GridLayout
{
    public const int DefaultTileSize = 64;
    public const int DefaultSpriteWidth = 64;
    public const int DefaultSpriteHeight = 74;

    public GridLayout(
        Vector2 origin,
        int tileSize = DefaultTileSize,
        int spriteWidth = DefaultSpriteWidth,
        int spriteHeight = DefaultSpriteHeight,
        int? rowPitch = null)
    {
        Origin = origin;
        TileSize = tileSize;
        SpriteWidth = spriteWidth;
        SpriteHeight = spriteHeight;
        RowPitch = rowPitch ?? tileSize;
    }

    /// <summary>Coin haut-gauche de la grille à l'écran.</summary>
    public Vector2 Origin { get; }

    public int TileSize { get; }
    public int SpriteWidth { get; }
    public int SpriteHeight { get; }

    /// <summary>Distance verticale à l'écran entre deux rangées (par défaut = TileSize).</summary>
    public int RowPitch { get; }

    /// <summary>Épaisseur visible du sprite (partie sous la zone de jeu).</summary>
    public int Thickness => SpriteHeight - TileSize;

    /// <summary>Coin haut-gauche du sprite pour une case donnée.</summary>
    public Vector2 CellToScreen(int column, int row) =>
        new(Origin.X + column * TileSize, Origin.Y + row * RowPitch);

    /// <summary>Rectangle de destination du sprite (64×74) pour une case.</summary>
    public Rectangle CellToSpriteRect(int column, int row)
    {
        var position = CellToScreen(column, row);
        return new Rectangle((int)position.X, (int)position.Y, SpriteWidth, SpriteHeight);
    }

    /// <summary>
    /// Case dont la zone de jeu (64×64) contient le point écran, ou null en dehors.
    /// L'épaisseur n'est pas cliquable (seule la surface compte).
    /// </summary>
    public (int Column, int Row)? ScreenToCell(Point point, int columns, int rows)
    {
        var localX = point.X - (int)Origin.X;
        var localY = point.Y - (int)Origin.Y;
        if (localX < 0 || localY < 0)
            return null;

        var column = localX / TileSize;
        var row = localY / RowPitch;
        if (column >= columns || row >= rows)
            return null;

        // Rejette les clics tombant dans la bande d'épaisseur d'une rangée.
        if (localY - row * RowPitch >= TileSize)
            return null;

        return (column, row);
    }

    /// <summary>Taille totale de la grille à l'écran (pour la centrer).</summary>
    public static Vector2 MeasureBoard(int columns, int rows, int tileSize = DefaultTileSize,
        int spriteHeight = DefaultSpriteHeight, int? rowPitch = null)
    {
        var pitch = rowPitch ?? tileSize;
        var width = columns * tileSize;
        var height = (rows - 1) * pitch + spriteHeight;
        return new Vector2(width, height);
    }
}
