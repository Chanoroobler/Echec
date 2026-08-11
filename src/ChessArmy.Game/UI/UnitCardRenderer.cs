using System;
using System.Collections.Generic;
using System.IO;
using ChessArmy.Core.Battle;
using ChessArmy.Engine;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.UI;

/// <summary>
/// Rendu AUTONOME d'une carte de pion et d'une tuile de pion, utilisable depuis n'importe quel écran (il ne
/// dépend que du <see cref="GameContext"/> : ni <c>Run</c>, ni état de combat). Reproduit la disposition de
/// <c>GameplayScene.DrawCardLayout</c> sur son chemin « découvert » — mêmes constantes, mêmes assets, mêmes
/// couleurs — sans équipement, arbre ni compteur de kills.
///
/// NOTE : <see cref="CodexView"/> porte encore sa PROPRE copie de ce rendu ; elle pourra déléguer ici.
/// Possède son cache de textures, à libérer via <see cref="Unload"/> depuis <c>Scene.Unload</c>.
/// </summary>
public sealed class UnitCardRenderer
{
    public const int CardW = 168, CardH = 340;
    public const int TileW = 96, TileH = 92;

    private const int CardPad = 12;
    private const int CardTierW = 23, CardTierH = 9;
    private const int CardDomaine = 39;
    private const int TileFramePad = 6;

    private readonly GameContext _ctx;
    private readonly Dictionary<string, Texture2D?> _textures = new();

    public UnitCardRenderer(GameContext context) => _ctx = context;

    /// <summary>Libère les textures chargées (appelé par <c>Scene.Unload</c>).</summary>
    public void Unload()
    {
        foreach (var tex in _textures.Values)
            tex?.Dispose();
        _textures.Clear();
    }

    public static string NameOf(UnitClass c) => Loc.TOr("unit." + c.Asset, c.Name).ToUpperInvariant();

    /// <summary>Carte complète : tier, nom, sprite, domaine, barre de PV, stats, mots-clés.</summary>
    public void DrawCard(SpriteBatch sb, UnitClass c, Domaine domaine, Rectangle rect)
    {
        _ctx.Style.DrawPanel(sb, rect);
        var y = rect.Y + CardPad;

        DrawTierAndDomaine(sb, c.Tier, domaine, rect);

        // Boîte de titre à la hauteur réelle du nom (14 latin / 24 cjk) : le nom remplit la boîte au lieu de
        // déborder vers le haut sur l'en-tête tier/domaine.
        var titleH = _ctx.Font.LineHeight(2);
        _ctx.Font.DrawCentered(sb, NameOf(c), new Rectangle(rect.X, y, rect.Width, titleH), 2, Palette.White);
        y += titleH + 8;

        var sprite = new Rectangle(rect.X + (rect.Width - 64) / 2, y, 64, 64);
        if (Sprite(c) is { } front)
            sb.Draw(front, sprite, Color.White);
        y = sprite.Bottom + 6;

        var dom = new Rectangle(rect.X + (rect.Width - CardDomaine) / 2, y, CardDomaine, CardDomaine);
        DrawDomaine(sb, domaine, dom);
        y = dom.Bottom + 10;

        var bar = new Rectangle(rect.X + CardPad, y, rect.Width - 2 * CardPad, 14);
        DrawHpBar(sb, bar, c.MaxHp);
        y = bar.Bottom + 2;
        _ctx.Font.DrawCentered(sb, $"{c.MaxHp}/{c.MaxHp}", new Rectangle(rect.X, y, rect.Width, 8), 1, Palette.White);
        y += 14;

        y = DrawStat(sb, rect, y, "deg", Loc.T("stat.power"), c.Damage.ToString(), Palette.Brown3);
        y = DrawStat(sb, rect, y, "dep", Loc.T("stat.movement"), c.MoveRange.ToString(), Palette.Cyan2);
        DrawStat(sb, rect, y, "tir", Loc.T("stat.range"), c.AttackRange.ToString(), Palette.Yellow2);

        // Mots-clés (traits) séparés par « | », ancrés en bas et empilés vers le haut.
        var kws = KeywordLabels(c);
        if (kws.Count > 0)
        {
            var lines = Wrap(string.Join(" | ", kws), rect.Width - 2 * CardPad, 1);
            var ty = rect.Bottom - CardPad - lines.Count * 9;
            foreach (var line in lines)
            {
                _ctx.Font.DrawCentered(sb, line, new Rectangle(rect.X, ty, rect.Width, 8), 1, Palette.Cyan1);
                ty += 9;
            }
        }
    }

