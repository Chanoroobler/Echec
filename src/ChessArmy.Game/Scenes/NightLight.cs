using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Ambiance « nuit » de PLEINE LUNE (purement cosmétique : on y voit bien, aucun halo autour des pions) par CARTE DE
/// LUMIÈRE : une cible de la taille du canvas, remplie du bleu
/// du clair de lune (<see cref="Ambient"/>), où l'on AJOUTE les halos des sources ; elle est ensuite MULTIPLIÉE sur la
/// scène (par-dessus les pions, sous l'UI). Là où il y a de la lumière, la scène garde ses couleurs ; ailleurs elle
/// plonge dans le bleu nuit.
/// <para>Halos TRAMÉS (Bayer 4×4, 4 paliers d'intensité) pour rester pixel-art. Lucioles : quelques points de
/// lumière qui errent au-dessus du plateau (petit halo dans la carte + point vif dessiné APRÈS la multiplication).
/// Rien d'alloué par frame (cible, textures et lucioles créées une fois).</para>
/// </summary>
internal sealed class NightLight
{
    public static readonly Color Ambient = new(138, 154, 206);         // PLEINE lune : multiplié sur la scène (on y voit bien)
    private static readonly Color FlyLight = new Color(190, 255, 120) * 0.5f;
    private static readonly Color FlyDot = new(236, 255, 170);
    private const int FlyRadius = 9;       // halo d'une luciole
    private const int FlyCount = 16;
    private static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    /// <summary>Addition pure (textures prémultipliées) : la lumière s'ajoute.</summary>
    private static readonly BlendState Add = new()
    {
        ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.One,
    };

    /// <summary>Multiplication : couleur finale = scène × carte de lumière (alpha de la scène inchangé).</summary>
    public static readonly BlendState Multiply = new()
    {
        ColorSourceBlend = Blend.DestinationColor, ColorDestinationBlend = Blend.Zero,
        AlphaSourceBlend = Blend.Zero, AlphaDestinationBlend = Blend.One,
    };

    private struct Firefly
    {
        public bool Active;
        public Vector2 Home;     // centre de son errance (pixels d'art depuis le coin du plateau)
        public float Phase, Speed, Blink;
        // Luciole SORTIE D'UN BUISSON (temporaire) : née à Born, vit Life secondes, s'envole de Home vers Home + Drift.
        public bool Burst;
        public float Born, Life;
        public Vector2 Drift;
    }

    private GraphicsDevice? _gd;
    private RenderTarget2D? _light;
    private Texture2D? _fly, _dot;
    private const int BurstMax = 12;   // lucioles de buisson en même temps, au plus
    private readonly Firefly[] _flies = new Firefly[FlyCount + BurstMax];   // résidentes puis emplacements de buisson
    private readonly Random _rng = new();
    private float _time;

    public RenderTarget2D? Map => _light;

    public void Load(GraphicsDevice gd)
    {
        _gd = gd;
        _fly = BuildHalo(gd, FlyRadius);
        _dot = new Texture2D(gd, 1, 1);
        _dot.SetData(new[] { Color.White });
    }

    public void Unload()
    {
        _light?.Dispose();
        _fly?.Dispose();
        _dot?.Dispose();
        _light = null;
        _fly = _dot = null;
    }

    /// <summary>Nouveau plateau (<paramref name="artW"/> × <paramref name="artH"/> pixels d'art) : lucioles réparties,
    /// aucune luciole de buisson.</summary>
    public void ResetBoard(int artW, int artH)
    {
        for (var i = 0; i < _flies.Length; i++)
            _flies[i] = i >= FlyCount ? default : new Firefly
            {
                Active = true,
                Home = new Vector2(_rng.Next(artW), _rng.Next(artH)),
                Phase = (float)_rng.NextDouble() * MathHelper.TwoPi,
                Speed = 0.25f + (float)_rng.NextDouble() * 0.35f,
                Blink = 0.25f + (float)_rng.NextDouble() * 0.3f,   // cycles lents : elles restent allumées longtemps
            };
    }

    /// <summary>
    /// 3 ou 4 lucioles s'échappent d'un buisson en <paramref name="at"/> (pixels d'art depuis le coin du plateau) :
    /// elles s'envolent en éventail vers le haut, errent un moment (7 à 11 s) puis s'éteignent.
    /// </summary>
    public void BurstFrom(Vector2 at)
    {
        var count = 3 + _rng.Next(2);
        for (var i = FlyCount; i < _flies.Length && count > 0; i++)
        {
            if (_flies[i].Active)
                continue;
            var angle = -MathHelper.PiOver2 + ((float)_rng.NextDouble() - 0.5f) * 2.2f;   // vers le haut, en éventail
            var dist = 14f + (float)_rng.NextDouble() * 22f;
            _flies[i] = new Firefly
            {
                Active = true,
                Burst = true,
                Home = at + new Vector2(_rng.Next(-6, 7), _rng.Next(-4, 3)),
                Drift = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * dist,
                Phase = (float)_rng.NextDouble() * MathHelper.TwoPi,
                Speed = 0.35f + (float)_rng.NextDouble() * 0.3f,
                Blink = 0.5f + (float)_rng.NextDouble() * 0.5f,
                Born = _time,
                Life = 7f + (float)_rng.NextDouble() * 4f,
            };
            count--;
        }
    }

    /// <summary>Horloge de la scène (lucioles), posée avant chaque usage de la frame.</summary>
    public float Time { set => _time = value; }

