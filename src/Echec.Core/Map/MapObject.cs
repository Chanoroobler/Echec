namespace Echec.Core.Map;

/// <summary>
/// Type d'objet posé sur une case du plateau (calque <c>objects</c> des maps, distinct du terrain et
/// des spawns). Un objet repose SUR une tuile normale (la case reste marchable). Pour l'instant seul
/// <see cref="ChestCommon"/> est exploité par le jeu ; clé et coffre à clé sont déclarés pour la suite.
/// </summary>
public enum MapObjectKind
{
    ChestCommon,   // coffre sans clé : butin commun (C)
    ChestRare,     // coffre à clé : butin rare (K) — à venir
    Key,           // clé : ouvre un coffre à clé (k) — à venir
}

/// <summary>Un objet placé sur une case du plateau : son type et sa case. Donnée pure.</summary>
/// <param name="Cell">Case du plateau où l'objet est posé.</param>
/// <param name="Kind">Type d'objet.</param>
public readonly record struct MapObject(Cell Cell, MapObjectKind Kind);
