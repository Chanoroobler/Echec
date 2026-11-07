using System;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine.Localization;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Mise en page de l'écran Armée, recalculée à chaque frame (arithmétique pure, aucune allocation) :
///  • COMPLÈTE (canevas ≥ 1280×720) : rectangles du document, centrés dans le canevas ;
///  • COMPACTE (canevas 960×540, écrans 1080p) : mêmes zones resserrées, portrait ×2, réserve en 3 colonnes,
///    plateau limité aux rangées du bas qui tiennent (les deux lignes du joueur sont toujours visibles).
/// Tout est dérivé du canevas : pas d'ordonnée fixe mêlée à des ordonnées calculées.
/// </summary>
public sealed partial class PvpArmyScene
{
    private sealed class Lay
    {
        public bool Full;
        public int W, H;
        public int Pad, Gap;
        public int TextScale;                // échelle des valeurs (2 en complet, 1 en compact)
        public Rectangle Top, Band;
        public Point TitlePos;
        public int TitleScale;
        public Rectangle RankPill, RatingPill, RecordPill, PointsPill, RankingBtn, ChestsBtn, BackBtn, HelpBtn;
        public readonly Rectangle[] Tabs = new Rectangle[PvpProfile.BuildSlots];
        public Rectangle Cmd, CmdInner, CmdPrev, CmdNext, Portrait, CmdName, CmdDots;
        public int PortraitScale, BarsY, BarStep, BarH;
        public Rectangle Board;              // zone des tuiles (épaisseur de la dernière rangée comprise)
        public int FirstRow;
        public Rectangle Res, ResInner, TabUnits, TabEquip;
        public readonly Rectangle[] Cards = new Rectangle[CardsPerPageMax];
        public int Cols, CardCount, CardW, CardH;
        public Rectangle PagePrev, PageNext, PageCounter;
        public Rectangle SheetFrame;         // fiche : panneau posé sur les rangées vides au-dessus des lignes du joueur
        public Rectangle Sheet;              // intérieur de ce panneau
        public Rectangle Friend, Launch, Cancel, Center;
        // Coffres
        public Rectangle ChestTitle;
        public readonly Rectangle[] ChestPanels = new Rectangle[2];
    }

    private readonly Lay _lay = new();

    private void ComputeLayout()
    {
        var L = _lay;
        var vr = Context.VirtualResolution;
        L.W = vr.X;
        L.H = vr.Y;
        L.Full = vr.X >= 1280 && vr.Y >= 720;
        var full = L.Full;
        var font = Context.Font;
        L.Pad = full ? 16 : 8;
        L.Gap = full ? 16 : 8;
        L.TextScale = full ? 2 : 1;

        // ── Barre du haut et bande du bas : toute la largeur ──
        var topH = full ? 52 : 44;
        var bandH = full ? 76 : 56;
        L.Top = new Rectangle(0, 0, L.W, topH);
        L.Band = new Rectangle(0, L.H - bandH, L.W, bandH);

        // Composition centrée (1280×720 ou 960×540) dans le canevas.
        var compW = full ? 1280 : 960;
        var compH = full ? 720 : 540;
        var ox = Math.Max(0, (L.W - compW) / 2);
        var oy = Math.Max(0, (L.H - compH) / 2);
        var pad = L.Pad;

        LayoutTopBar(L, font, topH);

        // ── Milieu : armées + commandant | plateau | réserve ──
        var midTop = topH + (full ? 12 : 6) + oy;
        var midBottom = L.Band.Y - (full ? 8 : 8) - oy;
        var leftW = full ? 320 : 192;
        var resW = full ? 384 : 224;
        var boardW = PvpBoardRenderer.Size * PvpBoardRenderer.Tile;
        var leftX = ox + pad;
        var boardX = leftX + leftW + L.Gap;
        var resX = boardX + boardW + L.Gap;

        var tabH = full ? 48 : 28;
        var tabGap = full ? 6 : 3;
        var tabW = (leftW - 2 * tabGap) / 3;
        for (var i = 0; i < 3; i++)
            L.Tabs[i] = new Rectangle(leftX + i * (tabW + tabGap), midTop, tabW, tabH);
        var cmdY = midTop + tabH + (full ? 8 : 6);
        L.Cmd = new Rectangle(leftX, cmdY, leftW, midBottom - cmdY);
        LayoutCommander(L);

        var rows = Math.Clamp((midBottom - midTop - PvpBoardRenderer.Thickness) / PvpBoardRenderer.Tile, 2, PvpBoardRenderer.Size);
        L.FirstRow = PvpBoardRenderer.Size - rows;
        L.Board = new Rectangle(boardX, midTop, boardW, rows * PvpBoardRenderer.Tile + PvpBoardRenderer.Thickness);
        LayoutSheet(L);

        L.Res = new Rectangle(resX, midTop, resW, L.Cmd.Bottom - midTop);
        LayoutReserve(L, font);

        LayoutBand(L, font);
        LayoutChests(L, oy);
    }

