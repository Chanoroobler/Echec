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
        IReadOnlyList<MapObject>? objects = null, IReadOnlyList<Cell>? defensiveEnemySpawns = null,
        SpecialObjective objective = SpecialObjective.Aucun, int phase = 0, int turnLimit = 0,
        IReadOnlyList<Cell>? offensiveEnemySpawns = null)
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
        DefensiveEnemySpawns = defensiveEnemySpawns ?? System.Array.Empty<Cell>();
        OffensiveEnemySpawns = offensiveEnemySpawns ?? System.Array.Empty<Cell>();
        Objective = objective;
        Phase = phase;
        TurnLimit = turnLimit;
    }

    public string Name { get; }
    public CombatType Type { get; }

    /// <summary>Sous-type d'objectif d'une mission <see cref="CombatType.Speciale"/> (sinon <see cref="SpecialObjective.Aucun"/>).</summary>
    public SpecialObjective Objective { get; }

    /// <summary>
    /// Phase de campagne (1..3) à laquelle cette map est réservée, ou <c>0</c> = « toutes phases ». Sert à
    /// filtrer les maps SPÉCIALES et BOSS par phase (tirage parmi celles de la phase courante, repli sur les
    /// « toutes phases » puis, pour un boss, sur n'importe quelle map boss). Ignoré pour les escarmouches
    /// (sélectionnées par taille).
    /// </summary>
    public int Phase { get; }

    /// <summary>
    /// Limite de tours (rounds) d'une mission SPÉCIALE, ou <c>0</c> = « valeur par défaut du jeu ». Au-delà,
    /// la mission se clôt (« trop tard », pas une défaite). Ignoré hors mission spéciale.
    /// </summary>
    public int TurnLimit { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Cases où le joueur peut déployer ses unités.</summary>
    public IReadOnlyList<Cell> PlayerSpawns { get; }

    /// <summary>Cases d'apparition des unités ennemies (inclut les cases défensives).</summary>
    public IReadOnlyList<Cell> EnemySpawns { get; }

    /// <summary>
    /// Sous-ensemble d'<see cref="EnemySpawns"/> dont le pion doit prendre l'IA
    /// <see cref="Battle.AiKind.Defensif"/> (garde de mission spéciale). Vide pour une escarmouche.
    /// </summary>
    public IReadOnlyList<Cell> DefensiveEnemySpawns { get; }

    /// <summary>
    /// Sous-ensemble d'<see cref="EnemySpawns"/> dont le pion doit prendre l'IA
    /// <see cref="Battle.AiKind.Offensif"/> (assaillant des paysans, mission « protéger »). Vide sinon.
    /// </summary>
    public IReadOnlyList<Cell> OffensiveEnemySpawns { get; }

    /// <summary>Cases d'apparition du/des boss (vide pour une escarmouche).</summary>
    public IReadOnlyList<Cell> BossSpawns { get; }

    /// <summary>Objets posés sur le plateau (coffres, clés…), calque <c>objects</c> de la map. Vide si aucun.</summary>
    public IReadOnlyList<MapObject> Objects { get; }

    /// <summary>Tuile à une case (origine en haut à gauche, comme <see cref="Battlefield"/>).</summary>
    public TileDef TileAt(Cell cell) => _tiles[cell.Column, cell.Row];
}
