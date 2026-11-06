using System;
using ChessArmy.Core.Battle;
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
/// Écran de FIN d'une partie multi (docs/multijoueur-fin-de-partie.md) : plateau figé sous un voile, fenêtre de
/// 600 px avec titre et raison, face-à-face des commandants, CLASSEMENT (cote et barre de division animées) et
/// POINTS DE COFFRE, bandeau coffre, stats de la partie, RETOUR À L'ARMÉE / REJOUER. Toutes les chaînes sont
/// préparées une fois à l'ouverture ; pendant l'animation, seules les valeurs qui changent sont reformatées.
/// </summary>
public sealed partial class GameplayScene
{
    private const float PvpEndAnimDelay = 0.4f, PvpEndAnimLength = 1.2f;
    private const int PvpEndWidth = 600;
    private const int PvpEndInset = 27;   // cadre 5 + marge 22 : la colonne de contenu fait 546 px
    private const int PvpEndTitleGap = 6;   // espacement des lettres du titre

    /// <summary>Gabarit vertical de la fenêtre : grand (canevas 1280×720 et plus) ou serré (960×540).</summary>
    private sealed record PvpEndMetrics(
        int Body, int Title, int TitleY, int AfterTitle, int AfterReason, int AfterSprite, int AfterName, int AfterCmd,
        int AfterRule, int AfterHeader, int RowH, int RowGap, int BoxH, int AfterBox, int AfterTowards, int BarH,
        int SectionGap, int BannerH, int StatH, int AfterStats, int ButtonH, int Bottom, int FaceOffset, int VsY)
    {
        public int Height(int lineTitle, int lineBody, int lineSmall)
        {
            var left = RowH + 2 + BoxH + AfterBox + lineSmall + AfterTowards + BarH;
            var right = 2 * (RowH + RowGap) + BoxH;
            return TitleY + lineTitle + AfterTitle + lineBody + AfterReason + 128 + AfterSprite + lineBody + AfterName
                + lineSmall + AfterCmd + 2 + AfterRule + lineSmall + AfterHeader + Math.Max(left, right) + SectionGap
                + BannerH + SectionGap + StatH + AfterStats + ButtonH + Bottom;
        }
    }

    private static readonly PvpEndMetrics PvpEndFull = new(
        Body: 2, Title: 5, TitleY: 34, AfterTitle: 21, AfterReason: 13, AfterSprite: 7, AfterName: 8, AfterCmd: 18,
        AfterRule: 13, AfterHeader: 14, RowH: 28, RowGap: 6, BoxH: 36, AfterBox: 11, AfterTowards: 11, BarH: 12,
        SectionGap: 10, BannerH: 40, StatH: 44, AfterStats: 14, ButtonH: 52, Bottom: 25, FaceOffset: 137, VsY: 110);

    private static readonly PvpEndMetrics PvpEndCompact = new(
        Body: 1, Title: 4, TitleY: 16, AfterTitle: 8, AfterReason: 8, AfterSprite: 4, AfterName: 3, AfterCmd: 8,
        AfterRule: 8, AfterHeader: 6, RowH: 18, RowGap: 4, BoxH: 22, AfterBox: 6, AfterTowards: 5, BarH: 8,
        SectionGap: 8, BannerH: 26, StatH: 30, AfterStats: 8, ButtonH: 34, Bottom: 17, FaceOffset: 137, VsY: 110);

    /// <summary>Tout ce que l'écran affiche, figé à l'ouverture.</summary>
    private sealed class PvpEndScreen
    {
        public PvpOutcome Outcome;
        public string[] TitleChars = Array.Empty<string>();
        public Color TitleColor;
        public string Reason = "";
        public UnitClass? MyCommander, TheirCommander;
        public string You = "", MyCommanderName = "", TheirCommanderName = "", Opponent = "", BotTag = "", Vs = "";
        public bool Bot, Friendly, Rewarded;
        public string[] Invalid = Array.Empty<string>();

        public int RatingBefore, RatingAfter;
        public PvpRank RankBefore;
        public string RatingBeforeText = "", DeltaText = "", Chevron = "›";
        public Color DeltaColor;

        public string KillsLine = "", KillsValue = "", BonusLine = "", BonusValue = "";
        public int PointsBefore, PointsAfter;
        public bool ChestReady;

        public readonly string[] StatValue = new string[4];
        public readonly string[] StatLabel = new string[4];

