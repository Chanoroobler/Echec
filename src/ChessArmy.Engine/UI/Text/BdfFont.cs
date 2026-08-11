using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Engine.UI.Text;

/// <summary>
/// Police bitmap chargée depuis un fichier BDF (Glyph Bitmap Distribution Format), utilisée pour le chinois :
/// la <see cref="PixelFont"/> 5×7 codée en dur ne peut pas porter les idéogrammes CJK. On embarque un
/// sous-ensemble de <b>Fusion Pixel 12px</b> (police pixel-art libre, licence OFL) réduit aux seuls glyphes
/// réellement affichés (cf. l'outil de trim). Rendu pixel par pixel depuis la texture 1×1, comme PixelFont,
/// donc net à l'échelle entière.
///
/// Le BDF donne par glyphe une avance (DWIDTH), une boîte (BBX : largeur hauteur décalageX décalageY relatif
/// à la ligne de base) et un bitmap. On dessine en plaçant chaque glyphe par rapport à une LIGNE DE BASE
/// commune (<see cref="Ascent"/>), ce qui aligne latin et CJK de hauteurs différentes. Police proportionnelle
/// (avances variables) : <see cref="Measure"/> somme les avances.
/// </summary>
public sealed class BdfFont : ITextFont
{
    // Métriques calées sur Fusion Pixel 12px : les idéogrammes montent d'environ 11 px au-dessus de la ligne
    // de base et descendent d'1 px ; une hauteur de ligne de 12 les contient pile. Les jambages latins
    // (g p y) dépassent d'1-2 px sous la boîte, sans gêne (rares en mode chinois).
    private const int Ascent = 11;
    private const int Line = 12;

    private sealed class Glyph
    {
        public int Advance;
        public int W, H, XOff, YOff;
        public int RowBits;      // bits significatifs par rangée = ceil(W/8)*8
        public int[] Rows = Array.Empty<int>();
    }

    private readonly Texture2D _pixel;
    private readonly PixelFont? _latin;
    private readonly Dictionary<int, Glyph> _glyphs = new();
    private int _defaultAdvance = 6;

    // Largeur d'un caractère latin rendu par la PixelFont (glyphe + espacement) : identique à PixelFont.Measure.
    private const int LatinAdvance = PixelFont.GlyphW + PixelFont.Spacing;

    public int GlyphHeight => Line;
    public int GlyphCount => _glyphs.Count;
    public bool Has(int codepoint) => _glyphs.ContainsKey(codepoint);

    // Latin de base + étendu (jusqu'à U+017F : accents FR/PL/TR/DE/ES) : rendu par la PixelFont 7px pour que
    // chiffres et lettres gardent leur taille normale au lieu de grossir en glyphe CJK. Au-delà (idéogrammes,
    // ponctuation pleine chasse) : police BDF. Seuil de plage = pas de coût de normalisation sur les CJK.
    private bool UseLatin(char ch) => _latin != null && ch < 'ƀ';

    /// <summary>Charge un BDF depuis le disque. <paramref name="latin"/> (recommandé) rend le latin/chiffres
    /// via la PixelFont (police COMPOSITE). <paramref name="only"/> limite le parsing aux codepoints donnés.</summary>
    public BdfFont(Texture2D pixel, string bdfPath, PixelFont? latin = null, HashSet<int>? only = null)
    {
        _pixel = pixel;
        _latin = latin;
        if (File.Exists(bdfPath))
            Parse(File.ReadAllLines(bdfPath), only);
        if (_glyphs.TryGetValue(' ', out var sp) && sp.Advance > 0)
            _defaultAdvance = sp.Advance;
    }

