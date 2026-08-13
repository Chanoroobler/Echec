using System;
using System.Collections.Generic;
using ChessArmy.Core.Map;
using ChessArmy.Engine.Rendering;
using Microsoft.Xna.Framework;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Glissade sur glace : après s'être arrêté sur une tuile « glisse », un pion dérape en chaîne jusqu'à sa
/// case de repos (le moteur l'y a DÉJÀ placé). Ce petit animateur rejoue le TRAJET visuel : le pion, dessiné
/// sur sa case de repos, est décalé en arrière sur son chemin (path[0] = case d'arrêt initiale → path[^1] =
/// repos) puis rattrape sa case en décélérant. Données + minuterie pures : la scène applique <see cref="Offset"/>
/// au dessin du pion de la case de repos (cf. DrawUnit) et gèle le tour tant que <see cref="Active"/>.
/// </summary>
internal sealed class SlideGlide
{
    private const float StepDuration = 0.12f;   // durée par case glissée (s)
    private const float MaxDuration = 0.5f;     // plafond (glissades très longues)

    private IReadOnlyList<Cell> _path = Array.Empty<Cell>();
    private float _time;
    private float _duration;

    public bool Active => _time > 0f;

    /// <summary>Case de repos (là où le moteur a placé le pion) : la seule dont le dessin est décalé.</summary>
    public Cell RestCell => _path.Count > 0 ? _path[_path.Count - 1] : default;

    /// <summary>Démarre la glissade sur <paramref name="path"/> (case d'arrêt en tête → repos en queue).
    /// Sans effet si moins de 2 cases (pas de glissade).</summary>
    public void Start(IReadOnlyList<Cell> path)
    {
        if (path is null || path.Count < 2) { _time = 0f; return; }
        _path = path;
        _duration = MathHelper.Min((path.Count - 1) * StepDuration, MaxDuration);
        _time = _duration;
    }

    public void Update(float dt)
    {
        if (_time > 0f)
            _time = Math.Max(0f, _time - dt);
    }

    /// <summary>Réinitialise (nouveau combat).</summary>
    public void Clear()
    {
        _path = Array.Empty<Cell>();
        _time = 0f;
    }

    /// <summary>
    /// Décalage (px entiers) à appliquer au dessin de <paramref name="cell"/> : non nul UNIQUEMENT pour la case
    /// de repos pendant la glissade. Le pion part de sa case d'arrêt initiale et rattrape sa case de repos le long
    /// du chemin (décélération douce). Zéro sinon (se branche au rendu comme <c>ReculeSlideOffset</c>).
    /// </summary>
    public Point Offset(Cell cell, GridLayout layout)
    {
        if (_time <= 0f || _path.Count < 2 || cell != RestCell)
            return Point.Zero;

        var eased = 1f - _time / _duration;          // 0 → 1
        eased = 1f - (1f - eased) * (1f - eased);     // easeOutQuad : dérapage qui décélère jusqu'au repos

        var steps = _path.Count - 1;
        var f = eased * steps;
        var seg = Math.Min((int)f, steps - 1);
        var frac = f - seg;
        var a = layout.CellToScreen(_path[seg].Column, _path[seg].Row);
        var b = layout.CellToScreen(_path[seg + 1].Column, _path[seg + 1].Row);
        var pos = Vector2.Lerp(a, b, frac);
        var rest = layout.CellToScreen(RestCell.Column, RestCell.Row);
        var off = pos - rest;                          // décalage relatif à la case de repos
        return new Point((int)MathF.Round(off.X), (int)MathF.Round(off.Y));
    }
}
