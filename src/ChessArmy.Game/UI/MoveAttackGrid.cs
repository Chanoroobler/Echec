using System;
using ChessArmy.Core.Battle;
using ChessArmy.Engine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.UI;

/// <summary>
/// Plateau 7×7 animé des cartes de pion (combat, recrutement, codex, sélection et fiche du commandant), généré à
/// la volée : le sprite du pion y parcourt ses déplacements. Cases UNIES, sans décoration : déplacement en jaune,
/// tir en rouge, les deux à la fois = jaune sous un voile rouge semi-transparent (orange). Damier sombre pour les
/// cases vides. Motif théorique, plateau vide : directions du domaine, portées, zone morte, saut en L. Taille
/// IMPAIRE pour que le pion soit au centre ; au-delà, la portée est coupée.
/// </summary>
public static class MoveAttackGrid
{
    private const int Gap = 1;        // joint noir entre les cases
    private const int Frame = 2;      // cadre : 1 px de joint + 1 px de liseré
    private const byte Move = 1, Attack = 2;

    private static readonly Color MoveColor = Palette.Yellow2;   // même jaune que la surbrillance de déplacement en jeu
    private static readonly Color AttackColor = Palette.Purple5;
    /// <summary>Opacité du rouge posé sur le jaune d'une case à la fois de déplacement ET de tir.</summary>
    private const float AttackVeilAlpha = 0.5f;

    // ── Grand plateau animé (carte détaillée de combat) ──────────────────────────────────────────────
    // 7×7 cases de 16 px (15 + 1 px de joint) : le pion, à MOITIÉ de sa taille, est posé socle au centre de sa
    // case et déborde sur ses voisines. Il part du centre vers chaque case de déplacement, une par une, en revenant
    // TOUJOURS au centre entre deux ; les zones de tir restent marquées en rouge.
    public const int BoardSize = 7;
    private const int BoardHalf = BoardSize / 2;
    private const int BoardPitch = 16;
    private const int BoardGridPx = BoardSize * BoardPitch - Gap;
    /// <summary>Côté total du plateau, cadre compris.</summary>
    public const int BoardOuter = BoardGridPx + 2 * Frame;

    // Chorégraphie d'une visite : aller, pause sur la case, retour, pause au centre (lente, à dessein).
    private const int VisitOutMs = 520, VisitHoldMs = 320, VisitBackMs = 520, VisitRestMs = 260;
    private const int VisitMs = VisitOutMs + VisitHoldMs + VisitBackMs + VisitRestMs;
    // Hauteur du centre du socle dans le sprite (0 haut … 1 bas) : en jeu, le sprite de 64 est remonté d'un quart
    // de case pour centrer son socle (GameplayScene.SpriteLiftFraction = 0,25), soit 0,5 + 0,25.
    public const float SocleCenterFraction = 0.75f;
    private const int HopPx = 8;   // hauteur du saut en cloche du cavalier (les autres glissent)

    private static readonly byte[] BoardCells = new byte[BoardSize * BoardSize];
    private static readonly int[] Visits = new int[BoardSize * BoardSize];   // cases à visiter, ordre de VisitOrder
    // Les cases autour du pion, triées dans le sens horaire depuis le haut (puis de la plus proche à la plus
    // lointaine) : le pion « fait le tour » de ses possibilités au lieu de sauter d'un bord à l'autre.
    private static readonly int[] VisitOrder = BuildVisitOrder();

    private static int[] BuildVisitOrder()
    {
        var order = new int[BoardSize * BoardSize - 1];
        var keys = new double[order.Length];
        var n = 0;
        for (var row = 0; row < BoardSize; row++)
            for (var col = 0; col < BoardSize; col++)
            {
                int dx = col - BoardHalf, dy = row - BoardHalf;
                if (dx == 0 && dy == 0)
                    continue;
                var angle = Math.Atan2(dx, -dy);              // 0 = haut, sens horaire
                if (angle < 0) angle += 2 * Math.PI;
                order[n] = row * BoardSize + col;
                keys[n] = Math.Round(angle, 4) * 10 + Math.Max(Math.Abs(dx), Math.Abs(dy));
                n++;
            }
        Array.Sort(keys, order);
        return order;
    }