    private void LayoutTopBar(Lay L, Engine.UI.Text.ITextFont font, int topH)
    {
        var full = L.Full;
        var pad = L.Pad;
        L.TitleScale = full ? 3 : 2;
        var titleH = font.LineHeight(L.TitleScale);
        L.TitlePos = new Point(pad, (topH - titleH) / 2);
        var pillH = full ? 36 : 28;
        var pillY = (topH - pillH) / 2;
        var s = L.TextScale;
        var pillPad = full ? 13 : 8;
        var x = pad + font.Measure(Loc.T("menu.multiplayer"), L.TitleScale) + (full ? 26 : 14);
        L.RankPill = new Rectangle(x, pillY, RankIconSmallW + RankIconGap + font.Measure(TopRank, s) + 2 * pillPad, pillH);
        x = L.RankPill.Right + (full ? 6 : 4);
        L.RatingPill = new Rectangle(x, pillY, font.Measure(TopRating, s) + 2 * pillPad, pillH);
        x = L.RatingPill.Right + (full ? 6 : 4);
        L.RecordPill = new Rectangle(x, pillY, font.Measure(TopRecord, s) + 2 * pillPad, pillH);

        var right = L.W - pad;
        var backW = font.Measure(Loc.T("pvp.back"), s) + 2 * (full ? 16 : 10);
        L.BackBtn = new Rectangle(right - backW, pillY, backW, pillH);
        var chestsW = 32 + 8 + font.Measure(Loc.T("pvp.chests"), s) + 2 * (full ? 12 : 8);
        L.ChestsBtn = new Rectangle(L.BackBtn.X - (full ? 14 : 8) - chestsW, pillY, chestsW, pillH);
        var coin = PvpArt.CoinSize * 2;
        var pointsW = coin + 6 + font.Measure(TopPoints, s) + 2 * (full ? 11 : 8);
        var rankingW = PvpArt.TrophySize * 2 + 8 + font.Measure(Loc.T("pvp.ranking"), s) + 2 * (full ? 12 : 8);
        L.RankingBtn = new Rectangle(L.ChestsBtn.X - (full ? 14 : 8) - rankingW, pillY, rankingW, pillH);
        L.PointsPill = new Rectangle(L.RankingBtn.X - (full ? 14 : 8) - pointsW, pillY, pointsW, pillH);
        L.HelpBtn = new Rectangle(L.PointsPill.X - (full ? 14 : 8) - pillH, pillY, pillH, pillH);   // « ? » : relance le tuto
    }

