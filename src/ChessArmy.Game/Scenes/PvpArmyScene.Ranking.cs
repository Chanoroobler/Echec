using System;
using System.Collections.Generic;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.UI;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Vue CLASSEMENT de l'écran Armée (docs/multijoueur-classement.md, même barre du haut) :
///  • MON RANG : icône ×8, rang, cote, place mondiale, progression vers la division suivante, 8 statistiques ;
///  • PALIERS : les 5 paliers et leurs plages de cote (pvp.json), le palier courant en évidence ;
///  • classement Steam : onglets MONDIAL (8 par page) / AMIS / AUTOUR DE MOI, ma ligne épinglée si absente de la
///    page ; au survol d'une ligne, les deux lignes de pions de sa dernière formation classée ;
///  • DERNIÈRES PARTIES : les 4 plus récentes, onglets CLASSÉES / DÉFIS (entre amis).
/// Le panneau du classement a une hauteur FIXE quel que soit l'onglet. Textes mis en cache (rien de formaté à
/// chaque image), relus quand les données changent et une fois par seconde (noms Steam, « il y a 3 min »).
/// </summary>
public sealed partial class PvpArmyScene
{
    private const int RankIconSmallW = PvpRankIcon.W * 2, RankIconGap = 6;
    private const int RankRows = 8;
    private const int HistRows = 4;
    private const int RefWidth = 834;   // largeur d'une ligne de la maquette : colonnes en proportion

    private enum RankTab { World, Friends, Around }

    private PvpRankIcon? _rankIconTex;
    private PvpRankIcon RankIcon => _rankIconTex ??= new PvpRankIcon(Context.GraphicsDevice);

    private RankTab _rankTab;
    private int _rankPage;
    private bool _histFriendly;

    // ── Mise en page ─────────────────────────────────────────────────────────────

    private sealed class RankLay
    {
        public Rectangle Mine, Tiers, Board, Hist;
        public Rectangle MineIn, TiersIn;
        public Rectangle Icon;            // icône ×8
        public readonly Rectangle[] Tabs = new Rectangle[3];
        public Rectangle Prev, Next, Counter;
        public int ColHeadY;
        public readonly Rectangle[] Rows = new Rectangle[RankRows];
        public Rectangle Pinned, ListArea;
        public int SepY;
        public Rectangle HistTitle;
        public readonly Rectangle[] HistTabs = new Rectangle[2];
        public readonly Rectangle[] HistRowRects = new Rectangle[HistRows];
        public int StatH, StatGap, TierRowH, TierPitch;
    }

    private readonly RankLay _rl = new();

