using System;
using System.Collections.Generic;
using ChessArmy.Core.Map;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Tremblement vertical LOCAL de tuiles : suite à un effet de zone (« Séisme » / « Impact »), les cases
/// de l'AoE tressautent de haut en bas, amplitude décroissante. Données + minuterie PURES : la scène
/// applique <see cref="OffsetY"/> au dessin de chaque tuile concernée (cf. DrawTerrain). Indépendant de
/// la secousse d'écran des FX d'attaque (cf. ShakeBoard).
/// </summary>
internal sealed class TileTremor
{
    private const float Duration = 0.35f;    // durée de la secousse (s)
    private const float Magnitude = 4f;      // amplitude verticale à pleine intensité (px canvas)
    private const float Frequency = 60f;     // vitesse de vibration (≈ 3 oscillations sur la durée)

    private readonly HashSet<Cell> _cells = new();
    private float _time;

    public bool Active => _time > 0f;

    /// <summary>Fait trembler les cases données (remplace toute secousse en cours ; sans effet si vide).</summary>
    public void Shake(IEnumerable<Cell> cells)
    {
        _cells.Clear();
        foreach (var c in cells)
            _cells.Add(c);
        _time = _cells.Count > 0 ? Duration : 0f;
    }

    public void Update(float dt)
    {
        if (_time > 0f)
            _time = Math.Max(0f, _time - dt);
    }

    /// <summary>Réinitialise (nouveau combat).</summary>
    public void Clear()
    {
        _cells.Clear();
        _time = 0f;
    }

    /// <summary>
    /// Décalage vertical (px entiers) à appliquer au dessin de <paramref name="cell"/> ; 0 si elle ne
    /// tremble pas. Sinusoïde décroissante, déphasée par case pour un rendu d'onde (pas de bloc rigide).
    /// </summary>
    public int OffsetY(Cell cell)
    {
        if (_time <= 0f || !_cells.Contains(cell))
            return 0;
        var decay = _time / Duration;                                   // 1 → 0
        var phase = (Duration - _time) * Frequency + cell.Column * 1.7f + cell.Row * 2.3f;
        return (int)MathF.Round(MathF.Sin(phase) * Magnitude * decay);
    }
}