        public string Ranking = "", Points = "", Rating = "", Total = "", Chest = "", FriendlyTitle = "", FriendlySub = "";
        public string Back = "", Replay = "";

        // Valeurs animées : texte refait seulement quand la valeur entière change.
        public int ShownRating = int.MinValue, ShownPoints = int.MinValue;
        public string RatingText = "", PointsText = "", RankName = "", Towards = "", Progress = "";
        public PvpRank ShownRank;
        public Color TowardsColor;
        public int Segments, Reference;
        public bool HasNext;
    }

    private PvpEndScreen? _pvpEnd;
    private float _pvpEndTime;
    private PvpRankIcon? _pvpEndRankIcon;
    private Rectangle _pvpEndBack, _pvpEndReplay, _pvpEndChest;
    private int _pvpEndHover;   // 0 aucun, 1 retour, 2 rejouer, 3 coffre

    private PvpRankIcon PvpEndRankIcon => _pvpEndRankIcon ??= new PvpRankIcon(Context.GraphicsDevice);

    private static float PvpEndProgress(float time) =>
        Math.Clamp((time - PvpEndAnimDelay) / PvpEndAnimLength, 0f, 1f);

    private bool PvpEndAnimating => PvpEndProgress(_pvpEndTime) < 1f;

    /// <summary>Prépare l'écran de fin (une seule fois, quand la partie vient d'être enregistrée).</summary>
    private void BeginPvpEndScreen(PvpOutcome outcome)
    {
        var info = _pvpInfo!;
        var e = new PvpEndScreen { Outcome = outcome };
        var title = outcome switch
        {
            PvpOutcome.Win => Loc.T("pvp.victory"),
            PvpOutcome.Loss => Loc.T("pvp.defeat"),
            _ => Loc.T("pvp.draw"),
        };
        e.TitleChars = new string[title.Length];
        for (var i = 0; i < title.Length; i++)
            e.TitleChars[i] = title[i].ToString();
        e.TitleColor = outcome switch { PvpOutcome.Win => Palette.Yellow2, PvpOutcome.Loss => Palette.Purple5, _ => Palette.White };
        var won = outcome == PvpOutcome.Win;
        e.Reason = Loc.T(_match.EndReason switch
        {
            MatchEndReason.Timeout => "pvp.end.reason_timeout",
            MatchEndReason.Resign => won ? "pvp.end.reason_resign_win" : "pvp.end.reason_resign_loss",
            MatchEndReason.Disconnect => "pvp.end.reason_disconnect",
            MatchEndReason.AgreedDraw => "pvp.end.reason_agreed",
            MatchEndReason.DeadPosition => "pvp.end.reason_dead",
            MatchEndReason.MutualDestruction => "pvp.end.reason_mutual",
            _ => won ? "pvp.end.reason_leader_win" : "pvp.end.reason_leader_loss",
        });

        e.MyCommander = PvpCatalog.CommanderClass(info.LocalBuild.CommanderId)?.Class;
        e.TheirCommander = PvpCatalog.CommanderClass(info.OpponentBuild.CommanderId)?.Class;
        e.MyCommanderName = PvpUi.CommanderName(info.LocalBuild.CommanderId);
        e.TheirCommanderName = PvpUi.CommanderName(info.OpponentBuild.CommanderId);
        e.You = Loc.T("pvp.you");
        e.Opponent = info.OpponentName;
        e.Bot = info.IsBot;
        e.BotTag = Loc.T("pvp.bot_tag");
        e.Vs = Loc.T("pvp.end.vs");
        e.Friendly = info.Friendly;

        // Gains : seulement si la partie est validée (rejeu, empreintes) et le profil sain.
        e.Rewarded = _pvpValid && _pvpReward != null;
        e.Invalid = _pvpDesync
            ? new[] { Loc.T("pvp.desync"), Loc.T("pvp.not_validated") }
            : new[] { Loc.T("pvp.not_validated") };
        var reward = _pvpReward ?? new PvpReward(0, info.LocalRating, info.LocalRating, info.Ranked);
        e.RatingBefore = reward.RatingBefore;
        e.RatingAfter = reward.Ranked ? reward.RatingAfter : reward.RatingBefore;
        e.RankBefore = PvpRanks.FromRating(e.RatingBefore);
        e.RatingBeforeText = e.RatingBefore.ToString();
        var delta = e.RatingAfter - e.RatingBefore;
        e.DeltaText = delta > 0 ? "+" + delta : delta < 0 ? delta.ToString() : "±0";
        e.DeltaColor = delta > 0 ? Palette.Cyan1 : delta < 0 ? Palette.Purple5 : Palette.Grey;

        var config = PvpConfig.Current;
        var kills = KillsByPlayer();
        e.KillsLine = Loc.T("pvp.end.kills", kills, config.PointsPerKill);
        e.KillsValue = "+" + kills * config.PointsPerKill;
        if (outcome != PvpOutcome.Loss)
        {
            e.BonusLine = outcome == PvpOutcome.Win ? Loc.T("pvp.victory") : Loc.T("pvp.draw");
            e.BonusValue = "+" + (outcome == PvpOutcome.Win ? config.PointsForWin : config.PointsForDraw);
        }
        e.PointsAfter = PvpProfileStore.Current.Points;
        e.PointsBefore = e.Rewarded ? e.PointsAfter - reward.Points : e.PointsAfter;
        e.ChestReady = e.Rewarded && (PvpProfileStore.Current.CanOpenChest(units: true) || PvpProfileStore.Current.CanOpenChest(units: false));

        e.StatValue[0] = _match.Round.ToString();
        e.StatValue[1] = PvpUi.Clock(_pvpClock.Elapsed);
        e.StatValue[2] = kills.ToString();
        e.StatValue[3] = LostByPlayer().ToString();
        e.StatLabel[0] = Loc.T("pvp.end.turns");
        e.StatLabel[1] = Loc.T("pvp.end.duration");
        e.StatLabel[2] = Loc.T("pvp.end.killed");
        e.StatLabel[3] = Loc.T("pvp.end.lost");

        e.Ranking = Loc.T("pvp.ranking");
        e.Points = Loc.T("pvp.end.points");
        e.Rating = Loc.T("pvp.end.rating");
        e.Total = Loc.T("pvp.end.total");
        e.Chest = Loc.T("pvp.end.chest");
        e.FriendlyTitle = Loc.T("pvp.end.friendly");
        e.FriendlySub = Loc.T("pvp.end.friendly_sub");
        e.Back = Loc.T("pvp.end.back");
        e.Replay = Loc.T("pvp.end.replay");

        _pvpEnd = e;
        _pvpEndTime = 0f;
        RefreshPvpEndValues(e, 0f);
    }