    /// <summary>Position (pixels d'art depuis le coin du plateau) et éclat (0..1) de la luciole <paramref name="i"/>
    /// (éclat 0 = éteinte ; une luciole de buisson arrivée en fin de vie libère son emplacement).</summary>
    private (Point At, float Glow) Fly(int i)
    {
        ref var f = ref _flies[i];
        if (!f.Active)
            return (Point.Zero, 0f);
        var t = _time * f.Speed + f.Phase;
        // Errance en boucles (deux sinus de fréquences différentes) autour de son point d'attache.
        var wander = new Vector2(MathF.Sin(t) * 22f + MathF.Sin(t * 2.3f) * 7f, MathF.Cos(t * 1.4f) * 14f);
        if (!f.Burst)
        {
            // Allumée la plus grande partie de son cycle (lent), avec une extinction douce.
            var g = Math.Clamp(0.35f + 0.9f * MathF.Sin(_time * f.Blink + f.Phase * 3f), 0f, 1f);
            return (ToPoint(f.Home + wander), g * g);
        }
        var age = _time - f.Born;
        if (age >= f.Life)
        {
            f.Active = false;
            return (Point.Zero, 0f);
        }
        // Jaillit du buisson (ralentit en s'éloignant), puis se met à errer peu à peu.
        var out1 = 1f - MathF.Exp(-age * 1.3f);
        var settle = MathF.Min(1f, age / 2.5f);
        var fade = MathF.Min(1f, age / 0.35f) * MathF.Min(1f, (f.Life - age) / 1.8f);
        var blink = 0.75f + 0.25f * MathF.Sin(_time * f.Blink * 3f + f.Phase);
        return (ToPoint(f.Home + f.Drift * out1 + wander * 0.5f * settle), fade * blink);
    }

    private static Point ToPoint(Vector2 v) => new((int)MathF.Floor(v.X), (int)MathF.Floor(v.Y));

    /// <summary>
    /// Ouvre la carte de lumière (<paramref name="w"/> × <paramref name="h"/>, taille du canvas) : la remplit du clair
    /// de lune et ouvre un batch ADDITIF pour <see cref="AddFireflyLights"/> (et de futures sources). À refermer
    /// par <see cref="EndLights"/>, AVANT de dessiner la scène (la cible du canvas est alors restaurée).
    /// </summary>
    public void BeginLights(SpriteBatch sb, int w, int h, Color ambient)
    {
        if (_light == null || _light.Width != w || _light.Height != h)
        {
            _light?.Dispose();
            _light = new RenderTarget2D(_gd!, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        }
        _gd!.SetRenderTarget(_light);
        _gd.Clear(ambient);   // clair de lune (× pluie éventuelle)
        sb.Begin(blendState: Add, samplerState: SamplerState.PointClamp);
    }

    public void EndLights(SpriteBatch sb, RenderTarget2D? restore)
    {
        sb.End();
        _gd!.SetRenderTarget(restore);
    }

    /// <summary>Petits halos des lucioles, plateau au coin <paramref name="origin"/> (écran canvas).</summary>
    public void AddFireflyLights(SpriteBatch sb, Point origin, int px, float alpha = 1f)
    {
        var r = FlyRadius;
        for (var i = 0; i < _flies.Length; i++)
        {
            var (at, glow) = Fly(i);
            if (glow <= 0.02f)
                continue;
            sb.Draw(_fly!, new Rectangle(origin.X + (at.X - r) * px, origin.Y + (at.Y - r) * px,
                (2 * r + 1) * px, (2 * r + 1) * px), FlyLight * (glow * alpha));
        }
    }

    /// <summary>Multiplie la carte de lumière sur la zone <paramref name="dest"/> (batch fermé par l'appelant).</summary>
    public void Apply(SpriteBatch sb, Rectangle dest)
    {
        if (_light == null)
            return;
        sb.Begin(blendState: Multiply, samplerState: SamplerState.PointClamp);
        sb.Draw(_light, dest, Color.White);
        sb.End();
    }

    /// <summary>Points vifs des lucioles, APRÈS la multiplication (dans un batch ouvert).</summary>
    public void DrawFireflies(SpriteBatch sb, Point origin, int px, float alpha = 1f)
    {
        for (var i = 0; i < _flies.Length; i++)
        {
            var (at, glow) = Fly(i);
            if (glow <= 0.15f)
                continue;
            sb.Draw(_dot!, new Rectangle(origin.X + at.X * px, origin.Y + at.Y * px, px, px), FlyDot * (glow * alpha));
        }
    }

    /// <summary>
    /// Halo de rayon <paramref name="r"/> : intensité (1 − d/r)^1.5 réduite à 4 paliers, la transition entre paliers
    /// TRAMÉE (Bayer 4×4). Blanc prémultiplié (teinté au dessin).
    /// </summary>
    private static Texture2D BuildHalo(GraphicsDevice gd, int r)
    {
        var size = 2 * r + 1;
        var data = new Color[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                float dx = x - r, dy = y - r;
                var d = MathF.Sqrt(dx * dx + dy * dy) / r;
                if (d >= 1f)
                    continue;
                var i = MathF.Pow(1f - d, 1.5f) * 4f;                       // 0..4 paliers
                var level = (int)i;
                if (i - level > (Bayer[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f)
                    level++;                                                 // tramé vers le palier du dessus
                if (level > 0)
                    data[y * size + x] = Color.White * Math.Min(1f, level / 4f);
            }
        var tex = new Texture2D(gd, size, size);
        tex.SetData(data);
        return tex;
    }
}
