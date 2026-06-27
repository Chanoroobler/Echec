namespace Echec.Core.Map;

/// <summary>
/// Définition d'une tuile du catalogue (chargée depuis <c>tiles.json</c>) : son identifiant
/// (= nom du PNG <c>Assets/Tiles/&lt;id&gt;.png</c>) et ses règles de jeu.
/// </summary>
/// <param name="Id">Identifiant unique de la tuile.</param>
/// <param name="BlocksMove">Vrai si on ne peut ni s'arrêter ni passer dessus (mur, eau).</param>
/// <param name="BlocksFire">Vrai si la tuile coupe la ligne de tir (mur). L'eau laisse passer.</param>
public sealed record TileDef(string Id, bool BlocksMove, bool BlocksFire);