    private int LostByPlayer()
    {
        var n = 0;
        foreach (var (_, unit) in _match.DeathLog)
            if (unit.Faction == Faction.Player)
                n++;
        return n;
    }

    /// <summary>Valeurs animées pour l'avancement <paramref name="t"/> (0 → 1, adouci) ; ne formate que ce qui change.</summary>
    private static void RefreshPvpEndValues(PvpEndScreen e, float t)
    {
        var k = 1f - (1f - t) * (1f - t) * (1f - t);   // ralentit à l'arrivée
        var rating = (int)MathF.Round(e.RatingBefore + (e.RatingAfter - e.RatingBefore) * k);
        var points = (int)MathF.Round(e.PointsBefore + (e.PointsAfter - e.PointsBefore) * k);
        if (points != e.ShownPoints)
        {
            e.ShownPoints = points;
            e.PointsText = points.ToString();
        }
        if (rating == e.ShownRating)
            return;
        e.ShownRating = rating;
        e.RatingText = rating.ToString();
        var rank = PvpRanks.FromRating(rating);
        var (next, done, needed) = PvpRanks.Progress(rating);
        e.HasNext = next != null;
        e.Segments = next == null ? 10 : Segments(done, needed);
        if (rank != e.ShownRank || e.RankName.Length == 0)
        {
            e.ShownRank = rank;
            e.RankName = PvpUi.RankName(rank);
            var order = RankOrder(rank).CompareTo(RankOrder(e.RankBefore));
            e.Reference = order == 0 ? BeforeSegments(e) : order > 0 ? 0 : 10;
            e.Towards = order > 0 ? Loc.T("pvp.end.promoted", e.RankName)
                : order < 0 ? Loc.T("pvp.end.demoted", e.RankName)
                : next is { } n ? Loc.T("pvp.towards", PvpUi.RankName(n)) : "";
            e.TowardsColor = order > 0 ? Palette.Yellow2 : order < 0 ? Palette.Purple5 : Palette.Blue1;
        }
        e.Progress = next != null ? $"{done} / {needed}" : "";
    }