    /// <summary>
    /// Grand plateau 7×7 animé, ancré en haut à gauche de <paramref name="area"/> (côté <see cref="BoardOuter"/>).
    /// <paramref name="pawn"/> = sprite de face du pion (null : simple pastille), <paramref name="revealed"/>
    /// faux : silhouette noire immobile sur un plateau vierge (rien n'est révélé d'une unité non découverte).
    /// Renvoie l'emplacement CANVAS du pion (sprite à moitié de sa taille). <paramref name="drawPawn"/> faux : le
    /// pion n'est PAS dessiné ici, l'appelant le pose à la résolution de l'écran pour garder chaque pixel du
    /// sprite (cf. GameplayScene.DrawScreenOverlays) ; vrai : repli dans le canvas, réduit au plus proche.
    /// </summary>
    public static Rectangle DrawBoard(SpriteBatch sb, Texture2D pixel, Rectangle area, Texture2D? pawn, bool drawPawn,
        bool revealed, Domaine moveDomaine, int moveRange, bool reposition,
        Domaine attackDomaine, int attackRange, int minAttackRange)
    {
        var ox = area.X + Frame;
        var oy = area.Y + Frame;
        var cell = BoardPitch - Gap;

        Fill(BoardCells, BoardHalf, revealed, moveDomaine, moveRange, reposition, attackDomaine, attackRange, minAttackRange);
        DrawFrame(sb, pixel, area with { Width = BoardOuter, Height = BoardOuter });
        for (var row = 0; row < BoardSize; row++)
            for (var col = 0; col < BoardSize; col++)
                DrawCell(sb, pixel, new Rectangle(ox + col * BoardPitch, oy + row * BoardPitch, cell, cell),
                    BoardCells[row * BoardSize + col], row, col);

        // Case visitée en ce moment et avancement (0 = au centre, 1 = arrivé sur la case).
        var visitCount = 0;
        foreach (var idx in VisitOrder)
            if ((BoardCells[idx] & Move) != 0)
                Visits[visitCount++] = idx;
        var center = new Point(ox + BoardHalf * BoardPitch, oy + BoardHalf * BoardPitch);
        var pos = center;
        var hop = 0;
        if (visitCount > 0)
        {
            var t = (int)(Environment.TickCount64 % ((long)visitCount * VisitMs));
            var target = Visits[t / VisitMs];
            var local = t % VisitMs;
            var progress = local < VisitOutMs ? Ease(local / (float)VisitOutMs)
                : local < VisitOutMs + VisitHoldMs ? 1f
                : local < VisitOutMs + VisitHoldMs + VisitBackMs ? 1f - Ease((local - VisitOutMs - VisitHoldMs) / (float)VisitBackMs)
                : 0f;
            // Du centre vers la case visitée, au pixel près (pas de position sous-pixel).
            var dest = new Point(ox + target % BoardSize * BoardPitch, oy + target / BoardSize * BoardPitch);
            pos = new Point(center.X + (int)MathF.Round((dest.X - center.X) * progress),
                center.Y + (int)MathF.Round((dest.Y - center.Y) * progress));
            if (Movement.Kind(moveDomaine) == MovementKind.Jump)
                hop = (int)MathF.Round(MathF.Sin(progress * MathF.PI) * HopPx);
        }

        // Emplacement du pion, en coordonnées CANVAS : sprite à MOITIÉ de sa taille, le CENTRE DU SOCLE posé au
        // centre de la case. Il déborde sur les cases voisines, comme un pion posé sur un plateau.
        var pw = (pawn?.Width ?? 2 * cell) / 2;
        var ph = (pawn?.Height ?? 2 * cell) / 2;
        var pawnRect = new Rectangle(pos.X + BoardPitch / 2 - pw / 2,
            pos.Y + BoardPitch / 2 - (int)(ph * SocleCenterFraction) - hop, pw, ph);
        if (drawPawn)
        {
            if (pawn != null)
                sb.Draw(pawn, pawnRect, revealed ? Color.White : Color.Black);   // repli : ÷2 au plus proche dans le canvas
            else
                sb.Draw(pixel, new Rectangle(pos.X + 2, pos.Y + 2 - hop, cell - 4, cell - 4), revealed ? Palette.White : Palette.Black1);
        }
        return pawnRect;
    }

