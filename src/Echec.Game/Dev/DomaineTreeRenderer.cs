using System.Collections.Generic;
using Echec.Core.Battle;
using Echec.Engine.UI;
using Echec.Engine.UI.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Game.Dev;

/// <summary>
/// Outil DEV (lecture seule) : dessine les arbres de classes de chaque domaine.
/// Base (Soldat) en bas, évolutions vers le haut, reliées par des traits. Chaque
/// nœud montre nom + stats (PV / DEG / POR) + asset. Aucune interaction de jeu.
/// </summary>
public sealed class DomaineTreeRenderer
{
    private const int NodeW = 132;
    private const int NodeH = 56;

    private readonly Texture2D _pixel;
    private readonly PixelFont _font;
    private readonly UiStyle _style;

    public DomaineTreeRenderer(Texture2D pixel, PixelFont font, UiStyle style)
    {
        _pixel = pixel;
        _font = font;
        _style = style;
    }

    public void Draw(SpriteBatch sb, int vpW, int vpH)
    {
        sb.Draw(_pixel, new Rectangle(0, 0, vpW, vpH), Palette.Navy2 * 0.94f);
        _font.DrawCentered(sb, "ARBRES DE CLASSES - DEV", new Rectangle(0, 16, vpW, 18), 2, Palette.Yellow2);
        _font.DrawCentered(sb, "F1 OU ECHAP POUR FERMER", new Rectangle(0, 44, vpW, 10), 1, Palette.Blue1);

        // Un domaine par colonne (un seul pour l'instant).
        var domaines = Domaines.All;
        var columnW = vpW / domaines.Count;
        for (var i = 0; i < domaines.Count; i++)
        {
            var area = new Rectangle(i * columnW + 24, 80, columnW - 48, vpH - 120);
            DrawDomaine(sb, domaines[i], area);
        }
    }

    private void DrawDomaine(SpriteBatch sb, DomaineDef def, Rectangle area)
    {
        var kind = def.MovementKind == MovementKind.Jump ? "SAUT" : "GLISSE";
        var header = $"{def.Name} - {kind}";
        _font.DrawCentered(sb, header, new Rectangle(area.X, area.Y, area.Width, 14), 1, Palette.Cyan1);

        var treeArea = new Rectangle(area.X, area.Y + 24, area.Width, area.Height - 24);
        var positions = new Dictionary<UnitClass, Vector2>();
        var leaves = new List<UnitClass>();
        CollectLeaves(def.BaseClass, leaves);
        var maxTier = MaxTier(def.BaseClass);

        AssignPositions(def.BaseClass, leaves, treeArea, maxTier, positions);

        DrawEdges(sb, def.BaseClass, positions);
        foreach (var (node, center) in positions)
            DrawNode(sb, node, center);
    }

    private float AssignPositions(UnitClass node, List<UnitClass> leaves, Rectangle area, int maxTier,
        Dictionary<UnitClass, Vector2> positions)
    {
        var colW = (float)area.Width / leaves.Count;
        var rowH = (float)area.Height / maxTier;

        float x;
        if (node.IsLeaf)
        {
            x = area.X + (leaves.IndexOf(node) + 0.5f) * colW;
        }
        else
        {
            float sum = 0;
            foreach (var child in node.Evolutions)
                sum += AssignPositions(child, leaves, area, maxTier, positions);
            x = sum / node.Evolutions.Count;
        }

        // Niveau 1 en bas, niveaux supérieurs vers le haut.
        var y = area.Bottom - (node.Tier - 1) * rowH - rowH / 2f;
        positions[node] = new Vector2(x, y);
        return x;
    }

    private void DrawEdges(SpriteBatch sb, UnitClass node, Dictionary<UnitClass, Vector2> positions)
    {
        var from = positions[node];
        foreach (var child in node.Evolutions)
        {
            DrawLine(sb, new Vector2(from.X, from.Y - NodeH / 2f),
                positions[child] + new Vector2(0, NodeH / 2f), Palette.Blue2, 2);
            DrawEdges(sb, child, positions);
        }
    }

    private void DrawNode(SpriteBatch sb, UnitClass node, Vector2 center)
    {
        var rect = new Rectangle((int)(center.X - NodeW / 2f), (int)(center.Y - NodeH / 2f), NodeW, NodeH);
        _style.DrawPanel(sb, rect);

        _font.DrawCentered(sb, node.Name.ToUpperInvariant(), new Rectangle(rect.X, rect.Y + 5, rect.Width, 12), 1, Palette.White);
        _font.DrawCentered(sb, $"PV {node.MaxHp}  DEG {node.Damage}",
            new Rectangle(rect.X, rect.Y + 19, rect.Width, 8), 1, Palette.Yellow2);
        _font.DrawCentered(sb, $"DEP {node.MoveRange}  TIR {node.AttackRange}",
            new Rectangle(rect.X, rect.Y + 31, rect.Width, 8), 1, Palette.Cyan2);
        _font.DrawCentered(sb, node.Asset.ToUpperInvariant(),
            new Rectangle(rect.X, rect.Y + 43, rect.Width, 8), 1, Palette.Blue1);
    }

    private void DrawLine(SpriteBatch sb, Vector2 a, Vector2 b, Color color, int thickness)
    {
        var edge = b - a;
        var length = edge.Length();
        var angle = (float)System.Math.Atan2(edge.Y, edge.X);
        sb.Draw(_pixel, a, null, color, angle, new Vector2(0, 0.5f),
            new Vector2(length, thickness), SpriteEffects.None, 0f);
    }

    private static void CollectLeaves(UnitClass node, List<UnitClass> leaves)
    {
        if (node.IsLeaf) { leaves.Add(node); return; }
        foreach (var child in node.Evolutions)
            CollectLeaves(child, leaves);
    }

    private static int MaxTier(UnitClass node)
    {
        var max = node.Tier;
        foreach (var child in node.Evolutions)
            max = System.Math.Max(max, MaxTier(child));
        return max;
    }
}
