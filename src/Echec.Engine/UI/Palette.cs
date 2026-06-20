using Microsoft.Xna.Framework;

namespace Echec.Engine.UI;

/// <summary>
/// Palette de couleurs du jeu. TOUTES les couleurs du projet doivent venir d'ici.
/// Aucune couleur en dur ailleurs dans le code.
/// (Portée depuis CosyFarmer pour cohérence visuelle entre les projets.)
/// </summary>
public static class Palette
{
    // ── Noirs / fonds ──────────────────────────────────────────────────────────
    public static readonly Color Black1 = Hex("#1a1516");
    public static readonly Color Black2 = Hex("#21181b");
    public static readonly Color Black3 = Hex("#2c2025");
    public static readonly Color Black4 = Hex("#3d2936");
    public static readonly Color Black5 = Hex("#52333f");

    // ── Bordeaux / bruns chauds ────────────────────────────────────────────────
    public static readonly Color Brown1 = Hex("#8f4d57");
    public static readonly Color Brown2 = Hex("#bd6a62");
    public static readonly Color Brown3 = Hex("#ffae70");
    public static readonly Color Brown4 = Hex("#ffce91");
    public static readonly Color Brown5 = Hex("#fea7a7");

    // ── Pourpres / magentas ────────────────────────────────────────────────────
    public static readonly Color Purple1 = Hex("#451d42");
    public static readonly Color Purple2 = Hex("#611e4a");
    public static readonly Color Purple3 = Hex("#81204f");
    public static readonly Color Purple4 = Hex("#ad2f45");
    public static readonly Color Purple5 = Hex("#de523e");

    // ── Oranges / jaunes ───────────────────────────────────────────────────────
    public static readonly Color Orange1 = Hex("#e67839");
    public static readonly Color Yellow1 = Hex("#f0b541");
    public static readonly Color Yellow2 = Hex("#ffee83");
    public static readonly Color Yellow3 = Hex("#c8d45d");
    public static readonly Color Yellow4 = Hex("#a4c443");

    // ── Verts ──────────────────────────────────────────────────────────────────
    public static readonly Color Green1 = Hex("#63ab3f");
    public static readonly Color Green2 = Hex("#3b7d4f");
    public static readonly Color Green3 = Hex("#233b36");
    public static readonly Color Green4 = Hex("#2a594f");
    public static readonly Color Green5 = Hex("#368782");

    // ── Bleus / cyans ──────────────────────────────────────────────────────────
    public static readonly Color Cyan1 = Hex("#4fa4b8");
    public static readonly Color Cyan2 = Hex("#92e8c0");
    public static readonly Color White = Hex("#ffffff");
    public static readonly Color Blue1 = Hex("#a3a7c2");
    public static readonly Color Blue2 = Hex("#686f99");

    // ── Bleus foncés ───────────────────────────────────────────────────────────
    public static readonly Color Navy1 = Hex("#454a6a");
    public static readonly Color Navy2 = Hex("#1d2235");

    // ── Gris (jauges : segment vide) ────────────────────────────────────────────
    public static readonly Color Grey = Hex("#6b6660");

    private static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        var r = System.Convert.ToByte(hex[0..2], 16);
        var g = System.Convert.ToByte(hex[2..4], 16);
        var b = System.Convert.ToByte(hex[4..6], 16);
        return new Color(r, g, b);
    }
}
