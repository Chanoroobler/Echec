using System.Collections.Generic;

namespace Echec.Core.Map;

/// <summary>
/// Map dessinée à la main, chargée depuis un <c>.json</c> : grille de tuiles (taille variable) +
/// cases de spawn par camp + objectif de combat. Domaine pur (aucun rendu, aucun accès disque).
/// </summary>
public sealed class MapData
{
    private readonly TileDef[,] _tiles;

    public MapData(string name, CombatType type, int width, int height, TileDef[,] tiles,
        IReadOnlyList<Cell> playerSpawns, IReadOnlyList<Cell> enemySpawns, IReadOnlyList<Cell> bossSpawns,
        IReadOnlyList<MapObject>? objects = null)
    {
        Name = name;
        Type = type;
        Width = width;
        Height = height;
        _tiles = tiles;
        PlayerSpawns = playerSpawns;
        EnemySpawns = enemySpawns;
        BossSpawns = bossSpawns;
        Objects = objects ?? System.Array.Empty<MapObject>();
    }

    public string Name { get; }
    public CombatType Type { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Cases où le joueur peut déployer ses unités.</summary>
    public IReadOnlyList<Cell> PlayerSpawns { get; }

    /// <summary>Cases d'apparition des unités ennemies.</summary>
    public IReadOnlyList<Cell> EnemySpawns { get; }

    /// <summary>Cases d'apparition du/des boss (vide pour une escarmouche).</summary>
    public IReadOnlyList<Cell> BossSpawns { get; }

    /// <summary>Objets posés sur le plateau (coffres, clés…), calque <c>objects</c> de la map. Vide si aucun.</summary>
    public IReadOnlyList<MapObject> Objects { get; }

    /// <summary>Tuile à une case (origine en haut à gauche, comme <see cref="Battlefield"/>).</summary>
    public TileDef TileAt(Cell cell) => _tiles[cell.Column, cell.Row];
}
