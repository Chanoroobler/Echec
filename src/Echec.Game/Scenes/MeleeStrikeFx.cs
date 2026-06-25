using System;
using Echec.Core.Map;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Game.Scenes;

/// <summary>Style d'animation d'attaque, choisi selon l'unité (cf. GameplayScene).</summary>
public enum AttackStyle
{
    /// <summary>Fente brève vers la cible puis recul/avance (soldat, lancier, défaut).</summary>
    Lunge,
    /// <summary>Charge sautée « façon cheval » : bond en arc qui percute, retombe à sa place ou
    /// atterrit sur la case si la cible meurt (cavalier).</summary>
    Leap,
    /// <summary>Incantation à distance : le mage reste en place (léger recul) et tire un projectile
    /// magique qui vole jusqu'à la cible ; l'impact se produit à l'arrivée (mage).</summary>
    Cast,
    /// <summary>Tir à l'arc : l'archer reste en place (recul de bande) et décoche une flèche rapide
    /// qui file jusqu'à la cible ; l'impact se produit à l'arrivée (archer / domaine Dame).</summary>
    Shoot,
}

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
    private const double LeapDur     = 0.26; // approche de la charge sautée (plus longue : on voit le bond)
    private const double CastDur     = 0.32; // vol du projectile magique jusqu'à la cible
    private const double ShootDur    = 0.22; // vol de la flèche (plus rapide que le sort)
    private const double DissolveDur = 0.45; // désintégration du mort
    private const double BlinkDur    = 0.34; // clignotement du survivant
    private const double AdvanceDur  = 0.14; // l'attaquant prend la place libérée
    private const double RecoilDur   = 0.12; // retour sur sa case (pas d'avance)
    private const double ShakeDur    = 0.22; // secousse d'écran après l'impact
    private const double KnockbackDur = 0.18; // recul de la victime après le contact

    private const float LungeFraction = 0.42f; // amplitude de la fente, en fraction de case
    private const float LeapContactGap = 0.25f; // arrêt de la charge avant la case cible (fraction de case)
    private const float LeapJumpFraction = 0.55f; // hauteur du bond principal (fraction de case)
    private const float LeapHopFraction = 0.30f;  // hauteur du petit saut d'avance/repli

    private double _elapsed;
    private double _total;
    private double _approachDur;   // durée de la phase d'approche (dépend du style)
    private AttackStyle _style;
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
        Texture2D? victimSprite, bool killed, bool advanced, AttackStyle style = AttackStyle.Lunge)
    {
        From = from;
        To = to;
        Attacker = attackerCell;
        AttackerSprite = attackerSprite;
        VictimSprite = victimSprite;
        Killed = killed;
        Advanced = advanced;
        _style = style;
        _approachDur = style switch
        {
            AttackStyle.Leap  => LeapDur,
            AttackStyle.Cast  => CastDur,
            AttackStyle.Shoot => ShootDur,
            _                 => LungeDur,
        };
        _elapsed = 0;
        _seed = new Vector2((_seedCounter * 37) % 251, (_seedCounter * 101) % 241);
        _seedCounter++;
        Active = true;

        _total = (killed, advanced) switch
        {
            (true, true)  => _approachDur + DissolveDur + AdvanceDur, // mêlée mortelle : avance après dissolution
            (true, false) => _approachDur + DissolveDur,              // tir mortel : reste en place
            _             => _approachDur + BlinkDur,                 // survivant : flash après contact
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

    /// <summary>Vrai à l'instant exact du contact (fin de l'approche) — pour déclencher l'impact.</summary>
    public bool HasImpacted => _elapsed >= _approachDur;

    /// <summary>
    /// Intensité du recul (knockback) de la victime [0,1] : max au contact, revient à 0. La scène en
    /// fait un décalage en pixels (direction = à l'opposé de l'attaquant).
    /// </summary>
    public float KnockbackAmount
    {
        get
        {
            var t = _elapsed - _approachDur;
            if (t < 0 || t > KnockbackDur)
                return 0f;
            return 1f - EaseInOut((float)(t / KnockbackDur));
        }
    }

    /// <summary>Avancement de la dissolution de la victime [0,1] (0 avant l'impact).</summary>
    public float DissolveProgress =>
        Killed ? (float)Math.Clamp((_elapsed - _approachDur) / DissolveDur, 0, 1) : 0f;

    /// <summary>Intensité du flash « touché » du survivant [0,1] (deux pulsations qui s'éteignent).</summary>
    public float FlashIntensity
    {
        get
        {
            if (Killed)
                return 0f;
            var k = (_elapsed - _approachDur) / BlinkDur;
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
        if (_style is AttackStyle.Cast or AttackStyle.Shoot)
        {
            // Tireur à distance : reste sur sa case, léger recul (incantation / bande d'arc) qui revient.
            var d = toTop - fromTop;
            if (d.LengthSquared() > 0.0001f)
                d.Normalize();
            var back = _elapsed < _approachDur ? Arc((float)(_elapsed / _approachDur)) * tile * 0.10f : 0f;
            return fromTop - d * back;
        }

        if (_style == AttackStyle.Leap)
            return LeapGround(fromTop, toTop, tile);

        var dir = toTop - fromTop;
        if (dir.LengthSquared() > 0.0001f)
            dir.Normalize();
        var peak = fromTop + dir * (tile * LungeFraction);

        if (_elapsed < _approachDur)
            return Vector2.Lerp(fromTop, peak, EaseOut((float)(_elapsed / _approachDur)));

        if (Killed && Advanced)
        {
            var advStart = _approachDur + DissolveDur;
            if (_elapsed < advStart)
                return peak;                                    // maintien pendant la dissolution
            var k = Clamp01((_elapsed - advStart) / AdvanceDur);
            return Vector2.Lerp(peak, toTop, EaseInOut(k));     // prend la place libérée
        }

        var r = Clamp01((_elapsed - _approachDur) / RecoilDur);
        return Vector2.Lerp(peak, fromTop, EaseOut(r));         // recul sur sa case
    }

    /// <summary>
    /// Position AU SOL (coin haut-gauche, sans le saut) de l'attaquant en charge sautée : approche
    /// jusqu'au contact de la cible, puis atterrissage sur la case (kill) ou retour à l'origine (survie).
    /// La hauteur du bond est fournie à part par <see cref="AttackerJumpLift"/> (l'ombre reste au sol).
    /// </summary>
    private Vector2 LeapGround(Vector2 fromTop, Vector2 toTop, float tile)
    {
        var dir = toTop - fromTop;
        if (dir.LengthSquared() > 0.0001f)
            dir.Normalize();
        var contact = toTop - dir * (tile * LeapContactGap);   // s'arrête au contact de la pièce

        if (_elapsed < _approachDur)
            return Vector2.Lerp(fromTop, contact, EaseOut((float)(_elapsed / _approachDur)));

        if (Killed && Advanced)
        {
            var advStart = _approachDur + DissolveDur;
            if (_elapsed < advStart)
                return contact;                                 // maintien au contact pendant la dissolution
            var k = Clamp01((_elapsed - advStart) / AdvanceDur);
            return Vector2.Lerp(contact, toTop, EaseInOut(k));  // atterrit sur la case libérée
        }

        var r = Clamp01((_elapsed - _approachDur) / RecoilDur);
        return Vector2.Lerp(contact, fromTop, EaseInOut(r));    // ressaute en arrière
    }

    /// <summary>
    /// Hauteur (px, positive = vers le haut) du bond de l'attaquant à cet instant. 0 pour la fente
    /// classique ; pour la charge sautée : grosse parabole à l'approche/au repli (retombe pile au
    /// contact pour « percuter »), petit saut à l'avance sur la case.
    /// </summary>
    public float AttackerJumpLift(float tile)
    {
        if (_style != AttackStyle.Leap)
            return 0f;

        if (_elapsed < _approachDur)
            return Arc((float)(_elapsed / _approachDur)) * tile * LeapJumpFraction;   // bond d'approche

        if (Killed && Advanced)
        {
            var advStart = _approachDur + DissolveDur;
            if (_elapsed < advStart)
                return 0f;                                       // posé au contact pendant la dissolution
            var k = Clamp01((_elapsed - advStart) / AdvanceDur);
            return Arc(k) * tile * LeapHopFraction;              // petit saut sur la case
        }

        var r = Clamp01((_elapsed - _approachDur) / RecoilDur);
        return Arc(r) * tile * LeapJumpFraction;                 // ressaut en arrière
    }

    /// <summary>Parabole de saut : 0 aux extrémités, 1 au sommet (à mi-parcours).</summary>
    private static float Arc(float t) => (float)Math.Sin(Math.Clamp(t, 0, 1) * Math.PI);

    /// <summary>Style d'attaque en cours (la scène choisit le visuel du projectile selon lui).</summary>
    public AttackStyle Style => _style;

    /// <summary>
    /// Avancement [0,1] du projectile (sort du mage OU flèche de l'archer) pendant son vol (ease-in =
    /// lancer qui accélère), ou −1 si aucun projectile n'est en vol (autre style, ou déjà arrivé à
    /// l'impact). À l'arrivée (fin de l'approche) l'impact prend le relais (dissolution / flash / dégâts).
    /// </summary>
    public float ProjectileFlight => (_style is AttackStyle.Cast or AttackStyle.Shoot) && _elapsed < _approachDur
        ? EaseIn((float)(_elapsed / _approachDur))
        : -1f;

    private static float EaseIn(float t) => t * t;

    /// <summary>Décalage de secousse d'écran (px entiers), s'éteignant après l'impact.</summary>
    public Point ShakeOffset(float magnitude)
    {
        var t = _elapsed - _approachDur;
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
