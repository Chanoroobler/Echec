namespace Echec.Core.Map;

/// <summary>
/// Coordonnée d'une case du champ de bataille : colonne (X) / rangée (Y),
/// origine en haut à gauche. Générique, indépendant de toute règle de jeu.
/// </summary>
public readonly record struct Cell(int Column, int Row);
