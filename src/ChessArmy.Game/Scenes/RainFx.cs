using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Pluie (purement cosmétique), indépendante de l'heure : se superpose au jour, au couchant ou à la nuit.
/// <list type="bullet">
/// <item>GOUTTES : fines traînées inclinées (vent de droite) qui tombent vite. Champ PÉRIODIQUE calculé à partir du
/// temps (aucun état) : chaque goutte a une position de départ dans un motif de <see cref="Period"/>² pixels répété
/// partout → la pluie se dessine pareil dans le canvas, dans les bandes du letterbox et sur la couche du dézoom, sans
/// couture.</item>
/// <item>ÉCLABOUSSURES : petits impacts (un point, puis deux gouttelettes qui jaillissent) qui naissent au hasard sur
/// la zone donnée, chacune à son rythme ; position retirée à chaque cycle (hachage), toujours sans état.</item>
/// </list>
/// Pixels d'art entiers, textures créées une fois, rien d'alloué par frame.
/// </summary>
internal sealed class RainFx
{
    private const int Period = 256;            // motif répété (pixels d'art)
    private const int DropsPerTile = 70;
    private const int SplashCount = 70;
    private static readonly Vector2 Fall = new(-60f, 360f);   // pixels d'art / s : vent de droite, chute rapide
    private static readonly Color DropColor = new Color(196, 212, 232) * 0.5f;
    private static readonly Color SplashColor = new Color(214, 226, 240) * 0.75f;

    private struct Drop
    {
        public Vector2 Start;
        public float Speed;   // facteur de vitesse (les gouttes ne tombent pas toutes pareil)
        public int Variant;   // traînée courte / longue
    }

    private struct Splash
    {
        public float Period, Phase;
        public uint Seed;
    }

    private readonly Drop[] _drops = new Drop[DropsPerTile];
    private readonly Splash[] _splashes = new Splash[SplashCount];
    private readonly Texture2D?[] _streaks = new Texture2D?[2];
    private Texture2D? _pixel;

