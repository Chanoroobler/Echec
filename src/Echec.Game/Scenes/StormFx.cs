using System;
using System.Collections.Generic;
using Echec.Core.Map;

namespace Echec.Game.Scenes;

/// <summary>
/// Animation de l'ORAGE / TEMPÊTE : de brefs éclairs s'abattent sur les ENNEMIS foudroyés lorsqu'un
/// porteur du trait attaque (les dégâts sont déjà résolus dans <see cref="Echec.Core.Battle.Match"/>).
/// Données + minuterie PURES : le rendu des éclairs (blocs pixel-art) vit dans la scène, qui interroge
/// <see cref="Progress"/>. Tant que <see cref="Active"/>, la scène gèle entrées, IA et fin de combat.
/// </summary>
public sealed class StormFx
{
    private const double Duration = 0.5;   // durée totale de la salve d'éclairs (s)

    private double _elapsed;
    private readonly List<Cell> _cells = new();

    public bool Active { get; private set; }

    /// <summary>Cases foudroyées : les ennemis frappés par l'orage au moment du déclenchement.</summary>
    public IReadOnlyList<Cell> Cells => _cells;

    /// <summary>Déclenche la salve sur les cases données (sans effet si la liste est vide).</summary>
    public void Begin(IEnumerable<Cell> cells)
    {
        _cells.Clear();
        _cells.AddRange(cells);
        _elapsed = 0;
        Active = _cells.Count > 0;
    }

    /// <summary>Réinitialise (nouveau combat).</summary>
    public void Clear()
    {
        _cells.Clear();
        _elapsed = 0;
        Active = false;
    }

    public void Update(double dt)
    {
        if (!Active)
            return;
        _elapsed += dt;
        if (_elapsed >= Duration)
            Active = false;
    }

    /// <summary>Avancement global [0,1] de la salve (0 au déclenchement, 1 à la fin).</summary>
    public float Progress => (float)Math.Clamp(_elapsed / Duration, 0, 1);
}