    private void RankingLayout()
    {
        var L = _lay;
        var R = _rl;
        var full = L.Full;
        var compW = full ? 1280 : 960;
        var ox = Math.Max(0, (L.W - compW) / 2);
        var oy = Math.Max(0, (L.H - (full ? 720 : 540)) / 2);
        var pad = full ? 16 : 8;
        var x0 = ox + pad;
        var y0 = L.Top.Bottom + pad + oy;
        var bottom = L.H - oy - pad;
        var leftW = full ? 360 : 264;
        var rightX = x0 + leftW + pad;
        var rightW = compW - 3 * pad - leftW;

        var mineH = full ? 352 : 290;
        R.Mine = new Rectangle(x0, y0, leftW, mineH);
        R.Tiers = new Rectangle(x0, R.Mine.Bottom + pad, leftW, bottom - R.Mine.Bottom - pad);
        var boardH = full ? 406 : 312;
        R.Board = new Rectangle(rightX, y0, rightW, boardH);
        R.Hist = new Rectangle(rightX, R.Board.Bottom + pad, rightW, bottom - R.Board.Bottom - pad);

        var inset = PvpArt.PanelFrame + (full ? 16 : 8);
        R.MineIn = Inset(R.Mine, inset);
        R.TiersIn = Inset(R.Tiers, inset);
        var lh1 = Font.LineHeight(1);
        R.Icon = new Rectangle(R.MineIn.X, R.MineIn.Y + lh1 + (full ? 19 : 8), PvpRankIcon.W * 8, PvpRankIcon.H * 8);
        R.StatH = full ? 30 : 22;
        R.StatGap = full ? 6 : 4;
        R.TierRowH = full ? 36 : 26;
        R.TierPitch = full ? 42 : 28;

        // Classement : onglets, pagination, en-tête des colonnes, 8 lignes, filet, ma ligne épinglée.
        var bx = R.Board.X + PvpArt.PanelFrame + (full ? 14 : 8);
        var bw = R.Board.Right - PvpArt.PanelFrame - (full ? 14 : 8) - bx;
        var ty = R.Board.Y + (full ? 15 : 9);
        var tabH = full ? 32 : 22;
        var ts = full ? 2 : 1;
        var tabPad = full ? 10 : 6;
        var tx = bx;
        for (var i = 0; i < 3; i++)
        {
            var w = Font.Measure(Loc.T(TabKey(i)), ts) + 2 * tabPad;
            R.Tabs[i] = new Rectangle(tx, ty, w, tabH);
            tx += w + 4;
        }
        var counterW = Font.Measure("00/00", 1) + (full ? 16 : 8);
        R.Next = new Rectangle(bx + bw - tabH, ty, tabH, tabH);
        R.Counter = new Rectangle(R.Next.X - counterW, ty, counterW, tabH);
        R.Prev = new Rectangle(R.Counter.X - tabH, ty, tabH, tabH);
        // Langue verbeuse : les onglets se partagent la place laissée par la pagination.
        var maxTabs = R.Prev.X - (full ? 8 : 4) - bx;
        if (R.Tabs[2].Right - bx > maxTabs)
        {
            var w = (maxTabs - 8) / 3;
            for (var i = 0; i < 3; i++)
                R.Tabs[i] = new Rectangle(bx + i * (w + 4), ty, w, tabH);
        }
        R.ColHeadY = ty + tabH + (full ? 12 : 6);
        var rowH = full ? 28 : 26;
        var pitch = rowH + (full ? 6 : 2);
        var rowsY = R.ColHeadY + lh1 + (full ? 10 : 5);
        for (var k = 0; k < RankRows; k++)
            R.Rows[k] = new Rectangle(bx, rowsY + k * pitch, bw, rowH);
        R.SepY = rowsY + RankRows * pitch + (full ? 3 : 2);
        R.Pinned = new Rectangle(bx, R.SepY + (full ? 10 : 6), bw, rowH);
        R.ListArea = new Rectangle(bx, rowsY, bw, R.Pinned.Bottom - rowsY);
        // Police plus haute (chinois) : le cadre grandit jusqu'à contenir la ligne épinglée, au détriment des
        // dernières parties (même hauteur pour les trois onglets).
        var need = R.Pinned.Bottom + PvpArt.PanelFrame + (full ? 10 : 6) - R.Board.Y;
        if (need > R.Board.Height)
        {
            R.Board.Height = need;
            R.Hist = new Rectangle(rightX, R.Board.Bottom + pad, rightW, bottom - R.Board.Bottom - pad);
        }

        // Dernières parties : titre à gauche, onglets CLASSÉES / DÉFIS à droite, 4 lignes.
        var hx = R.Hist.X + PvpArt.PanelFrame + (full ? 14 : 8);
        var hw = R.Hist.Right - PvpArt.PanelFrame - (full ? 14 : 8) - hx;
        var hy = R.Hist.Y + (full ? 15 : 9);
        var htH = full ? 24 : 18;
        var hPad = full ? 10 : 6;
        var w1 = Font.Measure(Loc.T("pvp.hist_friendly"), 1) + 2 * hPad;
        var w0 = Font.Measure(Loc.T("pvp.hist_ranked"), 1) + 2 * hPad;
        R.HistTabs[1] = new Rectangle(hx + hw - w1, hy, w1, htH);
        R.HistTabs[0] = new Rectangle(R.HistTabs[1].X - 4 - w0, hy, w0, htH);
        R.HistTitle = new Rectangle(hx, hy, R.HistTabs[0].X - 8 - hx, htH);
        var hRowH = full ? 32 : 26;
        var hPitch = hRowH + (full ? 6 : 2);
        var hRowsY = hy + htH + (full ? 6 : 5);
        for (var k = 0; k < HistRows; k++)
            R.HistRowRects[k] = new Rectangle(hx, hRowsY + k * hPitch, hw, hRowH);
    }

    private static Rectangle Inset(Rectangle r, int d) => new(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d);

    private static string TabKey(int i) => i switch { 0 => "pvp.tab_world", 1 => "pvp.tab_friends", _ => "pvp.tab_around" };

    /// <summary>Abscisse d'une colonne : position de la maquette (sur 834 px) ramenée à la largeur de la ligne.</summary>
    private static int Col(Rectangle row, int refX) => row.X + refX * row.Width / RefWidth;

    // ── Ouverture / fermeture ────────────────────────────────────────────────────

    private void OpenRanking()
    {
        if (_view == View.Ranking)
            return;
        CancelHeld();
        _view = View.Ranking;
        _focus.Index = 0;
        _rankPage = 0;
        _rowKey = default;
        PvpLeaderboardService.Refresh();
        Context.Sounds.Play("menu_click");
    }

    private void CloseRanking()
    {
        _view = View.Build;
        _focus.Index = 0;
        Context.Sounds.Play("menu_close");
    }

