using System;
using System.Collections.Generic;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Engine.UI;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.UI.Text;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>Rendu de l'écran Armée : barre du haut, armées, commandant, plateau, réserve et fiche, bande du bas.</summary>
public sealed partial class PvpArmyScene
{
    private static readonly Color GrassAlt = new(0x56, 0x66, 0x4a);   // herbe claire des cartes (maquette)
    private const float CardDim = 0.4f;
    private const float GhostAlpha = 0.55f;

    private ITextFont Font => Context.Font;
    private Texture2D Px => Context.Pixel;

    public override void Draw(GameTime gameTime)
    {
        ComputeLayout();
        EnsureEntries();
        BuildTargets();
        _hover = HitIndexAt(_pointer);   // cibles reconstruites : indice relu
        var sb = Context.SpriteBatch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        PvpArt.Fill(sb, Px, new Rectangle(0, 0, _lay.W, _lay.H), Palette.Black3);
        if (_view == View.Build)
            DrawBuild(sb);
        else if (_view == View.Ranking)
            DrawRanking(sb);
        else
            DrawChests(sb);
        DrawTopBar(sb);
        if (_view == View.Chests && _rolling)
            DrawReel(sb);     // roulette « casino », par-dessus tout comme la révélation
        if (_view == View.Chests && _revealOpen)
            DrawReveal(sb);   // par-dessus la barre du haut, voilée elle aussi
        if (_view == View.Build && _held.Active && !Context.Input.UsingGamepad)
            DrawHeldAtPointer(sb);
        if (_tutorialOpen)
            DrawTutorial(sb);
        if (_error != null)
            DrawError(sb, _error);
        sb.End();
    }

    // ── Textes mémorisés (aucune chaîne formatée à chaque frame) ──────────────────

    private static readonly string[] Numbers = new string[1000];

    /// <summary>« 42 » sans allocation récurrente (table paresseuse 0..999).</summary>
    private static string Num(int n)
    {
        if (n < 0 || n >= Numbers.Length)
            return n.ToString();
        return Numbers[n] ??= n.ToString();
    }

    private (int Rating, int W, int L, int D, int Points, Language Lang) _topKey = (-1, 0, 0, 0, 0, Language.Francais);
    private string _topRank = "", _topRating = "", _topRecord = "", _topPoints = "";

    private void RefreshTop()
    {
        var p = Profile;
        var key = (p.Rating, p.Wins, p.Losses, p.Draws, p.Points, Loc.Current);
        if (key == _topKey)
            return;
        _topKey = key;
        _topRank = PvpUi.RankName(p.Rank);
        _topRating = Loc.T("pvp.rating_short", p.Rating);
        _topRecord = Loc.T("pvp.record_short", p.Wins, p.Losses, p.Draws);
        _topPoints = Num(p.Points);
    }

    private string TopRank { get { RefreshTop(); return _topRank; } }
    private string TopRating { get { RefreshTop(); return _topRating; } }
    private string TopRecord { get { RefreshTop(); return _topRecord; } }
    private string TopPoints { get { RefreshTop(); return _topPoints; } }

    private readonly Dictionary<string, (string Prefix, string Suffix)> _templates = new();

    /// <summary>Gabarit « RESTE {0} » coupé autour de {0} (mis en cache) : le nombre prend sa propre couleur.</summary>
    private (string Prefix, string Suffix) Template(string key)
    {
        var raw = Loc.T(key);
        if (_templates.TryGetValue(raw, out var t))
            return t;
        var i = raw.IndexOf("{0}", StringComparison.Ordinal);
        t = i < 0 ? (raw + " ", "") : (raw[..i], raw[(i + 3)..]);
        _templates[raw] = t;
        return t;
    }

    /// <summary>Dessine « préfixe + valeur + suffixe », calé à droite sur <paramref name="right"/> ; renvoie sa largeur.</summary>
    private int DrawTemplateRight(SpriteBatch sb, string key, string value, int right, int y, int scale, Color label, Color valueColor)
    {
        var (pre, suf) = Template(key);
        var w = Font.Measure(pre, scale) + Font.Measure(value, scale) + Font.Measure(suf, scale);
        var x = right - w;
        Font.Draw(sb, pre, new Vector2(x, y), scale, label);
        x += Font.Measure(pre, scale);
        Font.Draw(sb, value, new Vector2(x, y), scale, valueColor);
        x += Font.Measure(value, scale);
        Font.Draw(sb, suf, new Vector2(x, y), scale, label);
        return w;
    }

    private void TextLeft(SpriteBatch sb, string text, Rectangle area, int scale, Color c, int dx = 0, bool preserveCase = false) =>
        Font.Draw(sb, text, new Vector2(area.X + dx, area.Y + (area.Height - Font.LineHeight(scale)) / 2), scale, c, preserveCase);

    private bool Hovered(Rectangle r) => _hover >= 0 && _targets[_hover].Rect == r;

    private bool HoveredKind(Hit kind, int index) =>
        _hover >= 0 && _targets[_hover].Kind == kind && _targets[_hover].Index == index;

    // ── Barre du haut ────────────────────────────────────────────────────────────

    private void DrawTopBar(SpriteBatch sb)
    {
        var L = _lay;
        var s = L.TextScale;
        PvpArt.Fill(sb, Px, L.Top, Palette.Black1);
        PvpArt.Fill(sb, Px, new Rectangle(0, L.Top.Bottom - 2, L.W, 2), Palette.Black5);
        Font.Draw(sb, Loc.T("menu.multiplayer"), L.TitlePos.ToVector2(), L.TitleScale, Palette.Yellow2);

        Pill(sb, L.RankPill, Palette.Brown2);
        var rw = RankIconSmallW + RankIconGap + Font.Measure(TopRank, s);
        var rx = L.RankPill.Center.X - rw / 2;
        RankIcon.Draw(sb, Profile.Rank.Id, new Point(rx, L.RankPill.Center.Y - PvpRankIcon.H), 2);
        TextLeft(sb, TopRank, L.RankPill, s, Palette.Yellow1, rx - L.RankPill.X + RankIconSmallW + RankIconGap);
        Pill(sb, L.RatingPill, Palette.Black5);
        Font.DrawCentered(sb, TopRating, L.RatingPill, s, Palette.White);
        Pill(sb, L.RecordPill, Palette.Black5);
        Font.DrawCentered(sb, TopRecord, L.RecordPill, s, Palette.Blue1);

        DrawHelpButton(sb);

        // Points : pièce + nombre, juste à gauche de COFFRES.
        Pill(sb, L.PointsPill, Palette.Brown1);
        var coin = PvpArt.CoinSize * 2;
        var pw = coin + 6 + Font.Measure(TopPoints, s);
        var px = L.PointsPill.Center.X - pw / 2;
        PvpArt.Coin(sb, Px, new Point(px, L.PointsPill.Center.Y - coin / 2), 2);
        TextLeft(sb, TopPoints, L.PointsPill, s, Palette.Yellow2, px - L.PointsPill.X + coin + 6);

        var rankingOn = _view == View.Ranking;
        PvpArt.Button(sb, Px, L.RankingBtn, rankingOn, Hovered(L.RankingBtn));
        var rankingLabel = Loc.T("pvp.ranking");
        var tw = PvpArt.TrophySize * 2 + 8 + Font.Measure(rankingLabel, s);
        var tx = L.RankingBtn.Center.X - tw / 2;
        PvpArt.Trophy(sb, Px, new Point(tx, L.RankingBtn.Center.Y - PvpArt.TrophySize), 2);
        TextLeft(sb, rankingLabel, L.RankingBtn, s, rankingOn ? Palette.Yellow2 : Palette.White, tx - L.RankingBtn.X + PvpArt.TrophySize * 2 + 8);

        var chestsOn = _view == View.Chests;
        PvpArt.Button(sb, Px, L.ChestsBtn, chestsOn, Hovered(L.ChestsBtn));
        var label = Loc.T("pvp.chests");
        var cw = 32 + 8 + Font.Measure(label, s);
        var cx = L.ChestsBtn.Center.X - cw / 2;
        DrawHalfSprite(sb, Tex("Objects/coffre"), new Rectangle(cx, L.ChestsBtn.Center.Y - 16, 32, 32), Color.White);
        TextLeft(sb, label, L.ChestsBtn, s, chestsOn ? Palette.Yellow2 : Palette.White, cx - L.ChestsBtn.X + 40);

        PvpArt.Button(sb, Px, L.BackBtn, false, Hovered(L.BackBtn), enabled: !Locked);
        Font.DrawCentered(sb, Loc.T("pvp.back"), L.BackBtn, s, Locked ? Palette.Grey : Palette.White);
    }

    private void Pill(SpriteBatch sb, Rectangle r, Color border)
    {
        PvpArt.Fill(sb, Px, r, Palette.Black3);
        PvpArt.Border(sb, Px, r, border, 2);
    }

    /// <summary>
    /// Sprite 64×64 posé dans une case de 32 : par la couche « nette » de l'écran (cf. <see cref="NativeSprites"/>)
    /// dès que le canevas est agrandi ×2 ou plus (chaque pixel du sprite reste un vrai pixel), sinon réduit d'un
    /// facteur entier dans le canevas. Un sprite déjà de 32 est posé tel quel.
    /// </summary>
    private void DrawHalfSprite(SpriteBatch sb, Texture2D? tex, Rectangle box, Color tint)
    {
        if (tex == null)
            return;
        if (tex.Width <= box.Width)
        {
            sb.Draw(tex, new Rectangle(box.X + (box.Width - tex.Width) / 2, box.Y + (box.Height - tex.Height) / 2, tex.Width, tex.Height), tint);
            return;
        }
        if (NativeSprites.CanQueue && _overlayDim == 0f)
            NativeSprites.Queue(tex, box, tint);
        else
            sb.Draw(tex, box, tint);   // réduit d'un facteur entier, sous le voile éventuel
    }

    // Voile d'une fenêtre par-dessus (révélation, tuto) : la couche nette passe AU-DESSUS du canevas, donc les
    // sprites qu'elle porterait resteraient vifs sous le voile ; on les dessine alors dans le canevas.
    private float _overlayDim;

