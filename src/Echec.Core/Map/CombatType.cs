namespace Echec.Core.Map;

/// <summary>Objectif du combat porté par une map.</summary>
public enum CombatType
{
    Escarmouche,  // tuer toutes les unités ennemies
    Speciale,     // mission spéciale (contenu à venir ; générée comme une escarmouche pour l'instant)
    Boss,         // tuer le boss
}
