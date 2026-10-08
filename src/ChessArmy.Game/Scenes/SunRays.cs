using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Ambiance « jour » : rayons de soleil en diagonale à 45° (lumière venue du haut-gauche) sur tout l'écran de jeu, purement
/// cosmétique.
/// <para>TRAMÉS (Bayer 4×4) : chaque pixel est allumé ou éteint, la DENSITÉ fait le dégradé (bords, extinction vers
/// le bas). Deux JEUX de rayons (bandes de largeurs et d'intensités au hasard) sont calculés
/// une fois au chargement dans une planche. Ils ne bougent PAS (un défilement au pixel près saccade) : ils
/// RESPIRENT en fondu enchaîné lent, l'un s'allume quand l'autre s'éteint. Plus forts en haut de l'écran, ils
/// s'estompent vers le bas. Le motif se répète tous les <see cref="Period"/> pixels en x (u = x − y), dessiné en
/// morceaux dans le batch ouvert : rien d'alloué par frame.</para>
/// </summary>
internal sealed class SunRays
{
    private const int Period = 256;            // répétition horizontale du motif (pixels d'art)
    private const int MaxHeight = 768;         // plateau de 12 rangées au plus
    private const float Breath = 0.22f;        // vitesse du fondu enchaîné (rad/s) : ~30 s par cycle
    public static readonly Color DayLight = new Color(255, 238, 186) * 0.3f;   // pixels allumés : la trame fait le dégradé
    public static readonly Color MoonLight = new Color(196, 220, 255) * 0.22f;     // nuit : rayons de lune, argentés
    public static readonly Color SunsetLight = new Color(255, 168, 92) * 0.34f;    // coucher de soleil : rayons orangés, plus marqués
    // Trame ordonnée 4×4 (Bayer) : seuils 0..15 / 16, alignés sur la grille du plateau (période 256 et cases de 64 =
    // multiples de 4 → la trame se raccorde d'un morceau à l'autre).
    private static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    private Texture2D? _tex;   // deux jeux l'un sous l'autre : [0, MaxHeight[ puis [MaxHeight, 2·MaxHeight[

    public void Load(GraphicsDevice gd)
    {
        var rng = new Random();
        var data = new Color[Period * MaxHeight * 2];
        for (var layer = 0; layer < 2; layer++)
        {
            var band = Bands(rng);
            for (var y = 0; y < MaxHeight; y++)
            {
                // Plus clair en haut de l'écran, s'éteint vers le bas (~420 pixels d'art).
                var fall = MathHelper.Clamp(1f - y / 420f, 0f, 1f);
                fall *= fall;
                for (var x = 0; x < Period; x++)
                {
                    var u = ((x - y) % Period + Period) % Period;
                    var a = band[u] * fall * 0.8f;   // jamais plein : un rayon reste une trame de lumière
                    if (a > (Bayer[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f)   // tramé : allumé ou pas
                        data[(layer * MaxHeight + y) * Period + x] = Color.White;
                }
            }
        }
        _tex = new Texture2D(gd, Period, MaxHeight * 2);
        _tex.SetData(data);
    }

    /// <summary>Intensité (0..1) le long de u : bandes de 3 à 16 pixels séparées de 18 à 60, bords à mi-intensité.</summary>
    private static float[] Bands(Random rng)
    {
        var band = new float[Period];
        var u = rng.Next(20);
        while (u < Period - 4)
        {
            var width = 3 + rng.Next(14);
            var strength = 0.45f + (float)rng.NextDouble() * 0.55f;
            for (var i = 0; i < width && u + i < Period; i++)
            {
                var edge = i == 0 || i == width - 1;
                band[u + i] = strength * (edge ? 0.5f : 1f);
            }
            u += width + 18 + rng.Next(43);
        }
        return band;
    }

    public void Unload()
    {
        _tex?.Dispose();
        _tex = null;
    }

    /// <summary>
    /// Rayons sur la zone d'écran <paramref name="area"/>, motif ancré en <paramref name="anchor"/> (écran) = haut
    /// de l'écran de jeu, d'où partent les rayons (rien au-dessus) ; <paramref name="px"/> = taille d'un pixel d'art
    /// à l'écran. La même ancre dans le canvas et dans les bandes du letterbox → raccord sans couture.
    /// <paramref name="light"/> : couleur des pixels allumés (défaut : soleil ; <see cref="MoonLight"/>, <see cref="SunsetLight"/>).
    /// </summary>
    public void Draw(SpriteBatch sb, float time, Point anchor, Rectangle area, int px, float alpha, Color? light = null)
    {
        if (_tex == null || alpha <= 0f || area.Width <= 0 || area.Height <= 0)
            return;
        // Zone en pixels d'art depuis l'ancre (arrondie vers l'extérieur), bornée au motif en hauteur.
        var ax = FloorDiv(area.Left - anchor.X, px);
        var ay = Math.Max(0, FloorDiv(area.Top - anchor.Y, px));
        var w = FloorDiv(area.Right - anchor.X + px - 1, px) - ax;
        var h = Math.Min(FloorDiv(area.Bottom - anchor.Y + px - 1, px), MaxHeight) - ay;
        if (h <= 0)
            return;
        var phase = MathF.Sin(time * Breath);
        Layer(0, 0.5f + 0.5f * phase);
        Layer(1, 0.5f - 0.5f * phase);

        void Layer(int layer, float k)
        {
            if (k <= 0.01f)
                return;
            var tint = (light ?? DayLight) * (alpha * k);
            for (int x = 0, sx = Mod(ax); x < w; sx = 0)
            {
                var cw = Math.Min(Period - sx, w - x);
                sb.Draw(_tex, new Rectangle(anchor.X + (ax + x) * px, anchor.Y + ay * px, cw * px, h * px),
                    new Rectangle(sx, layer * MaxHeight + ay, cw, h), tint);
                x += cw;
            }
        }
    }

    private static int Mod(int v) => ((v % Period) + Period) % Period;
    private static int FloorDiv(int v, int d) => v >= 0 ? v / d : -((-v + d - 1) / d);
}
