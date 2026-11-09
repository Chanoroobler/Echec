using System;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Engine.Input;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.UI;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Tuto de l'écran multi (<see cref="PvpTutorial"/>) : une bulle par zone de l'interface, voile sombre partout
/// sauf sur la zone expliquée (cadre uni), puis les règles du combat. S'ouvre seul tant que le profil n'a pas vu la
/// version actuelle (<see cref="PvpProfile.TutorialPending"/>) ; le bouton « ? » de la barre du haut le relance.
/// Suivant : clic, Entrée, Espace, →, A, RB. Précédent : ←, LB. Passer : Échap, B.
/// </summary>
public sealed partial class PvpArmyScene
{
    private const int TutoMaxHoles = 2;

    private readonly PvpTutorial _tuto = new();
    private readonly Rectangle[] _tutoHoles = new Rectangle[TutoMaxHoles];
    private int _tutoHoleCount;
    private Rectangle _tutoBubble, _tutoPrev, _tutoSkip, _tutoNext;
    private Rectangle _tutoTitleRect, _tutoTextRect;
    private int _tutoTitleScale;

    // Textes de l'étape (formatés seulement quand l'étape, la langue ou le périphérique changent).
    private (int Index, Language Lang, bool Pad) _tutoKey = (-1, Language.Francais, false);
    private string _tutoTitle = "", _tutoText = "", _tutoCounter = "";
    private string _tutoPrevLabel = "", _tutoSkipLabel = "", _tutoNextLabel = "";

    /// <summary>Le bouton « ? » relance le tuto : sur le build seulement, ni pendant une recherche ni après.</summary>
    private bool CanOpenTutorial => _view == View.Build && _mode == SearchMode.Idle && !Locked;

    private void StartTutorial()
    {
        if (!CanOpenTutorial)
            return;
        CancelHeld();
        _tuto.Restart();
        _tutorialOpen = true;
        Context.Sounds.Play("menu_click");
    }

    private void EndTutorial()
    {
        _tuto.Skip();
        _tutorialOpen = false;
        Profile.MarkTutorialSeen();
        PvpProfileStore.Save();
        Context.Sounds.Play("menu_close");
    }

    private void UpdateTutorial(InputManager input)
    {
        var hit = Hit.None;
        if (input.WasLeftClicked)
            for (var i = _targets.Count - 1; i >= 0; i--)
                if (_targets[i].Rect.Contains(input.MousePosition))
                {
                    hit = _targets[i].Kind;
                    break;
                }

        if (input.WasKeyPressed(Keys.Escape) || input.WasCancelPressed || hit == Hit.TutorialSkip)
        {
            EndTutorial();
            return;
        }
        if (input.WasKeyPressed(Keys.Left) || input.WasLeftShoulderPressed || input.Nav(NavDir.Left) || hit == Hit.TutorialPrev)
        {
            if (_tuto.Previous())
                Context.Sounds.Play("menu_click");
            return;
        }
        if (input.WasLeftClicked || input.WasConfirmPressed || input.WasRightShoulderPressed || input.Nav(NavDir.Right)
            || input.WasKeyPressed(Keys.Enter) || input.WasKeyPressed(Keys.Space) || input.WasKeyPressed(Keys.Right))
        {
            if (_tuto.IsLast)
            {
                EndTutorial();
                return;
            }
            _tuto.Next();
            Context.Sounds.Play("menu_click");
        }
    }

    /// <summary>Cibles du tuto : seulement ses boutons (non focalisables : la manette passe par A / B / LB).</summary>
    private void AddTutorialTargets()
    {
        LayoutTutorial();
        if (!_tuto.IsFirst)
            Add(_tutoPrev, Hit.TutorialPrev, focusable: false);
        Add(_tutoSkip, Hit.TutorialSkip, focusable: false);
        Add(_tutoNext, Hit.TutorialOk, focusable: false);
    }

    // ── Textes ───────────────────────────────────────────────────────────────────