    private void AddRankingTargets()
    {
        RankingLayout();
        var R = _rl;
        for (var i = 0; i < 3; i++)
            Add(R.Tabs[i], Hit.RankTab, i);
        if (_rankTab == RankTab.World && ShowList)
        {
            Add(R.Prev, Hit.RankPrev);
            Add(R.Next, Hit.RankNext);
        }
        Add(R.HistTabs[0], Hit.HistTab, 0);
        Add(R.HistTabs[1], Hit.HistTab, 1);
        RefreshRowText();
        for (var k = 0; k < _rowCount; k++)
            Add(RowRect(k), Hit.RankRow, k, focusable: false);
    }

    private void UpdateRanking()
    {
        var input = Context.Input;
        _focus.Navigate(input);
        _pointer = _focus.Pointer(input);
#if DEBUG
        if (DebugPointer is { } forced) _pointer = forced;
#endif
        _hover = HitIndexAt(_pointer);

        if (input.WasKeyPressed(Keys.Escape) || input.WasCancelPressed)
        {
            CloseRanking();
            return;
        }
        if (input.WasLeftShoulderPressed)
            SetRankTab((RankTab)(((int)_rankTab + 2) % 3));
        else if (input.WasRightShoulderPressed)
            SetRankTab((RankTab)(((int)_rankTab + 1) % 3));
        if (input.ScrollDelta != 0 && _rankTab == RankTab.World && _rl.ListArea.Contains(_pointer))
            TurnRankPage(-Math.Sign(input.ScrollDelta));

        var clicked = input.UsingGamepad
            ? (input.WasConfirmPressed && _hover >= 0 ? _targets[_hover] : (Target?)null)
            : (input.WasLeftClicked ? HitAt(input.MousePosition) : null);
        if (clicked is not { } t)
            return;
        switch (t.Kind)
        {
            case Hit.RankTab: SetRankTab((RankTab)t.Index); break;
            case Hit.RankPrev: TurnRankPage(-1); break;
            case Hit.RankNext: TurnRankPage(1); break;
            case Hit.HistTab:
                if (_histFriendly != (t.Index == 1))
                {
                    _histFriendly = t.Index == 1;
                    _histKey = default;
                    Context.Sounds.Play("menu_click");
                }
                break;
            case Hit.Chests: OpenChests(); break;
            case Hit.Back: CloseRanking(); break;
        }
    }

    private void SetRankTab(RankTab tab)
    {
        if (_rankTab == tab)
            return;
        _rankTab = tab;
        _rowKey = default;
        Context.Sounds.Play("menu_click");
    }

    private int RankPages => Math.Max(1, (PvpLeaderboardService.Top.Count + RankRows - 1) / RankRows);

    private void TurnRankPage(int dir)
    {
        var page = Math.Clamp(_rankPage + dir, 0, RankPages - 1);
        if (page == _rankPage)
            return;
        _rankPage = page;
        _rowKey = default;
        Context.Sounds.Play("menu_click");
    }

    // ── Lignes du classement (textes en cache) ───────────────────────────────────

    private struct RowText
    {
        public PvpLeaderboardEntry Entry;
        public bool Mine;
        public string TierId;
        public string Place, Name, Rank, Rating, Record, Pct;
    }

    private readonly RowText[] _rows = new RowText[RankRows + 1];
    private int _rowCount;          // lignes affichées, ma ligne épinglée comprise (dernière)
    private bool _pinned;
    private string? _listMessage;
    private (RankTab Tab, int Page, object? List, PvpLeaderboardService.LoadState State, Language Lang, bool Full, int Second) _rowKey;

    private bool ShowList => PvpLeaderboardService.State == PvpLeaderboardService.LoadState.Ready || PvpLeaderboardService.Entries.Count > 0;

    private IReadOnlyList<PvpLeaderboardEntry> TabList => _rankTab switch
    {
        RankTab.World => PvpLeaderboardService.Top,
        RankTab.Friends => PvpLeaderboardService.Friends,
        _ => PvpLeaderboardService.Around,
    };

    private Rectangle RowRect(int k) => _pinned && k == _rowCount - 1 ? _rl.Pinned : _rl.Rows[k];

