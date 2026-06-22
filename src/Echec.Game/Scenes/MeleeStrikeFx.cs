using System;
using Echec.Core.Map;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Game.Scenes;

/// <summary>
/// Animation d'une attaque, déroulée PAR-DESSUS un combat déjà résolu dans le domaine
/// (<see cref="Echec.Core.Battle.Match"/> est instantané). Séquence : fente de l'attaquant
/// → impact (estafilade + secousse) → réaction de la cible (clignotement si elle survit,
/// dissolution si elle meurt). L'attaquant ne PREND LA PLACE qu'une fois la dissolution
/// terminée. Tant que <see cref="Active"/>, la scène gèle entrées, IA et fin de combat.
///
/// Données + minuterie pures : le rendu (sprites, layout, shader) reste dans la scène, qui
/// interroge les avancements ci-dessous.
/// </summary>
public sealed class MeleeStrikeFx
{
    // Minutage (s).
    private const double LungeDur    = 0.14; // fente vers la cible = fenêtre du balayage de lame
    private const double DissolveDur = 0.45; // désintégration du mort
    private const double BlinkDur    = 0.34; // clignotement du survivant
    private const double AdvanceDur  = 0.14; // l'attaquant prend la place libérée
    private const double RecoilDur   = 0.12; // retour sur sa case (pas d'avance)
    private const double ShakeDur    = 0.22; // secousse d'écran après l'impact
    private const double KnockbackDur = 0.18; // recul de la victime après le contact

    private const float LungeFraction = 0.42f; // amplitude de la fente, en fraction de case

    private double _elapsed;
    private double _total;
    private Vector2 _seed;
    private static int _seedCounter;

    public bool Active { get; private set; }

    /// <summary>Case OÙ le domaine a laissé l'attaquant (origine, ou cible s'il a avancé).</summary>
    public Cell Attacker { get; private set; }
    public Cell From { get; private set; }
    public Cell To { get; private set; }
    public Texture2D? AttackerSprite { get; private set; }
    public Texture2D? VictimSprite { get; private set; }
    public bool Killed { get; private set; }
    public bool Advanced { get; private set; }

    /// <summary>Graine de bruit propre à cette mort (chaque dissolution diffère).</summary>
    public Vector2 Seed => _seed;

    public void Begin(Cell from, Cell to, Cell attackerCell, Texture2D? attackerSprite,
        Texture2D? victimSprite, bool killed, bool advanced)
    {
        From = from;
        To = to;
        Attacker = attackerCell;
        AttackerSprite = attackerSprite;
        VictimSprite = victimSprite;
        Killed = killed;
        Advanced = advanced;
        _elapsed = 0;
        _seed = new Vector2((_seedCounter * 37) % 251, (_seedCounter * 101) % 241);
        _seedCounter++;
        Active = true;

        _total = (killed, advanced) switch
        {
            (true, true)  => LungeDur + DissolveDur + AdvanceDur, // mêlée mortelle : avance après dissolution
            (true, false) => LungeDur + DissolveDur,              // tir mortel : reste en place
            _             => LungeDur + BlinkDur,                 // survivant : flash après contact
        };
    }

    public void Update(double dt)
    {
        if (!Active)
            return;
        _elapsed += dt;
        if (_elapsed >= _total)
            Active = false;
    }

    /// <summary>Vrai à l'instant exact du contact (fin de la fente) — pour déclencher les étincelles.</summary>
    public bool HasImpacted => _elapsed >= LungeDur;

    /// <summary>
    /// Intensité du recul (knockback) de la victime [0,1] : max au contact, revient à 0. La scène en
    /// fait un décalage en pixels (direction = à l'opposé de l'attaquant).
    /// </summary>
    public float KnockbackAmount
    {
        get
        {
            var t = _elapsed - LungeDur;
            if (t < 0 || t > KnockbackDur)
                return 0f;
            return 1f - EaseInOut((float)(t / KnockbackDur));
        }
    }

    /// <summary>Avancement de la dissolution de la victime [0,1] (0 avant l'impact).</summary>
    public float DissolveProgress =>
        Killed ? (float)Math.Clamp((_elapsed - LungeDur) / DissolveDur, 0, 1) : 0f;

    /// <summary>Intensité du flash « touché » du survivant [0,1] (deux pulsations qui s'éteignent).</summary>
    public float FlashIntensity
    {
        get
        {
            if (Killed)
                return 0f;
            var k = (_elapsed - LungeDur) / BlinkDur;
            if (k < 0 || k > 1)
                return 0f;
            var pulse = 0.5f + 0.5f * (float)Math.Cos(k * Math.PI * 4);
            return (1f - (float)k) * pulse;
        }
    }

    /// <summary>
    /// Coin haut-gauche du sprite de l'attaquant, interpolé entre les ancrages écran de sa case
    /// d'origine (<paramref name="fromTop"/>) et de la cible (<paramref name="toTop"/>) : fente,
    /// puis maintien + avance (mêlée mortelle) ou recul (sinon).
    /// </summary>
    public Vector2 AttackerTopLeft(Vector2 fromTop, Vector2 toTop, float tile)
    {
        var dir = toTop - fromTop;
        if (dir.LengthSquared() > 0.0001f)
            dir.Normalize();
        var peak = fromTop + dir * (tile * LungeFraction);

        if (_elapsed < LungeDur)
            return Vector2.Lerp(fromTop, peak, EaseOut((float)(_elapsed / LungeDur)));

        if (Killed && Advanced)
        {
            var advStart = LungeDur + DissolveDur;
            if (_elapsed < advStart)
                return peak;                                    // maintien pendant la dissolution
            var k = Clamp01((_elapsed - advStart) / AdvanceDur);
            return Vector2.Lerp(peak, toTop, EaseInOut(k));     // prend la place libérée
        }

        var r = Clamp01((_elapsed - LungeDur) / RecoilDur);
        return Vector2.Lerp(peak, fromTop, EaseOut(r));         // recul sur sa case
    }

    /// <summary>Décalage de secousse d'écran (px entiers), s'éteignant après l'impact.</summary>
    public Point ShakeOffset(float magnitude)
    {
        var t = _elapsed - LungeDur;
        if (t < 0 || t > ShakeDur)
            return Point.Zero;
        var decay = (float)(1 - t / ShakeDur);
        var phase = (float)t * 90f;
        var x = (float)Math.Sin(phase) * magnitude * decay;
        var y = (float)Math.Sin(phase * 1.7f + 1.1f) * magnitude * decay;
        return new Point((int)Math.Round(x), (int)Math.Round(y));
    }

    private static float Clamp01(double v) => (float)Math.Clamp(v, 0, 1);
    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);
    private static float EaseInOut(float t) =>
        t < 0.5f ? 2f * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 2) / 2f;
}