    /// <summary>
    /// Tuile compacte : sprite 64×64 natif dans un cadre, nom dessous. Même rendu que le Codex, silhouette
    /// noire et « ??? » comprises quand <paramref name="revealed"/> est faux.
    /// </summary>
    public void DrawTile(SpriteBatch sb, UnitClass c, Rectangle rect, bool highlighted, bool revealed = true)
    {
        var sprite = new Rectangle(rect.X + (rect.Width - 64) / 2, rect.Y + TileFramePad, 64, 64);
        var frame = Inflate(sprite, TileFramePad);
        _ctx.Style.DrawRecessed(sb, frame);
        Fill(sb, Inflate(frame, -2), Palette.Blue1 * 0.5f);

        if (Sprite(c) is { } tex)
            sb.Draw(tex, sprite, revealed ? Color.White : Color.Black);
        else if (!revealed)
            Fill(sb, sprite, Palette.Black1);

        if (highlighted)
            Border(sb, Inflate(frame, 3), Palette.Yellow2, 2);

        _ctx.Font.DrawCentered(sb, revealed ? NameOf(c) : "???",
            new Rectangle(rect.X - 8, frame.Bottom + 3, rect.Width + 16, 10), 1,
            revealed ? Palette.White : Palette.Grey);
    }

    /// <summary>
    /// Sprite d'une classe agrandi d'un facteur ENTIER (jamais fractionnaire : le pixel-art doit rester net),
    /// centré sur <paramref name="center"/>. Renvoie le rectangle occupé, vide si le PNG est absent.
    /// </summary>
    public Rectangle DrawScaled(SpriteBatch sb, UnitClass c, Point center, int scale, Color? tint = null)
    {
        if (Sprite(c) is not { } tex)
            return Rectangle.Empty;
        var dest = new Rectangle(center.X - tex.Width * scale / 2, center.Y - tex.Height * scale / 2,
            tex.Width * scale, tex.Height * scale);
        sb.Draw(tex, dest, tint ?? Color.White);
        return dest;
    }

    /// <summary>Icône 39×39 d'un domaine (motif de déplacement), avec repli dessiné si le PNG manque.</summary>
    public void DrawDomaineBadge(SpriteBatch sb, Domaine domaine, Rectangle area) => DrawDomaine(sb, domaine, area);

    /// <summary>Barre de PV « 1 carré = 1 PV », comme sur la carte de jeu.</summary>
    public void DrawHp(SpriteBatch sb, Rectangle area, int maxHp) => DrawHpBar(sb, area, maxHp);

    /// <summary>
    /// Ligne de stat « icône 32×32 + libellé + valeur alignée à droite », sur toute la largeur de
    /// <paramref name="row"/> : même grammaire visuelle que la carte, utilisable hors carte.
    /// </summary>
    public void DrawStatRow(SpriteBatch sb, Rectangle row, string iconKey, string label, string value, Color valueColor)
    {
        const int iconSize = 32;
        var icon = new Rectangle(row.X, row.Y + (row.Height - iconSize) / 2, iconSize, iconSize);
        if (Tex($"Icons/stat_{iconKey}") is { } png)
            DrawFit(sb, png, icon);
        else
        {
            Fill(sb, Inflate(icon, 1), Palette.Black1);
            Fill(sb, icon, Palette.Navy2);
            _ctx.Font.DrawCentered(sb, iconKey.ToUpperInvariant()[..1], icon, 2, valueColor);
        }

        _ctx.Font.Draw(sb, label, new Vector2(icon.Right + 8, row.Y + (row.Height - 7) / 2), 1, Palette.Blue1);
        var vw = _ctx.Font.Measure(value, 2);
        _ctx.Font.Draw(sb, value, new Vector2(row.Right - vw, row.Y + (row.Height - 14) / 2), 2, valueColor);
    }

