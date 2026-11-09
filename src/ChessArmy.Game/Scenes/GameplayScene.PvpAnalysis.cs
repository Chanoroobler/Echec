using System;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.UI;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Combat multi : après le pile ou face, phase d'ANALYSE (pvp.json « analysisSeconds », 60 s) pour étudier l'armée
/// adverse. Pendules gelées, aucun coup possible ; survol des pions (cartes), zones de danger (ESPACE) et curseur
/// manette restent actifs. Bouton « Prêt » : la partie démarre dès que les DEUX joueurs le sont (un bot l'est
/// toujours), sinon à la fin du temps. Un coup adverse reçu pendant l'analyse la termine aussi (son analyse a fini
/// un peu avant la nôtre : les deux clients ne lancent pas le pile ou face à la même milliseconde).
/// </summary>
public sealed partial class GameplayScene
{
    private float _pvpAnalysis = -1f;   // temps écoulé de l'analyse (-1 : pas d'analyse en cours)
    private bool _pvpLocalReady;
    private Rectangle _pvpReadyRect;

    private bool PvpAnalysisActive => _pvpAnalysis >= 0f;

    /// <summary>La partie est lancée : pile ou face et analyse terminés (« À TOI DE JOUER » n'a de sens qu'alors).</summary>
    private bool PvpGameRunning => _coinFlip < 0f && !PvpAnalysisActive;

    /// <summary>Adversaire prêt : signalé par le réseau ; un bot est toujours prêt.</summary>
    private bool PvpOpponentReady => PvpNet is not { } session || session.OpponentReady;

    private void StartPvpAnalysis()
    {
        _pvpAnalysis = 0f;
        _pvpLocalReady = false;
    }

    /// <summary>Fait avancer l'analyse. Vrai si l'entrée de la frame a été consommée (bouton « Prêt »).</summary>
    private bool UpdatePvpAnalysis(float dt)
    {
        _pvpAnalysis += dt;
        var input = Context.Input;
        var consumed = false;
        if (!_pvpLocalReady && ((input.WasLeftClicked && _pvpReadyRect.Contains(input.MousePosition))
                                || input.WasConfirmPressed || input.WasKeyPressed(Keys.Enter)))
        {
            _pvpLocalReady = true;
            PvpNet?.SendReady();
            Context.Sounds.Play("menu_click");
            consumed = true;
        }
        if ((_pvpLocalReady && PvpOpponentReady)
            || _pvpAnalysis >= PvpConfig.Current.AnalysisSeconds
            || PvpNet is { HasPendingAction: true }
            || _match.IsOver)
            _pvpAnalysis = -1f;   // la partie commence (la pendule du premier joueur part, cf. UpdatePvp)
        return consumed;
    }

    /// <summary>Panneau d'analyse en bas de la colonne de droite, où sont les boutons Abandonner / Égalité (masqués pendant cette phase).</summary>
    private void DrawPvpAnalysis(SpriteBatch sb, Rectangle column)
    {
        if (!PvpAnalysisActive)
            return;
        var font = Context.Font;
        var width = Math.Min(column.Width, 220);
        var panel = new Rectangle(column.X + (column.Width - width) / 2, column.Bottom - 112, width, 112);   // en bas, à la place des boutons Abandonner / Égalité
        Context.Style.DrawPanel(sb, panel);

        font.DrawCentered(sb, PvpArt.Fit(font, Loc.T("pvp.analysis.title"), panel.Width - 12, 1),
            new Rectangle(panel.X, panel.Y + 10, panel.Width, 7), 1, Palette.Yellow2);
        var left = Math.Max(0f, PvpConfig.Current.AnalysisSeconds - _pvpAnalysis);
        font.DrawCentered(sb, PvpUi.Clock(left), new Rectangle(panel.X, panel.Y + 24, panel.Width, 21), 3, Palette.White);

        _pvpReadyRect = new Rectangle(panel.X + 10, panel.Y + 54, panel.Width - 20, 24);
        var label = _pvpLocalReady ? Loc.T("pvp.analysis.waiting")
            : (Context.Input.UsingGamepad ? "(A) " : "") + Loc.T("pvp.analysis.ready");
        PvpUi.Button(Context, sb, _pvpReadyRect, PvpArt.Fit(font, label, _pvpReadyRect.Width - 12, 1), focused: false,
            enabled: !_pvpLocalReady);

        var ready = PvpOpponentReady;
        font.DrawCentered(sb, PvpArt.Fit(font, Loc.T(ready ? "pvp.analysis.opponent_ready" : "pvp.analysis.opponent_wait"),
                panel.Width - 12, 1),
            new Rectangle(panel.X, panel.Y + 90, panel.Width, 7), 1, ready ? Palette.Cyan1 : Palette.Blue1);
    }
}
