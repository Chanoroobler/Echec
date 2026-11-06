using System;
using ChessArmy.Core.Battle;

namespace ChessArmy.Core.Pvp;

/// <summary>
/// Pendule d'une partie multi : une RÉSERVE par joueur (10 min) et une limite PAR COUP (1 min). Le temps ne
/// s'écoule que pour le camp qui a la main, et seulement quand la scène le décide (jamais pendant les
/// animations ni pendant une déconnexion). Dépasser l'une ou l'autre = défaite (<see cref="Flagged"/>).
/// </summary>
public sealed class PvpClock
{
    private readonly float[] _reserve = new float[2];
    private Faction _turn;

    public PvpClock(float reserveSeconds, float moveSeconds)
    {
        MoveSeconds = moveSeconds;
        _reserve[0] = _reserve[1] = reserveSeconds;
        MoveLeft = moveSeconds;
    }

    public float MoveSeconds { get; }

    /// <summary>Durée de jeu cumulée : seul le temps où la pendule a tourné compte (ni animations, ni
    /// déconnexion).</summary>
    public float Elapsed { get; private set; }

    /// <summary>Temps restant pour le coup en cours.</summary>
    public float MoveLeft { get; private set; }

    /// <summary>Camp dont le temps est écoulé, ou null.</summary>
    public Faction? Flagged { get; private set; }

    public float Reserve(Faction faction) => _reserve[(int)faction];

    /// <summary>Recale la réserve d'un camp (valeur annoncée par l'adversaire avec son coup).</summary>
    public void SetReserve(Faction faction, float seconds) => _reserve[(int)faction] = Math.Max(0f, seconds);

    /// <summary>Nouveau coup pour <paramref name="faction"/> : la limite par coup repart de zéro.</summary>
    public void BeginTurn(Faction faction)
    {
        _turn = faction;
        MoveLeft = MoveSeconds;
    }

    /// <summary>Fait avancer la pendule du camp qui a la main. Vrai si son temps vient de s'écouler.</summary>
    public bool Tick(float dt)
    {
        if (Flagged != null)
            return false;
        Elapsed += dt;
        _reserve[(int)_turn] -= dt;
        MoveLeft -= dt;
        if (_reserve[(int)_turn] > 0f && MoveLeft > 0f)
            return false;
        _reserve[(int)_turn] = Math.Max(0f, _reserve[(int)_turn]);
        MoveLeft = Math.Max(0f, MoveLeft);
        Flagged = _turn;
        return true;
    }

    public Faction Turn => _turn;
}
