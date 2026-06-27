namespace Echec.Core.Map;

/// <summary>
/// Champ de bataille : grille rectangulaire de <see cref="Tile"/>.
/// État pur, sans logique de rendu ni de règles.
/// </summary>
public sealed class Battlefield
{
    private readonly Tile[,] _tiles;

    public Battlefield(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        Width = width;
        Height = height;
        _tiles = new Tile[width, height];
    }

    public int Width { get; }
    public int Height { get; }

    public Tile this[Cell cell]
    {
        get => _tiles[cell.Column, cell.Row];
        set => _tiles[cell.Column, cell.Row] = value;
    }

    public bool Contains(Cell cell) =>
        cell.Column >= 0 && cell.Column < Width &&
        cell.Row >= 0 && cell.Row < Height;

    /// <summary>Parcourt toutes les cases (ordre rangée par rangée, de haut en bas).</summary>
    public IEnumerable<Cell> Cells()
    {
        for (var row = 0; row < Height; row++)
            for (var column = 0; column < Width; column++)
                yield return new Cell(column, row);
    }

    /// <summary>Crée un champ de bataille plat d'une seule tuile (herbe par défaut).</summary>
    public static Battlefield CreateFlat(int width, int height, TileDef? terrain = null)
    {
        var tile = new Tile(terrain ?? BuiltInTiles.Grass);
        var battlefield = new Battlefield(width, height);
        foreach (var cell in battlefield.Cells())
            battlefield[cell] = tile;
        return battlefield;
    }

    /// <summary>Construit un champ de bataille à partir d'une map dessinée à la main.</summary>
    public static Battlefield FromMap(MapData map)
    {
        var battlefield = new Battlefield(map.Width, map.Height);
        foreach (var cell in battlefield.Cells())
            battlefield[cell] = new Tile(map.TileAt(cell));
        return battlefield;
    }
}
