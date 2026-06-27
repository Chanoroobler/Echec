namespace Echec.Core.Map;

/// <summary>
/// Donnée d'une case : sa tuile du catalogue (et, plus tard, occupant, hauteur, etc.).
/// Les règles de jeu sont déléguées à la <see cref="TileDef"/>.
/// </summary>
public readonly record struct Tile(TileDef Def)
{
    /// <summary>Identifiant de la tuile (= nom du PNG <c>Assets/Tiles/&lt;id&gt;.png</c>).</summary>
    public string Id => Def.Id;

    /// <summary>Vrai si une unité ne peut ni s'arrêter ni passer sur cette tuile (mur, eau).</summary>
    public bool BlocksMovement => Def.BlocksMove;

    /// <summary>Vrai si la tuile arrête une ligne de tir (mur ; l'eau laisse passer).</summary>
    public bool BlocksLineOfFire => Def.BlocksFire;
}
