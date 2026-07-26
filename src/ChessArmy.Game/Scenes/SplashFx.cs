using System;
using System.Collections.Generic;
using ChessArmy.Core.Map;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Animation du FEEDBACK d'EMBROCHAGE : les ennemis voisins de la cible, touchés par l'éclaboussure
/// (déjà résolue dans <see cref="ChessArmy.Core.Battle.Match"/>), réagissent — SURSAUT vers l'extérieur
/// (D) + flash « touché » (A) s'ils survivent, DISSOLUTION s'ils meurent (ils sont déjà retirés du
/// plateau : leur sprite est capturé AVANT l'attaque, comme la victime directe). Données + minuterie
/// PURES : le rendu vit dans la scène, qui interroge les avancements ci-dessous. Tant que
/// <see cref="Active"/>, la scène gèle entrées, IA et fin de combat (cf. <see cref="StormFx"/>).
/// </summary>
public sealed class SplashFx
{
    private const double JoltDur     = 0.18; // sursaut vers l'extérieur (max au contact, revient)
    private const double FlashDur    = 0.34; // clignotement « touché » du survivant
    private const double DissolveDur = 0.45; // désintégration d'un voisin tué (= victime directe)
    private const double Total       = 0.45; // la salve dure le plus long des trois

    /// <summary>Un voisin embroché : sa case, son sprite capturé, s'il est mort, la direction du sursaut, sa graine.</summary>
    public readonly record struct Hit(Cell Cell, Texture2D? Sprite, bool Killed, Vector2 Dir, Vector2 Seed);

    private readonly List<Hit> _hits = new();
    private double _elapsed;

    public bool Active { get; private set; }

    /// <summary>Voisins réellement touchés par l'embrochage au déclenchement.</summary>
    public IReadOnlyList<Hit> Hits => _hits;

    /// <summary>Déclenche la salve sur les voisins donnés (sans effet si la liste est vide).</summary>
    public void Begin(IEnumerable<Hit> hits)
    {
        _hits.Clear();
        _hits.AddRange(hits);
        _elapsed = 0;
        Active = _hits.Count > 0;
    }

    /// <summary>Réinitialise (nouveau combat).</summary>
    public void Clear()
    {
        _hits.Clear();
        _elapsed = 0;
        Active = false;
    }

    public void Update(double dt)
    {
        if (!Active)
            return;
        _elapsed += dt;
        if (_elapsed >= Total)
            Active = false;
    }

    /// <summary>Amplitude [0,1] du SURSAUT vers l'extérieur : max au contact, revient à 0 (recul, comme la victime).</summary>
    public float JoltAmount
    {
        get
        {
            var t = _elapsed / JoltDur;
            return t is >= 0 and <= 1 ? 1f - EaseInOut((float)t) : 0f;
        }
    }

    /// <summary>Intensité du flash « touché » du survivant [0,1] (deux pulsations qui s'éteignent, cf. MeleeStrikeFx).</summary>
    public float FlashIntensity
    {
        get
        {
            var k = _elapsed / FlashDur;
            if (k < 0 || k > 1)
                return 0f;
            var pulse = 0.5f + 0.5f * (float)Math.Cos(k * Math.PI * 4);
            return (1f - (float)k) * pulse;
        }
    }

    /// <summary>Avancement [0,1] de la dissolution d'un voisin tué (0 au déclenchement, 1 à la fin).</summary>
    public float DissolveProgress => (float)Math.Clamp(_elapsed / DissolveDur, 0, 1);

    private static float EaseInOut(float t) =>
        t < 0.5f ? 2f * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 2) / 2f;
}