    private void RefreshRowText()
    {
        var state = PvpLeaderboardService.State;
        var list = TabList;
        _rankPage = Math.Clamp(_rankPage, 0, RankPages - 1);
        var key = (_rankTab, _rankPage, (object?)list, state, Loc.Current, _lay.Full, (int)(Environment.TickCount64 / 1000));
        if (key == _rowKey)
            return;
        _rowKey = key;
        _rowCount = 0;
        _pinned = false;
        _listMessage = state switch
        {
            PvpLeaderboardService.LoadState.NoSteam => Loc.T("pvp.ranking_offline"),
            _ when list.Count > 0 => null,
            PvpLeaderboardService.LoadState.Loading => Loc.T("pvp.ranking_loading"),
            PvpLeaderboardService.LoadState.Failed => Loc.T("pvp.ranking_failed"),
            _ => Loc.T("pvp.ranking_empty"),
        };
        if (_listMessage != null)
            return;

        var mine = PvpLeaderboardService.Mine;
        int first, count;
        if (_rankTab == RankTab.World)
            (first, count) = (_rankPage * RankRows, Math.Min(RankRows, list.Count - _rankPage * RankRows));
        else if (_rankTab == RankTab.Friends)
            (first, count) = (0, Math.Min(RankRows, list.Count));
        else
        {
            var me = IndexOfMine(list);
            if (me < 0)
            {
                _listMessage = Loc.T("pvp.ranking_unranked");
                return;
            }
            first = Math.Clamp(me - 3, 0, Math.Max(0, list.Count - RankRows));   // 3 au-dessus, moi, 4 en dessous
            count = Math.Min(RankRows, list.Count - first);
        }
        var mineShown = false;
        for (var k = 0; k < count; k++)
        {
            var e = list[first + k];
            mineShown |= PvpLeaderboardService.IsLocal(e);
            _rows[_rowCount++] = MakeRow(e);
        }
        if (!mineShown && mine != null && _rankTab != RankTab.Around)
        {
            _rows[_rowCount++] = MakeRow(mine);
            _pinned = true;
        }
    }

    private static int IndexOfMine(IReadOnlyList<PvpLeaderboardEntry> list)
    {
        for (var i = 0; i < list.Count; i++)
            if (PvpLeaderboardService.IsLocal(list[i]))
                return i;
        return -1;
    }

    private RowText MakeRow(PvpLeaderboardEntry e)
    {
        var full = _lay.Full;
        var s = full ? 2 : 1;
        var mine = PvpLeaderboardService.IsLocal(e);
        var rank = PvpRanks.FromRating(e.Rating);
        var games = e.Wins + e.Losses + e.Draws;
        var row = _rl.Rows[0];
        var nameRoom = Col(row, 392) - Col(row, 80) - (full ? 12 : 6);
        var raw = PvpLeaderboardService.NameOf(e);
        var name = mine ? $"{raw} ({Loc.T("pvp.you")})" : raw;
        return new RowText
        {
            Entry = e,
            Mine = mine,
            TierId = rank.Id,
            Place = "#" + e.GlobalRank,
            Name = PvpArt.Fit(Font, name, nameRoom, s),
            Rank = PvpUi.RankName(rank),
            Rating = e.Rating.ToString(),
            Record = Loc.T("pvp.vdn", e.Wins, e.Losses, e.Draws),
            Pct = (games > 0 ? (int)Math.Round(100.0 * e.Wins / games) : 0) + "%",
        };
    }

    // ── Textes de MON RANG et de l'historique (en cache) ─────────────────────────

    private (int Rating, int Games, int Best, int Streak, int Week, int Place, Language Lang, bool Full) _mineKey = (-1, 0, 0, 0, 0, 0, Language.Francais, false);
    private string _mRank = "", _mRating = "", _mPlace = "", _mTowards = "", _mProgress = "";
    private readonly string[] _mStatLabel = new string[8];
    private readonly string[] _mStatValue = new string[8];
    private readonly bool[] _mStatHot = new bool[8];
    private int _mDone, _mNeeded;
    private bool _mHasNext;
    private readonly string[] _tierRange = new string[8];
    private readonly string[] _tierName = new string[8];

    private void RefreshMineText()
    {
        var p = Profile;
        var place = PvpLeaderboardService.Mine?.GlobalRank ?? 0;
        var week = p.WeekDelta(DateTime.UtcNow);
        var key = (p.Rating, p.Games, p.BestRating, p.Streak, week, place, Loc.Current, _lay.Full);
        if (key == _mineKey)
            return;
        _mineKey = key;
        _mRank = PvpUi.RankName(p.Rank);
        _mRating = Loc.T("pvp.rating_short", p.Rating);
        _mPlace = place > 0 ? Loc.T("pvp.world_place", place) : "";
        var (next, done, needed) = PvpRanks.Progress(p.Rating);
        _mHasNext = next != null;
        _mDone = done;
        _mNeeded = needed;
        _mTowards = next is { } n ? Loc.T("pvp.towards", PvpUi.RankName(n)) : "";
        _mProgress = next != null ? $"{done} / {needed}" : "";

        var rate = p.Games > 0 ? (int)Math.Round(100.0 * p.Wins / p.Games) : 0;
        SetStat(0, "pvp.stat_games", p.Games.ToString(), false);
        SetStat(1, "pvp.stat_wins", p.Wins.ToString(), false);
        SetStat(2, "pvp.stat_losses", p.Losses.ToString(), false);
        SetStat(3, "pvp.stat_draws", p.Draws.ToString(), false);
        SetStat(4, "pvp.stat_rate", rate + "%", false);
        SetStat(5, "pvp.stat_streak", p.Streak > 0 ? Loc.T("pvp.streak_win", p.Streak)
            : p.Streak < 0 ? Loc.T("pvp.streak_loss", -p.Streak) : "0", p.Streak != 0);
        SetStat(6, "pvp.stat_best", p.BestRating.ToString(), false);
        SetStat(7, "pvp.stat_week", week > 0 ? "+" + week : week.ToString(), week != 0);

        var ranks = PvpConfig.Current.Ranks;
        for (var i = 0; i < ranks.Count && i < _tierRange.Length; i++)
        {
            var (from, to) = PvpRanks.Range(i);
            _tierRange[i] = to is { } t ? $"{from} - {t}" : $"{from} +";
            _tierName[i] = i == p.Rank.TierIndex ? _mRank : Loc.T("pvp.rank." + ranks[i].Id);
        }
    }

