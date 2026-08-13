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
public sealed record TileDef(string Id, bool BlocksMove, bool BlocksFire, bool Slides = false);