    public void Load(GraphicsDevice gd)
    {
        var rng = new Random();
        for (var i = 0; i < _drops.Length; i++)
            _drops[i] = new Drop
            {
                Start = new Vector2(rng.Next(Period), rng.Next(Period)),
                Speed = 0.85f + (float)rng.NextDouble() * 0.3f,
                Variant = rng.Next(3) == 0 ? 1 : 0,
            };
        for (var i = 0; i < _splashes.Length; i++)
            _splashes[i] = new Splash
            {
                Period = 0.5f + (float)rng.NextDouble() * 0.8f,
                Phase = (float)rng.NextDouble() * 10f,
                Seed = (uint)rng.Next(),
            };
        _streaks[0] = BuildStreak(gd, 6);
        _streaks[1] = BuildStreak(gd, 9);
        _pixel = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });
    }

    public void Unload()
    {
        foreach (var t in _streaks) t?.Dispose();
        _pixel?.Dispose();
        _streaks[0] = _streaks[1] = null;
        _pixel = null;
    }

    /// <summary>
    /// Traînée de <paramref name="length"/> pixels inclinée comme la chute (en marches d'1 pixel) : la tête (en bas)
    /// pleine, la queue à mi-intensité. Blanc prémultiplié, teinté au dessin.
    /// </summary>
    private static Texture2D BuildStreak(GraphicsDevice gd, int length)
    {
        var slope = -Fall.X / Fall.Y;                                   // décalage x par pixel de hauteur (vers la droite en remontant)
        var width = (int)MathF.Ceiling((length - 1) * slope) + 1;
        var data = new Color[width * length];
        for (var j = 0; j < length; j++)
        {
            var x = (int)MathF.Round((length - 1 - j) * slope);        // la tête (j = length-1) à gauche
            data[j * width + x] = Color.White * (j >= length / 2 ? 1f : 0.5f);
        }
        var tex = new Texture2D(gd, width, length);
        tex.SetData(data);
        return tex;
    }

    /// <summary>
    /// Gouttes sur la zone d'écran <paramref name="area"/>, motif ancré en <paramref name="anchor"/> (écran du point
    /// monde 0,0) ; <paramref name="px"/> = taille d'un pixel d'art. Les gouttes dans <paramref name="skip"/> (écran)
    /// sont sautées (ex. le canvas, déjà couvert, quand on dessine les bandes du letterbox).
    /// </summary>
    public void DrawDrops(SpriteBatch sb, float time, Point anchor, int px, Rectangle area, Rectangle skip, float alpha)
    {
        if (_streaks[0] == null || alpha <= 0f)
            return;
        var color = DropColor * alpha;
        // Copies du motif qui touchent la zone (une de marge : une traînée peut déborder de son motif).
        var tx0 = FloorDiv(FloorDiv(area.Left - anchor.X, px), Period) - 1;
        var ty0 = FloorDiv(FloorDiv(area.Top - anchor.Y, px), Period) - 1;
        var tx1 = FloorDiv(FloorDiv(area.Right - anchor.X, px), Period) + 1;
        var ty1 = FloorDiv(FloorDiv(area.Bottom - anchor.Y, px), Period) + 1;
        for (var i = 0; i < _drops.Length; i++)
        {
            ref readonly var d = ref _drops[i];
            var tex = _streaks[d.Variant]!;
            var dx = Mod(d.Start.X + time * Fall.X * d.Speed);
            var dy = Mod(d.Start.Y + time * Fall.Y * d.Speed);
            for (var ty = ty0; ty <= ty1; ty++)
                for (var tx = tx0; tx <= tx1; tx++)
                {
                    var x = anchor.X + ((int)dx + tx * Period) * px;
                    var y = anchor.Y + ((int)dy + ty * Period) * px;
                    var w = tex.Width * px;
                    var h = tex.Height * px;
                    if (x + w <= area.Left || x >= area.Right || y + h <= area.Top || y >= area.Bottom)
                        continue;
                    if (x >= skip.Left && x + w <= skip.Right && y >= skip.Top && y + h <= skip.Bottom)
                        continue;
                    sb.Draw(tex, new Rectangle(x, y, w, h), color);
                }
        }
    }

    /// <summary>
    /// Éclaboussures sur la zone d'écran <paramref name="area"/> (sol et mer) : un point d'impact, puis deux
    /// gouttelettes qui jaillissent de part et d'autre, puis plus rien jusqu'au prochain cycle (ailleurs).
    /// </summary>
    public void DrawSplashes(SpriteBatch sb, float time, int px, Rectangle area, float alpha)
    {
        if (_pixel == null || alpha <= 0f || area.Width <= 0 || area.Height <= 0)
            return;
        var color = SplashColor * alpha;
        var cols = area.Width / px;
        var rows = area.Height / px;
        for (var i = 0; i < _splashes.Length; i++)
        {
            ref readonly var s = ref _splashes[i];
            var t = (time + s.Phase) / s.Period;
            var cycle = (uint)(int)MathF.Floor(t);
            var k = t - MathF.Floor(t);
            if (k > 0.3f)
                continue;                                   // l'éclaboussure ne dure qu'un instant par cycle
            var h = Hash(s.Seed ^ (cycle * 0x9E3779B9u));
            var x = area.X + (int)(h % (uint)cols) * px;
            var y = area.Y + (int)((h >> 12) % (uint)rows) * px;
            if (k < 0.12f)
                sb.Draw(_pixel, new Rectangle(x, y, px, px), color);                       // impact
            else
            {
                sb.Draw(_pixel, new Rectangle(x - px, y - px, px, px), color * 0.8f);      // gouttelettes
                sb.Draw(_pixel, new Rectangle(x + px, y - px, px, px), color * 0.8f);
            }
        }
    }

    private static uint Hash(uint v)
    {
        v ^= v >> 16;
        v *= 0x7FEB352Du;
        v ^= v >> 15;
        v *= 0x846CA68Bu;
        v ^= v >> 16;
        return v;
    }

    private static float Mod(float v) => ((v % Period) + Period) % Period;
    private static int FloorDiv(int v, int d) => v >= 0 ? v / d : -((-v + d - 1) / d);
}
