using System;
using ChessArmy.Core.Map;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

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
    /// <summary>Pion LANCÉ (« Chair à canon ») : grand arc depuis sa case jusqu'à percuter la cible, puis petit
    /// rebond sur sa case d'arrivée (<see cref="MeleeStrikeFx.Attacker"/>) — il ne revient jamais au départ.</summary>
    Throw,
}

/// <summary>
/// Animation d'une attaque, déroulée PAR-DESSUS un combat déjà résolu dans le domaine
/// (<see cref="ChessArmy.Core.Battle.Match"/> est instantané). Séquence : fente de l'attaquant
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
    private const double ThrowPickupDur = 0.24; // le pion saute de sa case au-dessus de la tête du lanceur
    private const double ThrowHoldDur   = 0.12; // brandi au-dessus de la tête, le temps de le voir
    private const double ThrowDur    = 0.40; // vol du pion lancé (2-3 cases, on doit le voir partir)
    private const double ThrowHopDur = 0.18; // rebond du pion lancé vers sa case d'arrivée (cible survivante)
    private const double DissolveDur = 0.45; // désintégration du mort
    private const double BurnDur     = 0.95; // mort par MAGIE : le pion brûle (remplace la dissolution, plus longue : on voit le feu monter)
    private const double SliceDur    = 0.50; // mort au corps à corps : pion COUPÉ en deux, moitiés qui tombent (avant la dissolution)
    private const double ArrowDeathDur = 0.60; // mort par flèche : recul, bascule et chute au sol (avant la dissolution)
    private const double BlinkDur    = 0.34; // clignotement du survivant
    private const double AdvanceDur  = 0.14; // l'attaquant prend la place libérée
    private const double RecoilDur   = 0.12; // retour sur sa case (pas d'avance)
    private const double ShakeDur    = 0.22; // secousse d'écran après l'impact
    private const double KnockbackDur = 0.18; // recul de la victime après le contact
    private const double SlideDur    = 0.20; // glissement « Recule » de la victime sur une case (permanent)
    private const double MoveDur     = 0.30; // glissement d'un DÉPLACEMENT rejoué (« revoir action IA »)

    private const float LungeFraction = 0.42f; // amplitude de la fente, en fraction de case
    private const float LeapContactGap = 0.25f; // arrêt de la charge avant la case cible (fraction de case)
    private const float LeapJumpFraction = 0.55f; // hauteur du bond principal (fraction de case)
    private const float LeapHopFraction = 0.30f;  // hauteur du petit saut d'avance/repli
    private const float ThrowJumpFraction = 1.1f; // hauteur de l'arc du pion lancé (fraction de case)
    private const float ThrowHeadFraction = 0.8f; // hauteur du pion brandi au-dessus du lanceur (fraction de case)
    private const float MoveHopFraction = 0.16f;  // petit saut du pion pendant un déplacement rejoué

    private double _elapsed;

    // OUVERTURE optionnelle (cf. Begin) : un temps AVANT que l'attaquant ne s'élance, pendant lequel la scène
    // joue ce qui doit se voir avant le coup — l'allié qui saute devant son commandant (« Bouclier humain »).
    // Toute l'animation est décalée d'autant : les mesures de temps passent par T, qui reste à 0 pendant l'ouverture.
    private double _leadIn;

    /// <summary>Temps écoulé APRÈS l'ouverture (0 tant qu'elle dure) : horloge de toute l'animation.</summary>
    private double T => Math.Max(0, _elapsed - _leadIn);

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

    /// <summary>
    /// Vrai si la victime meurt d'un coup de CORPS À CORPS (fente / charge) : elle est coupée en deux, les
    /// moitiés se séparent et tombent (<see cref="SliceProgress"/>), PUIS se dissolvent. Tirs, sorts et pion
    /// lancé gardent la dissolution seule.
    /// </summary>
    public bool Sliced { get; private set; }

    /// <summary>Coupe VERTICALE (tué par un pion lancé, qui tombe du ciel) : les deux moitiés gauche/droite
    /// s'écartent et basculent chacune de son côté, au lieu de la diagonale ou de la décapitation.</summary>
    public bool VerticalSlice => Sliced && _style == AttackStyle.Throw;

    /// <summary>
    /// Option « Sang » (GameSettings.Blood), poussée par la scène : <c>false</c> = plus aucune coupe, toutes les
    /// morts se dissolvent simplement.
    /// </summary>
    public static bool GoreEnabled { get; set; } = true;

    /// <summary>
    /// Vrai si la victime meurt d'un SORT (style Cast) : au lieu de se désintégrer, elle BRÛLE du bas vers le haut
    /// (<see cref="DissolveProgress"/> pilote la combustion, sur <see cref="BurnDur"/>). Pas du gore : toujours joué.
    /// </summary>
    public bool Burned { get; private set; }

    /// <summary>Durée de la disparition du mort : combustion (sort) ou dissolution (le reste).</summary>
    private double VanishDur => Burned ? BurnDur : DissolveDur;

    /// <summary>Durée de la mort : coupe éventuelle puis dissolution (ou combustion).</summary>
    private double DeathDur => PreDeathDur + VanishDur;

    /// <summary>Avancement [0,1] de la coupe (0 avant l'impact, 1 une fois les moitiés tombées).</summary>
    public float SliceProgress => Sliced ? Clamp01((T - _approachDur) / SliceDur) : 0f;

    /// <summary>
    /// Vrai si la victime meurt d'une FLÈCHE : la flèche reste plantée, le pion recule, bascule et tombe au sol
    /// (ou est projeté et cloué au sol sur un gros coup, choix de la scène), PUIS se dissout.
    /// </summary>
    public bool ArrowKill { get; private set; }

    /// <summary>Avancement [0,1] de la chute après la flèche (0 avant l'impact, 1 une fois le pion à terre).</summary>
    public float ArrowProgress => ArrowKill ? Clamp01((T - _approachDur) / ArrowDeathDur) : 0f;

    /// <summary>Animation de mort jouée AVANT la dissolution : coupe (corps à corps) ou chute (flèche).</summary>
    private double PreDeathDur => Sliced ? SliceDur : ArrowKill ? ArrowDeathDur : 0;

    /// <summary>Vrai si l'anim en cours est un DÉPLACEMENT rejoué (fonction « revoir la dernière action de l'IA »)
    /// et non une attaque : le pion glisse simplement de <see cref="From"/> à <see cref="To"/>, sans victime ni
    /// impact. Cf. <see cref="BeginMove"/>.</summary>
    public bool MoveOnly { get; private set; }

    /// <summary>Vrai si l'anim en cours est une DISSOLUTION SEULE (aucun attaquant) : on rejoue juste la mort d'une
    /// victime sur place. Sert au « Recule » qui ACHÈVE la cible avec son bonus de plaquage : elle survit au coup
    /// direct (flash) puis se dissout APRÈS l'apparition du +5. Cf. <see cref="BeginDissolve"/>.</summary>
    public bool DissolveOnly { get; private set; }

    /// <summary>Vrai si la victime est CONDAMNÉE (déjà retirée du plateau, tuée par le plaquage « Recule ») mais
    /// dessinée SOLIDE et STATIQUE le temps de l'anim d'attaque — SANS clignotement « touché » — car elle se
    /// dissoudra ensuite. La scène dessine son sprite tel quel au lieu du flash (cf. DrawCombatFx).</summary>
    public bool VictimDoomed { get; private set; }

    /// <summary>Graine de bruit propre à cette mort (chaque dissolution diffère).</summary>
    public Vector2 Seed => _seed;

    /// <summary>Case du LANCEUR (style <see cref="AttackStyle.Throw"/>) : le pion passe au-dessus de sa tête avant
    /// d'être jeté. Null hors lancer.</summary>
    public Cell? Thrower { get; private set; }

    public void Begin(Cell from, Cell to, Cell attackerCell, Texture2D? attackerSprite,
        Texture2D? victimSprite, bool killed, bool advanced, AttackStyle style = AttackStyle.Lunge,
        bool victimDoomed = false, double leadIn = 0, Cell? thrower = null)
    {
        Thrower = thrower;
        From = from;
        To = to;
        Attacker = attackerCell;
        AttackerSprite = attackerSprite;
        VictimSprite = victimSprite;
        Killed = killed;
        Advanced = advanced;
        MoveOnly = false;
        DissolveOnly = false;
        VictimDoomed = victimDoomed;
        _style = style;
        // Pion LANCÉ (« Chair à canon » de la Brute) compris : il s'abat sur sa cible, coupée VERTICALEMENT.
        Sliced = GoreEnabled && killed && victimSprite != null
            && style is AttackStyle.Lunge or AttackStyle.Leap or AttackStyle.Throw;
        ArrowKill = killed && victimSprite != null && style == AttackStyle.Shoot;   // pas du gore : toujours joué
        Burned = killed && victimSprite != null && style == AttackStyle.Cast;       // idem : brûlé par le sort
        _approachDur = style switch
        {
            AttackStyle.Leap  => LeapDur,
            AttackStyle.Cast  => CastDur,
            AttackStyle.Shoot => ShootDur,
            AttackStyle.Throw => ThrowPickupDur + ThrowHoldDur + ThrowDur,   // soulevé, brandi, puis jeté
            _                 => LungeDur,
        };
        _elapsed = 0;
        _leadIn = Math.Max(0, leadIn);
        _seed = new Vector2((_seedCounter * 37) % 251, (_seedCounter * 101) % 241);
        _seedCounter++;
        Active = true;

        _total = (killed, advanced) switch
        {
            (true, true)  => _approachDur + DeathDur + AdvanceDur, // mêlée mortelle : avance après dissolution
            (true, false) => _approachDur + DeathDur,              // tir mortel : reste en place
            _             => _approachDur + BlinkDur,                 // survivant : flash après contact
        };
    }

    /// <summary>
    /// Démarre un DÉPLACEMENT rejoué (fonction « revoir la dernière action de l'IA ») : le pion glisse de
    /// <paramref name="from"/> à <paramref name="to"/> en un léger arc, sans victime ni impact. Sa case de repos
    /// est la DESTINATION (le pion y est déjà sur le plateau ; la scène l'y masque le temps du glissement, comme
    /// pour un attaquant animé). <see cref="HasImpacted"/> ne devient vrai qu'à la toute fin : la scène en profite
    /// pour le rebond de pose.
    /// </summary>
    public void BeginMove(Cell from, Cell to, Texture2D? sprite)
    {
        From = from;
        To = to;
        Attacker = to;
        AttackerSprite = sprite;
        VictimSprite = null;
        Killed = false;
        Advanced = false;
        MoveOnly = true;
        Sliced = false;
        ArrowKill = false;
        Burned = false;
        DissolveOnly = false;
        VictimDoomed = false;
        _style = AttackStyle.Lunge;
        _approachDur = MoveDur;   // l'« impact » (= atterrissage) ne se déclenche qu'à la fin du glissement
        _total = MoveDur;
        _elapsed = 0;
        _leadIn = 0;
        _seed = new Vector2((_seedCounter * 37) % 251, (_seedCounter * 101) % 241);
        _seedCounter++;
        Active = true;
    }

    /// <summary>
    /// Démarre une DISSOLUTION SEULE de la victime sur <paramref name="cell"/> (aucun attaquant animé). Sert au
    /// « Recule » dont le bonus de plaquage ACHÈVE une cible ayant survécu au coup direct : on rejoue sa mort
    /// APRÈS l'anim d'attaque et l'apparition du +5. Impact immédiat (la dissolution démarre tout de suite).
    /// </summary>
    /// <param name="shotFrom">Non null : la victime meurt comme sous une FLÈCHE (recul, bascule ou clouage, flèche
    /// plantée) avant de se dissoudre, le coup venant de cette case. Sert au « Transpercement » qui tue le pion
    /// DERRIÈRE la cible : <paramref name="shotFrom"/> = la cible, d'où ressort le coup.</param>
    public void BeginDissolve(Cell cell, Texture2D? sprite, Cell? shotFrom = null)
    {
        From = shotFrom ?? cell;   // From → To donne le sens du tir (chute à l'opposé, cf. DrawArrowKilledVictim)
        To = Attacker = cell;
        AttackerSprite = null;   // pas d'attaquant : on ne rejoue QUE la mort
        VictimSprite = sprite;
        Killed = true;
        Advanced = false;
        MoveOnly = false;
        DissolveOnly = true;
        Sliced = false;
        ArrowKill = shotFrom != null && sprite != null;
        Burned = false;
        VictimDoomed = false;
        _style = AttackStyle.Lunge;
        _approachDur = 0;        // impact immédiat : la mort démarre à la première frame
        _total = DeathDur;       // chute de flèche éventuelle + dissolution
        _elapsed = 0;
        _leadIn = 0;
        _seed = new Vector2((_seedCounter * 37) % 251, (_seedCounter * 101) % 241);
        _seedCounter++;
        Active = true;
    }

    public void Update(double dt)
    {
        if (!Active)
            return;
        _elapsed += dt;
        if (_elapsed >= _leadIn + _total)
            Active = false;
    }

    /// <summary>Vrai à l'instant exact du contact (fin de l'approche) — pour déclencher l'impact.</summary>
    public bool HasImpacted => _elapsed >= _leadIn && T >= _approachDur;

    /// <summary>Vrai pendant l'OUVERTURE : l'attaquant ne s'est pas encore élancé.</summary>
    public bool InLeadIn => _elapsed < _leadIn;

    /// <summary>Avancement [0,1] de l'ouverture (1 d'emblée s'il n'y en a pas).</summary>
    public float LeadInProgress => _leadIn <= 0 ? 1f : Clamp01(_elapsed / _leadIn);

    /// <summary>Avancement [0,1] de tout ce qui suit l'impact, jusqu'à la fin de l'animation (0 avant).</summary>
    public float AfterImpactProgress
    {
        get
        {
            var span = _total - _approachDur;
            return span <= 0 ? 1f : Clamp01((T - _approachDur) / span);
        }
    }

    /// <summary>
    /// Intensité du recul (knockback) de la victime [0,1] : max au contact, revient à 0. La scène en
    /// fait un décalage en pixels (direction = à l'opposé de l'attaquant).
    /// </summary>
    public float KnockbackAmount
    {
        get
        {
            var t = T - _approachDur;
            // Mort rejouée seule (cf. BeginDissolve) : le recul a déjà eu lieu pendant l'attaque, et la chute de
            // flèche porte le sien.
            if (DissolveOnly || t < 0 || t > KnockbackDur)
                return 0f;
            return 1f - EaseInOut((float)(t / KnockbackDur));
        }
    }

    /// <summary>
    /// Avancement [0,1] du GLISSEMENT de la victime qui a CHANGÉ de case (« Recule » qui la repousse, ou repli
    /// d'« Esquive ») : 0 avant le contact, monte (ease-out) jusqu'à 1 après le contact et Y RESTE (déplacement
    /// permanent — à l'inverse de <see cref="KnockbackAmount"/> qui revient à 0). La scène l'utilise pour faire
    /// glisser le sprite (et sa barre / son flash) de la case d'origine vers la case d'arrivée.
    /// </summary>
    public float VictimSlide
    {
        get
        {
            var t = T - _approachDur;
            return t <= 0 ? 0f : EaseOut((float)Math.Clamp(t / SlideDur, 0, 1));
        }
    }

    /// <summary>Avancement de la dissolution de la victime [0,1] (0 avant l'impact).</summary>
    public float DissolveProgress =>
        Killed ? (float)Math.Clamp((T - _approachDur - PreDeathDur) / VanishDur, 0, 1) : 0f;

    /// <summary>Intensité du flash « touché » du survivant [0,1] (deux pulsations qui s'éteignent).</summary>
    public float FlashIntensity
    {
        get
        {
            if (Killed)
                return 0f;
            var k = (T - _approachDur) / BlinkDur;
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
    public Vector2 AttackerTopLeft(Vector2 fromTop, Vector2 toTop, float tile, Vector2? restTop = null,
        Vector2? throwerTop = null)
    {
        if (_style == AttackStyle.Throw)
            return ThrowGround(fromTop, toTop, restTop ?? toTop, throwerTop ?? fromTop, tile);

        if (MoveOnly)   // déplacement rejoué : glissement plein de From à To (ni fente ni recul)
            return Vector2.Lerp(fromTop, toTop, EaseInOut(Clamp01(T / _total)));

        if (_style is AttackStyle.Cast or AttackStyle.Shoot)
        {
            // Tireur à distance : reste sur sa case, léger recul (incantation / bande d'arc) qui revient.
            var d = toTop - fromTop;
            if (d.LengthSquared() > 0.0001f)
                d.Normalize();
            var back = T < _approachDur ? Arc((float)(T / _approachDur)) * tile * 0.10f : 0f;
            return fromTop - d * back;
        }

        if (_style == AttackStyle.Leap)
            return LeapGround(fromTop, toTop, tile);

        var dir = toTop - fromTop;
        if (dir.LengthSquared() > 0.0001f)
            dir.Normalize();
        var peak = fromTop + dir * (tile * LungeFraction);

        if (T < _approachDur)
            return Vector2.Lerp(fromTop, peak, EaseOut((float)(T / _approachDur)));

        if (Killed && Advanced)
        {
            var advStart = _approachDur + DeathDur;
            if (T < advStart)
                return peak;                                    // maintien pendant la dissolution
            var k = Clamp01((T - advStart) / AdvanceDur);
            return Vector2.Lerp(peak, toTop, EaseInOut(k));     // prend la place libérée
        }

        var r = Clamp01((T - _approachDur) / RecoilDur);
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

        if (T < _approachDur)
            return Vector2.Lerp(fromTop, contact, EaseOut((float)(T / _approachDur)));

        if (Killed && Advanced)
        {
            var advStart = _approachDur + DeathDur;
            if (T < advStart)
                return contact;                                 // maintien au contact pendant la dissolution
            var k = Clamp01((T - advStart) / AdvanceDur);
            return Vector2.Lerp(contact, toTop, EaseInOut(k));  // atterrit sur la case libérée
        }

        var r = Clamp01((T - _approachDur) / RecoilDur);
        return Vector2.Lerp(contact, fromTop, EaseInOut(r));    // ressaute en arrière
    }

    /// <summary>
    /// Position AU SOL du pion lancé : il saute de sa case jusqu'à l'aplomb du lanceur (<paramref name="throwerTop"/>),
    /// y reste brandi, part de là jusqu'au contact de la cible, puis rebondit vers sa case d'arrivée
    /// <paramref name="restTop"/> — après la dissolution si la cible meurt, aussitôt sinon.
    /// La hauteur (au-dessus de la tête, puis arc du jet) est fournie à part par <see cref="AttackerJumpLift"/>.
    /// </summary>
    private Vector2 ThrowGround(Vector2 fromTop, Vector2 toTop, Vector2 restTop, Vector2 throwerTop, float tile)
    {
        if (T < ThrowPickupDur)
            return Vector2.Lerp(fromTop, throwerTop, EaseInOut((float)(T / ThrowPickupDur)));
        if (T < ThrowPickupDur + ThrowHoldDur)
            return throwerTop;

        var dir = toTop - throwerTop;
        if (dir.LengthSquared() > 0.0001f)
            dir.Normalize();
        var contact = toTop - dir * (tile * LeapContactGap);

        if (T < _approachDur)
            return Vector2.Lerp(throwerTop, contact, EaseIn(ThrowFlightProgress()));   // lancer qui accélère
        var k = ThrowHopProgress();
        return k <= 0f ? contact : Vector2.Lerp(contact, restTop, EaseInOut(k));
    }

    /// <summary>Avancement [0,1] du vol du jet proprement dit (après le soulevé et le brandi).</summary>
    private float ThrowFlightProgress() => Clamp01((T - ThrowPickupDur - ThrowHoldDur) / ThrowDur);

    /// <summary>Avancement [0,1] du rebond final du pion lancé (0 tant qu'il n'a pas commencé).</summary>
    private float ThrowHopProgress()
    {
        var start = Killed ? _approachDur + DeathDur : _approachDur;
        var dur = Killed ? AdvanceDur : ThrowHopDur;
        return Clamp01((T - start) / dur);
    }

    /// <summary>
    /// Hauteur (px, positive = vers le haut) du bond de l'attaquant à cet instant. 0 pour la fente
    /// classique ; pour la charge sautée : grosse parabole à l'approche/au repli (retombe pile au
    /// contact pour « percuter »), petit saut à l'avance sur la case.
    /// </summary>
    public float AttackerJumpLift(float tile)
    {
        if (MoveOnly)   // déplacement rejoué : petit arc de saut, retombe à plat sur la case d'arrivée
            return Arc(Clamp01(T / _total)) * tile * MoveHopFraction;

        if (_style == AttackStyle.Throw)
        {
            var head = tile * ThrowHeadFraction;
            if (T < ThrowPickupDur)   // saute sur la tête du lanceur : monte jusqu'à la hauteur brandie, petit arc
            {
                var p = (float)(T / ThrowPickupDur);
                return head * EaseOut(p) + Arc(p) * tile * LeapHopFraction;
            }
            if (T < ThrowPickupDur + ThrowHoldDur)
                return head;                                                // brandi au-dessus de la tête
            if (T < _approachDur)   // jeté : part de la tête, grand arc, retombe pile au contact
            {
                var f = ThrowFlightProgress();
                return head * (1f - f) + Arc(f) * tile * ThrowJumpFraction;
            }
            return Arc(ThrowHopProgress()) * tile * LeapHopFraction;        // rebond vers la case d'arrivée
        }

        if (_style != AttackStyle.Leap)
            return 0f;

        if (T < _approachDur)
            return Arc((float)(T / _approachDur)) * tile * LeapJumpFraction;   // bond d'approche

        if (Killed && Advanced)
        {
            var advStart = _approachDur + DeathDur;
            if (T < advStart)
                return 0f;                                       // posé au contact pendant la dissolution
            var k = Clamp01((T - advStart) / AdvanceDur);
            return Arc(k) * tile * LeapHopFraction;              // petit saut sur la case
        }

        var r = Clamp01((T - _approachDur) / RecoilDur);
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
    public float ProjectileFlight => (_style is AttackStyle.Cast or AttackStyle.Shoot) && !InLeadIn && T < _approachDur
        ? EaseIn((float)(T / _approachDur))
        : -1f;   // pendant l'ouverture, rien n'est encore lancé

    private static float EaseIn(float t) => t * t;

    /// <summary>Décalage de secousse d'écran (px entiers), s'éteignant après l'impact.</summary>
    public Point ShakeOffset(float magnitude)
    {
        var t = T - _approachDur;
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
