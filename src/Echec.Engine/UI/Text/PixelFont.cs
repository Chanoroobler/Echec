using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Engine.UI.Text;

/// <summary>
/// Police bitmap procédurale 5×7, encodée directement en code (pas de pipeline de
/// contenu). Chaque glyphe est dessiné pixel par pixel depuis la texture 1×1, ce qui
/// le rend strictement pixel-perfect à l'échelle entière de l'UI et cohérent avec la
/// DA pixel art. Caractères gérés : A–Z, a–z, 0–9 et quelques symboles ; par DÉFAUT le
/// texte est passé en MAJUSCULES (comportement historique), sauf appel avec
/// <c>preserveCase</c> qui rend alors les minuscules avec leurs glyphes dédiés. Les
/// caractères inconnus (espace…) avancent simplement le curseur.
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

    public void Draw(SpriteBatch sb, string text, Vector2 pos, int scale, Color color, bool preserveCase = false)
    {
        int cx = (int)pos.X;
        int cy = (int)pos.Y;
        foreach (char ch in text)
        {
            if (TryResolveGlyph(ch, preserveCase, out var rows))
                DrawGlyph(sb, rows!, cx, cy, scale, color);
            cx += (GlyphW + Spacing) * scale;
        }
    }

    /// <summary>
    /// Résout le glyphe d'un caractère : casse demandée (par défaut MAJUSCULE), puis repli MAJUSCULE, puis
    /// repli SANS ACCENT (é→e, ç→c, É→E…). Les accents ne sont donc rendus qu'en MINUSCULES — le glyphe fait
    /// 7 px, sans place au-dessus d'une capitale — et partout ailleurs on retombe proprement sur la lettre nue.
    /// </summary>
    private bool TryResolveGlyph(char ch, bool preserveCase, out byte[]? rows)
    {
        var c = preserveCase ? ch : char.ToUpperInvariant(ch);
        if (_glyphs.TryGetValue(c, out rows))
            return true;
        var upper = char.ToUpperInvariant(c);
        if (upper != c && _glyphs.TryGetValue(upper, out rows))
            return true;
        var bare = StripAccent(c);
        if (bare != c && (_glyphs.TryGetValue(bare, out rows) || _glyphs.TryGetValue(char.ToUpperInvariant(bare), out rows)))
            return true;
        rows = null;
        return false;
    }

    /// <summary>Lettre de base d'un caractère accentué (é→e, ç→c…) via décomposition Unicode ; sinon le caractère tel quel.</summary>
    private static char StripAccent(char c)
    {
        var d = c.ToString().Normalize(System.Text.NormalizationForm.FormD);
        return char.IsLetter(d[0]) ? d[0] : c;
    }

    /// <summary><paramref name="preserveCase"/> : garde les minuscules du texte (sinon tout est mis en capitales).</summary>
    public void DrawCentered(SpriteBatch sb, string text, Rectangle area, int scale, Color color,
        bool preserveCase = false)
    {
        int w = Measure(text, scale);
        int h = GlyphH * scale;
        Draw(sb, text, new Vector2(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2), scale, color,
            preserveCase);
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

    // ── Texte en dégradé vertical tramé ──────────────────────────────────────
    // Matrice de Bayer 4×4 (dithering ordonné), même motif que le fond du menu.
    private static readonly int[,] Bayer4 =
    {
        {  0,  8,  2, 10 },
        { 12,  4, 14,  6 },
        {  3, 11,  1,  9 },
        { 15,  7, 13,  5 },
    };

    /// <summary>
    /// Texte en DÉGRADÉ VERTICAL TRAMÉ (dithering ordonné Bayer), du premier palier de <paramref name="stops"/>
    /// en haut au dernier en bas, réparti sur la hauteur d'un glyphe. Chaque SOUS-pixel est tramé (pixels
    /// nets, palette respectée) plutôt qu'un dégradé lissé. Plus coûteux que <see cref="Draw"/> (un quad par
    /// sous-pixel) : à réserver aux gros textes ponctuels (titres). Un seul palier ⇒ repli sur <see cref="Draw"/>.
    /// </summary>
    public void DrawGradient(SpriteBatch sb, string text, Vector2 pos, int scale, Color[] stops,
        bool preserveCase = false)
    {
        if (stops.Length == 0)
            return;
        if (stops.Length == 1)
        {
            Draw(sb, text, pos, scale, stops[0], preserveCase);
            return;
        }

        int cx = (int)pos.X;
        int cy = (int)pos.Y;
        int totalH = GlyphH * scale;
        foreach (char ch in text)
        {
            if (TryResolveGlyph(ch, preserveCase, out var rows))
                DrawGlyphGradient(sb, rows!, cx, cy, scale, stops, totalH);
            cx += (GlyphW + Spacing) * scale;
        }
    }

    /// <summary>Variante centrée de <see cref="DrawGradient"/> (même conventions que <see cref="DrawCentered"/>).</summary>
    public void DrawCenteredGradient(SpriteBatch sb, string text, Rectangle area, int scale, Color[] stops,
        bool preserveCase = false)
    {
        int w = Measure(text, scale);
        int h = GlyphH * scale;
        DrawGradient(sb, text, new Vector2(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2), scale,
            stops, preserveCase);
    }

    private void DrawGlyphGradient(SpriteBatch sb, byte[] rows, int x, int y, int scale, Color[] stops, int totalH)
    {
        int segs = Math.Max(1, stops.Length - 1);
        for (int r = 0; r < GlyphH; r++)
        {
            byte bits = rows[r];
            for (int col = 0; col < GlyphW; col++)
            {
                if ((bits & (1 << (GlyphW - 1 - col))) == 0)
                    continue;
                int bx = x + col * scale;
                int by = y + r * scale;
                for (int sy = 0; sy < scale; sy++)
                {
                    int gy = r * scale + sy;                       // position verticale dans le texte (0..totalH-1)
                    float p = totalH > 1 ? (float)gy / (totalH - 1) * segs : 0f;
                    int si = Math.Min((int)p, segs - 1);
                    float frac = p - si;                           // avancée (0..1) dans le segment courant
                    Color lo = stops[si], hi = stops[si + 1];
                    int py = by + sy;
                    for (int sx = 0; sx < scale; sx++)
                    {
                        int px = bx + sx;
                        float threshold = (Bayer4[py & 3, px & 3] + 0.5f) / 16f;
                        sb.Draw(_pixel, new Rectangle(px, py, 1, 1), frac > threshold ? hi : lo);
                    }
                }
            }
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

        // ── Minuscules (a–z) : hauteur d'x sur 5 rangées, hampes en haut, pas de jambages sous la ligne
        //    de base (le glyphe ne fait que 7 px) — rendues seulement via Draw(preserveCase: true). ──────
        ['a'] = G(".....", ".....", ".###.", "....#", ".####", "#...#", ".####"),
        ['b'] = G("#....", "#....", "#....", "####.", "#...#", "#...#", "####."),
        ['c'] = G(".....", ".....", ".###.", "#...#", "#....", "#...#", ".###."),
        ['d'] = G("....#", "....#", "....#", ".####", "#...#", "#...#", ".####"),
        ['e'] = G(".....", ".....", ".###.", "#...#", "#####", "#....", ".###."),
        ['f'] = G("..##.", ".#...", ".#...", "####.", ".#...", ".#...", ".#..."),
        ['g'] = G(".....", ".....", ".####", "#...#", ".####", "....#", ".###."),
        ['h'] = G("#....", "#....", "#....", "####.", "#...#", "#...#", "#...#"),
        ['i'] = G(".....", "..#..", ".....", "..#..", "..#..", "..#..", "..#.."),
        ['j'] = G(".....", "..#..", ".....", "..#..", "..#..", "..#..", "###.."),
        ['k'] = G("#....", "#....", "#..#.", "#.#..", "##...", "#.#..", "#..#."),
        ['l'] = G(".#...", ".#...", ".#...", ".#...", ".#...", ".#...", ".##.."),
        ['m'] = G(".....", ".....", "##.##", "#.#.#", "#.#.#", "#.#.#", "#.#.#"),
        ['n'] = G(".....", ".....", "####.", "#...#", "#...#", "#...#", "#...#"),
        ['o'] = G(".....", ".....", ".###.", "#...#", "#...#", "#...#", ".###."),
        ['p'] = G(".....", ".....", "####.", "#...#", "####.", "#....", "#...."),
        ['q'] = G(".....", ".....", ".####", "#...#", ".####", "....#", "....#"),
        ['r'] = G(".....", ".....", "#.##.", "##...", "#....", "#....", "#...."),
        ['s'] = G(".....", ".....", ".####", "#....", ".###.", "....#", "####."),
        ['t'] = G(".#...", ".#...", "####.", ".#...", ".#...", ".#..#", "..##."),
        ['u'] = G(".....", ".....", "#...#", "#...#", "#...#", "#...#", ".####"),
        ['v'] = G(".....", ".....", "#...#", "#...#", "#...#", ".#.#.", "..#.."),
        ['w'] = G(".....", ".....", "#...#", "#...#", "#.#.#", "#.#.#", ".#.#."),
        ['x'] = G(".....", ".....", "#...#", ".#.#.", "..#..", ".#.#.", "#...#"),
        ['y'] = G(".....", ".....", "#...#", "#...#", ".####", "....#", ".###."),
        ['z'] = G(".....", ".....", "#####", "...#.", "..#..", ".#...", "#####"),

        // ── Minuscules accentuées (français) : accent aigu / grave / circonflexe / tréma dans les 2 rangées
        //    du haut (a,e,i,o,u n'ont pas de hampe). Uniquement en minuscule — cf. TryResolveGlyph. ─────────
        ['é'] = G("...#.", "..#..", ".###.", "#...#", "#####", "#....", ".###."),
        ['è'] = G(".#...", "..#..", ".###.", "#...#", "#####", "#....", ".###."),
        ['ê'] = G("..#..", ".#.#.", ".###.", "#...#", "#####", "#....", ".###."),
        ['ë'] = G(".#.#.", ".....", ".###.", "#...#", "#####", "#....", ".###."),
        ['à'] = G(".#...", "..#..", ".###.", "....#", ".####", "#...#", ".####"),
        ['â'] = G("..#..", ".#.#.", ".###.", "....#", ".####", "#...#", ".####"),
        ['ç'] = G(".....", ".....", ".###.", "#....", "#....", ".###.", "..#.."),
        ['î'] = G("..#..", ".#.#.", ".....", "..#..", "..#..", "..#..", "..#.."),
        ['ï'] = G(".....", ".#.#.", ".....", "..#..", "..#..", "..#..", "..#.."),
        ['ô'] = G("..#..", ".#.#.", ".###.", "#...#", "#...#", "#...#", ".###."),
        ['ù'] = G(".#...", "..#..", "#...#", "#...#", "#...#", "#...#", ".####"),
        ['û'] = G("..#..", ".#.#.", "#...#", "#...#", "#...#", "#...#", ".####"),
        ['ü'] = G(".#.#.", ".....", "#...#", "#...#", "#...#", "#...#", ".####"),

        ['%'] = G("##..#", "##.#.", "..#..", ".#...", "#.#..", "#..##", "...##"),
        [':'] = G(".....", "..#..", "..#..", ".....", "..#..", "..#..", "....."),
        ['.'] = G(".....", ".....", ".....", ".....", ".....", "..#..", "..#.."),
        ['/'] = G("....#", "....#", "...#.", "..#..", ".#...", "#....", "#...."),
        ['-'] = G(".....", ".....", ".....", "#####", ".....", ".....", "....."),
        ['+'] = G(".....", "..#..", "..#..", "#####", "..#..", "..#..", "....."),
        ['<'] = G("...#.", "..#..", ".#...", "#....", ".#...", "..#..", "...#."),
        ['>'] = G(".#...", "..#..", "...#.", "....#", "...#.", "..#..", ".#..."),
        ['|'] = G("..#..", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."),
        ['('] = G("...#.", "..#..", ".#...", ".#...", ".#...", "..#..", "...#."),
        [')'] = G(".#...", "..#..", "...#.", "...#.", "...#.", "..#..", ".#..."),
        ['\''] = G("..#..", "..#..", ".#...", ".....", ".....", ".....", "....."),
        ['?'] = G(".###.", "#...#", "...#.", "..#..", "..#..", ".....", "..#.."),
        ['!'] = G("..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.."),
    };
}