    // ── Écran Armée ──────────────────────────────────────────────────────────────

    private void DrawBuild(SpriteBatch sb)
    {
        _overlayDim = _tutorialOpen || _error != null ? 1f : 0f;
        DrawArmyTabs(sb);
        DrawCommander(sb);
        DrawBoard(sb);
        DrawReserve(sb);
        DrawBand(sb);
    }

    private readonly int[] _tabCostValue = { -1, -1, -1 };
    private readonly string[] _tabCostText = new string[3];

    private void DrawArmyTabs(SpriteBatch sb)
    {
        var L = _lay;
        for (var i = 0; i < PvpProfile.BuildSlots; i++)
        {
            var r = L.Tabs[i];
            var selected = i == _tab;
            PvpArt.Button(sb, Px, r, selected, Hovered(r) && !selected, enabled: !ArmyFrozen || selected);
            var cost = PvpCatalog.Cost(Profile.Builds[i]);
            if (_tabCostValue[i] != cost)
            {
                _tabCostValue[i] = cost;
                _tabCostText[i] = Loc.T("pvp.army_cost", cost, PvpConfig.Current.Budget);
            }
            var name = Loc.T("pvp.army_n", i + 1);
            var nameScale = PvpArt.ScaleToFit(Font, name, r.Width - 8, L.Full ? 2 : 1);
            var nameH = Font.LineHeight(nameScale);
            var subH = Font.LineHeight(1);
            var top = r.Y + (r.Height - nameH - 4 - subH) / 2;
            Font.DrawCentered(sb, name, new Rectangle(r.X, top, r.Width, nameH), nameScale, selected ? Palette.White : Palette.Blue1);
            var over = cost > PvpConfig.Current.Budget || PvpPlacement.RemainingEquipment(Profile.Builds[i]) < 0;
            Font.DrawCentered(sb, _tabCostText[i], new Rectangle(r.X, top + nameH + 4, r.Width, subH), 1,
                over ? Palette.Purple5 : selected ? Palette.Yellow2 : Palette.Grey);
        }
    }

    // ── Commandant ───────────────────────────────────────────────────────────────

    private (string Id, Language Lang, bool Full) _cmdKey;
    private string _cmdName = "", _cmdCostLabel = "", _cmdMove = "";
    private int _cmdNameScale = 1;
    private readonly string[] _cmdValues = new string[4];
    private readonly List<string> _cmdTraits = new();

    private void RefreshCommander(CommandeDef? def, UnitClass? cls)
    {
        var key = (Build.CommanderId, Loc.Current, _lay.Full);
        if (key == _cmdKey)
            return;
        _cmdKey = key;
        var inner = _lay.CmdInner;
        _cmdName = PvpUi.CommanderName(Build.CommanderId);
        _cmdNameScale = PvpArt.ScaleToFit(Font, _cmdName, inner.Width, _lay.Full ? 3 : 2);
        _cmdName = PvpArt.Fit(Font, _cmdName, inner.Width, _cmdNameScale);
        var cost = PvpConfig.Current.CommanderCostOf(Build.CommanderId);
        _cmdCostLabel = cost == 0 ? Loc.T("pvp.free_fixed") : Loc.T("pvp.cost_fixed", cost);
        _cmdMove = def == null ? "" : Loc.T("pvp.move." + def.Movement.ToString().ToLowerInvariant());
        if (cls != null)
        {
            _cmdValues[0] = Num(cls.MaxHp);
            _cmdValues[1] = Num(cls.Damage);
            _cmdValues[2] = Num(cls.MoveRange);
            _cmdValues[3] = Num(cls.AttackRange);
        }
        _cmdTraits.Clear();
        if (cls != null)
            foreach (var label in TraitLabels(cls, null))
                _cmdTraits.Add(PvpArt.Fit(Font, label, inner.Width - 10, 1));
    }

    private static readonly int[] BarMax = { 40, 20, 3, 3 };
    private static readonly string[] BarKeys = { "stat.hp", "stat.power", "stat.movement", "stat.range" };

    private void DrawCommander(SpriteBatch sb)
    {
        var L = _lay;
        PvpArt.Panel(sb, Px, L.Cmd);
        var inner = L.CmdInner;
        var def = Commandes.ById(Build.CommanderId);
        var cls = PvpCatalog.CommanderClass(Build.CommanderId)?.Class;
        RefreshCommander(def, cls);

        // 1. En-tête.
        var head = Loc.T("pvp.commander");
        Font.Draw(sb, head, new Vector2(inner.X, inner.Y), 1, Palette.Blue1);
        var rightW = inner.Width - Font.Measure(head, 1) - 8;
        var right = PvpArt.Fit(Font, _cmdCostLabel, rightW, 1);
        Font.Draw(sb, right, new Vector2(inner.Right - Font.Measure(right, 1), inner.Y), 1, Palette.Cyan1);

        // 2. Portrait ×3 (×2 en compact) entre les flèches.
        if (cls != null && UnitFront(cls.Asset) is { } front)
        {
            var size = front.Width * L.PortraitScale;
            sb.Draw(front, new Rectangle(L.Portrait.Center.X - size / 2, L.Portrait.Bottom - front.Height * L.PortraitScale,
                size, front.Height * L.PortraitScale), Color.White);
        }
        var many = OwnedCommanders().Count > 1;
        Arrow(sb, L.CmdPrev, "<", many && !ArmyFrozen);
        Arrow(sb, L.CmdNext, ">", many && !ArmyFrozen);

        // 3. Nom + pagination des commandants débloqués.
        Font.DrawCentered(sb, _cmdName, L.CmdName, _cmdNameScale, Palette.White);
        var owned = OwnedCommanders();
        var dotsW = DotsWidth(owned.Count);
        var dx = L.CmdDots.Center.X - dotsW / 2;
        for (var i = 0; i < owned.Count; i++)
        {
            var on = owned[i].Id == Build.CommanderId;
            var w = on ? DotActiveW : DotW;
            PvpArt.Fill(sb, Px, new Rectangle(dx, L.CmdDots.Y, w, DotH), on ? Palette.Yellow1 : Palette.Black5);
            dx += w + DotGap;
        }

        // 4. Barres de stats multi (10 segments).
        if (cls != null)
        {
            var labelW = 0;
            foreach (var k in BarKeys)
                labelW = Math.Max(labelW, Font.Measure(Loc.T(k), 1));
            var valueW = Font.Measure("00", L.TextScale);
            var segGap = L.Full ? 2 : 1;
            var segArea = inner.Width - labelW - 10 - valueW - 10;
            var segW = Math.Clamp((segArea + segGap) / 10 - segGap, 3, 12);
            var colors = BarColors;
            for (var i = 0; i < 4; i++)
            {
                var y = L.BarsY + i * L.BarStep;
                var rowH = Math.Max(L.BarH, Font.LineHeight(L.TextScale));
                Font.Draw(sb, Loc.T(BarKeys[i]), new Vector2(inner.X, y + (rowH - Font.LineHeight(1)) / 2), 1, Palette.Blue1);
                var value = i switch { 0 => cls.MaxHp, 1 => cls.Damage, 2 => cls.MoveRange, _ => cls.AttackRange };
                var lit = Math.Clamp((int)Math.Round(value * 10.0 / BarMax[i]), value > 0 ? 1 : 0, 10);
                var sx = inner.Right - valueW - 10 - (10 * (segW + segGap) - segGap);
                var sy = y + (rowH - L.BarH) / 2;
                for (var sIdx = 0; sIdx < 10; sIdx++)
                    PvpArt.Fill(sb, Px, new Rectangle(sx + sIdx * (segW + segGap), sy, segW, L.BarH),
                        sIdx < lit ? colors[i] : Palette.Black1);
                var v = _cmdValues[i];
                Font.Draw(sb, v, new Vector2(inner.Right - Font.Measure(v, L.TextScale), y + (rowH - Font.LineHeight(L.TextScale)) / 2),
                    L.TextScale, colors[i]);
            }
        }

        // 5. Déplacement : icône du domaine (39×39 natif) + libellé.
        var domY = L.BarsY + 4 * L.BarStep + (L.Full ? 4 : 2);
        if (def != null)
        {
            var icon = Tex("Icons/domaine_" + def.Movement.ToString().ToLowerInvariant());
            var iconW = icon?.Width ?? 39;
            if (icon != null)
                sb.Draw(icon, new Vector2(inner.X, domY), Color.White);
            var tx = inner.X + iconW + 8;
            var tw = inner.Right - tx;
            Font.Draw(sb, Loc.T("pvp.movement"), new Vector2(tx, domY + 6), 1, Palette.Blue1);
            var moveScale = PvpArt.ScaleToFit(Font, _cmdMove, tw, L.Full ? 2 : 1);
            Font.Draw(sb, PvpArt.Fit(Font, _cmdMove, tw, moveScale), new Vector2(tx, domY + 6 + Font.LineHeight(1) + 6),
                moveScale, Palette.White);
            domY += Math.Max(39, 6 + Font.LineHeight(1) + 6 + Font.LineHeight(moveScale)) + (L.Full ? 8 : 6);
        }

        // 6. Traits en étiquettes (retour à la ligne automatique, jamais hors du panneau).
        var tagH = Font.LineHeight(1) + (L.Full ? 10 : 6);
        var tx2 = inner.X;
        var ty2 = domY;
        foreach (var t in _cmdTraits)
        {
            var w = Font.Measure(t, 1) + 10;
            if (tx2 > inner.X && tx2 + w > inner.Right)
            {
                tx2 = inner.X;
                ty2 += tagH + 4;
            }
            if (ty2 + tagH > L.Cmd.Bottom - PvpArt.PanelFrame)
                break;
            var tag = new Rectangle(tx2, ty2, w, tagH);
            PvpArt.Fill(sb, Px, tag, Palette.Black3);
            PvpArt.Border(sb, Px, tag, Palette.Black5, 2);
            Font.DrawCentered(sb, t, tag, 1, Palette.White);
            tx2 += w + 4;
        }
    }

