using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Engine.UI.Text;

/// <summary>
/// Police bitmap procédurale 5×7, encodée directement en code (pas de pipeline de
/// contenu). Chaque glyphe est dessiné pixel par pixel depuis la texture 1×1, ce qui
/// le rend strictement pixel-perfect à l'échelle entière de l'UI et cohérent avec la
/// DA pixel art. Caractères gérés : A–Z, 0–9 et quelques symboles ; les minuscules
/// sont automatiquement converties en majuscules, les caractères inconnus (espace…)
/// avancent simplement le curseur.
/// (Portée depuis CosyFarmer.)
/// </summary>
public sealed class PixelFont
{
    public const int GlyphW = 5;
    public const int GlyphH = 7;
    public const int Spacing = 1; // colonne vide entre deux glyphes

    private readonly Texture2D _pixel;
    private readonly Dictionary<char, byte[]> _glyphs;

    public PixelFont(Texture2D pixel)
    {
        _pixel = pixel;
        _glyphs = BuildGlyphs();
    }

    public int LineHeight(int scale = 1) => GlyphH * scale;

    public int Measure(string text, int scale = 1)
        => text.Length == 0 ? 0 : (text.Length * (GlyphW + Spacing) - Spacing) * scale;

    public void Draw(SpriteBatch sb, string text, Vector2 pos, int scale, Color color)
    {
        int cx = (int)pos.X;
        int cy = (int)pos.Y;
        foreach (char ch in text)
        {
            char c = char.ToUpperInvariant(ch);
            if (_glyphs.TryGetValue(c, out byte[]? rows))
                DrawGlyph(sb, rows, cx, cy, scale, color);
            cx += (GlyphW + Spacing) * scale;
        }
    }

    public void DrawCentered(SpriteBatch sb, string text, Rectangle area, int scale, Color color)
    {
        int w = Measure(text, scale);
        int h = GlyphH * scale;
        Draw(sb, text, new Vector2(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2), scale, color);
    }

    private void DrawGlyph(SpriteBatch sb, byte[] rows, int x, int y, int scale, Color color)
    {
        for (int r = 0; r < GlyphH; r++)
        {
            byte bits = rows[r];
            for (int col = 0; col < GlyphW; col++)
                if ((bits & (1 << (GlyphW - 1 - col))) != 0)
                    sb.Draw(_pixel, new Rectangle(x + col * scale, y + r * scale, scale, scale), color);
        }
    }

    // ── Encodage des glyphes ─────────────────────────────────────────────────
    private static byte RowBits(string s)
    {
        byte b = 0;
        for (int i = 0; i < GlyphW; i++)
            if (i < s.Length && s[i] == '#') b |= (byte)(1 << (GlyphW - 1 - i));
        return b;
    }

    private static byte[] G(params string[] rows)
    {
        var g = new byte[GlyphH];
        for (int r = 0; r < GlyphH; r++) g[r] = RowBits(rows[r]);
        return g;
    }

    private static Dictionary<char, byte[]> BuildGlyphs() => new()
    {
        ['A'] = G(".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"),
        ['B'] = G("####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."),
        ['C'] = G(".###.", "#...#", "#....", "#....", "#....", "#...#", ".###."),
        ['D'] = G("####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####."),
        ['E'] = G("#####", "#....", "#....", "####.", "#....", "#....", "#####"),
        ['F'] = G("#####", "#....", "#....", "####.", "#....", "#....", "#...."),
        ['G'] = G(".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".###."),
        ['H'] = G("#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"),
        ['I'] = G(".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###."),
        ['J'] = G("..###", "...#.", "...#.", "...#.", "#..#.", "#..#.", ".##.."),
        ['K'] = G("#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#"),
        ['L'] = G("#....", "#....", "#....", "#....", "#....", "#....", "#####"),
        ['M'] = G("#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#"),
        ['N'] = G("#...#", "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#"),
        ['O'] = G(".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."),
        ['P'] = G("####.", "#...#", "#...#", "####.", "#....", "#....", "#...."),
        ['Q'] = G(".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#"),
        ['R'] = G("####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"),
        ['S'] = G(".####", "#....", "#....", ".###.", "....#", "....#", "####."),
        ['T'] = G("#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."),
        ['U'] = G("#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."),
        ['V'] = G("#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.."),
        ['W'] = G("#...#", "#...#", "#...#", "#.#.#", "#.#.#", "#.#.#", ".#.#."),
        ['X'] = G("#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"),
        ['Y'] = G("#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."),
        ['Z'] = G("#####", "....#", "...#.", "..#..", ".#...", "#....", "#####"),

        ['0'] = G(".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."),
        ['1'] = G("..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."),
        ['2'] = G(".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"),
        ['3'] = G("#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###."),
        ['4'] = G("...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."),
        ['5'] = G("#####", "#....", "####.", "....#", "....#", "#...#", ".###."),
        ['6'] = G("..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."),
        ['7'] = G("#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."),
        ['8'] = G(".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."),
        ['9'] = G(".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."),

        ['%'] = G("##..#", "##.#.", "..#..", ".#...", "#.#..", "#..##", "...##"),
        [':'] = G(".....", "..#..", "..#..", ".....", "..#..", "..#..", "....."),
        ['.'] = G(".....", ".....", ".....", ".....", ".....", "..#..", "..#.."),
        ['/'] = G("....#", "....#", "...#.", "..#..", ".#...", "#....", "#...."),
        ['-'] = G(".....", ".....", ".....", "#####", ".....", ".....", "....."),
        ['<'] = G("...#.", "..#..", ".#...", "#....", ".#...", "..#..", "...#."),
        ['>'] = G(".#...", "..#..", "...#.", "....#", "...#.", "..#..", ".#..."),
    };
}
