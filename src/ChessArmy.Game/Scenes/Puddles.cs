using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Flaques de pluie GÉNÉRÉES (pas de PNG) : quelques formes basses et irrégulières (deux ellipses aplaties fondues,
/// bord grignoté au hasard), eau sombre semi-transparente (le sol transparaît), reflet du ciel en haut, liseré clair
/// sur le bord haut. Posées comme des props (cf. <see cref="SurfaceProps"/>), dessinées seulement quand il pleut.
/// </summary>
internal static class Puddles
{
    private static readonly Color Water = new Color(46, 70, 96) * 0.55f;       // eau sombre (prémultiplié)
    private static readonly Color Sky = new Color(108, 136, 166) * 0.5f;       // reflet du ciel, haut de la flaque
    private static readonly Color Rim = new Color(196, 214, 232) * 0.6f;       // liseré clair, bord haut

    /// <summary>Tailles (largeur × hauteur, pixels d'art) des variantes : vue en plongée, donc basses.</summary>
    private static readonly (int W, int H)[] Sizes = { (12, 5), (16, 6), (20, 7), (26, 9), (18, 6) };

    public static List<(Color[] Data, int W, int H, string Name)> Build(int seed)
    {
        var rng = new Random(seed);
        var list = new List<(Color[] Data, int W, int H, string Name)>();
        for (var v = 0; v < Sizes.Length; v++)
        {
            var (w, h) = Sizes[v];
            var data = new Color[w * h];
            var inside = new bool[w * h];
            // Deux ellipses (un lobe principal + un plus petit décalé) : silhouette irrégulière.
            float cx1 = w * (0.4f + (float)rng.NextDouble() * 0.1f), cy1 = h * 0.5f, rx1 = w * 0.36f, ry1 = h * 0.45f;
            float cx2 = w * (0.62f + (float)rng.NextDouble() * 0.1f), cy2 = h * 0.55f, rx2 = w * 0.3f, ry2 = h * 0.38f;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    var d1 = Sq((px - cx1) / rx1) + Sq((py - cy1) / ry1);
                    var d2 = Sq((px - cx2) / rx2) + Sq((py - cy2) / ry2);
                    var d = MathF.Min(d1, d2);
                    var jitter = ((float)rng.NextDouble() - 0.5f) * 0.25f;   // bord grignoté
                    inside[y * w + x] = d < 1f + jitter;
                }
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    if (!inside[y * w + x])
                        continue;
                    var topEdge = y == 0 || !inside[(y - 1) * w + x];
                    // Liseré clair sur le bord haut, reflet du ciel dans le tiers haut, eau sombre ailleurs.
                    data[y * w + x] = topEdge ? Rim : y < h / 3 + 1 ? Sky : Water;
                }
            list.Add((data, w, h, $"Flaque{v}"));
        }
        return list;
    }

    private static float Sq(float v) => v * v;
}