    private static readonly Color[] BarColors = { Palette.White, Palette.Brown3, Palette.Cyan1, Palette.Yellow2 };

    private void Arrow(SpriteBatch sb, Rectangle r, string glyph, bool enabled)
    {
        PvpArt.Button(sb, Px, r, false, enabled && Hovered(r), enabled);
        Font.DrawCentered(sb, glyph, r, _lay.Full ? 2 : 1, enabled ? Palette.White : Palette.Grey);
    }

    /// <summary>Libellés des traits d'une classe (et de son équipement), sans doublon, comme sur les cartes.</summary>
    private static List<string> TraitLabels(UnitClass c, Equipment? equipment)
    {
        var labels = new List<string>();
        void Add(string label)
        {
            if (label.Length > 0 && !labels.Contains(label))
                labels.Add(label);
        }
        if (c.PiercesAllies && !c.Traits.Contains("Franchissement"))
            Add(UnitKeywords.PiercesAllies.Label);
        foreach (var t in c.Traits)
            Add(UnitKeywords.For(t).Label);
        if (c.MinAttackRange > 1)
            Add(UnitKeywords.DeadZone.Label);
        if (equipment != null)
            foreach (var t in equipment.Traits)
                Add(UnitKeywords.For(t).Label);
        return labels;
    }

    // ── Plateau ──────────────────────────────────────────────────────────────────

    private void DrawBoard(SpriteBatch sb)
    {
        var L = _lay;
        var b = L.Board;
        PvpArt.Fill(sb, Px, new Rectangle(b.X - 4, b.Y - 4, b.Width + 8, b.Height + 8), Palette.Black1);
        _board.Draw(sb, Px, b.Location, L.FirstRow);

        // Les deux lignes du joueur : voile bleu ; case visée pendant un placement : voile cyan + cadre doré.
        var target = _hover >= 0 ? _targets[_hover] : default;
        var aimed = -1;
        if (_held.Active && target.Kind is Hit.Cell or Hit.Badge)
            aimed = target.Index;
        for (var row = PvpBoardRenderer.Size - 2; row < PvpBoardRenderer.Size; row++)
            for (var col = 0; col < PvpBoardRenderer.Size; col++)
            {
                var r = CellRect(row, col);
                var idx = row * PvpBoardRenderer.Size + col;
                PvpArt.Fill(sb, Px, r, (idx == aimed ? Palette.Cyan1 * 0.55f : Palette.Blue3 * 0.32f));
                if (idx == aimed || (!_held.Active && Context.Input.UsingGamepad && HoveredKind(Hit.Cell, idx)))
                    PvpArt.Border(sb, Px, r, Palette.Yellow2, 3);
            }
        if (aimed >= 0 && CellOf(aimed).Line < 0)
            PvpArt.Border(sb, Px, CellRect(aimed / PvpBoardRenderer.Size, aimed % PvpBoardRenderer.Size), Palette.Purple5, 3);

        // Pions, de l'arrière vers l'avant (rangée 6 puis 7), décalés de -8 comme en combat.
        var heldCell = _held.Active && _held.FromBoard ? CellIndex(_held.FromColumn, _held.FromLine) : -1;
        for (var row = PvpBoardRenderer.Size - 2; row < PvpBoardRenderer.Size; row++)
            for (var col = 0; col < PvpBoardRenderer.Size; col++)
            {
                var line = row == PvpBoardRenderer.Size - 1 ? PvpBuild.BackLine : PvpBuild.FrontLine;
                var idx = row * PvpBoardRenderer.Size + col;
                var cell = CellRect(row, col);
                string? asset = PvpBuild.IsKingCell(col, line)
                    ? PvpCatalog.CommanderClass(Build.CommanderId)?.Class.Asset
                    : Build.SlotAt(col, line)?.ClassAsset;
                if (asset == null || UnitBack(asset) is not { } tex)
                    continue;
                var movingAway = idx == heldCell && !_held.IsEquip;
                var lift = !_held.Active && !ArmyFrozen && !PvpBuild.IsKingCell(col, line)
                           && (HoveredKind(Hit.Cell, idx) || HoveredKind(Hit.Badge, idx)) ? 14 : 8;
                sb.Draw(tex, new Rectangle(cell.X + (cell.Width - tex.Width) / 2, cell.Bottom - tex.Height - lift, tex.Width, tex.Height),
                    Color.White * (movingAway ? 0.35f : 1f));
            }

        // Aperçu de dépôt : le sprite tenu en fantôme sur la case visée.
        if (_held.Active && aimed >= 0 && CellOf(aimed) is var (ac, al) && al >= 0 && !PvpBuild.IsKingCell(ac, al))
        {
            var cell = CellRect(aimed / PvpBoardRenderer.Size, ac);
            if (!_held.IsEquip && UnitBack(_held.Id) is { } ghost && aimed != heldCell)
                sb.Draw(ghost, new Rectangle(cell.X + (cell.Width - ghost.Width) / 2, cell.Bottom - ghost.Height - 8, ghost.Width, ghost.Height),
                    Color.White * GhostAlpha);
        }

        // Badges d'équipement (par-dessus tous les pions).
        foreach (var slot in Build.Slots)
        {
            if (slot.EquipmentId == null || Equipments.ById(slot.EquipmentId) is not { } eq)
                continue;
            var idx = CellIndex(slot.Column, slot.Line);
            var away = _held.Active && _held.FromBoard && (idx == heldCell);
            DrawBadge(sb, BadgeRect(slot.Column, slot.Line), eq, away ? 0.35f : 1f,
                !_held.Active && HoveredKind(Hit.Badge, idx));
        }
        if (_held.Active && _held.IsEquip && aimed >= 0 && CellOf(aimed) is var (ec, el) && el >= 0
            && !PvpBuild.IsKingCell(ec, el) && Build.SlotAt(ec, el) != null && Equipments.ById(_held.Id) is { } heq)
            DrawBadge(sb, BadgeRect(ec, el), heq, GhostAlpha, false);

        _board.DrawBushes(sb, b.Location, L.FirstRow);
    }

    /// <summary>Badge 32×32 : fond <c>background.png</c> + icône (natifs), contour 2 px <c>Black1</c> et liseré 1 px de rareté.</summary>
    private void DrawBadge(SpriteBatch sb, Rectangle r, Equipment eq, float alpha, bool hover)
    {
        PvpArt.Border(sb, Px, new Rectangle(r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6), (hover ? Palette.Yellow2 : PvpArt.RarityColor(eq.Rarity)) * alpha, 1);
        PvpArt.Border(sb, Px, new Rectangle(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4), Palette.Black1 * alpha, 2);
        if (Tex("Equipment/background") is { } bg)
            sb.Draw(bg, r, Color.White * alpha);
        if (EquipIcon(eq) is { } icon)
            sb.Draw(icon, new Rectangle(r.X + (r.Width - icon.Width) / 2, r.Y + (r.Height - icon.Height) / 2, icon.Width, icon.Height), Color.White * alpha);
    }

    private void DrawHeldAtPointer(SpriteBatch sb)
    {
        var p = _pointer;
        // Sur une case du joueur, l'aperçu de dépôt (fantôme / badge) est déjà dessiné sur la case : pas de doublon.
        if (_hover >= 0 && _targets[_hover].Kind is Hit.Cell or Hit.Badge
            && CellOf(_targets[_hover].Index) is var (hc, hl) && hl >= 0 && !PvpBuild.IsKingCell(hc, hl)
            && (!_held.IsEquip || Build.SlotAt(hc, hl) != null))
            return;
        if (_held.IsEquip)
        {
            if (Equipments.ById(_held.Id) is { } eq)
                DrawBadge(sb, new Rectangle(p.X - 16, p.Y - 16, 32, 32), eq, 0.9f, false);
            return;
        }
        if (UnitBack(_held.Id) is { } tex)
            sb.Draw(tex, new Rectangle(p.X - tex.Width / 2, p.Y - tex.Height / 2 - 8, tex.Width, tex.Height), Color.White * 0.85f);
    }

    // ── Réserve ──────────────────────────────────────────────────────────────────

    private void DrawReserve(SpriteBatch sb)
    {
        var L = _lay;
        PvpArt.Panel(sb, Px, L.Res);
        var s = L.Full ? 2 : 1;
        var inner = L.ResInner;

        if (_held.Active && _held.FromBoard)
        {
            // On tient un pion / un équipement du plateau : l'en-tête devient « REMETTRE EN RÉSERVE ».
            var r = ToReserveRect;
            var hot = Hovered(r);
            PvpArt.Fill(sb, Px, r, hot ? Palette.Purple3 : Palette.Purple2);
            PvpArt.Border(sb, Px, r, Palette.Purple5, 2);
            var label = Loc.T("pvp.to_reserve");
            Font.DrawCentered(sb, PvpArt.Fit(Font, label, r.Width - 8, PvpArt.ScaleToFit(Font, label, r.Width - 8, s)), r,
                PvpArt.ScaleToFit(Font, label, r.Width - 8, s), Palette.White);
        }
        else
        {
            ReserveTab(sb, L.TabUnits, Loc.T("pvp.palette_units"), !_reserveEquip, s);
            ReserveTab(sb, L.TabEquip, Loc.T("pvp.palette_equipment"), _reserveEquip, s);
            // Pagination : ‹ 1/2 › (grisée en bout de liste).
            var pages = PageCount;
            PageArrow(sb, L.PagePrev, "<", Page > 0);
            PageArrow(sb, L.PageNext, ">", Page < pages - 1);
            Font.DrawCentered(sb, PageText(Page + 1, pages), L.PageCounter, 1, Palette.Blue1);
        }

        var first = Page * L.CardCount;
        var remainingPts = _reserveEquip ? PvpPlacement.RemainingEquipment(Build) : PvpPlacement.Remaining(Build);
        for (var k = 0; k < L.CardCount && first + k < EntryCount; k++)
        {
            var r = L.Cards[k];
            var hover = HoveredKind(Hit.Card, first + k);
            if (_reserveEquip)
                DrawEquipCard(sb, r, _equips[first + k], hover, k, remainingPts);
            else
                DrawUnitCard(sb, r, _units[first + k], hover, k, remainingPts);
        }

        PvpArt.Fill(sb, Px, new Rectangle(inner.X, L.SepY, inner.Width, 1), Palette.Black5);
        DrawSheet(sb);
    }

