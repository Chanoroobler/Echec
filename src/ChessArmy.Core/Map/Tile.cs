namespace ChessArmy.Core.Map;

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

    /// <summary>Vrai si la tuile est glissante (glace) : une unité qui s'y arrête glisse d'une case.</summary>
    public bool Slippery => Def.Slides;

    /// <summary>Portée d'attaque gagnée par le TIREUR posté dessus (tour de guet) ; 0 = tuile ordinaire.</summary>
    public int RangeBonus => Def.RangeBonus;

    /// <summary>Décalage COSMÉTIQUE du pion posté dessus, en pixels de tuile native (droite / bas positifs).
    /// (0 0) = pion centré, le cas de toute tuile ordinaire.</summary>
    public (int Dx, int Dy) OccupantOffset => (Def.OccupantDx, Def.OccupantDy);

    /// <summary>Trait de combat prêté à l'unité postée dessus, ou null (le cas de toute tuile ordinaire).</summary>
    public string? GrantedTrait => Def.Trait;
}
