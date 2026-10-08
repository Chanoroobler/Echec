using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Ambiance « jour » : ombres de nuages qui glissent lentement sur tout l'écran de jeu (plateau ET mer de fond),
/// purement cosmétique.
/// <para>Un motif de nuages RÉPÉTABLE (bruit de valeur périodique, <see cref="Size"/>² pixels d'art) est calculé une
/// fois au chargement : intérieur plein, bord TRAMÉ en 3 densités (75 / 50 / 25 %). Le motif ne défile qu'en
/// DIAGONALE, par pas (+1, +1) : les trames n'utilisent que x + y (damier) et x − y, qui ne changent pas sous ce pas
/// → la trame reste calée sur la grille de l'écran et ne scintille pas en bougeant. Ancré sur le coin du plateau
/// (les ombres portées des pions s'y réfèrent, cf. <see cref="Coverage"/>). Dessiné en morceaux (répétition
/// manuelle) dans le batch ouvert : rien d'alloué par frame.</para>
/// </summary>
internal sealed class CloudShadows
{
    private const int Size = 256;              // motif répétable (pixels d'art) — pair : garde la parité du damier
    private const float Speed = 5f;            // pixels d'art / s, en diagonale (x et y ensemble)
    private const float Threshold = 0.6f;      // bruit ≥ seuil = nuage (~ un tiers du ciel couvert)
    private const float Edge = 0.06f;          // largeur du bord (en bruit), en 3 densités de trame
    public static readonly Color DayTint = new Color(16, 30, 52) * 0.2f;
    public static readonly Color SunsetTint = new Color(58, 28, 74) * 0.24f;   // coucher de soleil : ombres violettes

    private Texture2D? _tex;
    private float[]? _cover;   // densité de chaque pixel du motif (0..1), pour Coverage

