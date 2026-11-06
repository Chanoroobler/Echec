using System;
using ChessArmy.Core.Equip;
using ChessArmy.Engine;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.UI;
using ChessArmy.Engine.UI.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.UI;

/// <summary>
/// Habillage des écrans du multijoueur (écran Armée, Coffres) : panneaux cuivrés, boutons, bouton principal,
/// petites icônes pixel-art dessinées sans texture (pièce, cadenas), ovale d'ombre et troncature « … ».
/// Aplats unis de la <see cref="Palette"/>, aucune allocation par frame.
/// </summary>
public static class PvpArt
{
    public static void Fill(SpriteBatch sb, Texture2D pixel, Rectangle r, Color c) => sb.Draw(pixel, r, c);

    public static void Border(SpriteBatch sb, Texture2D pixel, Rectangle r, Color c, int t)
    {
        sb.Draw(pixel, new Rectangle(r.X, r.Y, r.Width, t), c);
        sb.Draw(pixel, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
        sb.Draw(pixel, new Rectangle(r.X, r.Y + t, t, r.Height - 2 * t), c);
        sb.Draw(pixel, new Rectangle(r.Right - t, r.Y + t, t, r.Height - 2 * t), c);
    }

    /// <summary>Épaisseur totale du cadre d'un panneau (bordure 3 + liseré 2) : le contenu commence au-delà.</summary>
    public const int PanelFrame = 5;

    /// <summary>Panneau : fond <c>Black4</c>, bordure 3 px <c>Brown2</c> et liseré intérieur 2 px <c>Brown1</c>.</summary>
    public static void Panel(SpriteBatch sb, Texture2D pixel, Rectangle r)
    {
        Fill(sb, pixel, r, Palette.Black4);
        Border(sb, pixel, r, Palette.Brown2, 3);
        Border(sb, pixel, new Rectangle(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6), Palette.Brown1, 2);
    }

    /// <summary>Bouton : fond <c>Black5</c> (survol : <c>Black4</c> plus sombre), bordure 2 px <c>Brown1</c> (survol <c>Brown2</c>) ;
    /// sélectionné : fond <c>Blue3</c> et bordure <c>Yellow2</c>. Désactivé : fond <c>Black3</c>, bordure <c>Black5</c>.</summary>
    public static void Button(SpriteBatch sb, Texture2D pixel, Rectangle r, bool selected, bool hover, bool enabled = true)
    {
        if (!enabled)
        {
            Fill(sb, pixel, r, Palette.Black3);
            Border(sb, pixel, r, Palette.Black5, 2);
            return;
        }
        Fill(sb, pixel, r, selected ? Palette.Blue3 : hover ? Palette.Black4 : Palette.Black5);
        Border(sb, pixel, r, selected ? Palette.Yellow2 : hover ? Palette.Brown2 : Palette.Brown1, 2);
    }

    /// <summary>Bouton PRINCIPAL (lancer, ouvrir, continuer) : fond <c>Brown2</c>, bordure 3 px <c>Yellow2</c>,
    /// ombre pleine 4 px <c>Brown1</c> dessous. Enfoncé : il descend sur son ombre. Renvoie le décalage du contenu.</summary>
    public static int Primary(SpriteBatch sb, Texture2D pixel, Rectangle r, bool hover, bool pressed)
    {
        const int Shadow = 4;
        var dy = pressed ? 3 : 0;
        Fill(sb, pixel, new Rectangle(r.X, r.Bottom, r.Width, Shadow - dy), Palette.Brown1);
        var face = new Rectangle(r.X, r.Y + dy, r.Width, r.Height);
        Fill(sb, pixel, face, hover ? Palette.Brown3 : Palette.Brown2);
        Border(sb, pixel, face, Palette.Yellow2, 3);
        return dy;
    }

    /// <summary>Bouton principal grisé (indisponible) : même gabarit, fond <c>Black5</c>, bordure <c>Grey</c>.</summary>
    public static void PrimaryDisabled(SpriteBatch sb, Texture2D pixel, Rectangle r)
    {
        Fill(sb, pixel, new Rectangle(r.X, r.Bottom, r.Width, 4), Palette.Black1);
        Fill(sb, pixel, r, Palette.Black5);
        Border(sb, pixel, r, Palette.Grey * 0.6f, 3);
    }

    /// <summary>Couleur d'une rareté d'équipement : Commun crème, Rare cyan, Légendaire cuivre.</summary>
    public static Color RarityColor(EquipmentRarity rarity) => rarity switch
    {
        EquipmentRarity.Rare => Palette.Cyan1,
        EquipmentRarity.Legendary => Palette.Brown3,
        _ => Palette.White,
    };

    // ── Texte ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="text"/> s'il tient dans <paramref name="maxWidth"/>, sinon tronqué avec « … ». Allocation
    /// seulement quand il faut tronquer (rare : noms longs dans une langue verbeuse).
    /// </summary>
    public static string Fit(ITextFont font, string text, int maxWidth, int scale)
    {
        if (font.Measure(text, scale) <= maxWidth)
            return text;
        for (var n = text.Length - 1; n > 0; n--)
        {
            var candidate = string.Concat(text.AsSpan(0, n).TrimEnd(), "…");
            if (font.Measure(candidate, scale) <= maxWidth)
                return candidate;
        }
        return "…";
    }

    /// <summary>Plus grande échelle (parmi <paramref name="preferred"/>, puis en dessous) à laquelle le texte tient.</summary>
    public static int ScaleToFit(ITextFont font, string text, int maxWidth, int preferred)
    {
        for (var s = preferred; s > 1; s--)
            if (font.Measure(text, s) <= maxWidth)
                return s;
        return 1;
    }

    // ── Icônes pixel-art (aucune texture) ─────────────────────────────────────────

    // Pièce de points 10×10 : # contour Brown1, o or Brown3, h reflet Yellow2, d gravure Brown2.
    private static readonly string[] CoinRows =
    {
        "...####...",
        ".##hhoo##.",
        "#hhoooood#",
        "#hooodood#",
        "#hooodood#",
        "#hooodood#",
        "#hooodood#",
        "#oooooodd#",
        ".##oddd##.",
        "...####...",
    };

    // Cadenas 8×10 : s anse Yellow3, # corps Black1, o Grey, d trou de serrure Black1.
    private static readonly string[] LockRows =
    {
        "..ssss..",
        ".s....s.",
        ".s....s.",
        "########",
        "#oooooo#",
        "#oodooo#",
        "#oodooo#",
        "#oooooo#",
        "#oooooo#",
        "########",
    };

    /// <summary>Taille de base (logique) de la pièce : l'image piece.png fait CoinSize×2, affichée à ×1 quand scale = 2.</summary>
    public const int CoinSize = 10;
    public const int LockW = 8, LockH = 10;

    // Icônes dessinées à la main (Assets/Icons, 20×20 = ×2 de la base 10×10), chargées une fois au premier tracé.
    // null après un essai raté (fichier absent ou mauvaise taille) : le motif ASCII prend le relais.
    private static Texture2D? _coinTex, _trophyTex;
    private static bool _iconsTried;

    private static void EnsureIcons(GraphicsDevice gd)
    {
        if (_iconsTried)
            return;
        _iconsTried = true;
        _coinTex = LoadIcon(gd, "Assets/Icons/piece.png", CoinSize * 2);
        _trophyTex = LoadIcon(gd, "Assets/Icons/trophee.png", TrophySize * 2);
    }

    private static Texture2D? LoadIcon(GraphicsDevice gd, string relPath, int size)
    {
        var tex = Textures.LoadPngOrNull(gd, System.IO.Path.Combine(AppContext.BaseDirectory, relPath));
        if (tex != null && (tex.Width != size || tex.Height != size))
        {
            tex.Dispose();
            tex = null;
        }
        return tex;
    }

    /// <summary>
    /// Dessine une icône native (2× la base) dans la boîte <c>baseSize × scale</c> : à ×(scale/2), centrée. Faux si
    /// l'icône manque ou si l'échelle est 1 (trop petite pour l'icône native) : l'appelant dessine alors le motif.
    /// </summary>
    private static bool DrawIcon(SpriteBatch sb, Texture2D? tex, int baseSize, Point at, int scale, float alpha)
    {
        if (tex == null || scale < 2)
            return false;
        var size = tex.Width * (scale / 2);
        var off = (baseSize * scale - size) / 2;
        sb.Draw(tex, new Rectangle(at.X + off, at.Y + off, size, size), Color.White * alpha);
        return true;
    }

    /// <summary>
    /// Pièce de points (boîte <see cref="CoinSize"/>×scale), coin haut-gauche en <paramref name="at"/>. Image
    /// <c>piece.png</c> à ×(scale/2), centrée dans la boîte ; motif 10×10 en secours.
    /// </summary>
    public static void Coin(SpriteBatch sb, Texture2D pixel, Point at, int scale, float alpha = 1f)
    {
        EnsureIcons(pixel.GraphicsDevice);
        if (DrawIcon(sb, _coinTex, CoinSize, at, scale, alpha))
            return;
        Pattern(sb, pixel, CoinRows, at, scale, alpha, static c => c switch
        {
            '#' => Palette.Brown1, 'o' => Palette.Brown3, 'h' => Palette.Yellow2, 'd' => Palette.Brown2, _ => Color.Transparent,
        });
    }

    private static readonly string[] TrophyRows =
    {
        "##########",
        "#hooooood#",
        "#hooooood#",
        ".#hoooood#",
        ".#hooood#.",
        "..#hood#..",
        "...#od#...",
        "....##....",
        "..######..",
        "..#dddd#..",
    };

    /// <summary>Taille de base (logique) de la coupe : l'image trophee.png fait TrophySize×2, affichée à ×1 quand scale = 2.</summary>
    public const int TrophySize = 10;

    /// <summary>
    /// Coupe du bouton CLASSEMENT (boîte <see cref="TrophySize"/>×scale), coin haut-gauche en <paramref name="at"/>.
    /// Image <c>trophee.png</c> à ×(scale/2), centrée ; motif 10×10 en secours.
    /// </summary>
    public static void Trophy(SpriteBatch sb, Texture2D pixel, Point at, int scale)
    {
        EnsureIcons(pixel.GraphicsDevice);
        if (DrawIcon(sb, _trophyTex, TrophySize, at, scale, 1f))
            return;
        Pattern(sb, pixel, TrophyRows, at, scale, 1f, static c => c switch
        {
            '#' => Palette.Brown1, 'o' => Palette.Brown3, 'h' => Palette.Yellow2, 'd' => Palette.Brown2, _ => Color.Transparent,
        });
    }

    /// <summary>Cadenas, coin haut-gauche en <paramref name="at"/>, agrandi d'un facteur entier.</summary>
    public static void Lock(SpriteBatch sb, Texture2D pixel, Point at, int scale, float alpha = 1f) =>
        Pattern(sb, pixel, LockRows, at, scale, alpha, static c => c switch
        {
            's' => Palette.Yellow3, '#' or 'd' => Palette.Black1, 'o' => Palette.Grey, _ => Color.Transparent,
        });

    /// <summary>Dessine un motif par segments horizontaux de même caractère (un rectangle par segment).</summary>
    private static void Pattern(SpriteBatch sb, Texture2D pixel, string[] rows, Point at, int scale, float alpha,
        Func<char, Color> color)
    {
        for (var y = 0; y < rows.Length; y++)
        {
            var line = rows[y];
            for (var x = 0; x < line.Length; x++)
            {
                var ch = line[x];
                if (ch == '.')
                    continue;
                var start = x;
                while (x + 1 < line.Length && line[x + 1] == ch)
                    x++;
                sb.Draw(pixel, new Rectangle(at.X + start * scale, at.Y + y * scale, (x - start + 1) * scale, scale),
                    color(ch) * alpha);
            }
        }
    }

    /// <summary>Ombre ovale pleine (une rangée de pixels par ligne), centrée sur <paramref name="center"/>.</summary>
    public static void Oval(SpriteBatch sb, Texture2D pixel, Point center, int width, int height, Color c)
    {
        for (var y = 0; y < height; y++)
        {
            var t = (y + 0.5f) / height * 2f - 1f;
            var w = (int)MathF.Round(width * MathF.Sqrt(Math.Max(0f, 1f - t * t)));
            if (w > 0)
                sb.Draw(pixel, new Rectangle(center.X - w / 2, center.Y - height / 2 + y, w, 1), c);
        }
    }
}