    /// <summary>Pose la carte à côté de <paramref name="anchor"/>, TOUJOURS rabattue dans <paramref name="bounds"/>.</summary>
    public void DrawCardNear(SpriteBatch sb, UnitClass c, Domaine domaine, Rectangle anchor, Rectangle bounds)
    {
        var x = anchor.Right + 8;
        if (x + CardW > bounds.Right)
            x = anchor.X - CardW - 8;   // pas la place à droite : bascule à gauche
        x = Math.Clamp(x, bounds.X, Math.Max(bounds.X, bounds.Right - CardW));
        var y = Math.Clamp(anchor.Center.Y - CardH / 2, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - CardH));
        DrawCard(sb, c, domaine, new Rectangle(x, y, CardW, CardH));
    }

    // ── Morceaux de carte ────────────────────────────────────────────────────────

    private void DrawTier(SpriteBatch sb, int tier, Rectangle area)
    {
        if (Tex($"Icons/tier_{tier}") is { } png)
        {
            sb.Draw(png, area, Color.White);   // 23×9 natif, pas de déformation
            return;
        }
        Fill(sb, Inflate(area, 1), Palette.Black1);
        Fill(sb, area, Palette.Navy2);
        _ctx.Font.DrawCentered(sb, $"T{tier}", area, 1, Palette.White);
    }

    /// <summary>Marge haute : icône de TIER + NOM DU DOMAINE, l'ensemble centré (cf. carte de jeu).</summary>
    private void DrawTierAndDomaine(SpriteBatch sb, int tier, Domaine domaine, Rectangle rect)
    {
        var name = Loc.TOr($"domaine.{domaine}".ToLowerInvariant(), domaine.ToString().ToUpperInvariant());
        const int gap = 5;
        var nameW = _ctx.Font.Measure(name, 1);
        var startX = rect.X + (rect.Width - (CardTierW + gap + nameW)) / 2;
        DrawTier(sb, tier, new Rectangle(startX, rect.Y + 2, CardTierW, CardTierH));
        _ctx.Font.Draw(sb, name, new Vector2(startX + CardTierW + gap, rect.Y + 3), 1, Palette.Cyan1);
    }

    private void DrawDomaine(SpriteBatch sb, Domaine domaine, Rectangle area)
    {
        if (Tex($"Icons/domaine_{domaine}".ToLowerInvariant()) is { } png)
        {
            DrawFit(sb, png, area);
            return;
        }
        var color = domaine switch
        {
            Domaine.Cavalier => Palette.Green1,
            Domaine.Tour => Palette.Navy1,
            Domaine.Dame => Palette.Yellow1,
            _ => Palette.Grey,
        };
        Fill(sb, Inflate(area, 1), Palette.Black1);
        Fill(sb, area, color);
        _ctx.Font.DrawCentered(sb, domaine.ToString()[..1].ToUpperInvariant(), area, 2, Palette.Black1);
    }

    private int DrawStat(SpriteBatch sb, Rectangle card, int y, string iconKey, string label, string value, Color valueColor)
    {
        const int iconSize = 32;
        var icon = new Rectangle(card.X + CardPad, y, iconSize, iconSize);
        if (Tex($"Icons/stat_{iconKey}") is { } png)
            DrawFit(sb, png, icon);
        else
        {
            Fill(sb, Inflate(icon, 1), Palette.Black1);
            Fill(sb, icon, Palette.Navy2);
            _ctx.Font.DrawCentered(sb, iconKey.ToUpperInvariant()[..1], icon, 2, valueColor);
        }

        _ctx.Font.Draw(sb, label, new Vector2(icon.Right + 8, y + (iconSize - 7) / 2), 1, Palette.Blue1);
        var vw = _ctx.Font.Measure(value, 2);
        _ctx.Font.Draw(sb, value, new Vector2(card.Right - CardPad - vw, y + (iconSize - 14) / 2), 2, valueColor);
        return y + iconSize + 4;
    }

    private void DrawHpBar(SpriteBatch sb, Rectangle area, int maxHp)
    {
        if (maxHp <= 0)
            return;
        const int gap = 1;
        for (var i = 0; i < maxHp; i++)
        {
            var left = area.X + (int)Math.Round((double)i * area.Width / maxHp);
            var right = area.X + (int)Math.Round((double)(i + 1) * area.Width / maxHp);
            var w = Math.Max(1, right - left - (i < maxHp - 1 ? gap : 0));
            Fill(sb, new Rectangle(left, area.Y, w, area.Height), Palette.Purple5);   // pion neutre : PV pleins
        }
    }

    /// <summary>Libellés des mots-clés (traits) d'une classe, sans doublon (mêmes règles que la carte de jeu).</summary>
    private static List<string> KeywordLabels(UnitClass c)
    {
        var seen = new HashSet<string>();
        var labels = new List<string>();
        void Add(string label)
        {
            if (label.Length > 0 && seen.Add(label))
                labels.Add(label);
        }
        foreach (var t in c.Traits)
            Add(UnitKeywords.For(t).Label);
        if (c.PiercesAllies && !c.Traits.Contains("Franchissement"))
            Add(UnitKeywords.PiercesAllies.Label);
        if (c.MinAttackRange > 1)
            Add(UnitKeywords.DeadZone.Label);
        return labels;
    }

    /// <summary>Découpe un texte en lignes tenant dans <paramref name="maxWidth"/> px (échelle donnée).</summary>
    public List<string> Wrap(string text, int maxWidth, int scale)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (_ctx.Font.Measure(candidate, scale) > maxWidth && line.Length > 0)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = candidate;
            }
        }
        if (line.Length > 0)
            lines.Add(line);
        return lines.Count > 0 ? lines : new List<string> { "" };
    }

    // ── Textures / primitives ─────────────────────────────────────────────────────

    private Texture2D? Sprite(UnitClass c) => Tex($"Units/{c.Asset}_front") ?? Tex($"Units/{c.Asset}");

    /// <summary>PNG sous <c>Assets/&lt;rel&gt;.png</c> (mis en cache ; null si absent).</summary>
    private Texture2D? Tex(string rel)
    {
        if (_textures.TryGetValue(rel, out var tex))
            return tex;
        tex = Textures.LoadPngOrNull(_ctx.GraphicsDevice, Path.Combine(AppContext.BaseDirectory, $"Assets/{rel}.png"));
        _textures[rel] = tex;
        return tex;
    }

    /// <summary>Sprite à sa taille native centré dans <paramref name="area"/>, réduit d'un facteur ENTIER si trop grand.</summary>
    private static void DrawFit(SpriteBatch sb, Texture2D sprite, Rectangle area)
    {
        var src = sprite.Width;
        var box = Math.Min(area.Width, area.Height);
        var size = box >= src ? src : src / ((src + box - 1) / box);
        var x = area.X + (area.Width - size) / 2;
        var y = area.Y + (area.Height - size) / 2;
        sb.Draw(sprite, new Rectangle(x, y, size, size), Color.White);
    }

    private void Fill(SpriteBatch sb, Rectangle r, Color c) => sb.Draw(_ctx.Pixel, r, c);

    private void Border(SpriteBatch sb, Rectangle r, Color c, int t)
    {
        Fill(sb, new Rectangle(r.X, r.Y, r.Width, t), c);
        Fill(sb, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
        Fill(sb, new Rectangle(r.X, r.Y, t, r.Height), c);
        Fill(sb, new Rectangle(r.Right - t, r.Y, t, r.Height), c);
    }

    private static Rectangle Inflate(Rectangle r, int by) =>
        new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);
}