    public void Load(GraphicsDevice gd)
    {
        var data = new Color[Size * Size];
        _cover = new float[Size * Size];
        var rng = new Random();
        // Trois octaves de bruit de valeur périodiques (grilles de 4, 8 et 16 cases sur le motif) : grosses masses
        // aux contours irréguliers. Grilles tirées au hasard : un ciel différent à chaque lancement.
        var g1 = Lattice(rng, 4);
        var g2 = Lattice(rng, 8);
        var g3 = Lattice(rng, 16);
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var n = (Sample(g1, 4, x, y) + 0.5f * Sample(g2, 8, x, y) + 0.25f * Sample(g3, 16, x, y)) / 1.75f;
                var density = n >= Threshold + Edge ? 1f
                    : n >= Threshold + Edge * 2f / 3f ? 0.75f
                    : n >= Threshold + Edge / 3f ? 0.5f
                    : n >= Threshold ? 0.25f
                    : 0f;
                _cover[y * Size + x] = density;
                if (Lit(x, y, density))
                    data[y * Size + x] = Color.White;
            }
        _tex = new Texture2D(gd, Size, Size);
        _tex.SetData(data);
    }

    /// <summary>
    /// Trame invariante sous un pas (+1, +1) : a = parité de x + y (damier), b = (x − y) mod 4. 50 % = une case du
    /// damier ; 25 % = une sur deux de celles-là (b = 0) ; 75 % = le damier + la moitié de l'autre (b = 1).
    /// </summary>
    private static bool Lit(int x, int y, float density)
    {
        var a = (x + y) & 1;
        var b = ((x - y) % 4 + 4) % 4;
        return density switch
        {
            >= 1f => true,
            >= 0.75f => a == 0 || b == 1,
            >= 0.5f => a == 0,
            >= 0.25f => a == 0 && b == 0,
            _ => false,
        };
    }

    public void Unload()
    {
        _tex?.Dispose();
        _tex = null;
        _cover = null;
    }

    /// <summary>Décalage entier du motif à l'instant <paramref name="time"/> : en diagonale vers le bas-droite.</summary>
    private static Point Offset(float time)
    {
        var d = (int)(time * Speed) % Size;
        return new Point(d, d);
    }

    /// <summary>
    /// Ombres sur la zone d'écran <paramref name="area"/>, motif ancré au coin du plateau <paramref name="origin"/>
    /// (écran) ; <paramref name="px"/> = taille d'un pixel d'art à l'écran.
    /// </summary>
    public void Draw(SpriteBatch sb, float time, Point origin, int px, Rectangle area, float alpha, Color? tint = null)
    {
        if (_tex == null || alpha <= 0f || area.Width <= 0 || area.Height <= 0)
            return;
        // Zone en pixels d'art depuis le coin du plateau (arrondie vers l'extérieur).
        var ax = FloorDiv(area.Left - origin.X, px);
        var ay = FloorDiv(area.Top - origin.Y, px);
        var w = FloorDiv(area.Right - origin.X + px - 1, px) - ax;
        var h = FloorDiv(area.Bottom - origin.Y + px - 1, px) - ay;
        // Mouvement SOUS-PIXEL sans quitter la grille : le motif est dessiné à la position entière d ET à d + 1 (en
        // diagonale), en fondu selon la fraction f. La trame du bord ne dépend que de x + y et x − y : elle est la même
        // aux deux positions, seul le CONTOUR du nuage glisse en douceur (plus de petits sauts d'un pixel).
        var d = time * Speed;
        var whole = (int)MathF.Floor(d);
        var f = d - whole;
        var shade = (tint ?? DayTint) * alpha;
        // Là où les deux copies se recouvrent (intérieur), deux voiles d'opacité a(1−f) et af donnent a − a²f(1−f) :
        // un peu plus pâle à mi-pas (≈ 5 %) → petit battement. On compense pour garder la même ombre à tout instant.
        var a = shade.A / 255f;
        shade *= 1f + a * f * (1f - f);
        if (f < 0.99f)
            DrawAt(whole % Size, shade * (1f - f));
        if (f > 0.01f)
            DrawAt((whole + 1) % Size, shade * f);

        void DrawAt(int off, Color color)
        {
            for (int y = 0, sy = Mod(ay - off); y < h; sy = 0)
            {
                var ch = Math.Min(Size - sy, h - y);
                for (int x = 0, sx = Mod(ax - off); x < w; sx = 0)
                {
                    var cw = Math.Min(Size - sx, w - x);
                    sb.Draw(_tex, new Rectangle(origin.X + (ax + x) * px, origin.Y + (ay + y) * px, cw * px, ch * px),
                        new Rectangle(sx, sy, cw, ch), color);
                    x += cw;
                }
                y += ch;
            }
        }
    }

    private static int FloorDiv(int v, int d) => v >= 0 ? v / d : -((-v + d - 1) / d);

    /// <summary>
    /// Part (0..1) de la zone <paramref name="area"/> (pixels d'art depuis le coin du plateau) à l'ombre d'un nuage
    /// à l'instant <paramref name="time"/> : moyenne sur une grille de points (tous les 8 pixels : 64 par case), qui varie donc
    /// en douceur quand un bord de nuage passe.
    /// </summary>
    public float Coverage(float time, Rectangle area)
    {
        if (_cover == null)
            return 0f;
        var off = Offset(time);
        float sum = 0f;
        var count = 0;
        for (var y = area.Top; y < area.Bottom; y += 8)
            for (var x = area.Left; x < area.Right; x += 8)
            {
                sum += _cover[Mod(y - off.Y) * Size + Mod(x - off.X)];
                count++;
            }
        return count == 0 ? 0f : sum / count;
    }

    private static int Mod(int v) => ((v % Size) + Size) % Size;

    private static float[] Lattice(Random rng, int cells)
    {
        var g = new float[cells * cells];
        for (var i = 0; i < g.Length; i++)
            g[i] = (float)rng.NextDouble();
        return g;
    }

    /// <summary>Bruit de valeur périodique (lissage smoothstep) : la grille se referme sur elle-même → motif répétable.</summary>
    private static float Sample(float[] g, int cells, int x, int y)
    {
        var step = Size / (float)cells;
        float fx = x / step, fy = y / step;
        int x0 = (int)fx, y0 = (int)fy;
        float tx = fx - x0, ty = fy - y0;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        int x1 = (x0 + 1) % cells, y1 = (y0 + 1) % cells;
        x0 %= cells;
        y0 %= cells;
        var a = MathHelper.Lerp(g[y0 * cells + x0], g[y0 * cells + x1], tx);
        var b = MathHelper.Lerp(g[y1 * cells + x0], g[y1 * cells + x1], tx);
        return MathHelper.Lerp(a, b, ty);
    }
}
