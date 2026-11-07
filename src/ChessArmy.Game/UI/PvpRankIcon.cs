using System;
using System.IO;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.UI;

/// <summary>
/// Icône de rang du multi, en deux tailles dessinées à la main en niveaux de gris : petite
/// <c>Assets/Icons/rang.png</c> (20×24) et grande <c>Assets/Icons/rang_grand.png</c> (80×96), affichées à ×1.
/// Chacune est découpée au chargement par luminance en cinq couches : contour (noir, reste noir) et quatre tons de
/// gris (du plus sombre au reflet), teintés de la rampe de couleurs du palier. Si un fichier manque ou n'a pas la
/// bonne taille, un écusson de repli 10×12 suit le même chemin, agrandi ×2 ou ×8 : l'encombrement reste identique.
/// </summary>
public sealed class PvpRankIcon
{
    /// <summary>Taille logique (base 10×12) : la petite icône occupe W×2 × H×2, la grande W×8 × H×8.</summary>
    public const int W = 10, H = 12;

    private const int Shades = 4;

    private static readonly string[] FallbackRows =
    {
        ".########.",
        "#oooooooo#",
        "#ohhhoooo#",
        "#ohoooooo#",
        "#ohoooooo#",
        "#ohoooooo#",
        "#oooooooo#",
        "#oooooooo#",
        ".#oooooo#.",
        ".#oooooo#.",
        "..#oooo#..",
        "...####...",
    };

    /// <summary>Une taille d'icône : ses couches et le facteur entier qui l'amène à la taille affichée.</summary>
    private sealed class Variant
    {
        public Texture2D Outline = null!;
        public readonly Texture2D[] Shade = new Texture2D[Shades];
        public int Scale;
        public int Width, Height;
    }

    private readonly Variant _small, _large;

    public PvpRankIcon(GraphicsDevice gd)
    {
        _small = Load(gd, "Assets/Icons/rang.png", 2);
        _large = Load(gd, "Assets/Icons/rang_grand.png", 8);
    }

    /// <summary>
    /// Charge le PNG s'il fait exactement (W×H)×<paramref name="factor"/> (affiché à ×1), sinon l'écusson de repli
    /// (affiché à ×<paramref name="factor"/>).
    /// </summary>
    private static Variant Load(GraphicsDevice gd, string relPath, int factor)
    {
        int w = W * factor, h = H * factor;
        var source = Textures.LoadPngOrNull(gd, Path.Combine(AppContext.BaseDirectory, relPath));
        Color[] pixels;
        var v = new Variant();
        if (source != null && source.Width == w && source.Height == h)
        {
            pixels = new Color[w * h];
            source.GetData(pixels);
            v.Scale = 1;
        }
        else
        {
            w = W;
            h = H;
            pixels = new Color[w * h];
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                    pixels[y * W + x] = FallbackRows[y][x] switch
                    {
                        '#' => Color.Black, 'o' => new Color(133, 140, 147), 'h' => new Color(186, 192, 198),
                        _ => Color.Transparent,
                    };
            v.Scale = factor;
        }
        source?.Dispose();
        v.Width = w;
        v.Height = h;

        // Découpe par luminance, calée sur les gris du dessin (0 / 50 / 84 / 133 / 186) : seuils à mi-chemin.
        var outline = new Color[w * h];
        var shade = new Color[Shades][];
        for (var s = 0; s < Shades; s++)
            shade[s] = new Color[w * h];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            if (p.A < 128)
                continue;
            var lum = (p.R + p.G + p.B) / 3;
            if (lum < 28) outline[i] = Palette.Black1;
            else if (lum < 72) shade[0][i] = Color.White;
            else if (lum < 112) shade[1][i] = Color.White;
            else if (lum < 162) shade[2][i] = Color.White;
            else shade[3][i] = Color.White;
        }
        v.Outline = Make(gd, w, h, outline);
        for (var s = 0; s < Shades; s++)
            v.Shade[s] = Make(gd, w, h, shade[s]);
        return v;
    }

    private static Texture2D Make(GraphicsDevice gd, int w, int h, Color[] data)
    {
        var tex = new Texture2D(gd, w, h);
        tex.SetData(data);
        return tex;
    }

    /// <summary>Couleur de base du palier (corps de l'icône).</summary>
    public static Color BaseColor(string tierId) => tierId switch
    {
        "bronze" => Palette.Brown2,
        "fer" => Palette.Grey,
        "argent" => Palette.White,
        "or" => Palette.Brown3,
        _ => Palette.Cyan1,
    };

    /// <summary>Couleur claire du palier (reflet de l'icône, nom du rang).</summary>
    public static Color LightColor(string tierId) => tierId switch
    {
        "bronze" => Palette.Brown3,
        "fer" => Palette.White,
        "argent" => Color.White,
        "or" => Palette.Yellow2,
        _ => Palette.White,
    };

    /// <summary>Ton le plus sombre du palier (gris 50 du dessin).</summary>
    private static Color DarkColor(string tierId) => tierId switch
    {
        "bronze" => Palette.Purple1,
        "fer" => Palette.Black5,
        "argent" => Palette.Yellow4,
        "or" => Palette.Brown1,
        _ => Palette.Blue2,
    };

    /// <summary>Ton intermédiaire du palier (gris 84 du dessin, le plus présent).</summary>
    private static Color MidColor(string tierId) => tierId switch
    {
        "bronze" => Palette.Brown1,
        "fer" => Palette.Yellow4,
        "argent" => Palette.Grey,
        "or" => Palette.Brown2,
        _ => Palette.Blue3,
    };

    /// <summary>Dessine l'icône du palier, coin haut-gauche en <paramref name="at"/>, à ×2 (20×24) ou ×8 (80×96) seulement.</summary>
    public void Draw(SpriteBatch sb, string tierId, Point at, int scale)
    {
        var v = scale switch
        {
            2 => _small,
            8 => _large,
            _ => throw new ArgumentOutOfRangeException(nameof(scale), "icône de rang : ×2 ou ×8 seulement"),
        };
        Draw(sb, v, tierId, new Rectangle(at.X, at.Y, v.Width * v.Scale, v.Height * v.Scale));
    }

    /// <summary>Petite icône DOUBLÉE (×4 logique, 40×48), centrée sur <paramref name="center"/> : le « pop » d'une promotion.</summary>
    public void DrawPop(SpriteBatch sb, string tierId, Point center)
    {
        var v = _small;
        int w = v.Width * v.Scale * 2, h = v.Height * v.Scale * 2;
        Draw(sb, v, tierId, new Rectangle(center.X - w / 2, center.Y - h / 2, w, h));
    }

    private static void Draw(SpriteBatch sb, Variant v, string tierId, Rectangle r)
    {
        sb.Draw(v.Shade[0], r, DarkColor(tierId));
        sb.Draw(v.Shade[1], r, MidColor(tierId));
        sb.Draw(v.Shade[2], r, BaseColor(tierId));
        sb.Draw(v.Shade[3], r, LightColor(tierId));
        sb.Draw(v.Outline, r, Color.White);
    }
}