    private void LayoutCommander(Lay L)
    {
        var full = L.Full;
        var p = L.Cmd;
        var inset = PvpArt.PanelFrame + (full ? 13 : 6);
        L.CmdInner = new Rectangle(p.X + inset, p.Y + inset - 2, p.Width - 2 * inset, p.Height - 2 * inset + 2);
        var inner = L.CmdInner;
        var headerH = Context.Font.LineHeight(1);
        var nameH = Context.Font.LineHeight(full ? 3 : 2);
        L.BarStep = full ? 31 : 15;
        L.BarH = full ? 12 : 7;
        // Portrait ×3 (×2 en compact), réduit d'un cran si le plateau animé du déplacement ne tient plus dessous.
        int Needed(int scale) => headerH + (full ? 8 : 4) + 64 * scale + (full ? 6 : 4) + nameH + (full ? 8 : 6) + 4
                                 + (full ? 16 : 10) + 4 * L.BarStep + (full ? 4 : 2) + MoveAttackGrid.BoardOuter;
        L.PortraitScale = full ? 3 : 2;
        if (Needed(L.PortraitScale) > inner.Height)
            L.PortraitScale--;
        var size = 64 * L.PortraitScale;
        var py = inner.Y + headerH + (full ? 8 : 4);
        L.Portrait = new Rectangle(inner.Center.X - size / 2, py, size, size);
        var arrowW = full ? 40 : 20;
        var arrowH = full ? 48 : 36;
        L.CmdPrev = new Rectangle(inner.X, L.Portrait.Center.Y - arrowH / 2 + (full ? 12 : 6), arrowW, arrowH);
        L.CmdNext = new Rectangle(inner.Right - arrowW, L.CmdPrev.Y, arrowW, arrowH);
        L.CmdName = new Rectangle(inner.X, L.Portrait.Bottom + (full ? 6 : 4), inner.Width, nameH);
        L.CmdDots = new Rectangle(inner.X, L.CmdName.Bottom + (full ? 8 : 6), inner.Width, 4);
        L.BarsY = L.CmdDots.Bottom + (full ? 16 : 10);
    }

    private void LayoutReserve(Lay L, Engine.UI.Text.ITextFont font)
    {
        var full = L.Full;
        var p = L.Res;
        var inset = PvpArt.PanelFrame + (full ? 9 : 3);
        L.ResInner = new Rectangle(p.X + inset, p.Y + inset, p.Width - 2 * inset, p.Height - 2 * inset);
        var inner = L.ResInner;
        var tabH = full ? 32 : 22;
        var tabPad = full ? 12 : 6;
        var ty = inner.Y + (full ? 3 : 2);
        // Pagination à DROITE de l'en-tête : ‹ 1/2 › (boutons carrés de la hauteur des onglets).
        var counterW = font.Measure("00/00", 1) + (full ? 12 : 6);
        L.PageNext = new Rectangle(inner.Right - tabH, ty, tabH, tabH);
        L.PageCounter = new Rectangle(L.PageNext.X - counterW, ty, counterW, tabH);
        L.PagePrev = new Rectangle(L.PageCounter.X - tabH, ty, tabH, tabH);
        L.TabUnits = new Rectangle(inner.X, ty, font.Measure(Loc.T("pvp.palette_units"), 1 + (full ? 1 : 0)) + 2 * tabPad, tabH);
        var equipW = font.Measure(Loc.T("pvp.palette_equipment"), full ? 2 : 1) + 2 * tabPad;
        L.TabEquip = new Rectangle(L.TabUnits.Right + 4, ty, equipW, tabH);
        // Langue verbeuse : les deux onglets se partagent la place laissée par la pagination (texte réduit / tronqué).
        var maxTabs = L.PagePrev.X - (full ? 8 : 4) - inner.X;
        if (L.TabEquip.Right - inner.X > maxTabs)
        {
            var half = (maxTabs - 4) / 2;
            L.TabUnits.Width = half;
            L.TabEquip = new Rectangle(L.TabUnits.Right + 4, ty, half, tabH);
        }

        L.Cols = full ? 4 : 3;
        L.CardW = full ? 84 : 66;
        L.CardH = full ? 94 : 80;
        var gap = (inner.Width - L.Cols * L.CardW) / (L.Cols - 1);
        var rowGap = full ? 6 : 5;
        var gy = ty + tabH + (full ? 8 : 6);
        // La fiche est passée sur le plateau : la grille descend jusqu'en bas du panneau (autant de rangées qu'il en tient).
        var cardRows = Math.Max(1, (inner.Bottom - gy + rowGap) / (L.CardH + rowGap));
        cardRows = Math.Min(cardRows, CardsPerPageMax / L.Cols);
        L.CardCount = L.Cols * cardRows;
        if (cardRows > 1)   // rangées réparties sur toute la hauteur : pas de vide sous la dernière
            rowGap = (inner.Bottom - gy - cardRows * L.CardH) / (cardRows - 1);
        for (var k = 0; k < L.CardCount; k++)
            L.Cards[k] = new Rectangle(inner.X + k % L.Cols * (L.CardW + gap), gy + k / L.Cols * (L.CardH + rowGap), L.CardW, L.CardH);
    }