    private void RefreshTutorialTexts()
    {
        var pad = Context.Input.UsingGamepad;
        var key = (_tuto.Index, Loc.Current, pad);
        if (key == _tutoKey)
            return;
        _tutoKey = key;
        var c = PvpConfig.Current;
        var id = _tuto.Id;
        _tutoTitle = Loc.T($"pvp.tuto.{id}.title");
        _tutoText = id switch
        {
            "board" => Loc.T(pad ? "pvp.tuto.board.text_pad" : "pvp.tuto.board.text"),
            "budget" => Loc.T("pvp.tuto.budget.text", c.Budget),
            "equipment" => Loc.T("pvp.tuto.equipment.text", c.EquipmentBudget, c.MaxSameEquipment),
            "points" => Loc.T("pvp.tuto.points.text", c.PointsPerKill, c.PointsForWin, c.PointsForDraw),
            "chests" => Loc.T("pvp.tuto.chests.text", c.StartPoints),
            "launch" => Loc.T("pvp.tuto.launch.text", (int)c.BotAfterSeconds, (int)c.BotAfterBusySeconds),
            "clock" => Loc.T("pvp.tuto.clock.text", (int)(c.ReserveSeconds / 60f), (int)c.MoveSeconds),
            "collapse" => Loc.T("pvp.tuto.collapse.text", (int)(c.CollapseBelowSeconds / 60f)),
            "rules" => Loc.T("pvp.tuto.rules.text", (int)c.ReconnectSeconds),
            _ => Loc.T($"pvp.tuto.{id}.text"),
        };
        _tutoCounter = $"{_tuto.Index + 1}/{PvpTutorial.Count}";
        _tutoPrevLabel = (pad ? "(LB) " : "") + Loc.T("pvp.tuto.prev");
        _tutoSkipLabel = (pad ? "(B) " : "") + Loc.T("pvp.tuto.skip");
        _tutoNextLabel = (pad ? "(A) " : "") + Loc.T(_tuto.IsLast ? "pvp.tuto.done" : "pvp.tuto.next");
    }

    // ── Mise en page ─────────────────────────────────────────────────────────────

    private void TutorialHoles()
    {
        var L = _lay;
        _tutoHoleCount = 0;
        switch (_tuto.Zone)
        {
            case PvpTutorialZone.Profile: Hole(Rectangle.Union(L.RankPill, L.RecordPill)); break;
            case PvpTutorialZone.ArmyTabs: Hole(Rectangle.Union(L.Tabs[0], L.Tabs[PvpProfile.BuildSlots - 1])); break;
            case PvpTutorialZone.Commander: Hole(L.Cmd); break;
            case PvpTutorialZone.Board: Hole(L.Board); break;
            case PvpTutorialZone.Reserve: Hole(L.Res); break;
            case PvpTutorialZone.Budget: Hole(L.Center); break;
            case PvpTutorialZone.Points: Hole(L.PointsPill); break;
            case PvpTutorialZone.Chests: Hole(L.ChestsBtn); break;
            case PvpTutorialZone.Ranking: Hole(L.RankingBtn); break;
            case PvpTutorialZone.Launch: Hole(L.Launch); break;
            case PvpTutorialZone.Friend: Hole(L.Friend); break;
        }
    }

    /// <summary>Zone mise en lumière, élargie de 4 px (le cadre ne mord pas sur le contenu), dans l'écran.</summary>
    private void Hole(Rectangle r)
    {
        if (_tutoHoleCount >= TutoMaxHoles)
            return;
        r.Inflate(4, 4);
        var x = Math.Max(0, r.X);
        var y = Math.Max(0, r.Y);
        _tutoHoles[_tutoHoleCount++] = new Rectangle(x, y, Math.Min(_lay.W, r.Right) - x, Math.Min(_lay.H, r.Bottom) - y);
    }

    private void LayoutTutorial()
    {
        RefreshTutorialTexts();
        TutorialHoles();
        var L = _lay;
        var full = L.Full;
        var pad = full ? 14 : 10;
        var btnH = full ? 32 : 26;
        var btnGap = 6;
        var centered = _tutoHoleCount == 0;
        var w = centered ? (full ? 540 : 440) : (full ? 440 : 360);

        // Boutons : ‹ PRÉCÉDENT · PASSER · SUIVANT › calés à droite ; la bulle s'élargit si une langue ne tient pas.
        var prevW = Font.Measure(_tutoPrevLabel, 1) + 20;
        var skipW = Font.Measure(_tutoSkipLabel, 1) + 20;
        var nextW = Font.Measure(_tutoNextLabel, 1) + 24;
        var rowW = (_tuto.IsFirst ? 0 : prevW + btnGap) + skipW + btnGap + nextW;
        w = Math.Min(L.W - 16, Math.Max(w, rowW + 2 * pad));

        var counterW = Font.Measure(_tutoCounter, 1) + 8;
        _tutoTitleScale = PvpArt.ScaleToFit(Font, _tutoTitle, w - 2 * pad - counterW, full ? 3 : 2);
        var titleH = Font.LineHeight(_tutoTitleScale);
        var lh = Font.LineHeight(1) + 4;
        var lines = TutoWrap(_tutoText, w - 2 * pad).Count;
        var h = pad + titleH + (full ? 10 : 8) + lines * lh - 4 + (full ? 14 : 10) + btnH + 4 + pad;

        _tutoBubble = PlaceBubble(w, h);
        var b = _tutoBubble;
        _tutoTitleRect = new Rectangle(b.X + pad, b.Y + pad, w - 2 * pad, titleH);
        _tutoTextRect = new Rectangle(b.X + pad, _tutoTitleRect.Bottom + (full ? 10 : 8), w - 2 * pad, lines * lh);
        var by = b.Bottom - pad - 4 - btnH;
        var x = b.Right - pad - nextW;
        _tutoNext = new Rectangle(x, by, nextW, btnH);
        x -= btnGap + skipW;
        _tutoSkip = new Rectangle(x, by, skipW, btnH);
        x -= btnGap + prevW;
        _tutoPrev = new Rectangle(x, by, prevW, btnH);
    }

