using System;
using System.Collections.Generic;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Scenes;
using ChessArmy.Engine.UI;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Mode ENTRAÎNEMENT : bouton à côté de DÉFIER UN AMI → fenêtre des 13 niveaux de bot (une colonne par palier,
/// une case par division, Master seul) → combat aussitôt contre le bot de ce niveau, avec l'armée sélectionnée.
/// La cote ne bouge pas, les points sont réduits (« trainingPointsFactor » de pvp.json) et la partie reste hors
/// du bilan V/D/N (cf. <see cref="PvpMatchInfo.Training"/>).
/// </summary>
public sealed partial class PvpArmyScene
{
    private bool _trainingOpen;
    private readonly string? _trainingReplay;   // REJOUER depuis l'écran de fin d'un entraînement : même niveau

    // Fenêtre : rectangles recalculés à chaque frame, textes mémorisés (aucune chaîne formatée par frame).
    private readonly List<string> _trainingKeys = new();
    private readonly List<PvpRank> _trainingRanks = new();
    private readonly List<string> _trainingNames = new();
    private readonly List<Rectangle> _trainingCells = new();
    private Rectangle _trainingPanel, _trainingClose, _trainingTuto;
    private Language _trainingNamesLang = (Language)(-1);

    public PvpArmyScene(GameContext context, Entry entry, string? trainingKey) : this(context, entry)
    {
        _trainingReplay = trainingKey;
    }

    private void EnsureTrainingLevels()
    {
        if (_trainingKeys.Count == 0)
            foreach (var key in PvpRanks.BotKeys())
                if (PvpRanks.RatingFor(key) is { } rating)
                {
                    _trainingKeys.Add(key);
                    _trainingRanks.Add(PvpRanks.FromRating(rating));
                }
        if (_trainingNamesLang == Loc.Current && _trainingNames.Count == _trainingKeys.Count)
            return;
        _trainingNamesLang = Loc.Current;
        _trainingNames.Clear();
        foreach (var rank in _trainingRanks)
            _trainingNames.Add(PvpUi.RankName(rank));
    }

    private void OpenTraining()
    {
        if (_mode != SearchMode.Idle || Locked)
            return;
        if (LaunchProblem() is { } problem)
        {
            Say(problem, error: true);
            return;
        }
        CancelHeld();
        _trainingOpen = true;
        Context.Sounds.Play("menu_open");
    }

    private void CloseTraining()
    {
        _trainingOpen = false;
        Context.Sounds.Play("menu_close");
    }

    /// <summary>Combat d'entraînement contre le bot de la division <paramref name="key"/> (même compte à rebours
    /// qu'un adversaire trouvé, armée verrouillée).</summary>
    private void StartTraining(string key)
    {
        _trainingOpen = false;
        if (key == PvpMatchInfo.TutorialKey)
        {
            StartTutorialMatch();
            return;
        }
        if (Locked || PvpRanks.RatingFor(key) == null)
            return;
        if (LaunchProblem() is { } problem)
        {
            Say(problem, error: true);
            return;
        }
        StopMatchmaking();
        _locked = LockBuild();
        _botMatch = PvpMatchInfo.ForTraining(_locked, key, Profile.Rating, _rng);
        _countdown = LockSeconds;
        Context.Sounds.Play("battle_start");
    }

    /// <summary>Partie TUTO (bot « tuto », bulles des règles), avec l'armée sélectionnée ; rien d'enregistré. Marquée
    /// faite dès son lancement : le premier « LANCER UNE PARTIE » ne la relancera plus.</summary>
    private void StartTutorialMatch()
    {
        _trainingOpen = false;
        if (Locked)
            return;
        if (LaunchProblem() is { } problem)
        {
            Say(problem, error: true);
            return;
        }
        StopMatchmaking();
        _locked = LockBuild();
        _botMatch = PvpMatchInfo.ForTutorial(_locked, Profile.Rating, _rng);
        if (!Profile.TutorialMatchDone)
        {
            Profile.TutorialMatchDone = true;
            PvpProfileStore.Save();
        }
        _countdown = LockSeconds;
        Context.Sounds.Play("battle_start");
    }

    // ── Mise en page ─────────────────────────────────────────────────────────────

    /// <summary>Une colonne par palier (Bronze → Master), divisions de la plus basse (3) à la plus haute (1).</summary>
    private void LayoutTraining()
    {
        EnsureTrainingLevels();
        var L = _lay;
        var full = L.Full;
        var tiers = PvpConfig.Current.Ranks.Count;
        var rows = 1;
        foreach (var r in PvpConfig.Current.Ranks)
            rows = Math.Max(rows, r.Divisions);
        var cellW = full ? 150 : 108;
        var cellH = full ? 44 : 34;
        var gap = full ? 10 : 6;
        var ins = PvpArt.PanelFrame + (full ? 18 : 10);
        var titleH = Font.LineHeight(full ? 3 : 2);
        var subH = Font.LineHeight(1);
        var closeH = full ? 40 : 32;
        var gridW = tiers * cellW + (tiers - 1) * gap;
        var gridH = rows * cellH + (rows - 1) * gap;
        var w = Math.Min(L.W - 2 * L.Pad, gridW + 2 * ins);
        var h = ins + titleH + 6 + subH + (full ? 18 : 10) + gridH + (full ? 18 : 10) + closeH + ins;
        _trainingPanel = new Rectangle((L.W - w) / 2, (L.H - h) / 2, w, h);
        // Canevas trop étroit (ne devrait pas arriver : 960 px au minimum) : les cases se resserrent.
        cellW = Math.Min(cellW, (w - 2 * ins - (tiers - 1) * gap) / tiers);
        gridW = tiers * cellW + (tiers - 1) * gap;
        var gx = _trainingPanel.X + (w - gridW) / 2;
        var gy = _trainingPanel.Y + ins + titleH + 6 + subH + (full ? 18 : 10);

        _trainingCells.Clear();
        for (var i = 0; i < _trainingRanks.Count; i++)
        {
            var rank = _trainingRanks[i];
            var tierRows = Math.Max(1, PvpConfig.Current.Ranks[rank.TierIndex].Divisions);
            // Division la plus basse en haut ; Master (une seule case) centré dans sa colonne.
            var row = rank.Division > 0 ? tierRows - rank.Division : 0;
            var y = rank.Division > 0 ? gy + row * (cellH + gap) : gy + (gridH - cellH) / 2;
            _trainingCells.Add(new Rectangle(gx + rank.TierIndex * (cellW + gap), y, cellW, cellH));
        }
        // Rangée du bas : TUTO (partie d'apprentissage) puis ANNULER, centrés ensemble.
        var closeW = Math.Max(full ? 160 : 120, Font.Measure(Loc.T("pvp.cancel"), 1) + 32);
        var tutoW = Math.Max(full ? 160 : 120, Font.Measure(Loc.T("pvp.training_tuto"), 1) + 32);
        var rowX = _trainingPanel.Center.X - (tutoW + gap + closeW) / 2;
        var rowY = _trainingPanel.Bottom - ins - closeH;
        _trainingTuto = new Rectangle(rowX, rowY, tutoW, closeH);
        _trainingClose = new Rectangle(rowX + tutoW + gap, rowY, closeW, closeH);
    }

    private void AddTrainingTargets()
    {
        LayoutTraining();
        for (var i = 0; i < _trainingCells.Count; i++)
            Add(_trainingCells[i], Hit.TrainingPick, i);
        Add(_trainingTuto, Hit.TrainingTuto);
        Add(_trainingClose, Hit.TrainingClose);
    }

    // ── Dessin ───────────────────────────────────────────────────────────────────

    private void DrawTraining(SpriteBatch sb)
    {
        var L = _lay;
        var full = L.Full;
        PvpArt.Fill(sb, Px, new Rectangle(0, 0, L.W, L.H), Palette.Black1 * 0.8f);
        PvpArt.Panel(sb, Px, _trainingPanel);
        var ins = PvpArt.PanelFrame + (full ? 18 : 10);
        var inner = _trainingPanel.Width - 2 * ins;
        var titleScale = full ? 3 : 2;
        var y = _trainingPanel.Y + ins;
        var title = Loc.T("pvp.training");
        Font.DrawCentered(sb, PvpArt.Fit(Font, title, inner, titleScale),
            new Rectangle(_trainingPanel.X, y, _trainingPanel.Width, Font.LineHeight(titleScale)), titleScale, Palette.Yellow2);
        y += Font.LineHeight(titleScale) + 6;
        Font.DrawCentered(sb, PvpArt.Fit(Font, Loc.T("pvp.training_sub"), inner, 1),
            new Rectangle(_trainingPanel.X, y, _trainingPanel.Width, Font.LineHeight(1)), 1, Palette.Grey);

        var mine = Profile.Rank;
        for (var i = 0; i < _trainingCells.Count; i++)
        {
            var r = _trainingCells[i];
            var rank = _trainingRanks[i];
            var current = rank == mine;   // niveau du joueur : repère
            var hover = Hovered(r);
            PvpArt.Button(sb, Px, r, current, hover && !current);
            var iconW = PvpRankIcon.W * 2;
            var iconX = r.X + (full ? 10 : 6);
            RankIcon.Draw(sb, rank.Id, new Point(iconX, r.Center.Y - PvpRankIcon.H), 2);
            var tx = iconX + iconW + (full ? 8 : 5);
            var label = PvpArt.Fit(Font, _trainingNames[i], r.Right - 4 - tx, 1);
            Font.Draw(sb, label, new Vector2(tx, r.Center.Y - Font.LineHeight(1) / 2), 1,
                current ? Palette.Yellow2 : hover ? Palette.White : PvpRankIcon.LightColor(rank.Id));
        }

        PvpArt.Button(sb, Px, _trainingTuto, false, Hovered(_trainingTuto));
        Font.DrawCentered(sb, PvpArt.Fit(Font, Loc.T("pvp.training_tuto"), _trainingTuto.Width - 8, 1), _trainingTuto, 1,
            Palette.Cyan1);
        PvpArt.Button(sb, Px, _trainingClose, false, Hovered(_trainingClose));
        Font.DrawCentered(sb, Loc.T("pvp.cancel"), _trainingClose, 1, Palette.White);
    }
}