    private void Parse(string[] lines, HashSet<int>? only)
    {
        int enc = -1, adv = 0, w = 0, h = 0, xo = 0, yo = 0;
        int[]? rows = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("ENCODING ", StringComparison.Ordinal))
                enc = ParseInt(line, 9);
            else if (line.StartsWith("DWIDTH ", StringComparison.Ordinal))
                adv = ParseInt(line, 7);
            else if (line.StartsWith("BBX ", StringComparison.Ordinal))
            {
                var p = line.Substring(4).Split(' ');
                w = int.Parse(p[0]); h = int.Parse(p[1]); xo = int.Parse(p[2]); yo = int.Parse(p[3]);
            }
            else if (line == "BITMAP")
            {
                rows = new int[Math.Max(0, h)];
                for (var r = 0; r < h && i + 1 + r < lines.Length; r++)
                {
                    var hex = lines[i + 1 + r].Trim();
                    rows[r] = hex.Length == 0 ? 0 : int.Parse(hex, NumberStyles.HexNumber);
                }
                i += h;
            }
            else if (line.StartsWith("ENDCHAR", StringComparison.Ordinal))
            {
                if (enc >= 0 && rows != null && (only == null || only.Contains(enc)))
                    _glyphs[enc] = new Glyph
                    {
                        Advance = adv, W = w, H = h, XOff = xo, YOff = yo,
                        RowBits = ((Math.Max(w, 1) + 7) / 8) * 8, Rows = rows,
                    };
                enc = -1; adv = 0; w = h = xo = yo = 0; rows = null;
            }
        }
    }

    // Premier entier après un offset (le BDF sépare par des espaces ; DWIDTH/ENCODING peuvent en avoir 2).
    private static int ParseInt(string line, int start)
    {
        var s = line.Substring(start).TrimStart();
        var end = s.IndexOf(' ');
        return int.Parse(end < 0 ? s : s.Substring(0, end));
    }

    public int LineHeight(int scale = 1) => Line * scale;

    public int Measure(string text, int scale = 1)
    {
        var w = 0;
        foreach (var ch in text)
        {
            if (UseLatin(ch)) w += LatinAdvance;
            else w += _glyphs.TryGetValue(ch, out var g) ? g.Advance : _defaultAdvance;
        }
        return w * scale;
    }

    public void Draw(SpriteBatch sb, string text, Vector2 pos, int scale, Color color, bool preserveCase = false)
    {
        int penX = (int)pos.X;
        int baseline = (int)pos.Y + Ascent * scale;
        // Ligne MIXTE (au moins un idéogramme) : on descend le latin pour que sa base s'aligne sur la ligne de
        // base CJK (sinon les chiffres flottent en haut à côté des caractères chinois). Ligne 100% latine :
        // aucun décalage, alignement HAUT comme la PixelFont, pour que les valeurs seules tombent où l'appelant
        // les attend. (7 px = hauteur pixel ; base CJK à Ascent.)
        var mixed = false;
        foreach (var ch in text)
            if (!UseLatin(ch)) { mixed = true; break; }
        int latinDy = mixed ? (Ascent - PixelFont.GlyphH) * scale : 0;
        foreach (var ch in text)
        {
            if (UseLatin(ch))
            {
                _latin!.DrawChar(sb, ch, new Vector2(penX, pos.Y + latinDy), scale, color, preserveCase);
                penX += LatinAdvance * scale;
                continue;
            }
            if (_glyphs.TryGetValue(ch, out var g))
            {
                int gx = penX + g.XOff * scale;
                int gy = baseline - (g.YOff + g.H) * scale;
                for (var r = 0; r < g.H; r++)
                {
                    int bits = g.Rows[r];
                    for (var c = 0; c < g.W; c++)
                        if (((bits >> (g.RowBits - 1 - c)) & 1) != 0)
                            sb.Draw(_pixel, new Rectangle(gx + c * scale, gy + r * scale, scale, scale), color);
                }
                penX += g.Advance * scale;
            }
            else penX += _defaultAdvance * scale;
        }
    }

    public void DrawCentered(SpriteBatch sb, string text, Rectangle area, int scale, Color color, bool preserveCase = false)
    {
        int w = Measure(text, scale);
        int h = ContentHeight(text) * scale;   // hauteur RÉELLE : 7 (latin/chiffres seuls) ou 12 (contient du CJK)
        Draw(sb, text, new Vector2(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2), scale, color, preserveCase);
    }

    // Hauteur visuelle d'une chaîne : pleine ligne CJK dès qu'elle contient un idéogramme, sinon hauteur pixel.
    // Sans ça un nombre seul (18, 28/28) rendu à 7px serait centré comme s'il faisait 12px → dessiné trop haut.
    private int ContentHeight(string text)
    {
        foreach (var ch in text)
            if (!UseLatin(ch)) return Line;
        return PixelFont.GlyphH;
    }

    // Le chinois ne reçoit pas le dégradé tramé (réservé aux gros titres latins) : repli sur une couleur pleine.
    public void DrawGradient(SpriteBatch sb, string text, Vector2 pos, int scale, Color[] stops, bool preserveCase = false)
    {
        if (stops.Length > 0) Draw(sb, text, pos, scale, stops[0], preserveCase);
    }

    public void DrawCenteredGradient(SpriteBatch sb, string text, Rectangle area, int scale, Color[] stops, bool preserveCase = false)
    {
        if (stops.Length > 0) DrawCentered(sb, text, area, scale, stops[0], preserveCase);
    }
}
