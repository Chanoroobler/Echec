namespace ChessArmy.Core.Map;

/// <summary>
/// Définition d'une tuile du catalogue (chargée depuis <c>tiles.json</c>) : son identifiant
/// (= nom du PNG <c>Assets/Tiles/&lt;id&gt;.png</c>) et ses règles de jeu.
/// </summary>
/// <param name="Id">Identifiant unique de la tuile.</param>
/// <param name="BlocksMove">Vrai si on ne peut ni s'arrêter ni passer dessus (mur, eau).</param>
/// <param name="BlocksFire">Vrai si la tuile coupe la ligne de tir (mur). L'eau laisse passer.</param>
/// <param name="Slides">Vrai si la tuile est GLISSANTE (glace) : une unité qui s'arrête dessus glisse
/// d'une case dans sa direction d'arrivée, en chaîne tant qu'elle atterrit sur une autre tuile glissante,
/// jusqu'à un obstacle, un pion ou le bord du plateau (cf. <see cref="Battle.Match"/>).</param>
/// <param name="RangeBonus">
/// Portée d'attaque GAGNÉE par l'unité postée dessus (tour de guet). 0 = tuile ordinaire. Ne profite qu'aux
/// TIREURS — portée native &gt;= <see cref="Battle.Match.RangedAttackRange"/> — : une tour n'allonge pas le
/// bras d'un soldat au contact. Cf. <see cref="Battle.Match.AttackRangeBonus"/>.
/// </param>
/// <param name="OccupantDx">
/// Décalage HORIZONTAL, en pixels de tuile NATIVE (64), du pion posté sur cette case. Repère écran :
/// positif = vers la droite. 0 = pion centré, le cas de toute tuile ordinaire. Purement COSMÉTIQUE — les
/// règles ne connaissent que la case — : sert aux tuiles dont l'art n'a pas son plancher au centre (tour de
/// guet), sur lesquelles un pion centré paraît décroché.
/// </param>
/// <param name="OccupantDy">Décalage VERTICAL du pion posté (repère écran : NÉGATIF = plus haut).
/// Cf. <paramref name="OccupantDx"/>.</param>
/// <param name="Trait">
/// Trait de combat (cf. <c>Battle.Trait</c>) PRÊTÉ à l'unité postée dessus, tant qu'elle y reste. Null = la
/// tuile n'en prête aucun, le cas de toute tuile ordinaire. Contrairement à <paramref name="RangeBonus"/>, il
/// vaut pour N'IMPORTE QUELLE unité : c'est au trait choisi de n'avoir de sens que pour certaines (un mirador
/// prête « Balistique », qui ne sert qu'à qui tire).
/// </param>
public sealed record TileDef(string Id, bool BlocksMove, bool BlocksFire, bool Slides = false, int RangeBonus = 0,
    int OccupantDx = 0, int OccupantDy = 0, string? Trait = null);
