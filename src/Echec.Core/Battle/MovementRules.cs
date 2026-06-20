using System.Collections.Generic;
using Echec.Core.Map;

namespace Echec.Core.Battle;

/// <summary>
/// Règles de déplacement par type d'unité, exprimées en décalages de cases
/// (portée 1 pour le POC). Le Soldat se déplace comme le roi : 1 case dans
/// n'importe quelle direction.
/// </summary>
public static class MovementRules
{
    private static readonly Cell[] King =
    {
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1, 0), new(1, 0),
        new(-1, 1), new(0, 1), new(1, 1),
    };

    /// <summary>Décalages de destination possibles depuis la case d'origine.</summary>
    public static IReadOnlyList<Cell> Offsets(UnitType type) => type switch
    {
        UnitType.Soldier => King,
        _ => King
    };
}