    /// <summary>Segments pleins (sur 10), arrondis au plus proche : 66 / 100 → 7.</summary>
    private static int Segments(int done, int needed) => Math.Clamp((done * 10 + needed / 2) / Math.Max(1, needed), 0, 10);

    private static int BeforeSegments(PvpEndScreen e)
    {
        var (next, done, needed) = PvpRanks.Progress(e.RatingBefore);
        return next == null ? 10 : Segments(done, needed);
    }

    /// <summary>Ordre des divisions : palier d'abord, puis la division (III est la plus basse, Master n'en a pas).</summary>
    private static int RankOrder(PvpRank rank) => rank.TierIndex * 10 + (rank.Division == 0 ? 9 : 9 - rank.Division);

    // ── Entrées ──────────────────────────────────────────────────────────────────

    private void UpdatePvpEndScreen(float dt)
    {
        if (_pvpEnd is not { } e)
            return;
        _pvpEndTime += dt;
        var input = Context.Input;
        var mouse = input.MousePosition;
        var click = input.WasLeftClicked;
        var confirm = input.WasConfirmPressed || input.WasKeyPressed(Keys.Enter);
        var cancel = input.WasCancelPressed || input.WasKeyPressed(Keys.Escape);
        var hover = _pvpEndReplay.Contains(mouse) ? 2 : _pvpEndBack.Contains(mouse) ? 1
            : e.ChestReady && _pvpEndChest.Contains(mouse) ? 3 : 0;
        if (hover != _pvpEndHover && hover != 0)
            Context.Sounds.Play("button_hover");
        _pvpEndHover = hover;

        if (PvpEndAnimating)
        {
            // Clic ou A pendant l'animation : valeurs finales tout de suite, rien d'autre.
            if (click || confirm)
                _pvpEndTime = PvpEndAnimDelay + PvpEndAnimLength;
            RefreshPvpEndValues(e, PvpEndProgress(_pvpEndTime));
            return;
        }
        RefreshPvpEndValues(e, 1f);

        PvpArmyScene.Entry? go = null;
        if (confirm || (click && hover == 2))
            go = _pvpInfo!.Friendly ? PvpArmyScene.Entry.ReplayFriend : PvpArmyScene.Entry.Replay;
        else if (cancel || (click && hover == 1))
            go = PvpArmyScene.Entry.Build;
        else if (click && hover == 3)
            go = PvpArmyScene.Entry.Chests;
        if (go is not { } entry)
            return;
        Context.Sounds.Play("menu_click");
        Context.Scenes.Change(new PvpArmyScene(Context, entry));   // Unload libère la session (on quitte le lobby)
    }

    // ── Dessin ───────────────────────────────────────────────────────────────────

