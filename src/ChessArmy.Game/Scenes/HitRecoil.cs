using System;
using ChessArmy.Core.Map;
using Microsoft.Xna.Framework;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Recul DIRECTIONNEL bref d'un pion touché par un coup annexe qui n'est PAS l'attaque principale animée
/// par <see cref="MeleeStrikeFx"/> — aujourd'hui le pion TRANSPERCÉ (une case derrière la cible). Il est
/// repoussé dans l'axe du coup puis revient sur sa case, exactement comme le recul d'une victime directe
/// (cf. <c>VictimKnockback</c>). Données + minuterie PURES : la scène applique <see cref="Offset"/> au
/// dessin du pion. Une seule secousse active à la fois (un transpercement par attaque).
/// </summary>
internal sealed class HitRecoil
{
    private const float Duration = 0.18f;   // calé sur le knockback de la victime directe (MeleeStrikeFx.KnockbackDur)
    private const float Fraction = 0.16f;   // amplitude (fraction de case), calée sur VictimKnockback

    private Cell _cell;
    private Vector2 _dir;
    private float _time;

    public bool Active => _time > 0f;

    /// <summary>Repousse <paramref name="cell"/> dans la direction (dc, dr) du coup, puis la ramène.</summary>
    public void Begin(Cell cell, int dc, int dr)
    {
        _cell = cell;
        _dir = new Vector2(dc, dr);
        if (_dir.LengthSquared() > 0f)
            _dir.Normalize();
        _time = Duration;
    }

    public void Update(float dt)
    {
        if (_time > 0f)
            _time = MathF.Max(0f, _time - dt);
    }

    /// <summary>Réinitialise (nouveau combat).</summary>
    public void Clear() => _time = 0f;

    /// <summary>
    /// Décalage (px entiers) de <paramref name="cell"/> pendant le recul ; <see cref="Point.Zero"/> sinon.
    /// Max au départ (contact) puis retour à 0 en ease-out — même courbe que <c>MeleeStrikeFx.KnockbackAmount</c>.
    /// </summary>
    public Point Offset(Cell cell, int size)
    {
        if (_time <= 0f || cell != _cell)
            return Point.Zero;
        var t = 1f - _time / Duration;            // 0 → 1
        var amt = 1f - EaseInOut(t);              // 1 au contact → 0 (recul qui se résorbe)
        var mag = size * Fraction * amt;
        return new Point((int)MathF.Round(_dir.X * mag), (int)MathF.Round(_dir.Y * mag));
    }

    private static float EaseInOut(float t) =>
        t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2) / 2f;
}
