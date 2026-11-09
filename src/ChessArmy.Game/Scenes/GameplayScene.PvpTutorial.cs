using System;
using System.Collections.Generic;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.UI;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Partie TUTO du multi (<see cref="Net.PvpMatchInfo.Tutorial"/>) : des bulles expliquent les règles AU MOMENT où
/// elles servent, chacune une seule fois, toujours au début du tour du joueur (plateau posé) : but du jeu, un coup
/// par tour, fiches, zones de danger, moral au premier pion tombé, pendule, boutons, commandant isolé. Tant qu'une
/// bulle est ouverte la frame est consommée (pendules et bot attendent) ; un clic ou A la ferme.
/// </summary>
public sealed partial class GameplayScene
{
    private enum TutoStep { Goal, Turn, Cards, Danger, Morale, Clock, Buttons, Lone, Count }

    private const float TutoBubbleIn = 0.15f;

    private int _tutoSeen;                 // étapes déjà montrées (bit par étape)
    private TutoStep? _tutoOpen;
    private float _tutoTime;
    private string _tutoTitle = "";
    private readonly List<string> _tutoLines = new();
    private int _tutoLinesWidth;
    private bool _tutoLinesGamepad;

    private bool TutoSeen(TutoStep s) => (_tutoSeen & (1 << (int)s)) != 0;

    /// <summary>Bulles du tuto : vrai tant qu'une bulle est ouverte (la frame est consommée).</summary>
    private bool UpdatePvpTutorial(float dt)
    {
        if (_pvpInfo is not { Tutorial: true })
            return false;
        if (_tutoOpen is { } open)
        {
            _tutoTime += dt;
            var input = Context.Input;
            if (_tutoTime >= TutoBubbleIn
                && (input.WasLeftClicked || input.WasConfirmPressed || input.WasKeyPressed(Keys.Enter)))
            {
                _tutoSeen |= 1 << (int)open;
                _tutoOpen = null;
                Context.Sounds.Play("menu_click");
            }
            return true;
        }
        if (_match.IsOver || !PvpGameRunning || !BattleSettled || _match.CurrentTurn != Faction.Player || _loneBanner >= 0f)
            return false;
        if (NextTutoStep() is { } step)
        {
            _tutoOpen = step;
            _tutoTime = 0f;
            _tutoTitle = Loc.T($"pvp.tutorial.{Id(step)}.title");
            _tutoLinesWidth = -1;   // texte coupé au premier dessin (largeur connue là)
            Context.Sounds.Play("menu_open");
            return true;
        }
        return false;
    }

    /// <summary>Prochaine étape à montrer maintenant : les situations (pion tombé, commandant seul) passent avant
    /// celles qui suivent le numéro du tour.</summary>
    private TutoStep? NextTutoStep()
    {
        if (!TutoSeen(TutoStep.Lone) && (LoneTagShown(Faction.Player) || LoneTagShown(Faction.Enemy)))
            return TutoStep.Lone;
        if (!TutoSeen(TutoStep.Morale))
            foreach (var (_, dead) in _match.DeathLog)
                if (!dead.IsEssential)
                    return TutoStep.Morale;
        var round = _match.Round;
        for (var i = 0; i < TutoByRound.Length; i++)
            if (!TutoSeen(TutoByRound[i]) && round >= TutoFromRound[i])
                return TutoByRound[i];
        return null;
    }

    // Étapes liées au tour du joueur, et tour à partir duquel chacune se montre.
    private static readonly TutoStep[] TutoByRound =
        { TutoStep.Goal, TutoStep.Turn, TutoStep.Cards, TutoStep.Danger, TutoStep.Clock, TutoStep.Buttons };
    private static readonly int[] TutoFromRound = { 1, 1, 2, 3, 4, 5 };

    private static string Id(TutoStep s) => s switch
    {
        TutoStep.Goal => "goal",
        TutoStep.Turn => "turn",
        TutoStep.Cards => "cards",
        TutoStep.Danger => "danger",
        TutoStep.Morale => "morale",
        TutoStep.Clock => "clock",
        TutoStep.Buttons => "buttons",
        _ => "lone",
    };

    /// <summary>Texte de l'étape : les touches suivent le périphérique courant (manette ou clavier/souris).</summary>
    private string TutoText(TutoStep s, bool gamepad) => s switch
    {
        TutoStep.Turn => Loc.T(gamepad ? "pvp.tutorial.turn.text_gp" : "pvp.tutorial.turn.text"),
        TutoStep.Cards => Loc.T(gamepad ? "pvp.tutorial.cards.text_gp" : "pvp.tutorial.cards.text"),
        TutoStep.Danger => Loc.T("pvp.tutorial.danger.text", gamepad ? "RT" : Loc.T("tuto.key_space")),
        TutoStep.Clock => Loc.T("pvp.tutorial.clock.text", (int)(PvpConfig.Current.CollapseBelowSeconds / 60f)),
        TutoStep.Lone => Loc.T("pvp.tutorial.lone.text", PvpConfig.Current.LoneCommanderMorale),
        _ => Loc.T($"pvp.tutorial.{Id(s)}.text"),
    };

    /// <summary>Bulle ouverte : panneau au centre du plateau, titre, texte, indice pour continuer.</summary>
    private void DrawPvpTutorial(SpriteBatch sb, Viewport viewport)
    {
        if (_tutoOpen is not { } step)
            return;
        var font = Context.Font;
        var gamepad = Context.Input.UsingGamepad;
        var width = Math.Min(viewport.Width - 32, 520);
        const int Pad = 14;
        var textW = width - 2 * Pad;
        if (_tutoLinesWidth != textW || _tutoLinesGamepad != gamepad)
        {
            // Coupé une fois par bulle (et si le périphérique ou la largeur change), jamais à chaque frame.
            _tutoLinesWidth = textW;
            _tutoLinesGamepad = gamepad;
            _tutoLines.Clear();
            _tutoLines.AddRange(WrapText(TutoText(step, gamepad), textW, 1));
        }
        var titleH = font.LineHeight(2);
        var lineH = font.LineHeight(1) + 4;
        var hint = Loc.T(gamepad ? "pvp.tutorial.next_gp" : "pvp.tutorial.next");
        var height = Pad + titleH + 10 + _tutoLines.Count * lineH + 8 + font.LineHeight(1) + Pad;
        // Au milieu du plateau : la zone neutre entre les deux armées (ni nos pions ni ceux de l'adversaire cachés).
        var rect = new Rectangle((viewport.Width - width) / 2, (viewport.Height - height) / 2, width, height);
        var alpha = MathHelper.Clamp(_tutoTime / TutoBubbleIn, 0f, 1f);
        Context.Style.DrawPanel(sb, rect);
        var y = rect.Y + Pad;
        font.DrawCentered(sb, PvpArt.Fit(font, _tutoTitle, textW, 2), new Rectangle(rect.X, y, rect.Width, titleH), 2, Palette.Yellow2 * alpha);
        y += titleH + 10;
        foreach (var line in _tutoLines)
        {
            font.Draw(sb, line, new Vector2(rect.X + Pad, y), 1, Palette.White * alpha, preserveCase: true);
            y += lineH;
        }
        y += 8;
        font.DrawCentered(sb, hint, new Rectangle(rect.X, y, rect.Width, font.LineHeight(1)), 1, Palette.Blue1 * alpha,
            preserveCase: true);
    }
}
