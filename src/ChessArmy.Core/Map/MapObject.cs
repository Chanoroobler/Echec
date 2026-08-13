namespace ChessArmy.Core.Map;

/// <summary>
/// Type d'objet posé sur une case du plateau (calque <c>objects</c> des maps, distinct du terrain et
/// des spawns). Un objet repose SUR une tuile normale (la case reste marchable). Exploités par le jeu :
/// <see cref="ChestCommon"/>, <see cref="Recruit"/>, <see cref="Bush"/> ; clé et coffre à clé sont
/// déclarés pour la suite.
/// </summary>
public enum MapObjectKind
{
    ChestCommon,   // coffre sans clé : butin commun (C)
    ChestRare,     // coffre à clé : butin rare (K) — à venir
    Key,           // clé : ouvre un coffre à clé (k) — à venir
    Recruit,       // tuile de recrutement : un allié qui entre dessus gagne une unité aléatoire (R)
    Bush,          // buisson : un pion DESSUS reçoit -4 dégâts (couvert, non consommé) (B)
    Chute,         // tuile piège : marchable une fois ; quand le pion qui l'occupait la QUITTE (en combat),
                   // elle s'effondre en trou infranchissable. Tremble tant qu'un pion est dessus. (F)
}

/// <summary>Un objet placé sur une case du plateau : son type et sa case. Donnée pure.</summary>
/// <param name="Cell">Case du plateau où l'objet est posé.</param>
/// <param name="Kind">Type d'objet.</param>
public readonly record struct MapObject(Cell Cell, MapObjectKind Kind);
