using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Chute de neige (purement cosmétique) : remplace la pluie sur les maps de neige. Flocons de 1 pixel (et quelques
/// « proches » de 2 × 2) qui tombent lentement en se balançant de gauche à droite, poussés par un léger vent. Comme
/// <see cref="RainFx"/>, champ PÉRIODIQUE calculé à partir du temps (aucun état) : motif de <see cref="Period"/>²
/// pixels répété partout → identique dans le canvas, les bandes du letterbox et la couche du dézoom, sans couture.
/// Rien d'alloué par frame.
/// </summary>
internal sealed class SnowFx
{
    private const int Period = 256;
    private const int FlakesPerTile = 90;
    private const float Wind = -8f;   // pixels d'art / s : léger vent de droite, comme la pluie
    private static readonly Color FlakeColor = new(244, 248, 255);

    private struct Flake
    {
        public Vector2 Start;
        public float Fall;      // vitesse de chute (pixels / s)
        public float Sway;      // amplitude du balancement (pixels)
        public float Freq;      // fréquence du balancement (rad / s)
        public float Phase;
        public int Size;        // 1, ou 2 pour les flocons « proches » (plus gros, plus rapides)
        public float Alpha;
    }

    private readonly Flake[] _flakes = new Flake[FlakesPerTile];
    private Texture2D? _pixel;

    public void Load(GraphicsDevice gd)
    {
        var rng = new Random();
        for (var i = 0; i < _flakes.Length; i++)
        {
            var near = rng.Next(6) == 0;
            _flakes[i] = new Flake
            {
                Start = new Vector2(rng.Next(Period), rng.Next(Period)),
                Fall = near ? 38f + (float)rng.NextDouble() * 14f : 18f + (float)rng.NextDouble() * 16f,
                Sway = 2f + (float)rng.NextDouble() * 5f,
                Freq = 0.6f + (float)rng.NextDouble() * 1.1f,
                Phase = (float)rng.NextDouble() * MathHelper.TwoPi,
                Size = near ? 2 : 1,
                Alpha = near ? 0.95f : 0.55f + (float)rng.NextDouble() * 0.35f,
            };
        }
        _pixel = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });
    }

    public void Unload()
    {
        _pixel?.Dispose();
        _pixel = null;
    }

    /// <summary>
    /// Flocons sur la zone d'écran <paramref name="area"/>, motif ancré en <paramref name="anchor"/> (écran du point
    /// monde 0,0) ; <paramref name="px"/> = taille d'un pixel d'art. Ceux dans <paramref name="skip"/> sont sautés (le
    /// canvas, déjà couvert, quand on dessine les bandes du letterbox).
    /// </summary>
    public void Draw(SpriteBatch sb, float time, Point anchor, int px, Rectangle area, Rectangle skip, float alpha)
    {
        if (_pixel == null || alpha <= 0f)
            return;
        var tx0 = FloorDiv(FloorDiv(area.Left - anchor.X, px), Period) - 1;
        var ty0 = FloorDiv(FloorDiv(area.Top - anchor.Y, px), Period) - 1;
        var tx1 = FloorDiv(FloorDiv(area.Right - anchor.X, px), Period) + 1;
        var ty1 = FloorDiv(FloorDiv(area.Bottom - anchor.Y, px), Period) + 1;
        for (var i = 0; i < _flakes.Length; i++)
        {
            ref readonly var f = ref _flakes[i];
            var fx = Mod(f.Start.X + time * Wind + MathF.Sin(time * f.Freq + f.Phase) * f.Sway);
            var fy = Mod(f.Start.Y + time * f.Fall);
            var color = FlakeColor * (f.Alpha * alpha);
            var size = f.Size * px;
            for (var ty = ty0; ty <= ty1; ty++)
                for (var tx = tx0; tx <= tx1; tx++)
                {
                    var x = anchor.X + ((int)fx + tx * Period) * px;
                    var y = anchor.Y + ((int)fy + ty * Period) * px;
                    if (x + size <= area.Left || x >= area.Right || y + size <= area.Top || y >= area.Bottom)
                        continue;
                    if (x >= skip.Left && x + size <= skip.Right && y >= skip.Top && y + size <= skip.Bottom)
                        continue;
                    sb.Draw(_pixel, new Rectangle(x, y, size, size), color);
                }
        }
    }

    private static float Mod(float v) => ((v % Period) + Period) % Period;
    private static int FloorDiv(int v, int d) => v >= 0 ? v / d : -((-v + d - 1) / d);
}