    private void DrawPvpEndPanel(SpriteBatch sb, Viewport viewport, PvpMatchInfo info)
    {
        if (_pvpEnd is not { } e)
            return;
        var font = Context.Font;
        var px = Context.Pixel;
        DrawRect(sb, new Rectangle(0, 0, viewport.Width, viewport.Height), Palette.Black1 * 0.72f);

        var small = font.LineHeight(1);
        var full = viewport.Width >= 1280 && viewport.Height >= PvpEndFull.Height(font.LineHeight(PvpEndFull.Title), font.LineHeight(2), small) + 8;
        var m = full ? PvpEndFull : PvpEndCompact;
        var b = m.Body;
        var body = font.LineHeight(b);
        var titleH = font.LineHeight(m.Title);
        var height = m.Height(titleH, body, small) - (e.ChestReady ? 0 : m.BannerH + m.SectionGap);   // pas de bandeau : la fenêtre raccourcit
        var panel = new Rectangle((viewport.Width - PvpEndWidth) / 2,
            Math.Max(4, Math.Min(full ? 36 : int.MaxValue, (viewport.Height - height) / 2)), PvpEndWidth, height);
        PvpArt.Fill(sb, px, new Rectangle(panel.X - 4, panel.Y - 4, panel.Width + 8, panel.Height + 8), Palette.Black1);
        PvpArt.Panel(sb, px, panel);
        var cx = panel.X + PvpEndInset;
        var cw = panel.Width - 2 * PvpEndInset;
        var mid = panel.Center.X;

        // 1-2. Titre espacé et raison.
        var y = panel.Y + m.TitleY;
        var ts = m.Title;
        var tw = -PvpEndTitleGap;
        foreach (var ch in e.TitleChars)
            tw += font.Measure(ch, ts) + PvpEndTitleGap;
        if (tw > cw)
        {
            ts = Math.Max(2, ts - 1);
            tw = -PvpEndTitleGap;
            foreach (var ch in e.TitleChars)
                tw += font.Measure(ch, ts) + PvpEndTitleGap;
        }
        var tx = mid - tw / 2;
        foreach (var ch in e.TitleChars)
        {
            font.Draw(sb, ch, new Vector2(tx, y + (titleH - font.LineHeight(ts))), ts, e.TitleColor);
            tx += font.Measure(ch, ts) + PvpEndTitleGap;
        }
        y += titleH + m.AfterTitle;
        font.DrawCentered(sb, PvpArt.Fit(font, e.Reason, cw, b), new Rectangle(cx, y, cw, body), b, Palette.Grey);
        y += body + m.AfterReason;

        // 3. Face-à-face.
        var drawTint = new Color(140, 140, 140);   // perdant : luminosité 55 %
        DrawPvpEndSide(sb, e.MyCommander, Faction.Player, mid - m.FaceOffset, y, m,
            e.Outcome == PvpOutcome.Loss ? drawTint : Color.White, e.You, Palette.Yellow2, preserve: false,
            tag: null, e.MyCommanderName);
        DrawPvpEndSide(sb, e.TheirCommander, Faction.Enemy, mid + m.FaceOffset, y, m,
            e.Outcome == PvpOutcome.Win ? drawTint : Color.White, e.Opponent, Palette.White, preserve: true,
            tag: e.Bot ? e.BotTag : null, e.TheirCommanderName);
        font.DrawCentered(sb, e.Vs, new Rectangle(mid - 30, y + m.VsY, 60, body), b, Palette.Brown1);
        y += 128 + m.AfterSprite + body + m.AfterName + small + m.AfterCmd;

        // 4. Filet.
        PvpArt.Fill(sb, px, new Rectangle(cx, y, cw, 2), Palette.Black5);
        y += 2 + m.AfterRule;

        // 5. Deux colonnes : CLASSEMENT | POINTS DE COFFRE (ou le message rouge si la partie n'est pas validée).
        const int ColGap = 16;
        var colW = (cw - ColGap) / 2;
        var left = new Rectangle(cx, y, colW, 0);
        var right = new Rectangle(cx + cw - colW, y, colW, 0);
        var leftH = m.RowH + 2 + m.BoxH + m.AfterBox + small + m.AfterTowards + m.BarH;
        var rightH = 2 * (m.RowH + m.RowGap) + m.BoxH;
        var colsH = small + m.AfterHeader + Math.Max(leftH, rightH);
        if (!e.Rewarded)
        {
            var box = new Rectangle(cx, y, cw, colsH);
            PvpArt.Fill(sb, px, box, Palette.Black3);
            var ly = box.Center.Y - (e.Invalid.Length * (body + 6) - 6) / 2;
            foreach (var line in e.Invalid)
            {
                var s = PvpArt.ScaleToFit(font, line, cw - 16, b);
                font.DrawCentered(sb, PvpArt.Fit(font, line, cw - 16, s), new Rectangle(cx, ly, cw, body), s, Palette.Purple5);
                ly += body + 6;
            }
        }
        else
        {
            font.Draw(sb, PvpArt.Fit(font, e.Ranking, colW, 1), new Vector2(left.X, y), 1, Palette.Grey);
            font.Draw(sb, PvpArt.Fit(font, e.Points, colW, 1), new Vector2(right.X, y), 1, Palette.Grey);
            var top = y + small + m.AfterHeader;
            if (e.Friendly)
                DrawPvpEndFriendly(sb, new Rectangle(left.X, top, colW, leftH), b);
            else
                DrawPvpEndRanking(sb, e, new Rectangle(left.X, top, colW, leftH), m);
            DrawPvpEndPoints(sb, e, new Rectangle(right.X, top, colW, rightH), m);
        }
        y += colsH + m.SectionGap;

        // 6. Bandeau coffre (seulement si un coffre non vide peut s'ouvrir).
        _pvpEndChest = new Rectangle(cx, y, cw, m.BannerH);
        if (e.ChestReady)
        {
            var on = _pvpEndHover == 3;
            PvpArt.Fill(sb, px, _pvpEndChest, on ? Palette.Blue3 : Palette.Black5);
            PvpArt.Border(sb, px, _pvpEndChest, Palette.Yellow2, 2);
            var label = PvpArt.Fit(font, e.Chest, cw - 60, b);
            var lw = font.Measure(label, b);
            var icon = full ? 32 : 16;
            var gx = mid - (icon + 10 + lw) / 2;
            if (_chestSprite != null)
                DrawPvpEndIcon(sb, _chestSprite, new Rectangle(gx, _pvpEndChest.Center.Y - icon / 2, icon, icon));
            font.Draw(sb, label, new Vector2(gx + icon + 10, _pvpEndChest.Center.Y - body / 2), b, Palette.Yellow2);
            y += m.BannerH + m.SectionGap;
        }

        // 7. Stats de la partie.
        const int StatGap = 4;
        var sw = (cw - 3 * StatGap) / 4;
        for (var i = 0; i < 4; i++)
        {
            var r = new Rectangle(cx + i * (sw + StatGap), y, i == 3 ? cw - 3 * (sw + StatGap) : sw, m.StatH);
            PvpArt.Fill(sb, px, r, Palette.Black3);
            var inner = (m.StatH - body - small - (full ? 6 : 3)) / 2;
            font.DrawCentered(sb, e.StatValue[i], new Rectangle(r.X, r.Y + inner, r.Width, body), b, Palette.White);
            font.DrawCentered(sb, PvpArt.Fit(font, e.StatLabel[i], r.Width - 6, 1),
                new Rectangle(r.X, r.Bottom - inner - small, r.Width, small), 1, Palette.Grey);
        }
        y += m.StatH + m.AfterStats;

        // 8. Boutons (l'indice manette n'apparaît qu'à la manette).
        const int BtnGap = 10;
        var bw = (cw - BtnGap) / 2;
        _pvpEndBack = new Rectangle(cx, y, bw, m.ButtonH);
        _pvpEndReplay = new Rectangle(cx + cw - bw, y, bw, m.ButtonH);
        var pad = Context.Input.UsingGamepad;
        var backHover = _pvpEndHover == 1;
        PvpArt.Fill(sb, px, _pvpEndBack, Palette.Black4);
        PvpArt.Border(sb, px, _pvpEndBack, backHover ? Palette.Brown2 : Palette.Brown1, 3);
        DrawPvpEndButtonLabel(sb, (pad ? "(B) " : "") + e.Back, _pvpEndBack, 0, Palette.White, b);
        var dy = PvpArt.Primary(sb, px, _pvpEndReplay, _pvpEndHover == 2, _pvpEndHover == 2 && Context.Input.IsLeftDown);
        DrawPvpEndButtonLabel(sb, (pad ? "(A) " : "") + e.Replay, _pvpEndReplay, dy, Palette.Black1, b);
    }