    private void SetStat(int i, string key, string value, bool hot)
    {
        _mStatLabel[i] = Loc.T(key);
        _mStatValue[i] = value;
        _mStatHot[i] = hot;
    }

    private struct HistText
    {
        public PvpGameRecord Game;
        public string Result, Name, RankLine, Kills, Delta, When, TierId;
        public Color ResultColor, DeltaColor;
    }

    private readonly HistText[] _hist = new HistText[HistRows];
    private int _histCount;
    private (object? List, int Count, long Newest, Language Lang, bool Full, long Minute) _histKey;

    private void RefreshHistText()
    {
        var list = _histFriendly ? Profile.FriendlyHistory : Profile.RankedHistory;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var key = ((object?)list, list.Count, list.Count > 0 ? list[0].When : 0, Loc.Current, _lay.Full, now / 60);
        if (key == _histKey)
            return;
        _histKey = key;
        _histCount = Math.Min(HistRows, list.Count);
        var full = _lay.Full;
        var row = _rl.HistRowRects[0];
        var nameRoom = Col(row, 296) - Col(row, 126) - (full ? 12 : 6);
        for (var k = 0; k < _histCount; k++)
        {
            var g = list[k];
            var rank = PvpRanks.FromRating(g.OpponentRating);
            var room = g.Bot ? nameRoom - BotTagWidth() - 6 : nameRoom;
            _hist[k] = new HistText
            {
                Game = g,
                TierId = rank.Id,
                Result = Loc.T(g.Outcome switch { PvpOutcome.Win => "pvp.victory", PvpOutcome.Loss => "pvp.defeat", _ => "pvp.result_draw" }),
                ResultColor = g.Outcome switch { PvpOutcome.Win => Palette.Cyan1, PvpOutcome.Loss => Palette.Purple5, _ => Palette.Grey },
                Name = PvpArt.Fit(Font, g.Opponent.Length > 0 ? g.Opponent : "?", room, full ? 2 : 1),
                RankLine = $"{PvpUi.RankName(rank)} · {g.OpponentRating}",
                Kills = Loc.T("pvp.kills", g.Kills),
                Delta = _histFriendly ? "" : g.RatingDelta > 0 ? "+" + g.RatingDelta : g.RatingDelta.ToString(),
                DeltaColor = g.RatingDelta > 0 ? Palette.Cyan1 : g.RatingDelta < 0 ? Palette.Purple5 : Palette.Grey,
                When = Ago(now - g.When),
            };
        }
    }

    private int BotTagWidth() => Font.Measure(Loc.T("pvp.bot_tag"), 1) + (_lay.Full ? 12 : 6);

    /// <summary>« IL Y A 12 MIN », « IL Y A 2 H », « HIER », « IL Y A 3 J ».</summary>
    private static string Ago(long seconds)
    {
        var minutes = Math.Max(0, seconds) / 60;
        if (minutes < 1) return Loc.T("pvp.ago_now");
        if (minutes < 60) return Loc.T("pvp.ago_min", minutes);
        var hours = minutes / 60;
        if (hours < 24) return Loc.T("pvp.ago_hour", hours);
        var days = hours / 24;
        // {1} / {0} en heures : le chinois compte en heures (pas de glyphe « jour » dans sa police).
        return days < 2 ? Loc.T("pvp.yesterday", hours) : Loc.T("pvp.ago_day", days, hours);
    }

    // ── Rendu ────────────────────────────────────────────────────────────────────

    private void DrawRanking(SpriteBatch sb)
    {
        _overlayDim = _tutorialOpen || _error != null ? 1f : 0f;
        RankingLayout();
        RefreshMineText();
        RefreshRowText();
        RefreshHistText();
        var R = _rl;
        PvpArt.Panel(sb, Px, R.Mine);
        PvpArt.Panel(sb, Px, R.Tiers);
        PvpArt.Panel(sb, Px, R.Board);
        PvpArt.Panel(sb, Px, R.Hist);
        DrawMyRank(sb);
        DrawTiers(sb);
        DrawBoardList(sb);
        DrawHistory(sb);
        DrawFormationTip(sb);
    }