    private Rectangle ToReserveRect => new(_lay.ResInner.X, _lay.TabUnits.Y, _lay.ResInner.Width, _lay.TabUnits.Height);

    private (int Page, int Pages) _pageKey = (-1, -1);
    private string _pageText = "";

    private string PageText(int page, int pages)
    {
        if (_pageKey != (page, pages))
        {
            _pageKey = (page, pages);
            _pageText = $"{page}/{pages}";
        }
        return _pageText;
    }

    private void ReserveTab(SpriteBatch sb, Rectangle r, string label, bool selected, int scale)
    {
        PvpArt.Button(sb, Px, r, selected, Hovered(r) && !selected);
        var sc = PvpArt.ScaleToFit(Font, label, r.Width - 6, scale);
        Font.DrawCentered(sb, PvpArt.Fit(Font, label, r.Width - 6, sc), r, sc, selected ? Palette.White : Palette.Blue1);
    }

    private void PageArrow(SpriteBatch sb, Rectangle r, string glyph, bool enabled)
    {
        PvpArt.Button(sb, Px, r, false, enabled && Hovered(r), enabled);
        Font.DrawCentered(sb, glyph, r, _lay.Full ? 2 : 1, enabled ? Palette.White : Palette.Grey * 0.6f);
    }

    private Rectangle CardSquare(Rectangle r) => new(r.X + (r.Width - 64) / 2, r.Y + (_lay.Full ? 5 : 2), 64, 64);

    private void CardFrame(SpriteBatch sb, Rectangle r, bool hover, float alpha)
    {
        PvpArt.Fill(sb, Px, r, Palette.Black3 * alpha);
        PvpArt.Border(sb, Px, r, (hover ? Palette.Yellow2 : Palette.Brown1) * (hover ? 1f : alpha), _lay.Full ? 2 : 1);
    }

    private void CostTag(SpriteBatch sb, Rectangle square, string cost, float alpha)
    {
        var w = Font.Measure(cost, 1) + 4;
        var h = Font.LineHeight(1) + 2;
        var r = new Rectangle(square.Right - w, square.Bottom - h, w, h);
        PvpArt.Fill(sb, Px, r, Palette.Black1 * alpha);
        Font.DrawCentered(sb, cost, r, 1, Palette.Yellow2 * alpha);
    }

    private void LockVeil(SpriteBatch sb, Rectangle square)
    {
        PvpArt.Fill(sb, Px, square, Palette.Black1 * 0.55f);
        PvpArt.Lock(sb, Px, new Point(square.Center.X - PvpArt.LockW, square.Center.Y - PvpArt.LockH), 2);
    }

    private void DrawUnitCard(SpriteBatch sb, Rectangle r, UnitEntry e, bool hover, int k, int remaining)
    {
        var heldHere = _held.Active && !_held.IsEquip && !_held.FromBoard && _held.Id == e.Class.Asset;
        var alpha = !e.Owned || e.Price > remaining ? CardDim : 1f;
        CardFrame(sb, r, hover || heldHere, alpha);
        var sq = CardSquare(r);
        var row = k / _lay.Cols;
        PvpArt.Fill(sb, Px, sq, ((k + row) % 2 == 0 ? Palette.Yellow4 : GrassAlt) * alpha);
        if (UnitBack(e.Class.Asset) is { } tex)
            sb.Draw(tex, new Rectangle(sq.X + (64 - tex.Width) / 2, sq.Bottom - tex.Height, tex.Width, tex.Height), Color.White * alpha);
        CostTag(sb, sq, e.Cost, alpha);
        if (!e.Owned)
            LockVeil(sb, sq);
        Font.DrawCentered(sb, e.Name, new Rectangle(r.X, sq.Bottom + (_lay.Full ? 5 : 3), r.Width, Font.LineHeight(1)), 1,
            (e.Owned ? Palette.White : Palette.Grey) * Math.Max(alpha, 0.7f));
    }

    private void DrawEquipCard(SpriteBatch sb, Rectangle r, EquipEntry e, bool hover, int k, int remaining)
    {
        var heldHere = _held.Active && _held.IsEquip && !_held.FromBoard && _held.Id == e.Equipment.Id;
        var alpha = !e.Owned || e.Price > remaining ? CardDim : 1f;
        CardFrame(sb, r, hover || heldHere, alpha);
        var sq = CardSquare(r);
        if (Tex("Equipment/background") is { } bg)
            sb.Draw(bg, sq, Color.White * alpha);   // 32×32 ×2
        if (EquipIcon(e.Equipment) is { } icon)
        {
            var w = icon.Width * 2;
            var h = icon.Height * 2;
            sb.Draw(icon, new Rectangle(sq.Center.X - w / 2, sq.Center.Y - h / 2, w, h), Color.White * alpha);
        }
        PvpArt.Border(sb, Px, sq, PvpArt.RarityColor(e.Equipment.Rarity) * alpha, 2);
        CostTag(sb, sq, e.Cost, alpha);
        if (!e.Owned)
            LockVeil(sb, sq);
        Font.DrawCentered(sb, e.Name, new Rectangle(r.X, sq.Bottom + (_lay.Full ? 5 : 3), r.Width, Font.LineHeight(1)), 1,
            PvpArt.RarityColor(e.Equipment.Rarity) * Math.Max(alpha, 0.7f));
    }

    // ── Fiche (sous la pagination) ───────────────────────────────────────────────

    private void DrawSheet(SpriteBatch sb)
    {
        var area = _lay.Sheet;
        // Ce qu'on survole (ou tient) : carte de la réserve, pion posé, badge.
        UnitClass? cls = null;
        Domaine dom = Domaine.Dame;
        Equipment? eq = null;
        var isCommander = false;
        Equipment? lone = null;   // équipement seul (carte de la réserve ou badge)
        var lockedLone = false;
        var t = _hover >= 0 ? _targets[_hover] : default;
        if (_held.Active)
        {
            if (_held.IsEquip) lone = Equipments.ById(_held.Id);
            else if (PvpCatalog.FindClass(_held.Id) is { } hc)
            {
                (dom, cls) = hc;
                if (_held.FromBoard && Build.SlotAt(_held.FromColumn, _held.FromLine)?.EquipmentId is { } he)
                    eq = Equipments.ById(he);
            }
        }
        else if (t.Kind == Hit.Card)
        {
            if (_reserveEquip) { lone = _equips[t.Index].Equipment; lockedLone = !_equips[t.Index].Owned; }
            else { cls = _units[t.Index].Class; dom = _units[t.Index].Domaine; }
        }
        else if (t.Kind == Hit.Badge)
        {
            var (c, line) = CellOf(t.Index);
            if (Build.SlotAt(c, line)?.EquipmentId is { } be)
                lone = Equipments.ById(be);
        }
        else if (t.Kind == Hit.Cell)
        {
            var (c, line) = CellOf(t.Index);
            if (line >= 0 && PvpBuild.IsKingCell(c, line) && PvpCatalog.CommanderClass(Build.CommanderId) is { } kc)
            {
                (dom, cls) = kc;
                isCommander = true;
            }
            else if (line >= 0 && Build.SlotAt(c, line) is { } slot && PvpCatalog.FindClass(slot.ClassAsset) is { } sc)
            {
                (dom, cls) = sc;
                if (slot.EquipmentId != null)
                    eq = Equipments.ById(slot.EquipmentId);
            }
        }

        // Souris posée SUR la fiche (pour la faire défiler) : elle garde ce qu'on survolait juste avant.
        if (lone == null && cls == null && !_held.Active && !Context.Input.UsingGamepad && area.Contains(_pointer))
            (cls, dom, eq, isCommander, lone, lockedLone) = _sheetSubject;
        _sheetSubject = (cls, dom, eq, isCommander, lone, lockedLone);

        if (lone != null)
            DrawEquipSheet(sb, area, lone, lockedLone);
        else if (cls != null)
            DrawUnitSheet(sb, area, cls, dom, eq, isCommander);
        else
        {
            DrawWrapped(sb, Loc.T(_reserveEquip ? "pvp.help_equipment" : "pvp.help_units"), area, Palette.Blue1);
        }
    }

    private (UnitClass? Cls, Domaine Dom, Equipment? Eq, bool Cmd, Equipment? Lone, bool Locked) _sheetSubject;

    private int DrawWrapped(SpriteBatch sb, string text, Rectangle area, Color color, int y = -1)
    {
        var lh = Font.LineHeight(1) + 4;
        y = y < 0 ? area.Y : y;
        foreach (var line in WrapCached(text, area.Width))
        {
            if (y + Font.LineHeight(1) > area.Bottom)
                break;
            Font.Draw(sb, line, new Vector2(area.X, y), 1, color, preserveCase: true);
            y += lh;
        }
        return y;
    }

    private readonly Dictionary<(string, int), List<string>> _wraps = new();

