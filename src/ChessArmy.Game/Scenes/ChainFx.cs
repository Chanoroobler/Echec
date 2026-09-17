using System;
using ChessArmy.Core.Map;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Animation de la « Réaction en chaîne » (trait de l'artisan meurtrier) : il SAUTE de pion en pion, frappe
/// chaque victime en se posant dessus, puis — la chaîne finie — repart d'un dernier bond sur sa case de
/// départ. Un saut à la fois : la scène enchaîne en appelant <see cref="Begin"/> à chaque atterrissage.
///
/// Les dégâts ne sont PAS résolus d'avance : le moteur applique le maillon quand le pied se pose
/// (<see cref="ChessArmy.Core.Battle.Match.ResolveNextChainLink"/>), sinon toute la file mourrait avant
/// qu'on ait vu le premier saut partir.
///
/// Données + minuterie PURES, comme <see cref="BounceFx"/> : le rendu vit dans la scène, qui lit
/// <see cref="From"/>, <see cref="To"/>, <see cref="T"/> et <see cref="Height"/> pour poser le sprite.
/// Tant que <see cref="Active"/>, la scène gèle entrées, IA et fin de combat.
/// </summary>
public sealed class ChainFx
{
    /// <summary>Durée d'UN saut (s) : vif — c'est un bond, pas une marche.</summary>
    private const double HopDuration = 0.22;

    /// <summary>Hauteur de l'arc, en fraction de case.</summary>
    private const float ArcHeight = 0.55f;

    private double _elapsed;
    private bool _landed;

    public bool Active { get; private set; }

    /// <summary>Case d'où part le saut en cours.</summary>
    public Cell From { get; private set; }

    /// <summary>Case visée par le saut en cours.</summary>
    public Cell To { get; private set; }

    /// <summary>Vrai si l'arrivée de ce saut porte un COUP (faux pour le retour à la case de départ).</summary>
    public bool Strikes { get; private set; }

    /// <summary>Avancement du saut [0,1] : 0 au décollage, 1 à l'atterrissage.</summary>
    public float T => Active ? (float)Math.Clamp(_elapsed / HopDuration, 0, 1) : 0f;

    /// <summary>Hauteur du bond à l'instant courant, en fraction de case (cloche : 0 aux deux bouts).</summary>
    public float Height => Active ? ArcHeight * MathF.Sin(MathF.PI * T) : 0f;

    /// <summary>
    /// Lance un saut de <paramref name="from"/> vers <paramref name="to"/>. <paramref name="strikes"/> = ce
    /// saut porte un coup à l'arrivée (faux pour le retour au bercail).
    /// </summary>
    public void Begin(Cell from, Cell to, bool strikes)
    {
        From = from;
        To = to;
        Strikes = strikes;
        _elapsed = 0;
        _landed = false;
        Active = true;
    }

    /// <summary>Réinitialise (nouveau combat, ou chaîne interrompue).</summary>
    public void Clear()
    {
        _elapsed = 0;
        _landed = false;
        Active = false;
    }

    /// <summary>
    /// Avance l'animation. Renvoie <c>true</c> UNE SEULE FOIS, à l'ATTERRISSAGE : c'est là que la scène
    /// résout le maillon (si <see cref="Strikes"/>) et décide du saut suivant. Le FX reste
    /// <see cref="Active"/> jusqu'à ce que la scène le relance ou le termine — sinon le tour se dégèlerait
    /// une frame entre deux sauts.
    /// </summary>
    public bool Advance(double dt)
    {
        if (!Active || _landed)
            return false;

        _elapsed += dt;
        if (_elapsed < HopDuration)
            return false;

        _elapsed = HopDuration;
        _landed = true;
        return true;
    }
}