    private void DrawMyRank(SpriteBatch sb)
    {
        var R = _rl;
        var full = _lay.Full;
        var inner = R.MineIn;
        var s = full ? 2 : 1;
        var lh1 = Font.LineHeight(1);
        var tier = Profile.Rank.Id;
        Font.Draw(sb, Loc.T("pvp.my_rank"), new Vector2(inner.X, inner.Y), 1, Palette.Blue1);

        RankIcon.Draw(sb, tier, R.Icon.Location, 8);
        var tx = R.Icon.Right + (full ? 16 : 10);
        var room = inner.Right - tx;
        var ns = PvpArt.ScaleToFit(Font, _mRank, room, full ? 3 : 2);
        var y = R.Icon.Y + (full ? 8 : 6);
        Font.Draw(sb, PvpArt.Fit(Font, _mRank, room, ns), new Vector2(tx, y), ns, PvpRankIcon.LightColor(tier));
        y += Font.LineHeight(ns) + (full ? 14 : 10);
        Font.Draw(sb, PvpArt.Fit(Font, _mRating, room, s), new Vector2(tx, y), s, Palette.White);
        y += Font.LineHeight(s) + (full ? 10 : 7);
        if (_mPlace.Length > 0)
            Font.Draw(sb, PvpArt.Fit(Font, _mPlace, room, s), new Vector2(tx, y), s, Palette.Blue1);

        // Progression vers la division suivante : libellé, « 50 / 100 », barre de 10 segments.
        var py = R.Icon.Bottom + (full ? 14 : 8);
        var bar = new Rectangle(inner.X, py + lh1 + (full ? 6 : 4), inner.Width, full ? 12 : 7);
        if (_mHasNext)
        {
            var pw = Font.Measure(_mProgress, 1);
            Font.Draw(sb, PvpArt.Fit(Font, _mTowards, inner.Width - pw - 8, 1), new Vector2(inner.X, py), 1, Palette.Blue1);
            Font.Draw(sb, _mProgress, new Vector2(inner.Right - pw, py), 1, Palette.Yellow2);
        }
        var gap = full ? 4 : 2;
        var segW = (bar.Width - 9 * gap) / 10;
        var filled = _mHasNext ? _mDone * 10 / Math.Max(1, _mNeeded) : 10;
        var light = PvpRankIcon.LightColor(tier);
        for (var i = 0; i < 10; i++)
            PvpArt.Fill(sb, Px, new Rectangle(bar.X + i * (segW + gap), bar.Y, segW, bar.Height), i < filled ? light : Palette.Black1);

        // 8 cases (2 colonnes), calées en bas du panneau.
        var cw = (inner.Width - R.StatGap) / 2;
        var top = inner.Bottom - (4 * R.StatH + 3 * R.StatGap);
        for (var i = 0; i < 8; i++)
        {
            var r = new Rectangle(inner.X + i % 2 * (cw + R.StatGap), top + i / 2 * (R.StatH + R.StatGap), cw, R.StatH);
            PvpArt.Fill(sb, Px, r, Palette.Black3);
            var value = _mStatValue[i];
            var vw = Font.Measure(value, s);
            var px = full ? 8 : 5;
            Font.Draw(sb, value, new Vector2(r.Right - px - vw, r.Center.Y - Font.LineHeight(s) / 2), s,
                _mStatHot[i] ? Palette.Yellow2 : Palette.White);
            var label = PvpArt.Fit(Font, _mStatLabel[i], r.Width - 2 * px - vw - 4, 1);
            Font.Draw(sb, label, new Vector2(r.X + px, r.Center.Y - lh1 / 2), 1, Palette.Blue1);
        }
    }

    private void DrawTiers(SpriteBatch sb)
    {
        var R = _rl;
        var full = _lay.Full;
        var inner = R.TiersIn;
        var s = full ? 2 : 1;
        Font.Draw(sb, Loc.T("pvp.tiers"), new Vector2(inner.X, inner.Y), 1, Palette.Blue1);
        var ranks = PvpConfig.Current.Ranks;
        var current = Profile.Rank.TierIndex;
        var y = inner.Y + Font.LineHeight(1) + (full ? 13 : 6);
        for (var i = ranks.Count - 1; i >= 0; i--, y += R.TierPitch)   // du plus haut (Master) au plus bas
        {
            var r = new Rectangle(inner.X, y, inner.Width, R.TierRowH);
            var on = i == current;
            PvpArt.Fill(sb, Px, r, on ? Palette.Black5 : Palette.Black3);
            if (on)
                PvpArt.Border(sb, Px, r, Palette.Yellow2, 2);
            var px = full ? 10 : 6;
            RankIcon.Draw(sb, ranks[i].Id, new Point(r.X + px, r.Center.Y - PvpRankIcon.H), 2);
            var range = _tierRange[i];
            var rw = Font.Measure(range, 1);
            Font.Draw(sb, range, new Vector2(r.Right - px - rw, r.Center.Y - Font.LineHeight(1) / 2), 1, Palette.Blue1);
            var nx = r.X + px + RankIconSmallW + (full ? 12 : 8);
            var name = PvpArt.Fit(Font, _tierName[i], r.Right - px - rw - 8 - nx, s);
            Font.Draw(sb, name, new Vector2(nx, r.Center.Y - Font.LineHeight(s) / 2), s, on ? Palette.Yellow2 : Palette.White);
        }
    }