    private void DrawPvpEndButtonLabel(SpriteBatch sb, string label, Rectangle r, int dy, Color color, int scale)
    {
        var font = Context.Font;
        var s = PvpArt.ScaleToFit(font, label, r.Width - 20, scale);
        font.DrawCentered(sb, PvpArt.Fit(font, label, r.Width - 12, s), new Rectangle(r.X, r.Y + dy, r.Width, r.Height), s, color);
    }

    /// <summary>Un camp du face-à-face, centré sur <paramref name="center"/> : sprite ×2, nom, commandant.</summary>
    private void DrawPvpEndSide(SpriteBatch sb, UnitClass? cls, Faction faction, int center, int y, PvpEndMetrics m,
        Color tint, string who, Color whoColor, bool preserve, string? tag, string commander)
    {
        const int ColumnW = 200;
        var font = Context.Font;
        var b = m.Body;
        if (cls != null && SpriteFor(cls, faction, front: true) is { } tex)
            sb.Draw(tex, new Rectangle(center - tex.Width, y, tex.Width * 2, tex.Height * 2), tint);
        var ny = y + 128 + m.AfterSprite;
        var body = font.LineHeight(b);
        var tagW = tag != null ? font.Measure(tag, 1) + 8 : 0;
        var name = PvpArt.Fit(font, who, ColumnW - (tagW > 0 ? tagW + 4 : 0), b);
        var nw = font.Measure(name, b) + (tagW > 0 ? tagW + 4 : 0);
        var nx = center - nw / 2;
        font.Draw(sb, name, new Vector2(nx, ny), b, whoColor, preserveCase: preserve);
        if (tag != null)
        {
            var tr = new Rectangle(nx + nw - tagW, ny + body / 2 - (font.LineHeight(1) + 6) / 2, tagW, font.LineHeight(1) + 6);
            PvpArt.Fill(sb, Context.Pixel, tr, Palette.Black5);
            font.DrawCentered(sb, tag, tr, 1, Palette.Grey);
        }
        var cy = ny + body + m.AfterName;
        font.DrawCentered(sb, PvpArt.Fit(font, commander, ColumnW, 1), new Rectangle(center - ColumnW / 2, cy, ColumnW, font.LineHeight(1)), 1, Palette.Grey);
    }