    /// <summary>
    /// <see cref="DrawBoard"/> + pion NET : envoyé à la couche écran (<see cref="NativeSprites"/>) quand c'est
    /// possible et que <paramref name="allowNative"/> (rien ne sera dessiné par-dessus dans le canvas cette frame),
    /// sinon dessiné dans le canvas. <paramref name="alpha"/> = fondu de la carte (le pion le suit) ;
    /// <paramref name="pawnTint"/> = teinte du pion en couche écran (voile de la carte), défaut blanc / noir.
    /// </summary>
    public static void DrawBoardWithPawn(SpriteBatch sb, Texture2D pixel, Rectangle area, Texture2D? pawn,
        bool revealed, Domaine moveDomaine, int moveRange, bool reposition,
        Domaine attackDomaine, int attackRange, int minAttackRange, bool allowNative = true, float alpha = 1f,
        Color? pawnTint = null)
    {
        var native = pawn != null && allowNative && NativeSprites.CanQueue;
        var pawnRect = DrawBoard(sb, pixel, area, pawn, drawPawn: !native, revealed, moveDomaine, moveRange, reposition,
            attackDomaine, attackRange, minAttackRange);
        if (native)
            NativeSprites.Queue(pawn!, pawnRect, (pawnTint ?? (revealed ? Color.White : Color.Black)) * alpha);
    }

    private static float Ease(float t) => t * t * (3f - 2f * t);   // départ et arrivée en douceur

    /// <summary>Cadre : liseré clair puis fond noir qui sert de joints entre les cases.</summary>
    private static void DrawFrame(SpriteBatch sb, Texture2D pixel, Rectangle outer)
    {
        sb.Draw(pixel, outer, Palette.Navy1);
        sb.Draw(pixel, new Rectangle(outer.X + 1, outer.Y + 1, outer.Width - 2, outer.Height - 2), Palette.Black1);
    }

    /// <summary>Une case unie : jaune (déplacement), rouge (tir), jaune sous un voile rouge (les deux), damier sinon.</summary>
    private static void DrawCell(SpriteBatch sb, Texture2D pixel, Rectangle r, byte flags, int row, int col)
    {
        switch (flags)
        {
            case Move:
                sb.Draw(pixel, r, MoveColor);
                break;
            case Attack:
                sb.Draw(pixel, r, AttackColor);
                break;
            case Move | Attack:
                sb.Draw(pixel, r, MoveColor);
                sb.Draw(pixel, r, AttackColor * AttackVeilAlpha);
                break;
            default:
                sb.Draw(pixel, r, ((row + col) & 1) == 0 ? Palette.Black4 : Palette.Black3);
                break;
        }
    }

    /// <summary>Vide puis remplit <paramref name="cells"/> : déplacement (motif du domaine + « Repositionnement »)
    /// et tir. Rien n'est marqué pour une unité non découverte.</summary>
    private static void Fill(byte[] cells, int half, bool revealed, Domaine moveDomaine, int moveRange, bool reposition,
        Domaine attackDomaine, int attackRange, int minAttackRange)
    {
        Array.Clear(cells);
        if (!revealed)
            return;
        Mark(cells, half, moveDomaine, moveRange, 1, Move);
        if (reposition)
        {
            Set(cells, half, -1, 0, Move);
            Set(cells, half, 1, 0, Move);
        }
        Mark(cells, half, attackDomaine, attackRange, minAttackRange, Attack);
    }

    /// <summary>Marque dans <paramref name="cells"/> (grille carrée de demi-côté <paramref name="half"/>) les cases du
    /// motif <paramref name="domaine"/> : saut (offsets du cavalier, sans portée) ou glissé de
    /// <paramref name="minRange"/> à <paramref name="range"/> pas. La portée minimale ne vaut qu'en ligne droite
    /// (en diagonale on frappe dès 1), comme dans <see cref="Match"/>.</summary>
    private static void Mark(byte[] cells, int half, Domaine domaine, int range, int minRange, byte flag)
    {
        var jump = Movement.Kind(domaine) == MovementKind.Jump;
        foreach (var dir in Movement.Vectors(domaine))
        {
            if (jump)
            {
                Set(cells, half, dir.Column, dir.Row, flag);
                continue;
            }
            var min = dir.Column != 0 && dir.Row != 0 ? 1 : minRange;
            for (var step = Math.Max(1, min); step <= Math.Min(range, half); step++)
                Set(cells, half, dir.Column * step, dir.Row * step, flag);
        }
    }

    private static void Set(byte[] cells, int half, int dx, int dy, byte flag)
    {
        if (Math.Abs(dx) <= half && Math.Abs(dy) <= half)
            cells[(dy + half) * (2 * half + 1) + dx + half] |= flag;
    }
}
