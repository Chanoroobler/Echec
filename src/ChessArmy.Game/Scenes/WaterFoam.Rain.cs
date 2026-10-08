using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Pluie sur les tuiles d'eau : <see cref="RainSlots"/> gouttes par case, chacune à son rythme (haché, sans état) :
/// un point d'impact, puis un rond aplati qui s'élargit en deux temps (5 × 3 puis 7 × 3) et pâlit. Chaque cycle tire
/// un nouvel endroit, seulement sur de l'eau DÉGAGÉE (à 4 pixels au moins de tout rocher / berge, cf. la carte de
/// distance des props). Dessinée sous les props flottants et l'écume. Rien d'alloué par frame.
/// </summary>
internal sealed partial class WaterFoam
{
    private const int RainSlots = 6;
    private const int RainClear = 4;   // le grand rond (7 × 3) tient sans toucher la berge
    private static readonly Color RainRing = new(204, 228, 242);

    /// <summary>Ronds de pluie de la case d'eau n° <paramref name="index"/> (batch ouvert, face du dessus en
    /// <paramref name="dest"/>), à l'instant <paramref name="time"/>.</summary>
    public void DrawRain(SpriteBatch sb, int index, Rectangle dest, float alpha, float time)
    {
        if (_cells[index].Foam?.Clear is not { } clear)
            return;
        var px = Math.Max(1, dest.Width / Face);
        for (var slot = 0; slot < RainSlots; slot++)
        {
            var h0 = Mix((uint)index * 73856093u ^ (uint)slot * 19349663u ^ 0x5bd1e995u);
            var period = 0.55f + (h0 % 1000) / 1000f * 0.7f;
            var t = (time + (h0 >> 10) % 1000 / 97f) / period;
            var k = t - MathF.Floor(t);
            if (k > 0.42f)
                continue;
            // Endroit du cycle : quelques essais pour tomber sur de l'eau dégagée (sinon, pas de goutte ce cycle).
            var h = Mix(h0 ^ (uint)(int)MathF.Floor(t) * 0x9E3779B9u);
            int cx = -1, cy = -1;
            for (var attempt = 0; attempt < 3 && cx < 0; attempt++, h = Mix(h))
            {
                var x = RainClear + (int)(h % (uint)(Face - 2 * RainClear));
                var y = RainClear + (int)((h >> 16) % (uint)(Face - 2 * RainClear));
                if (clear[y * Face + x] >= RainClear)
                {
                    cx = x;
                    cy = y;
                }
            }
            if (cx < 0)
                continue;
            if (k < 0.1f)
                Dot(0, 0, 0.75f);                       // la goutte touche l'eau
            else if (k < 0.25f)
            {
                var a = 0.6f;                           // petit rond 5 × 3
                Dot(-2, 0, a); Dot(2, 0, a);
                Dot(-1, -1, a); Dot(0, -1, a); Dot(1, -1, a);
                Dot(-1, 1, a); Dot(0, 1, a); Dot(1, 1, a);
            }
            else
            {
                var a = 0.32f;                          // grand rond 7 × 3, plus pâle
                Dot(-3, 0, a); Dot(3, 0, a);
                Dot(-2, -1, a); Dot(-1, -1, a); Dot(0, -1, a); Dot(1, -1, a); Dot(2, -1, a);
                Dot(-2, 1, a); Dot(-1, 1, a); Dot(0, 1, a); Dot(1, 1, a); Dot(2, 1, a);
            }

            void Dot(int dx, int dy, float a) =>
                sb.Draw(_rings[1], new Rectangle(dest.X + (cx + dx) * px, dest.Y + (cy + dy) * px, px, px),
                    new Rectangle(1, 1, 1, 1), RainRing * (a * alpha));   // pixel central du rond de rayon 1 (blanc)
        }
    }

    private static uint Mix(uint v)
    {
        v ^= v >> 16;
        v *= 0x7FEB352Du;
        v ^= v >> 15;
        v *= 0x846CA68Bu;
        v ^= v >> 16;
        return v;
    }
}