    /// <summary>Bulle à côté de la zone : dessous, dessus (zones basses : d'abord dessus), à droite, à gauche ;
    /// sinon dans le haut de la zone (le plateau, assez large pour qu'aucun côté ne suffise). Étape sans zone : centrée.</summary>
    private Rectangle PlaceBubble(int w, int h)
    {
        var L = _lay;
        const int Margin = 8, Gap = 10;
        if (_tutoHoleCount == 0)
            return new Rectangle((L.W - w) / 2, (L.H - h) / 2, w, h);
        var z = _tutoHoles[0];
        for (var i = 1; i < _tutoHoleCount; i++)
            z = Rectangle.Union(z, _tutoHoles[i]);
        var cx = Math.Clamp(z.Center.X - w / 2, Margin, L.W - Margin - w);
        var cy = Math.Clamp(z.Center.Y - h / 2, Margin, L.H - Margin - h);
        var tall = z.Height > L.H / 2;
        var low = z.Center.Y > L.H / 2;
        if (!tall)
        {
            var below = new Rectangle(cx, z.Bottom + Gap, w, h);
            var above = new Rectangle(cx, z.Y - Gap - h, w, h);
            var belowOk = below.Bottom <= L.H - Margin;
            var aboveOk = above.Y >= Margin;
            if (low ? aboveOk : belowOk) return low ? above : below;
            if (low ? belowOk : aboveOk) return low ? below : above;
        }
        if (z.Right + Gap + w <= L.W - Margin)
            return new Rectangle(z.Right + Gap, cy, w, h);
        if (z.X - Gap - w >= Margin)
            return new Rectangle(z.X - Gap - w, cy, w, h);
        return new Rectangle(cx, Math.Clamp(z.Y + Gap, Margin, L.H - Margin - h), w, h);
    }

    private readonly System.Collections.Generic.Dictionary<(string, int), System.Collections.Generic.List<string>> _tutoWraps = new();

    /// <summary>Idéogramme (chinois) : une ligne peut se couper avant ou après lui, sans espace.</summary>
    private static bool IsCjk(char c) => c >= '⺀';

    /// <summary>Ponctuation qui ne commence jamais une ligne (reste collée au caractère d'avant).</summary>
    private static bool NoLineStart(char c) => c is '。' or '：' or '！' or '.' or ':' or '!' or '?';