    private void DrawBoardList(SpriteBatch sb)
    {
        var R = _rl;
        var full = _lay.Full;
        var s = full ? 2 : 1;
        for (var i = 0; i < 3; i++)
            ReserveTab(sb, R.Tabs[i], Loc.T(TabKey(i)), (int)_rankTab == i, s);
        if (_rankTab == RankTab.World && _listMessage == null)
        {
            var pages = RankPages;
            PageArrow(sb, R.Prev, "<", _rankPage > 0);
            PageArrow(sb, R.Next, ">", _rankPage < pages - 1);
            Font.DrawCentered(sb, PageText(_rankPage + 1, pages), R.Counter, 1, Palette.Blue1);
        }
        if (_listMessage != null)
        {
            Font.DrawCentered(sb, PvpArt.Fit(Font, _listMessage, R.ListArea.Width - 16, 1), R.ListArea, 1, Palette.Blue1);
            return;
        }

        // En-tête des colonnes.
        var head = new Rectangle(R.Rows[0].X, R.ColHeadY, R.Rows[0].Width, Font.LineHeight(1));
        var hy = head.Y;
        Font.Draw(sb, "#", new Vector2(Col(head, 8), hy), 1, Palette.Blue1);
        Font.Draw(sb, Loc.T("pvp.col_player"), new Vector2(Col(head, 80), hy), 1, Palette.Blue1);
        Font.Draw(sb, Loc.T("pvp.col_rank"), new Vector2(Col(head, 392), hy), 1, Palette.Blue1);
        DrawRight(sb, Loc.T("pvp.col_rating"), Col(head, 630), hy, 1, Palette.Blue1);
        DrawRight(sb, Loc.T("pvp.col_vdn"), Col(head, 760), hy, 1, Palette.Blue1);
        DrawRight(sb, "%", head.Right - 8, hy, 1, Palette.Blue1);

        if (_pinned)
            for (var x = R.Pinned.X; x < R.Pinned.Right; x += 8)   // filet pointillé au-dessus de ma ligne épinglée
                PvpArt.Fill(sb, Px, new Rectangle(x, R.SepY, Math.Min(4, R.Pinned.Right - x), 1), Palette.Black5);

        for (var k = 0; k < _rowCount; k++)
        {
            ref readonly var t = ref _rows[k];
            var r = RowRect(k);
            PvpArt.Fill(sb, Px, r, t.Mine ? Palette.Black5 : k % 2 == 0 ? Palette.Black3 : Palette.Black4);
            if (t.Mine)
                PvpArt.Border(sb, Px, r, Palette.Yellow2, 2);
            var y = r.Center.Y - Font.LineHeight(s) / 2;
            var y1 = r.Center.Y - Font.LineHeight(1) / 2;
            var place = t.Entry.GlobalRank;
            var placeColor = t.Mine ? Palette.Blue1 : place == 1 ? Palette.Yellow2 : place <= 3 ? Palette.Brown3 : Palette.Blue1;
            // Grande place (« #12470 ») : un cran plus petite plutôt que de toucher le pseudo.
            var ps = Font.Measure(t.Place, s) <= Col(r, 80) - Col(r, 8) - 6 ? s : 1;
            Font.Draw(sb, t.Place, new Vector2(Col(r, 8), r.Center.Y - Font.LineHeight(ps) / 2), ps, placeColor);
            Font.Draw(sb, t.Name, new Vector2(Col(r, 80), y), s, t.Mine ? Palette.Yellow2 : Palette.White, preserveCase: !t.Mine);
            var rx = Col(r, 392);
            RankIcon.Draw(sb, t.TierId, new Point(rx, r.Center.Y - PvpRankIcon.H), 2);
            Font.Draw(sb, t.Rank, new Vector2(rx + RankIconSmallW + 6, y), s, PvpRankIcon.LightColor(t.TierId));
            DrawRight(sb, t.Rating, Col(r, 630), y, s, Palette.White);
            DrawRight(sb, t.Record, Col(r, 760), y1, 1, Palette.Blue1);
            DrawRight(sb, t.Pct, r.Right - 8, y1, 1, Palette.Blue1);
        }
    }

    private void DrawRight(SpriteBatch sb, string text, int right, int y, int scale, Color c) =>
        Font.Draw(sb, text, new Vector2(right - Font.Measure(text, scale), y), scale, c);

