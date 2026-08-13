using System.Collections.Generic;
using ChessArmy.Core.Map;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Animation d'effondrement d'une tuile « chute » : quand le pion qui l'occupait la quitte, la tuile tombe
/// (descend + s'estompe) sur une courte durée, puis laisse un trou. Données + minuterie pures : la scène lit
/// <see cref="IsFalling"/> / <see cref="Progress"/> pour dessiner la tuile qui chute, et récolte via
/// <see cref="Update"/> les cases dont la chute vient de se terminer (à convertir en trou).
/// </summary>
internal sealed class ChuteFall
{
    private const float Duration = 0.45f;                    // durée de la chute (s)
    private readonly Dictionary<Cell, float> _falling = new();   // case → temps restant
    private readonly List<Cell> _scratch = new();

    /// <summary>Vrai tant qu'au moins une tuile est en train de tomber (gèle le tour côté scène).</summary>
    public bool Active => _falling.Count > 0;

    /// <summary>Déclenche la chute de <paramref name="cell"/> (sans effet si déjà en cours).</summary>
    public void Start(Cell cell)
    {
        if (!_falling.ContainsKey(cell))
            _falling[cell] = Duration;
    }

    /// <summary>Vrai si cette case est en cours de chute.</summary>
    public bool IsFalling(Cell cell) => _falling.ContainsKey(cell);

    /// <summary>Avancement de la chute (0 → 1) ; 1 si la case ne tombe pas/plus.</summary>
    public float Progress(Cell cell) => _falling.TryGetValue(cell, out var t) ? 1f - t / Duration : 1f;

    /// <summary>Fait avancer les chutes ; ajoute à <paramref name="justLanded"/> les cases qui viennent de
    /// finir de tomber (la scène les transforme alors en trou infranchissable).</summary>
    public void Update(float dt, ICollection<Cell> justLanded)
    {
        if (_falling.Count == 0)
            return;
        _scratch.Clear();
        foreach (var c in _falling.Keys)
            _scratch.Add(c);
        foreach (var c in _scratch)
        {
            var t = _falling[c] - dt;
            if (t <= 0f)
            {
                _falling.Remove(c);
                justLanded.Add(c);
            }
            else
            {
                _falling[c] = t;
            }
        }
    }

    /// <summary>Réinitialise (nouveau combat).</summary>
    public void Clear() => _falling.Clear();
}
