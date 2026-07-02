using System.Collections.Generic;
using Echec.Core.Map;

namespace Echec.Core.Battle;

/// <summary>
/// Motif de déplacement par domaine : les directions (glissé) ou décalages (sauté).
/// La portée est appliquée par le <see cref="Match"/> à partir de la classe de l'unité.
/// </summary>
public static class Movement
{
    // Directions glissées
    private static readonly Cell[] EightWay =
    {
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1, 0), new(1, 0),
        new(-1, 1), new(0, 1), new(1, 1),
    };

    private static readonly Cell[] Diagonals =
    {
        new(-1, -1), new(1, -1), new(-1, 1), new(1, 1),
    };

    private static readonly Cell[] Orthogonals =
    {
        new(0, -1), new(-1, 0), new(1, 0), new(0, 1),
    };

    // Sauts du cavalier (en L)
    private static readonly Cell[] KnightJumps =
    {
        new(-1, -2), new(1, -2), new(-2, -1), new(2, -1),
        new(-2, 1), new(2, 1), new(-1, 2), new(1, 2),
    };

    public static MovementKind Kind(Domaine domaine) =>
        domaine == Domaine.Cavalier ? MovementKind.Jump : MovementKind.Slide;

    /// <summary>Directions (glissé) ou décalages (sauté) du domaine.</summary>
    public static IReadOnlyList<Cell> Vectors(Domaine domaine) => domaine switch
    {
        Domaine.Dame => EightWay,        // 8 directions (base des unités de troupe), distance via la classe
        Domaine.Fou => Diagonals,
        Domaine.Tour => Orthogonals,
        Domaine.Cavalier => KnightJumps,
        _ => EightWay
    };
}