    private void DrawHistory(SpriteBatch sb)
    {
        var R = _rl;
        var full = _lay.Full;
        var s = full ? 2 : 1;
        TextLeft(sb, PvpArt.Fit(Font, Loc.T("pvp.last_games"), R.HistTitle.Width, 1), R.HistTitle, 1, Palette.Blue1);
        ReserveTab(sb, R.HistTabs[0], Loc.T("pvp.hist_ranked"), !_histFriendly, 1);
        ReserveTab(sb, R.HistTabs[1], Loc.T("pvp.hist_friendly"), _histFriendly, 1);
        if (_histCount == 0)
        {
            var area = new Rectangle(R.HistRowRects[0].X, R.HistRowRects[0].Y, R.HistRowRects[0].Width,
                R.HistRowRects[HistRows - 1].Bottom - R.HistRowRects[0].Y);
            Font.DrawCentered(sb, Loc.T("pvp.no_games"), area, 1, Palette.Blue1);
            return;
        }
        for (var k = 0; k < _histCount; k++)
        {
            ref readonly var h = ref _hist[k];
            var r = R.HistRowRects[k];
            PvpArt.Fill(sb, Px, r, Palette.Black3);
            var y = r.Center.Y - Font.LineHeight(s) / 2;
            var y1 = r.Center.Y - Font.LineHeight(1) / 2;
            Font.Draw(sb, h.Result, new Vector2(Col(r, 8), y), s, h.ResultColor);
            var nx = Col(r, 126);
            Font.Draw(sb, h.Name, new Vector2(nx, y), s, Palette.White, preserveCase: true);
            if (h.Game.Bot)
            {
                var tagLabel = Loc.T("pvp.bot_tag");
                var tx = nx + Font.Measure(h.Name, s) + (full ? 8 : 5);
                var tag = new Rectangle(tx, r.Center.Y - (full ? 10 : 7), BotTagWidth(), full ? 20 : 14);
                PvpArt.Fill(sb, Px, tag, Palette.Black5);
                Font.DrawCentered(sb, tagLabel, tag, 1, Palette.Blue1);
            }
            var ix = Col(r, 304);
            RankIcon.Draw(sb, h.TierId, new Point(ix, r.Center.Y - PvpRankIcon.H), 2);
            var lx = ix + RankIconSmallW + 6;
            Font.Draw(sb, PvpArt.Fit(Font, h.RankLine, Col(r, 484) - lx, 1), new Vector2(lx, y1), 1, Palette.Blue1);
            Font.Draw(sb, PvpArt.Fit(Font, h.Kills, Col(r, 640) - Col(r, 490), 1), new Vector2(Col(r, 490), y1), 1, Palette.Blue1);
            if (h.Delta.Length > 0)
                DrawRight(sb, h.Delta, Col(r, 688), y, s, h.DeltaColor);
            DrawRight(sb, h.When, r.Right - 8, y1, 1, Palette.Blue1);
        }
    }

    /// <summary>Survol d'une ligne du classement : les deux lignes de pions de sa dernière formation classée
    /// (devant en haut, commandant à sa case), sous la ligne (au-dessus s'il n'y a pas la place).</summary>
    private void DrawFormationTip(SpriteBatch sb)
    {
        if (_hover < 0 || _hover >= _targets.Count || _targets[_hover].Kind != Hit.RankRow)
            return;
        var k = _targets[_hover].Index;
        if (k >= _rowCount || _rows[k].Entry.Build is not { } build)
            return;
        var row = RowRect(k);
        const int cell = 32, frame = 4;
        var w = PvpBuild.Columns * cell + 2 * frame;
        var h = 2 * cell + 2 * frame;
        var x = Math.Min(Col(row, 80), _lay.W - w - 4);
        var y = row.Bottom + 4 + h <= _lay.H - 4 ? row.Bottom + 4 : row.Y - 4 - h;
        var box = new Rectangle(x, y, w, h);
        PvpArt.Fill(sb, Px, box, Palette.Black1);
        PvpArt.Border(sb, Px, box, Palette.Brown2, 2);
        for (var line = 0; line < 2; line++)
        {
            var lineIndex = line == 0 ? PvpBuild.FrontLine : PvpBuild.BackLine;
            for (var c = 0; c < PvpBuild.Columns; c++)
            {
                var r = new Rectangle(box.X + frame + c * cell, box.Y + frame + line * cell, cell, cell);
                PvpArt.Fill(sb, Px, r, (c + line) % 2 == 0 ? Palette.Yellow4 : GrassAlt);
                string? asset = null;
                if (PvpBuild.IsKingCell(c, lineIndex))
                    asset = PvpCatalog.CommanderClass(build.CommanderId)?.Class.Asset;
                else if (build.IsReserved(c, lineIndex))
                    asset = PvpCatalog.CompanionClass(build.CommanderId)?.Class.Asset;
                else if (build.SlotAt(c, lineIndex) is { } slot)
                    asset = slot.ClassAsset;
                if (asset != null)
                    DrawHalfSprite(sb, UnitFront(asset), r, Color.White);
            }
        }
    }
}