    /// <summary>Bloc CLASSEMENT : rang, encadré de cote, progression et barre de division (animées).</summary>
    private void DrawPvpEndRanking(SpriteBatch sb, PvpEndScreen e, Rectangle col, PvpEndMetrics m)
    {
        var font = Context.Font;
        var px = Context.Pixel;
        var b = m.Body;
        var body = font.LineHeight(b);
        var small = font.LineHeight(1);
        var tier = e.ShownRank.Id;

        // Rang : icône ×2 + nom dans la couleur claire du palier.
        var y = col.Y;
        PvpEndRankIcon.Draw(sb, tier, new Point(col.X, y + (m.RowH - PvpRankIcon.H * 2) / 2), 2);
        var nx = col.X + PvpRankIcon.W * 2 + 8;
        font.Draw(sb, PvpArt.Fit(font, e.RankName, col.Right - nx, b), new Vector2(nx, y + (m.RowH - body) / 2), b, PvpRankIcon.LightColor(tier));
        y += m.RowH + 2;

        // Encadré COTE : « 950 › 966  +16 ».
        var box = new Rectangle(col.X, y, col.Width, m.BoxH);
        PvpArt.Fill(sb, px, box, Palette.Black3);
        PvpArt.Border(sb, px, box, Palette.Black5, 2);
        var ty = box.Center.Y - body / 2;
        font.Draw(sb, PvpArt.Fit(font, e.Rating, col.Width / 3, 1), new Vector2(box.X + 10, box.Center.Y - small / 2), 1, Palette.Grey);
        var x = box.Right - 10 - font.Measure(e.DeltaText, b);
        font.Draw(sb, e.DeltaText, new Vector2(x, ty), b, e.DeltaColor);
        x -= 12 + font.Measure(e.RatingText, b);
        font.Draw(sb, e.RatingText, new Vector2(x, ty), b, Palette.White);
        x -= 6 + font.Measure(e.Chevron, b);
        font.Draw(sb, e.Chevron, new Vector2(x, ty), b, Palette.Brown1);
        x -= 6 + font.Measure(e.RatingBeforeText, b);
        font.Draw(sb, e.RatingBeforeText, new Vector2(x, ty), b, Palette.Grey);
        y += m.BoxH + m.AfterBox;

        // « VERS BRONZE I » (ou PROMU / RÉTROGRADÉ) et « 66 / 100 ».
        var pw = e.Progress.Length > 0 ? font.Measure(e.Progress, 1) : 0;
        if (e.Towards.Length > 0)
            font.Draw(sb, PvpArt.Fit(font, e.Towards, col.Width - pw - 8, 1), new Vector2(col.X, y), 1, e.TowardsColor);
        if (pw > 0)
            font.Draw(sb, e.Progress, new Vector2(col.Right - pw, y), 1, Palette.White);
        y += small + m.AfterTowards;

        // Barre de 10 segments : base claire du palier, gagnés en Yellow2 encadrés Brown2, perdus en Purple5.
        const int Gap = 2;
        var segW = (col.Width - 9 * Gap) / 10;
        var light = PvpRankIcon.LightColor(tier);
        var seg = e.Segments;
        var reference = e.Reference;
        for (var i = 0; i < 10; i++)
        {
            var r = new Rectangle(col.X + i * (segW + Gap), y, i == 9 ? col.Right - (col.X + 9 * (segW + Gap)) : segW, m.BarH);
            if (i < Math.Min(seg, reference))
                PvpArt.Fill(sb, px, r, light);
            else if (i < seg)
            {
                PvpArt.Fill(sb, px, r, Palette.Yellow2);
                PvpArt.Border(sb, px, r, Palette.Brown2, 2);
            }
            else if (i < reference)
                PvpArt.Fill(sb, px, r, Palette.Purple5);
            else
                PvpArt.Fill(sb, px, r, Palette.Black1);
        }
    }