    /// <summary>Découpage de la bulle (mis en cache) : aux espaces, et entre deux idéogrammes pour le chinois
    /// (qui n'a pas d'espaces : sans ça, toute une phrase passerait pour un seul mot).</summary>
    private System.Collections.Generic.List<string> TutoWrap(string text, int width)
    {
        if (_tutoWraps.TryGetValue((text, width), out var lines))
            return lines;
        lines = new System.Collections.Generic.List<string>();
        var start = 0;
        var cut = -1;   // dernière coupure possible (indice du début du morceau suivant)
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (i > start && !NoLineStart(c) && (c == ' ' || IsCjk(c) || IsCjk(text[i - 1])))
                cut = i;
            if (c == ' ' || Font.Measure(text[start..(i + 1)], 1) <= width || cut <= start)
                continue;
            lines.Add(text[start..cut].TrimEnd());
            start = cut;
            while (start < text.Length && text[start] == ' ')
                start++;
            cut = -1;
            i = start - 1;
        }
        if (start < text.Length)
            lines.Add(text[start..].TrimEnd());
        _tutoWraps[(text, width)] = lines;
        return lines;
    }

    // ── Dessin ───────────────────────────────────────────────────────────────────

    private readonly int[] _tutoSlabs = new int[2 + 2 * TutoMaxHoles];

    private void DrawTutorial(SpriteBatch sb)
    {
        var veil = Palette.Black1 * 0.8f;
        DrawVeilWithHoles(sb, veil);
        for (var i = 0; i < _tutoHoleCount; i++)
            PvpArt.Border(sb, Px, _tutoHoles[i], Palette.Yellow2, 2);

        var b = _tutoBubble;
        PvpArt.Panel(sb, Px, b);
        Font.Draw(sb, _tutoTitle, new Vector2(_tutoTitleRect.X, _tutoTitleRect.Y), _tutoTitleScale, Palette.Cyan1);
        Font.Draw(sb, _tutoCounter, new Vector2(_tutoTitleRect.Right - Font.Measure(_tutoCounter, 1), _tutoTitleRect.Y), 1, Palette.Grey);
        var ly = _tutoTextRect.Y;
        foreach (var line in TutoWrap(_tutoText, _tutoTextRect.Width))
        {
            Font.Draw(sb, line, new Vector2(_tutoTextRect.X, ly), 1, Palette.White, preserveCase: true);
            ly += Font.LineHeight(1) + 4;
        }

        var mouse = Context.Input.MousePosition;
        var pad = Context.Input.UsingGamepad;
        if (!_tuto.IsFirst)
        {
            PvpArt.Button(sb, Px, _tutoPrev, false, !pad && _tutoPrev.Contains(mouse));
            Font.DrawCentered(sb, _tutoPrevLabel, _tutoPrev, 1, Palette.White);
        }
        PvpArt.Button(sb, Px, _tutoSkip, false, !pad && _tutoSkip.Contains(mouse));
        Font.DrawCentered(sb, _tutoSkipLabel, _tutoSkip, 1, Palette.Blue1);
        var hover = pad || _tutoNext.Contains(mouse);
        var dy = PvpArt.Primary(sb, Px, _tutoNext, hover, false);
        Font.DrawCentered(sb, _tutoNextLabel, new Rectangle(_tutoNext.X, _tutoNext.Y + dy, _tutoNext.Width, _tutoNext.Height), 1, Palette.Black1);
    }

    /// <summary>Voile sur tout l'écran sauf les zones mises en lumière : bandes horizontales entre les bords des
    /// zones, et dans chaque bande les intervalles hors des zones qui la couvrent (aucune allocation).</summary>
    private void DrawVeilWithHoles(SpriteBatch sb, Color veil)
    {
        var L = _lay;
        var n = 0;
        _tutoSlabs[n++] = 0;
        _tutoSlabs[n++] = L.H;
        for (var i = 0; i < _tutoHoleCount; i++)
        {
            _tutoSlabs[n++] = _tutoHoles[i].Y;
            _tutoSlabs[n++] = _tutoHoles[i].Bottom;
        }
        Array.Sort(_tutoSlabs, 0, n);
        // Zones triées par abscisse (deux au plus).
        if (_tutoHoleCount == 2 && _tutoHoles[1].X < _tutoHoles[0].X)
            (_tutoHoles[0], _tutoHoles[1]) = (_tutoHoles[1], _tutoHoles[0]);
        for (var s = 0; s + 1 < n; s++)
        {
            var y0 = _tutoSlabs[s];
            var y1 = _tutoSlabs[s + 1];
            if (y1 <= y0)
                continue;
            var x = 0;
            for (var i = 0; i < _tutoHoleCount; i++)
            {
                var hole = _tutoHoles[i];
                if (hole.Y > y0 || hole.Bottom < y1)
                    continue;
                if (hole.X > x)
                    PvpArt.Fill(sb, Px, new Rectangle(x, y0, hole.X - x, y1 - y0), veil);
                x = Math.Max(x, hole.Right);
            }
            if (x < L.W)
                PvpArt.Fill(sb, Px, new Rectangle(x, y0, L.W - x, y1 - y0), veil);
        }
    }

    /// <summary>Bouton « ? » de la barre du haut (grisé hors du build et pendant une recherche).</summary>
    private void DrawHelpButton(SpriteBatch sb)
    {
        var r = _lay.HelpBtn;
        var on = CanOpenTutorial;
        PvpArt.Button(sb, Px, r, _tutorialOpen, on && Hovered(r), enabled: on || _tutorialOpen);
        Font.DrawCentered(sb, "?", r, _lay.TextScale, on || _tutorialOpen ? Palette.White : Palette.Grey);
    }
}
