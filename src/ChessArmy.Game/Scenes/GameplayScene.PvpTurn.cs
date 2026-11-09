using System;
using System.Runtime.InteropServices;
using ChessArmy.Core.Battle;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Combat multi : signaler clairement que la main nous revient. Au moment où notre tour commence VRAIMENT
/// (coup adverse fini de se jouer), un son, le clignotement de la fenêtre dans la barre des tâches si le jeu
/// n'est pas au premier plan, et le panneau « TOI » qui fait un bond puis ondule doucement tant qu'on a la main.
/// </summary>
public sealed partial class GameplayScene
{
    private bool _pvpWasMyTurn;
    private float _pvpMyTurnTime = -1f;   // temps écoulé depuis le début de notre tour (-1 : pas notre tour)

    private const float TurnPopDuration = 0.8f;   // bond d'arrivée (rebonds amortis)
    private const float TurnPopAmp = 12f;         // px canvas
    private const float TurnPopFreq = 2.5f;       // demi-rebonds par seconde ×π (≈ 2 rebonds sur la durée)
    private const float TurnPopDamp = 4f;
    private const float TurnBobFreq = 3f;         // rad/s : ondulation lente pendant le reste du tour
    private const float TurnBobAmp = 2f;          // px canvas

    /// <summary>Chaque frame de combat multi (hors pile ou face) : détecte le début de notre tour.</summary>
    private void UpdatePvpTurnCue(float dt)
    {
        var mine = !_match.IsOver && !PvpAnalysisActive && _match.CurrentTurn == Faction.Player && BattleSettled;
        if (mine && !_pvpWasMyTurn)
        {
            _pvpMyTurnTime = 0f;
            Context.Sounds.Play("your_turn");
            if (!Context.Input.HasFocus)
                FlashTaskbar();
        }
        else if (mine)
            _pvpMyTurnTime += dt;
        else
            _pvpMyTurnTime = -1f;
        _pvpWasMyTurn = mine;

        // Décompte : un « tic » à chaque seconde entière des dernières secondes, et le chiffre qui bat (cf. DrawSide).
        _pvpTickAge += dt;
        var left = PvpMyTimeLeft;
        if (mine && left > 0f && left < CountdownFrom)
        {
            var second = (int)MathF.Ceiling(left);
            if (second != _pvpLastTickSecond)
            {
                _pvpLastTickSecond = second;
                _pvpTickAge = 0f;
                Context.Sounds.Play("countdown_tick");
            }
        }
        else
            _pvpLastTickSecond = -1;
    }

    // Décompte des dernières secondes de notre coup.
    private const float CountdownFrom = 10f;      // secondes : début du décompte (= passage au rouge)
    private const float CountdownShakeFrom = 5f;  // dernières secondes : le chiffre tremble en plus
    private const float CountdownBeat = 0.15f;    // durée du « battement » (chiffre en ×4) après chaque tic
    private const float CountdownShakeFreq = 30f; // allers-retours du tremblement par seconde
    private const int CountdownShakeAmp = 2;      // px canvas
    private int _pvpLastTickSecond = -1;
    private float _pvpTickAge = 99f;

    /// <summary>Temps réellement restant pour notre coup : la limite du coup, ou la réserve si elle est plus courte.</summary>
    private float PvpMyTimeLeft => MathF.Min(_pvpClock.MoveLeft, _pvpClock.Reserve(Faction.Player));

    /// <summary>Décompte en cours (notre tour, dernières secondes).</summary>
    private bool PvpCountdownActive => _pvpMyTurnTime >= 0f && PvpMyTimeLeft < CountdownFrom;

    /// <summary>Échelle (entière) et décalage horizontal du temps du coup pendant le décompte.</summary>
    private (int Scale, int Dx) PvpCountdownPose()
    {
        if (!PvpCountdownActive)
            return (3, 0);
        var scale = _pvpTickAge < CountdownBeat ? 4 : 3;
        var dx = PvpMyTimeLeft < CountdownShakeFrom
            ? ((int)(_pvpMyTurnTime * CountdownShakeFreq) % 2 == 0 ? -CountdownShakeAmp : CountdownShakeAmp)
            : 0;
        return (scale, dx);
    }

    /// <summary>Décalage vertical (px entiers, négatif = vers le haut) du panneau « TOI » pendant notre tour.</summary>
    private int PvpMyPanelOffset()
    {
        var t = _pvpMyTurnTime;
        if (t < 0f)
            return 0;
        if (t < TurnPopDuration)
            return -(int)MathF.Round(TurnPopAmp * MathF.Abs(MathF.Sin(MathF.PI * TurnPopFreq * t)) * MathF.Exp(-TurnPopDamp * t));
        return (int)MathF.Round(MathF.Sin((t - TurnPopDuration) * TurnBobFreq) * TurnBobAmp);
    }

    // Clignotement de la fenêtre (barre des tâches) jusqu'au retour du focus : SDL2 ≥ 2.0.16 (MonoGame DesktopGL).
    private const int SdlFlashUntilFocused = 2;

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_FlashWindow(IntPtr window, int operation);

    private static bool _flashUnavailable;

    private void FlashTaskbar()
    {
        if (_flashUnavailable)
            return;
        try
        {
            SDL_FlashWindow(Context.Window.Handle, SdlFlashUntilFocused);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _flashUnavailable = true;   // vieille SDL : on se contente du son
        }
    }
}