    /// <summary>Lift maximal d'un pion survolé (cf. DrawBoard) : la fiche s'arrête au-dessus de sa tête.</summary>
    private const int SheetHeadroom = 16;

    /// <summary>
    /// Fiche (pion, commandant, équipement) : panneau posé sur les 4 rangées juste au-dessus des deux lignes du
    /// joueur (vides pendant la composition), sur toute la largeur du plateau. Bien plus de place que sous la réserve.
    /// </summary>
    private void LayoutSheet(Lay L)
    {
        var full = L.Full;
        var tile = PvpBoardRenderer.Tile;
        var playerTop = PvpBoardRenderer.Size - 2;
        var sheetRows = Math.Max(1, Math.Min(4, playerTop - L.FirstRow));
        var top = L.Board.Y + (playerTop - sheetRows - L.FirstRow) * tile;
        L.SheetFrame = new Rectangle(L.Board.X + 4, top + 4, L.Board.Width - 8, sheetRows * tile - 4 - SheetHeadroom);
        var ins = PvpArt.PanelFrame + (full ? 10 : 6);
        L.Sheet = new Rectangle(L.SheetFrame.X + ins, L.SheetFrame.Y + ins, L.SheetFrame.Width - 2 * ins, L.SheetFrame.Height - 2 * ins);
    }

    private const int DotW = 8, DotActiveW = 16, DotH = 4, DotGap = 4;

    private static int DotsWidth(int pages) => pages <= 0 ? 0 : (pages - 1) * (DotW + DotGap) + DotActiveW;

    private void LayoutBand(Lay L, Engine.UI.Text.ITextFont font)
    {
        var full = L.Full;
        var b = L.Band;
        var pad = L.Pad;
        var friendW = Math.Max(full ? 146 : 98, font.Measure(Loc.T("pvp.challenge"), 1) + (full ? 28 : 20));
        var friendH = full ? 40 : 32;
        L.Friend = new Rectangle(pad, b.Center.Y - friendH / 2 + 1, friendW, friendH);
        var launchW = full ? 340 : 240;
        var launchH = full ? 60 : 44;
        L.Launch = new Rectangle(L.W - pad - launchW, b.Y + (b.Height - launchH) / 2 - 2, launchW, launchH);
        var cancelW = Math.Max(full ? 110 : 80, font.Measure(Loc.T("pvp.cancel"), full ? 2 : 1) + 24);
        var cancelH = full ? 50 : 36;
        L.Cancel = new Rectangle(L.Launch.X - (full ? 16 : 10) - cancelW, b.Center.Y - cancelH / 2, cancelW, cancelH);
        var cx = L.Friend.Right + (full ? 16 : 12);
        L.Center = new Rectangle(cx, b.Y, L.Launch.X - (full ? 16 : 12) - cx, b.Height);
    }

    private void LayoutChests(Lay L, int oy)
    {
        var full = L.Full;
        var ox = Math.Max(0, (L.W - (full ? 1280 : 960)) / 2);
        var margin = full ? 32 : 12;
        var titleY = L.Top.Bottom + (full ? 28 : 12) + oy;
        L.ChestTitle = new Rectangle(ox + margin, titleY, (full ? 1280 : 960) - 2 * margin, Context.Font.LineHeight(full ? 4 : 3));
        var py = L.ChestTitle.Bottom + (full ? 24 : 12);
        var bottomLine = Context.Font.LineHeight(1) + (full ? 30 : 16);
        var ph = L.H - oy - bottomLine - py;
        var gap = full ? 24 : 12;
        var pw = ((full ? 1280 : 960) - 2 * margin - gap) / 2;
        L.ChestPanels[0] = new Rectangle(ox + margin, py, pw, ph);
        L.ChestPanels[1] = new Rectangle(ox + margin + pw + gap, py, pw, ph);
    }
}