    /// <summary>Défi entre amis : le bloc CLASSEMENT laisse place à « PARTIE AMICALE ».</summary>
    private void DrawPvpEndFriendly(SpriteBatch sb, Rectangle col, int b)
    {
        var e = _pvpEnd!;
        var font = Context.Font;
        PvpArt.Fill(sb, Context.Pixel, col, Palette.Black3);
        var body = font.LineHeight(b);
        var small = font.LineHeight(1);
        var y = col.Center.Y - (body + 8 + small) / 2;
        font.DrawCentered(sb, PvpArt.Fit(font, e.FriendlyTitle, col.Width - 12, b), new Rectangle(col.X, y, col.Width, body), b, Palette.White);
        font.DrawCentered(sb, PvpArt.Fit(font, e.FriendlySub, col.Width - 12, 1), new Rectangle(col.X, y + body + 8, col.Width, small), 1, Palette.Grey);
    }

    /// <summary>Bloc POINTS DE COFFRE : une ligne par gain (barème de pvp.json), puis le total avec la pièce.</summary>
    private void DrawPvpEndPoints(SpriteBatch sb, PvpEndScreen e, Rectangle col, PvpEndMetrics m)
    {
        var y = col.Y;
        DrawPvpEndGainRow(sb, new Rectangle(col.X, y, col.Width, m.RowH), e.KillsLine, e.KillsValue, m.Body);
        y += m.RowH + m.RowGap;
        if (e.BonusLine.Length > 0)
        {
            DrawPvpEndGainRow(sb, new Rectangle(col.X, y, col.Width, m.RowH), e.BonusLine, e.BonusValue, m.Body);
            y += m.RowH + m.RowGap;
        }
        var font = Context.Font;
        var px = Context.Pixel;
        var b = m.Body;
        var body = font.LineHeight(b);
        var box = new Rectangle(col.X, y, col.Width, m.BoxH);
        PvpArt.Fill(sb, px, box, Palette.Black3);
        PvpArt.Border(sb, px, box, Palette.Brown1, 2);
        font.Draw(sb, PvpArt.Fit(font, e.Total, col.Width / 2, 1), new Vector2(box.X + 10, box.Center.Y - font.LineHeight(1) / 2), 1, Palette.Grey);
        var vw = font.Measure(e.PointsText, b);
        var vx = box.Right - 10 - vw;
        font.Draw(sb, e.PointsText, new Vector2(vx, box.Center.Y - body / 2), b, Palette.White);
        var coin = b == 2 ? 2 : 1;   // pièce 10×10 affichée ×2 (×1 au format serré)
        var size = PvpArt.CoinSize * coin;
        PvpArt.Coin(sb, px, new Point(vx - 8 - size, box.Center.Y - size / 2), coin);
    }

    private void DrawPvpEndGainRow(SpriteBatch sb, Rectangle r, string label, string value, int b)
    {
        var font = Context.Font;
        PvpArt.Fill(sb, Context.Pixel, r, Palette.Black3);
        var vw = font.Measure(value, b);
        font.Draw(sb, value, new Vector2(r.Right - 8 - vw, r.Center.Y - font.LineHeight(b) / 2), b, Palette.Yellow2);
        font.Draw(sb, PvpArt.Fit(font, label, r.Width - 24 - vw, 1), new Vector2(r.X + 8, r.Center.Y - font.LineHeight(1) / 2), 1, Palette.White);
    }

    /// <summary>Icône 64 px affichée à demi-taille : couche écran nette quand l'échelle le permet, sinon réduite d'un facteur entier.</summary>
    private static void DrawPvpEndIcon(SpriteBatch sb, Texture2D tex, Rectangle box)
    {
        if (tex.Width <= box.Width)
            sb.Draw(tex, new Rectangle(box.Center.X - tex.Width / 2, box.Center.Y - tex.Height / 2, tex.Width, tex.Height), Color.White);
        else if (NativeSprites.CanQueue)
            NativeSprites.Queue(tex, box, Color.White);
        else
            sb.Draw(tex, box, Color.White);
    }
}