    /// <summary>Découpage d'un texte en lignes (mis en cache par texte et largeur).</summary>
    private List<string> WrapCached(string text, int width)
    {
        if (_wraps.TryGetValue((text, width), out var lines))
            return lines;
        lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (Font.Measure(candidate, 1) > width && line.Length > 0)
            {
                lines.Add(line);
                line = PvpArt.Fit(Font, word, width, 1);
            }
            else
                line = Font.Measure(candidate, 1) > width ? PvpArt.Fit(Font, candidate, width, 1) : candidate;
        }
        if (line.Length > 0)
            lines.Add(line);
        if (_wraps.Count > 256)
            _wraps.Clear();
        _wraps[(text, width)] = lines;
        return lines;
    }

    // ── Fiche d'un pion / du commandant ─────────────────────────────────────────
    //  Ni nom ni coût (déjà sur la carte de la réserve). En haut, côte à côte : le plateau 7×7 animé
    //  (MoveAttackGrid, tel quel) et les 4 stats empilées (icône 32×32 native, libellé gris, « +5 » doré, valeur
    //  à droite, dorée si un équipement la modifie). Dessous, sur toute la largeur : l'encart de l'équipement
    //  porté puis chaque trait (nom + fonctionnement complet), « NOM : fonctionnement », sans défilement (interligne resserré si ça ne tient pas).

    private const int StatPitch = 32;      // une ligne de stat par icône 32×32 (fond de 28 px centré dessus)
    private const int StatRowH = 28;
    private const int SheetGapPx = 5;      // espace entre deux traits

    private int SheetStep => Font.LineHeight(1) + 4;
    private int SheetHeadH => 4 * StatPitch;
    private int SheetColX(Rectangle area) => area.X + MoveAttackGrid.BoardOuter + (_lay.Full ? 12 : 6);
    private int SheetBodyY(Rectangle area) => area.Y + SheetHeadH + (_lay.Full ? 8 : 6);

    private (string A, string? B, int Kind, Language Lang, bool Full, Rectangle Area) _sheetKey;
    private int _sheetValueScale = 1, _sheetEquipH;
    private bool _sheetLabels;
    private readonly string[] _sheetLabel = new string[4];
    private readonly string[] _sheetValues = new string[4];
    private readonly string[] _sheetBonus = new string[4];   // « +5 » ; vide sans bonus d'équipement
    private readonly bool[] _sheetBoosted = new bool[4];
    private (int Move, bool Reposition, bool FreeAttack, int Range, int MinRange) _sheetGrid;
    private static readonly string[] StatIcons = { "pv", "deg", "dep", "tir" };
    private static readonly string[] StatKeys = { "stat.hp", "stat.power", "stat.movement", "stat.range" };
    private readonly List<(string Text, Color Color)> _sheetEquipLines = new();
    /// <summary>Lignes de la fiche (« NOM : fonctionnement », rappels) ; Gap = espace avant la ligne ; les Head
    /// premiers caractères (nom du trait) sont dessinés en HeadColor. Jamais de défilement : la fiche apparaît au
    /// survol, tout doit se lire d'un coup (interligne resserré si besoin, cf. FitSheetLines).</summary>
    private readonly List<(string Text, Color Color, bool Gap, int Head, Color HeadColor)> _sheetLines = new();
    /// <summary>Paragraphes de la fiche avant découpage (texte, couleur, longueur du nom en tête, sa couleur).</summary>
    private readonly List<(string Text, Color Color, int Head, Color HeadColor)> _sheetParas = new();
    private Rectangle _sheetZone;
    private int _sheetStep = 11, _sheetGap = SheetGapPx;
    // Texte trop long pour la zone : police en DEMI-TAILLE, rendue une fois dans _sheetSmall (1 pixel de glyphe =
    // 1 pixel d'écran) puis posée par la couche écran (NativeSprites) : nette, jamais d'échelle fractionnaire.
    private bool _sheetHalf, _sheetSmallDirty;
    private RenderTarget2D? _sheetSmall;

    /// <summary>Recalcule la fiche d'un pion seulement quand le sujet, la langue ou la mise en page change.</summary>
    private void RefreshUnitSheet(UnitClass cls, Equipment? eq, bool isCommander, Rectangle area)
    {
        var key = (cls.Asset, eq?.Id, isCommander ? 1 : 0, Loc.Current, _lay.Full, area);
        if (key == _sheetKey)
            return;
        _sheetKey = key;
        var full = _lay.Full;

        // Valeurs totales (classe + équipement) ; le bonus est rappelé à part en « +5 ».
        int[] baseV = { cls.MaxHp, cls.Damage, cls.MoveRange, cls.AttackRange };
        EquipStat[] stats = { EquipStat.Hp, EquipStat.Damage, EquipStat.MoveRange, EquipStat.AttackRange };
        for (var i = 0; i < 4; i++)
        {
            var bonus = eq?.BonusFor(stats[i]) ?? 0;
            _sheetValues[i] = Num(Math.Max(0, baseV[i] + bonus));
            _sheetBonus[i] = bonus == 0 ? "" : EquipmentNames.Signed(bonus);
            _sheetBoosted[i] = bonus != 0;
        }
        bool Has(string t) => cls.Traits.Contains(t) || (eq != null && eq.GrantsTrait(t));
        _sheetGrid = (cls.MoveRange + (eq?.BonusFor(EquipStat.MoveRange) ?? 0),
            Has(ChessArmy.Core.Battle.Trait.RepositionnementStrategique), Has(ChessArmy.Core.Battle.Trait.AttaqueLibre),
            cls.AttackRange + (eq?.BonusFor(EquipStat.AttackRange) ?? 0),
            Math.Max(cls.MinAttackRange, Has(ChessArmy.Core.Battle.Trait.ZoneMorte) ? 2 : 1));

        // Libellés : en entier s'ils tiennent tous, sinon aucun (pas de « PUIS… » à côté de « PV »).
        var colW = area.Right - SheetColX(area);
        _sheetValueScale = full ? 2 : 1;
        _sheetLabels = true;
        for (var i = 0; i < 4; i++)
        {
            _sheetLabel[i] = Loc.T(StatKeys[i]);
            if (32 + 6 + Font.Measure(_sheetLabel[i], 1) + 8 + StatValueW(i) + 6 > colW)
                _sheetLabels = false;
        }

        // Encart de l'équipement porté (toute la largeur) : nom en couleur de rareté puis son effet.
        _sheetEquipLines.Clear();
        _sheetEquipH = 0;
        var step = SheetStep;
        if (eq != null)
        {
            var textW = area.Width - 12 - 32 - 8;
            foreach (var line in WrapCached(EquipmentNames.Localized(eq).ToUpperInvariant(), textW))
                _sheetEquipLines.Add((line, PvpArt.RarityColor(eq.Rarity)));
            var effect = EquipEffectSummary(eq);
            if (effect.Length > 0)
                foreach (var line in WrapCached(effect, textW))
                    _sheetEquipLines.Add((line, Palette.White));
            _sheetEquipH = Math.Max(32, _sheetEquipLines.Count * step - 4) + 12;
        }

        // Fonctionnement de chaque trait (classe, puis équipement en doré), sur toute la largeur.
        var zoneY = SheetBodyY(area) + (_sheetEquipH > 0 ? _sheetEquipH + 8 : 0);
        _sheetZone = new Rectangle(area.X, zoneY, area.Width - 6, Math.Max(0, area.Bottom - zoneY));
        _sheetParas.Clear();
        _sheetReminders.Clear();
        var entries = TraitEntries(cls, eq);
        _sheetShown.Clear();
        foreach (var (_, trait, _) in entries)
            _sheetShown.Add(trait);
        foreach (var (kw, trait, fromEquip) in entries)
        {
            AddTraitLines(kw, trait, fromEquip ? Palette.Yellow2 : Palette.Cyan1);
            AddTraitReminders(trait);
        }
        FitSheetLines();
    }

    /// <summary>Largeur de « +5 » + valeur, calés à droite d'une ligne de stat.</summary>
    private int StatValueW(int i) =>
        Font.Measure(_sheetValues[i], _sheetValueScale) + (_sheetBonus[i].Length > 0 ? 4 + Font.Measure(_sheetBonus[i], 1) : 0);

    /// <summary>Effet d'un équipement en une ligne : bonus de stats, sinon ses traits.</summary>
    private static string EquipEffectSummary(Equipment eq)
    {
        var s = "";
        foreach (var e in eq.StatBonuses)
            s += (s.Length > 0 ? " · " : "") + StatEffectText(e);
        if (s.Length == 0)
            foreach (var t in eq.Traits)
                s += (s.Length > 0 ? " · " : "") + UnitKeywords.For(t).Label;
        return s;
    }

    /// <summary>« NOM DU TRAIT : fonctionnement complet » d'un seul tenant (valeurs du moteur), le nom en
    /// <paramref name="labelColor"/>, la description à sa suite sur la même ligne.</summary>
    private void AddTraitLines(UnitKeywords.Keyword kw, string trait, Color labelColor)
    {
        if (kw.Label.Length == 0)
            return;
        var desc = kw.Description.Length > 0 ? PvpUi.SentenceCase(TraitDescription(kw, trait)) : "";   // casse de phrase : plus lisible
        var text = desc.Length > 0 ? Loc.T("pvp.trait_line", kw.Label, desc) : kw.Label;
        var head = desc.Length > 0 ? text.Length - desc.Length : text.Length;   // nom + « : » en couleur du trait
        _sheetParas.Add((text, Palette.Blue1, head, labelColor));
    }

    /// <summary>Traits déjà expliqués sur la fiche en cours : un trait cité n'y est rappelé qu'une fois.</summary>
    private readonly HashSet<string> _sheetShown = new();

    /// <summary>Juste sous un trait dont la description en cite un autre (Aura de rempart → Rempart), rappel du
    /// trait cité, nom en <see cref="UnitKeywords.ReminderColor"/>, sauf s'il est déjà expliqué sur la fiche.
    /// Facultatif : retiré par <see cref="FitSheetLines"/> s'il ne tient pas tel quel.</summary>
    private void AddTraitReminders(string trait)
    {
        foreach (var r in ChessArmy.Core.Battle.Trait.ReferencedBy(trait))
            if (_sheetShown.Add(r))
            {
                _sheetReminders.Add(_sheetParas.Count);
                AddTraitLines(UnitKeywords.For(r), r, UnitKeywords.ReminderColor);
            }
    }

    /// <summary>Index (croissants) des paragraphes de rappel dans <see cref="_sheetParas"/>.</summary>
    private readonly List<int> _sheetReminders = new();

    /// <summary>Découpe les paragraphes de la fiche en lignes de <paramref name="width"/> px (échelle 1), en
    /// reportant sur chaque ligne la part du nom du trait qu'elle porte.</summary>
    private void LayoutSheetLines(int width)
    {
        _sheetLines.Clear();
        foreach (var (text, color, head, headColor) in _sheetParas)
        {
            var pos = 0;
            var first = true;
            foreach (var line in WrapCached(text, width))
            {
                var h = Math.Clamp(head - pos, 0, line.Length);
                _sheetLines.Add((line, color, first && _sheetLines.Count > 0, h, headColor));
                pos += line.Length + 1;   // l'espace avalé par la coupure
                first = false;
            }
        }
    }

    /// <summary>Traits d'une classe et de son équipement, dans l'ordre des cartes, sans doublon.</summary>
    private static List<(UnitKeywords.Keyword Kw, string Trait, bool FromEquip)> TraitEntries(UnitClass c, Equipment? equipment)
    {
        var list = new List<(UnitKeywords.Keyword, string, bool)>();
        void Add(UnitKeywords.Keyword kw, string trait, bool fromEquip)
        {
            if (kw.Label.Length == 0)
                return;
            foreach (var (k, _, _) in list)
                if (k.Label == kw.Label)
                    return;
            list.Add((kw, trait, fromEquip));
        }
        if (c.PiercesAllies && !c.Traits.Contains("Franchissement"))
            Add(UnitKeywords.PiercesAllies, "", false);
        foreach (var t in c.Traits)
            Add(UnitKeywords.For(t), t, false);
        if (c.MinAttackRange > 1)
            Add(UnitKeywords.DeadZone, "", false);
        if (equipment != null)
        {
            foreach (var t in equipment.Traits)
                Add(UnitKeywords.For(t), t, true);
            if (equipment.IsThrown)
                Add(UnitKeywords.Ephemeral, "", true);
        }
        return list;
    }

    /// <summary>Hauteur totale des lignes pour un interligne et un espace entre traits donnés.</summary>
    private int SheetLinesHeight(int step, int gap)
    {
        var h = 0;
        for (var i = 0; i < _sheetLines.Count; i++)
            h += (i > 0 ? step : Font.LineHeight(1)) + (_sheetLines[i].Gap && i > 0 ? gap : 0);
        return h;
    }

    // Interlignes essayés, du plus aéré au plus serré (extra sous la ligne, espace entre traits). Pas en dessous
    // de + 2 : à + 1, les jambages (g, p, q) touchent la ligne suivante.
    private static readonly (int Extra, int Gap)[] SheetSpacings = { (4, SheetGapPx), (3, 4), (3, 3), (2, 3), (2, 2) };

    /// <summary>
    /// Fait tenir tout le texte dans la zone, sans défilement : interligne resserré d'abord, puis, si ça ne suffit
    /// pas, police en DEMI-TAILLE (couche écran, cf. <see cref="_sheetSmall"/>) quand l'écran est au moins ×2. En
    /// dernier recours, la dernière ligne visible se termine par « … ».
    /// </summary>
    private void FitSheetLines()
    {
        _sheetHalf = false;
        LayoutSheetLines(_sheetZone.Width);
        if (PickSpacing(_sheetZone.Height))
            return;
        if (_sheetReminders.Count > 0)   // les rappels de traits cités ne valent pas une fiche resserrée : on les retire
        {
            for (var i = _sheetReminders.Count - 1; i >= 0; i--)
                _sheetParas.RemoveAt(_sheetReminders[i]);
            _sheetReminders.Clear();
            LayoutSheetLines(_sheetZone.Width);
            if (PickSpacing(_sheetZone.Height))
                return;
        }
        if (NativeSprites.Scale < 2)
            return;
        _sheetHalf = true;   // deux fois plus de place, en pixels d'écran
        _sheetSmallDirty = true;
        LayoutSheetLines(_sheetZone.Width * 2);
        PickSpacing(_sheetZone.Height * 2);
    }

    private bool PickSpacing(int height)
    {
        var lh = Font.LineHeight(1);
        foreach (var (extra, gap) in SheetSpacings)
        {
            _sheetStep = lh + extra;
            _sheetGap = gap;
            if (SheetLinesHeight(_sheetStep, _sheetGap) <= height)
                return true;
        }
        return false;
    }

    /// <summary>Lignes de la fiche, le nom de chaque trait en sa couleur, la description à sa suite. En demi-taille,
    /// la texture préparée passe par la couche écran (rien n'est dessiné tant qu'elle n'est pas prête).</summary>
    private void DrawSheetLines(SpriteBatch sb)
    {
        if (!_sheetHalf)
        {
            DrawSheetLinesIn(sb, _sheetZone);
            return;
        }
        if (_sheetSmall != null && !_sheetSmallDirty && NativeSprites.CanQueue && !_tutorialOpen && _error == null)
            NativeSprites.Queue(_sheetSmall, _sheetZone, Color.White);
    }

    /// <summary>Prépare la texture demi-taille de la fiche (appelé depuis Update, hors de tout lot de dessin).</summary>
    private void RenderSmallSheet()
    {
        if (!_sheetSmallDirty || !_sheetHalf || _sheetZone.Width <= 0 || _sheetZone.Height <= 0)
            return;
        _sheetSmallDirty = false;
        var gd = Context.GraphicsDevice;
        var w = _sheetZone.Width * 2;
        var h = _sheetZone.Height * 2;
        if (_sheetSmall == null || _sheetSmall.Width != w || _sheetSmall.Height != h)
        {
            _sheetSmall?.Dispose();
            _sheetSmall = new RenderTarget2D(gd, w, h);
        }
        gd.SetRenderTarget(_sheetSmall);
        gd.Clear(Color.Transparent);
        var sb = Context.SpriteBatch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawSheetLinesIn(sb, new Rectangle(0, 0, w, h));
        sb.End();
        gd.SetRenderTarget(null);
    }

    private void DrawSheetLinesIn(SpriteBatch sb, Rectangle z)
    {
        var lh = Font.LineHeight(1);
        var y = z.Y;
        for (var i = 0; i < _sheetLines.Count; i++)
        {
            var (text, color, gap, head, headColor) = _sheetLines[i];
            if (gap && i > 0)
                y += _sheetGap;
            if (y + lh > z.Bottom)
                break;
            var cut = i + 1 < _sheetLines.Count && y + _sheetStep + lh > z.Bottom;   // plus de place après : « … »
            if (cut)
                text = PvpArt.Fit(Font, text + "…", z.Width, 1);
            var x = z.X;
            head = Math.Min(head, text.Length);
            if (head > 0)
            {
                var h = text[..head];
                Font.Draw(sb, h, new Vector2(x, y), 1, headColor, preserveCase: true);   // noms de traits déjà en capitales
                x += Font.Measure(h, 1);
            }
            if (head < text.Length)
                Font.Draw(sb, text[head..], new Vector2(x, y), 1, color, preserveCase: true);
            if (cut)
                break;
            y += _sheetStep;
        }
    }

    private int PillWidth(string text) => Font.Measure(text, _lay.TextScale) + (_lay.Full ? 12 : 8);

    /// <summary>Coût en pastille <c>Black1</c>, calée à droite sur <paramref name="right"/>, centrée sur <paramref name="centerY"/>.</summary>
    private void CostPill(SpriteBatch sb, string text, int right, int centerY)
    {
        var s = _lay.TextScale;
        var h = Font.LineHeight(s) + (_lay.Full ? 10 : 6);
        var r = new Rectangle(right - PillWidth(text), centerY - h / 2, PillWidth(text), h);
        PvpArt.Fill(sb, Px, r, Palette.Black1);
        Font.DrawCentered(sb, text, r, s, Palette.Yellow2);
    }

    private static string StatEffectText(EquipEffect effect) =>
        Loc.T("equip.stat_bonus", EquipmentNames.Signed(effect.Amount), effect.Stat switch
        {
            EquipStat.Hp => Loc.T("stat.hp"),
            EquipStat.Damage => Loc.T("stat.power"),
            EquipStat.MoveRange => Loc.T("stat.movement"),
            EquipStat.AttackRange => Loc.T("stat.range"),
            _ => "",
        });

    private void DrawUnitSheet(SpriteBatch sb, Rectangle area, UnitClass cls, Domaine dom, Equipment? eq, bool isCommander)
    {
        RefreshUnitSheet(cls, eq, isCommander, area);
        var full = _lay.Full;
        var lh = Font.LineHeight(1);

        // À gauche : plateau 7×7 animé du jeu (tel quel), centré sur la hauteur des 4 stats.
        var board = new Rectangle(area.X, area.Y + (SheetHeadH - MoveAttackGrid.BoardOuter) / 2,
            MoveAttackGrid.BoardOuter, MoveAttackGrid.BoardOuter);
        var g = _sheetGrid;
        MoveAttackGrid.DrawBoardWithPawn(sb, Px, board, UnitFront(cls.Asset), revealed: true, dom, g.Move, g.Reposition,
            g.FreeAttack ? Domaine.Dame : cls.AttackDomaine ?? dom, g.Range, g.MinRange,
            allowNative: !_tutorialOpen && _error == null);

        // À droite : PV / PUISSANCE / MOUVEMENT / PORTÉE empilées (icône native, libellé gris, « +5 », valeur).
        var cx = SheetColX(area);
        var right = area.Right;
        for (var i = 0; i < 4; i++)
        {
            var top = area.Y + i * StatPitch;
            var row = new Rectangle(cx, top + (StatPitch - StatRowH) / 2, right - cx, StatRowH);
            PvpArt.Fill(sb, Px, row, Palette.Black3);
            if (Tex("Icons/stat_" + StatIcons[i]) is { } icon)
                sb.Draw(icon, new Vector2(cx, top + (StatPitch - icon.Height) / 2), Color.White);
            var ty = row.Y + (StatRowH - lh) / 2;
            if (_sheetLabels)
                Font.Draw(sb, _sheetLabel[i], new Vector2(cx + 32 + 6, ty), 1, Palette.Blue1);
            var vh = Font.LineHeight(_sheetValueScale);
            var vy = row.Y + (StatRowH - vh) / 2;
            var vx = right - (full ? 8 : 5) - Font.Measure(_sheetValues[i], _sheetValueScale);
            Font.Draw(sb, _sheetValues[i], new Vector2(vx, vy), _sheetValueScale, _sheetBoosted[i] ? Palette.Yellow2 : Palette.White);
            if (_sheetBonus[i].Length > 0)
                Font.Draw(sb, _sheetBonus[i], new Vector2(vx - 4 - Font.Measure(_sheetBonus[i], 1), vy + vh - lh), 1, Palette.Yellow2);
        }

        // Encart de l'équipement porté (fond Black3, bordure Brown1) : badge, nom en couleur de rareté, effet.
        if (eq != null)
        {
            var box = new Rectangle(area.X, SheetBodyY(area), area.Width, _sheetEquipH);
            PvpArt.Fill(sb, Px, box, Palette.Black3);
            PvpArt.Border(sb, Px, box, Palette.Brown1, 1);
            DrawBadge(sb, new Rectangle(box.X + 6, box.Y + 6, 32, 32), eq, 1f, false);
            var textH = _sheetEquipLines.Count * SheetStep - 4;
            var ty = box.Y + 6 + Math.Max(0, (32 - textH) / 2);
            foreach (var (text, color) in _sheetEquipLines)
            {
                Font.Draw(sb, text, new Vector2(box.X + 6 + 32 + 8, ty), 1, color);
                ty += SheetStep;
            }
        }
        DrawSheetLines(sb);
    }

    // ── Fiche d'un équipement ────────────────────────────────────────────────────
    //  Case 64×64 (background + icône ×2, liseré de rareté) | NOM, rareté, coût en pastille ;
    //  une ligne encadrée par effet (« PV  +4 », « TRAIT  Rempart ») ; fonctionnement du trait ; rappel.

    private string _eqName = "", _eqRarity = "", _eqCost = "";
    private int _eqNameScale = 1;
    private readonly List<(string Label, string Value)> _eqEffects = new();

    private int EquipEffectRowH => _lay.Full ? 28 : 20;

    private void RefreshEquipSheet(Rectangle area, Equipment eq, bool locked)
    {
        var key = ("#eq", (string?)eq.Id, locked ? 3 : 2, Loc.Current, _lay.Full, area);
        if (key == _sheetKey)
            return;
        _sheetKey = key;
        var full = _lay.Full;
        _eqCost = Num(PvpConfig.Current.CostOf(eq));
        var nameX = area.X + 1 + 64 + 9;
        var room = area.Right - nameX - PillWidth(_eqCost) - 6;
        _eqName = EquipmentNames.Localized(eq).ToUpperInvariant();
        _eqNameScale = PvpArt.ScaleToFit(Font, _eqName, room, full ? 2 : 1);
        _eqName = PvpArt.Fit(Font, _eqName, room, _eqNameScale);
        _eqRarity = PvpArt.Fit(Font, Loc.T(eq.Rarity switch
        {
            EquipmentRarity.Rare => "equip.rarity.rare",
            EquipmentRarity.Legendary => "equip.rarity.legendary",
            _ => "equip.rarity.common",
        }), area.Right - nameX, 1);

        _eqEffects.Clear();
        foreach (var e in eq.Effects)
        {
            if (e.Trait is { } tr)
                _eqEffects.Add((Loc.T("pvp.trait"), UnitKeywords.For(tr).Label));
            else
                _eqEffects.Add((StatLabel(e.Stat), EquipmentNames.Signed(e.Amount)));
        }
        for (var i = 0; i < _eqEffects.Count; i++)   // libellé et valeur tiennent dans la ligne encadrée
        {
            var (label, value) = _eqEffects[i];
            var valueW = Font.Measure(value, 1);
            value = PvpArt.Fit(Font, value, area.Width - 24 - Math.Min(valueW, area.Width / 2), 1);
            label = PvpArt.Fit(Font, label, area.Width - 24 - Font.Measure(value, 1) - 8, 1);
            _eqEffects[i] = (label, value);
        }

        var zoneY = area.Y + 72 + _eqEffects.Count * (EquipEffectRowH + 4) + (full ? 6 : 2);
        _sheetZone = new Rectangle(area.X, zoneY, area.Width - 6, Math.Max(0, area.Bottom - zoneY));
        _sheetParas.Clear();
        _sheetReminders.Clear();
        if (locked)
            _sheetParas.Add((Loc.T("pvp.locked_equip"), Palette.Purple5, 0, default));
        _sheetShown.Clear();
        foreach (var t in eq.Traits)
            _sheetShown.Add(t);
        foreach (var t in eq.Traits)
        {
            AddTraitLines(UnitKeywords.For(t), t, Palette.Cyan1);
            AddTraitReminders(t);
        }
        if (eq.IsThrown)
            AddTraitLines(UnitKeywords.Ephemeral, "", Palette.Cyan1);
        FitSheetLines();
    }

    private static string StatLabel(EquipStat stat) => Loc.T(stat switch
    {
        EquipStat.Hp => "stat.hp",
        EquipStat.Damage => "stat.power",
        EquipStat.MoveRange => "stat.movement",
        _ => "stat.range",
    });

    private void DrawEquipSheet(SpriteBatch sb, Rectangle area, Equipment eq, bool locked)
    {
        RefreshEquipSheet(area, eq, locked);
        var rarity = PvpArt.RarityColor(eq.Rarity);

        // Case 64×64 : background ×2 + icône ×2, liseré intérieur 2 px de rareté (comme les cartes).
        var sq = new Rectangle(area.X + 1, area.Y + 4, 64, 64);
        if (Tex("Equipment/background") is { } bg)
            sb.Draw(bg, sq, Color.White);
        if (EquipIcon(eq) is { } icon)
            sb.Draw(icon, new Rectangle(sq.Center.X - icon.Width, sq.Center.Y - icon.Height, icon.Width * 2, icon.Height * 2), Color.White);
        PvpArt.Border(sb, Px, sq, rarity, 2);
        if (locked)
            LockVeil(sb, sq);

        var nameX = sq.Right + 9;
        var nameH = Font.LineHeight(_eqNameScale);
        var lh = Font.LineHeight(1);
        var top = sq.Y + (64 - (nameH + 8 + lh)) / 2;
        Font.Draw(sb, _eqName, new Vector2(nameX, top), _eqNameScale, rarity);
        Font.Draw(sb, _eqRarity, new Vector2(nameX, top + nameH + 8), 1, rarity);
        CostPill(sb, _eqCost, area.Right, top + nameH / 2);

        // Une ligne encadrée par effet : libellé à gauche, valeur à droite en doré.
        var y = area.Y + 72;
        var rowH = EquipEffectRowH;
        foreach (var (label, value) in _eqEffects)
        {
            var r = new Rectangle(area.X, y, area.Width, rowH);
            PvpArt.Fill(sb, Px, r, Palette.Black3);
            PvpArt.Border(sb, Px, r, Palette.Black5, _lay.Full ? 2 : 1);
            var ty = r.Y + (rowH - lh) / 2;
            Font.Draw(sb, label, new Vector2(r.X + 10, ty), 1, Palette.Blue1);
            Font.Draw(sb, value, new Vector2(r.Right - 10 - Font.Measure(value, 1), ty), 1, Palette.Yellow2);
            y += rowH + 4;
        }
        DrawSheetLines(sb);
    }

    // ── Bande du bas ─────────────────────────────────────────────────────────────

    private void DrawBand(SpriteBatch sb)
    {
        var L = _lay;
        var full = L.Full;
        PvpArt.Fill(sb, Px, L.Band, Palette.Black1);
        PvpArt.Fill(sb, Px, new Rectangle(0, L.Band.Y, L.W, 2), Palette.Black5);

        var friendOn = _friend;
        var friendEnabled = _mode == SearchMode.Idle && !Locked;
        PvpArt.Button(sb, Px, L.Friend, friendOn, friendEnabled && Hovered(L.Friend) && !friendOn, friendEnabled || friendOn);
        Font.DrawCentered(sb, PvpArt.Fit(Font, Loc.T("pvp.challenge"), L.Friend.Width - 8, 1), L.Friend, 1,
            friendOn ? Palette.Yellow2 : friendEnabled ? Palette.White : Palette.Grey);

        var c = L.Center;
        if (Locked)
            DrawBandFound(sb, c);
        else if (_mode != SearchMode.Idle)
            DrawBandSearching(sb, c);
        else
            DrawBandBudget(sb, c);

        if (_mode != SearchMode.Idle && !Locked)
        {
            PvpArt.Button(sb, Px, L.Cancel, false, Hovered(L.Cancel));
            Font.DrawCentered(sb, Loc.T("pvp.cancel"), L.Cancel, full ? 2 : 1, Palette.White);
        }
        DrawLaunch(sb);
    }

    private (int Cost, int Budget, int Equip, int EquipBudget, Language Lang) _budgetKey = (-1, -1, -1, -1, Language.Francais);
    private string _budgetText = "", _equipText = "";

    private void DrawBandBudget(SpriteBatch sb, Rectangle c)
    {
        var full = _lay.Full;
        var cost = PvpCatalog.Cost(Build);
        var budget = PvpConfig.Current.Budget;
        var equip = PvpCatalog.EquipmentCost(Build);
        var equipBudget = PvpConfig.Current.EquipmentBudget;
        if (_budgetKey != (cost, budget, equip, equipBudget, Loc.Current))
        {
            _budgetKey = (cost, budget, equip, equipBudget, Loc.Current);
            _budgetText = Loc.T("pvp.army_cost", cost, budget);
            _equipText = Loc.T("pvp.army_cost", equip, equipBudget);   // budget d'équipement, séparé
        }
        var top = c.Y + (full ? 16 : 10);
        // Deux barres côte à côte : armée (2/3 de la zone) puis équipement, plus petite (1/3).
        var gap = full ? 24 : 16;
        var armyRight = c.X + (c.Width - gap) * 2 / 3;
        DrawBudgetBar(sb, c.X, armyRight, top, Loc.T("pvp.budget_army"), _budgetText, cost, budget);
        DrawBudgetBar(sb, armyRight + gap, c.Right, top, Loc.T("pvp.budget_equip"), _equipText, equip, equipBudget);

        // Ligne d'aide / d'erreur.
        string help;
        var color = Palette.Grey;
        if (_messageTime > 0f && _message.Length > 0)
        {
            help = _message;
            color = _messageError ? Palette.Purple5 : Palette.Grey;
        }
        else if (_held.Active)
            help = Loc.T(Context.Input.UsingGamepad ? "pvp.holding_pad" : "pvp.holding_mouse");
        else
            help = Loc.T(Context.Input.UsingGamepad ? "pvp.band_help_pad" : "pvp.band_help");
        Font.Draw(sb, PvpArt.Fit(Font, help, c.Width, 1), new Vector2(c.X, top + (full ? 28 : 20)), 1, color, preserveCase: true);
    }

    /// <summary>Libellé + barre + « utilisé/budget » entre x et right ; rouge en dépassement.</summary>
    private void DrawBudgetBar(SpriteBatch sb, int x, int right, int top, string label, string value, int used, int budget)
    {
        var full = _lay.Full;
        var vs = full ? 2 : 1;
        Font.Draw(sb, label, new Vector2(x, top + (14 - Font.LineHeight(1)) / 2), 1, Palette.Blue1);
        var valueW = Font.Measure(value, vs);
        var barX = x + Font.Measure(label, 1) + 8;
        var bar = new Rectangle(barX, top + (full ? 0 : 2), Math.Max(12, right - valueW - 8 - barX), full ? 14 : 10);
        PvpArt.Fill(sb, Px, bar, Palette.Black3);
        PvpArt.Border(sb, Px, bar, Palette.Black5, 1);
        var over = used > budget;
        var fillW = budget <= 0 ? 0 : (int)((long)(bar.Width - 2) * Math.Min(used, budget) / budget);
        PvpArt.Fill(sb, Px, new Rectangle(bar.X + 1, bar.Y + 1, fillW, bar.Height - 2), over ? Palette.Purple5 : Palette.Brown3);
        Font.Draw(sb, value, new Vector2(right - valueW, top + (14 - Font.LineHeight(vs)) / 2), vs, over ? Palette.Purple5 : Palette.White);
    }

    private int _searchSecond = -1;
    private string _searchClock = "";
    private string SearchClock
    {
        get
        {
            var s = (int)_searchTime;
            if (s != _searchSecond)
            {
                _searchSecond = s;
                _searchClock = PvpUi.Clock(s);
            }
            return _searchClock;
        }
    }

    private void DrawBandSearching(SpriteBatch sb, Rectangle c)
    {
        var full = _lay.Full;
        var room = c.Width - (_lay.Launch.X - _lay.Cancel.X) - 24;
        var blink = (int)(_time * 2f) % 2 == 0;
        var sq = full ? 10 : 8;
        var lineScale = full ? 2 : 1;
        var top = c.Y + (full ? 20 : 12);
        PvpArt.Fill(sb, Px, new Rectangle(c.X, top + (Font.LineHeight(lineScale) - sq) / 2, sq, sq), blink ? Palette.Cyan1 : Palette.Black5);
        string line1, line2;
        if (_mm is { Failed: true })
        {
            line1 = Loc.T(SteamOn ? "pvp.join_failed" : "pvp.no_steam");
            line2 = "";
        }
        else switch (_mode)
        {
            case SearchMode.FriendHost:
                line1 = Loc.T("pvp.wait_friend_s");
                line2 = Loc.T("pvp.overlay_open");
                break;
            case SearchMode.Join:
                line1 = Loc.T("pvp.joining_s");
                line2 = "";
                break;
            default:
                line1 = Loc.T("pvp.searching_s");
                var left = PvpBotFallback.SecondsLeft(_mm?.SearchTime ?? _searchTime, PvpConfig.Current.BotAfterSeconds);
                line2 = Loc.T("pvp.bot_in_s", left);
                break;
        }
        var tx = c.X + sq + 12;
        var tw = room - sq - 12;
        var s1 = PvpArt.ScaleToFit(Font, line1, tw, lineScale);
        Font.Draw(sb, PvpArt.Fit(Font, line1, tw, s1), new Vector2(tx, top), s1, Palette.White, preserveCase: true);
        if (line2.Length > 0)
            Font.Draw(sb, PvpArt.Fit(Font, line2, tw, 1), new Vector2(tx, top + Font.LineHeight(lineScale) + 6), 1, Palette.Blue1, preserveCase: true);
    }

    private void DrawBandFound(SpriteBatch sb, Rectangle c)
    {
        var full = _lay.Full;
        var name = _session?.OpponentName ?? _botMatch?.DisplayName ?? "";
        var rating = _session?.OpponentRating ?? _botMatch?.OpponentRating ?? Profile.Rating;
        var pill = Loc.T("pvp.opponent", name) + "  " + PvpUi.RankName(rating);
        var top = c.Y + (full ? 14 : 8);
        var ph = Font.LineHeight(1) + (full ? 12 : 8);
        var pw = Math.Min(c.Width, Font.Measure(pill, 1) + 20);
        var r = new Rectangle(c.X, top, pw, ph);
        PvpArt.Fill(sb, Px, r, Palette.Black3);
        PvpArt.Border(sb, Px, r, Palette.Cyan1, 2);
        Font.DrawCentered(sb, PvpArt.Fit(Font, pill, pw - 12, 1), r, 1, Palette.White);
        var sub = _countdown > 0f
            ? Loc.T("pvp.fight_in", Math.Max(1, (int)Math.Ceiling(_countdown)))
            : Loc.T("pvp.waiting_army");
        Font.Draw(sb, PvpArt.Fit(Font, sub, c.Width, 1), new Vector2(c.X, r.Bottom + (full ? 8 : 5)), 1, Palette.Cyan1, preserveCase: true);
    }

    private void DrawLaunch(SpriteBatch sb)
    {
        var L = _lay;
        var r = L.Launch;
        var full = L.Full;
        var hover = Hovered(r);
        string title, sub;
        Color titleColor, subColor;
        var dy = 0;
        if (Locked)
        {
            PvpArt.Fill(sb, Px, new Rectangle(r.X, r.Bottom, r.Width, 4), Palette.Blue3);
            PvpArt.Fill(sb, Px, r, Palette.Cyan1);
            PvpArt.Border(sb, Px, r, Palette.Yellow2, 3);
            title = Loc.T("pvp.found_btn");
            sub = _countdown > 0f ? Loc.T("pvp.fight_in", Math.Max(1, (int)Math.Ceiling(_countdown))) : Loc.T("pvp.waiting_army");
            titleColor = subColor = Palette.Black1;
        }
        else if (_mode == SearchMode.Search || _mode == SearchMode.Join)
        {
            PvpArt.Fill(sb, Px, new Rectangle(r.X, r.Bottom, r.Width, 4), Palette.Black1);
            PvpArt.Fill(sb, Px, r, Palette.Black5);
            PvpArt.Border(sb, Px, r, Palette.Cyan1, 3);
            title = _mode == SearchMode.Join ? Loc.T("pvp.waiting") : Loc.T("pvp.search_btn", SearchClock);
            sub = Loc.T("pvp.search_hint");
            titleColor = Palette.White;
            subColor = Palette.Blue1;
        }
        else if (_mode == SearchMode.FriendHost)
        {
            dy = PvpArt.Primary(sb, Px, r, hover, hover && Context.Input.IsLeftDown);
            title = Loc.T("pvp.waiting");
            sub = Loc.T("pvp.invite_again");
            titleColor = subColor = Palette.Black1;
        }
        else if (LaunchProblem() is { } problem)
        {
            PvpArt.PrimaryDisabled(sb, Px, r);
            title = Loc.T(_friend ? "pvp.invite" : "pvp.launch");
            sub = problem;
            titleColor = Palette.Grey;
            subColor = Palette.Purple5;
        }
        else
        {
            dy = PvpArt.Primary(sb, Px, r, hover, hover && Context.Input.IsLeftDown);
            title = Loc.T(_friend ? "pvp.invite" : "pvp.launch");
            sub = LaunchSubtitle();
            titleColor = subColor = Palette.Black1;
        }
        var ts = PvpArt.ScaleToFit(Font, title, r.Width - 16, full ? 3 : 2);
        var th = Font.LineHeight(ts);
        var sh = Font.LineHeight(1);
        var top = r.Y + dy + (r.Height - th - (full ? 6 : 4) - sh) / 2;
        Font.DrawCentered(sb, title, new Rectangle(r.X, top, r.Width, th), ts, titleColor);
        Font.DrawCentered(sb, PvpArt.Fit(Font, sub, r.Width - 12, 1), new Rectangle(r.X, top + th + (full ? 6 : 4), r.Width, sh), 1, subColor,
            preserveCase: char.IsLower(sub.Length > 0 ? sub[0] : 'A'));
    }

    private (int Tab, string Cmd, Language Lang) _launchKey;
    private string _launchSub = "";

    private string LaunchSubtitle()
    {
        var key = (_tab, Build.CommanderId, Loc.Current);
        if (key != _launchKey)
        {
            _launchKey = key;
            _launchSub = Loc.T("pvp.army_n", _tab + 1) + " · " + PvpUi.CommanderName(Build.CommanderId);
        }
        return _launchSub;
    }

    // ── Fenêtres ─────────────────────────────────────────────────────────────────

    private void DrawError(SpriteBatch sb, string text)
    {
        var L = _lay;
        PvpArt.Fill(sb, Px, new Rectangle(0, 0, L.W, L.H), Palette.Black1 * 0.8f);
        var w = Math.Min(L.W - 40, Font.Measure(text, 1) + 60);
        var box = new Rectangle((L.W - w) / 2, L.H / 2 - 30, w, 60);
        PvpArt.Panel(sb, Px, box);
        Font.DrawCentered(sb, PvpArt.Fit(Font, text, w - 20, 1), box, 1, Palette.Purple5);
    }
}
