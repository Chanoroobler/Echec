using System;
using System.Collections.Generic;
using ChessArmy.Core.Common;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>Une étincelle d'impact : carré pixel qui fuse puis s'éteint. Mutable (recyclée par le pool).</summary>
internal sealed class Spark
{
    public Vector2 Position;   // px canvas
    public Vector2 Velocity;   // px/s
    public float Life;         // restant (s)
    public float MaxLife;
    public int Size;           // côté du carré, en px canvas
    public Color Color;
    public bool Outline;       // contour sombre d'1 px (sang : lisible sur tous les terrains)
    public float FloorY = float.NaN;   // sang : hauteur du sol où la goutte se pose (NaN = ne se pose pas)
    public Vector2 BoardOrigin;        // sang : origine du plateau à l'émission (pour ancrer la tache au plateau)
    public float Tile;                 // sang : taille de case à l'émission
}

/// <summary>Pixel de sang posé au sol, ancré au PLATEAU (en cases depuis son origine) : suit la caméra.</summary>
internal readonly record struct BloodStain(Vector2 Cells, float SizeCells, Color Color);   // taille en fraction de case : suit le zoom

/// <summary>Goutte de sang tombée à l'eau : flotte en px CANVAS (l'eau est ancrée au canvas) et dérive.</summary>
internal readonly record struct FloatingDrop(Vector2 Position, int Size, Color Color, float Phase);

/// <summary>
/// Gerbe de particules pixel-art recyclées via <see cref="Pool{T}"/> (pas d'allocation). Soumises à
/// la gravité, s'éteignent par fondu postérisé, rendu en carrés alignés à la grille (pixel-perfect,
/// chunky). Seul usage restant : le feu d'artifice d'extinction des chiffres de dégâts (cf. <see
/// cref="EmitFirework"/>) — les étincelles d'impact/recrutement ont été retirées.
/// </summary>
internal sealed class SparkBurst
{
    private const float Gravity = 760f;        // px/s² (canvas)

    private readonly Pool<Spark> _pool = new(() => new Spark(), prewarm: 64);
    private readonly List<Spark> _active = new();
    // Sang posé au sol : reste TOUT le combat et s'accumule (vidé par Clear au combat suivant).
    // Plafond de sécurité : au-delà, les plus anciens pixels disparaissent.
    private readonly List<BloodStain> _stains = new();
    private const int MaxStains = 4000;

    // Sang tombé à l'eau : dérive avec le courant jusqu'à sortir du canvas.
    private readonly List<FloatingDrop> _floating = new();
    private const int MaxFloating = 1500;
    private const float FloatSway = 1.5f;   // roulis (px/s) superposé au courant
    private const float FloatSpeedFactor = 3f;   // le sang dérive 3× plus vite que le motif de l'eau (sinon il met des minutes à sortir)
    private float _floatTime;
    private readonly Random _rng = new();

    // Teintes « feu d'artifice » (or, orange chaud, rouge vif, crème) — même entorse palette
    // assumée que les étincelles d'impact : ces FX doivent péter à l'écran.
    private static readonly Color[] FireworkColors =
    {
        new(255, 210, 90), new(255, 140, 60), new(255, 70, 70), new(255, 240, 200),
    };

    // Même feu d'artifice SANS le rouge : éclatement du chiffre de dégâts, pour ne pas le confondre avec le sang.
    private static readonly Color[] FireworkColorsNoRed =
    {
        new(255, 210, 90), new(255, 140, 60), new(255, 240, 200),
    };

    // Teintes TERREUSES (brun, terre, sable, gris) pour la poussière du « Séisme ».
    private static readonly Color[] DustColors =
    {
        new(150, 120, 80), new(110, 90, 60), new(180, 160, 130), new(95, 85, 72),
    };

    // Teintes SANG : rouges VIFS (les rouges sombres de la palette se perdaient sur le terrain) ; le contour
    // sombre de chaque goutte assure le contraste. Même entorse palette assumée que le feu d'artifice.
    private static readonly Color[] BloodColors =
    {
        new(200, 20, 30), new(230, 35, 40), new(170, 10, 25), Engine.UI.Palette.Purple5,
    };
    private static readonly Color BloodOutline = Engine.UI.Palette.Black1;

    public bool HasActive => _active.Count > 0 || _floating.Count > 0;   // le sang à l'eau continue de dériver

    /// <summary>Vrai si la case (colonne, rangée) est une tuile du plateau : le sang ne s'y pose que là.</summary>
    public Func<int, int, bool>? IsGround { get; set; }

    /// <summary>
    /// Giclée de SANG depuis <paramref name="origin"/> (le corps du pion touché) : projetée vers
    /// <paramref name="direction"/> (normalisée ; zéro = vers le haut) dans un cône large, puis retombe
    /// sous la gravité et se POSE au sol, entre <paramref name="floorMin"/> et <paramref name="floorMax"/>
    /// (Y canvas), où elle reste jusqu'à la fin du combat (cf. <see cref="DrawGround"/>).
    /// <paramref name="boardOrigin"/>/<paramref name="tile"/> = repère du plateau à l'émission, pour ancrer
    /// la tache au plateau. <paramref name="strength"/> 0..1 accélère la giclée (coup puissant = gicle plus loin).
    /// </summary>
    public void EmitBlood(Vector2 origin, int count, float pixel, Vector2 direction, float strength,
        float floorMin, float floorMax, Vector2 boardOrigin, float tile)
    {
        var size = Math.Max(2, (int)pixel);
        var baseAng = direction == Vector2.Zero
            ? -MathF.PI / 2f
            : MathF.Atan2(direction.Y - 0.6f, direction.X);   // biaisé vers le haut : ça gicle avant de retomber
        // PORTÉE selon la force du coup : un coup faible retombe au pied du pion, un coup puissant projette
        // le sang jusqu'à ~2 cases. Horizontal : vitesse latérale ; vertical (attaque venue d'en haut / d'en bas) :
        // le sol d'arrivée s'allonge dans le sens du coup.
        var lateral = 0.3f + strength * 1.2f;
        var reachY = direction.Y * tile * (0.3f + strength * 1.7f);
        for (var i = 0; i < count; i++)
        {
            var s = _pool.Get();
            var ang = baseAng + (float)(_rng.NextDouble() - 0.5) * 1.9f;   // cône ±~55°
            var speed = (60f + (float)_rng.NextDouble() * 140f) * (0.7f + strength * 1.3f);
            s.Position = origin + new Vector2((float)(_rng.NextDouble() - 0.5) * size * 4, 0f);
            s.Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * speed;
            s.Velocity.X *= lateral;
            s.MaxLife = 3f;         // filet : la goutte se pose bien avant (cf. Update)
            s.Life = s.MaxLife;
            s.Size = _rng.Next(3) == 0 ? size * 2 : size;   // un tiers de grosses gouttes
            s.Color = BloodColors[_rng.Next(BloodColors.Length)];
            s.Outline = true;
            s.FloorY = MathHelper.Lerp(floorMin, floorMax, (float)_rng.NextDouble())
                       + reachY * (float)_rng.NextDouble();
            s.BoardOrigin = boardOrigin;
            s.Tile = tile;
            _active.Add(s);
        }
    }

    /// <summary>Vide les particules en cours (au démarrage d'un nouveau combat) et les rend au pool.</summary>
    public void Clear()
    {
        foreach (var s in _active)
            _pool.Return(s);
        _active.Clear();
        _stains.Clear();
        _floating.Clear();
    }

    /// <summary>
    /// Gerbe RADIALE (360°) de particules colorées depuis <paramref name="origin"/> : un petit feu
    /// d'artifice. Vitesses variées + gravité (cf. <see cref="Update"/>) → éclat puis retombée.
    /// </summary>
    public void EmitFirework(Vector2 origin, int count, float pixel, bool withRed = true)
    {
        var size = Math.Max(2, (int)pixel);
        for (var i = 0; i < count; i++)
        {
            var s = _pool.Get();
            var ang = (float)(_rng.NextDouble() * Math.PI * 2);          // tout autour
            var speed = 120f + (float)_rng.NextDouble() * 220f;
            s.Position = origin;
            s.Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * speed;
            s.MaxLife = 0.35f + (float)_rng.NextDouble() * 0.30f;
            s.Life = s.MaxLife;
            s.Size = _rng.Next(4) == 0 ? size * 2 : size;               // quelques grosses braises
            s.Outline = false;
            s.FloorY = float.NaN;   // recyclé du pool : ne se pose pas comme une goutte de sang
            var palette = withRed ? FireworkColors : FireworkColorsNoRed;   // sans rouge : ne se confond pas avec le sang
            s.Color = palette[_rng.Next(palette.Length)];
            _active.Add(s);
        }
    }

    /// <summary>
    /// Gerbe de POUSSIÈRE / débris terreux depuis <paramref name="origin"/> (au sol) : projetée surtout vers
    /// le HAUT et les côtés, puis retombe sous la gravité (cf. <see cref="Update"/>). Sert au « Séisme ».
    /// </summary>
    public void EmitDust(Vector2 origin, int count, float pixel)
    {
        var size = Math.Max(2, (int)pixel);
        for (var i = 0; i < count; i++)
        {
            var s = _pool.Get();
            var ang = (float)(-Math.PI / 2 + (_rng.NextDouble() - 0.5) * Math.PI);   // vers le haut, ±90°
            var speed = 70f + (float)_rng.NextDouble() * 150f;
            s.Position = origin;
            s.Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * speed;
            s.MaxLife = 0.30f + (float)_rng.NextDouble() * 0.35f;
            s.Life = s.MaxLife;
            s.Size = size;
            s.Outline = false;
            s.FloorY = float.NaN;   // recyclé du pool : ne se pose pas comme une goutte de sang
            s.Color = DustColors[_rng.Next(DustColors.Length)];
            _active.Add(s);
        }
    }

    public void Update(float dt)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var s = _active[i];
            s.Life -= dt;
            if (s.Life <= 0f)
            {
                _active.RemoveAt(i);
                _pool.Return(s);
                continue;
            }
            s.Velocity.Y += Gravity * dt;
            s.Position += s.Velocity * dt;

            // Goutte de sang qui retombe sur son sol : elle s'y pose en pixel PERMANENT (tout le combat),
            // plus sombre, ancré au plateau. Les pixels s'accumulent au fil des coups.
            if (!float.IsNaN(s.FloorY) && s.Velocity.Y > 0f && s.Position.Y >= s.FloorY && s.Tile > 0f)
            {
                var landed = new Vector2(s.Position.X, s.FloorY);
                var dark = new Color((int)(s.Color.R * 0.7f), (int)(s.Color.G * 0.7f), (int)(s.Color.B * 0.7f));
                if (_stains.Count >= MaxStains)
                    _stains.RemoveAt(0);
                var cells = (landed - s.BoardOrigin) / s.Tile;
                if (IsGround?.Invoke((int)MathF.Floor(cells.X), (int)MathF.Floor(cells.Y)) ?? true)
                    _stains.Add(new BloodStain(cells, s.Size / s.Tile, dark));
                else if (_floating.Count < MaxFloating)
                    // Hors des tuiles (eau du fond) : la goutte FLOTTE et dérive avec le courant (cf. Update).
                    // L'eau est ancrée au canvas, pas au plateau : position gardée en px canvas.
                    _floating.Add(new FloatingDrop(landed, s.Size, dark, (float)_rng.NextDouble() * MathF.Tau));
                _active.RemoveAt(i);
                _pool.Return(s);
            }
        }

        // Sang à l'eau : suit le courant de surface, avec un léger roulis pour ne pas glisser en bloc.
        var drift = Engine.Rendering.WaterRenderer.SurfaceDrift * FloatSpeedFactor;
        _floatTime += dt;
        for (var i = 0; i < _floating.Count; i++)
        {
            var f = _floating[i];
            var sway = new Vector2(MathF.Sin(_floatTime * 0.9f + f.Phase), MathF.Cos(_floatTime * 0.7f + f.Phase)) * FloatSway;
            _floating[i] = f with { Position = f.Position + (drift + sway) * dt };
        }
    }

    /// <summary>
    /// Sang qui flotte sur l'eau du fond, en px canvas, dans son PROPRE batch : à tracer juste après l'eau,
    /// AVANT le plateau (les tuiles passent par-dessus). Les gouttes sorties du canvas (<paramref name="width"/>
    /// × <paramref name="height"/>) disparaissent.
    /// </summary>
    public void DrawFloating(SpriteBatch sb, Texture2D pixel, int width, int height)
    {
        _floating.RemoveAll(f => f.Position.X < -f.Size || f.Position.Y < -f.Size
                                 || f.Position.X > width || f.Position.Y > height);
        if (_floating.Count == 0)
            return;
        sb.Begin(samplerState: SamplerState.PointClamp);
        foreach (var f in _floating)
        {
            var x = (int)MathF.Round(f.Position.X / f.Size) * f.Size;
            var y = (int)MathF.Round(f.Position.Y / f.Size) * f.Size;
            DrawRound(sb, pixel, new Rectangle(x, y, f.Size, f.Size), f.Color);
        }
        sb.End();
    }

    /// <summary>
    /// Sang au sol, dans le batch OUVERT par l'appelant : à tracer SOUS les unités (le pion touché se
    /// tient dessus). <paramref name="boardOrigin"/>/<paramref name="tile"/> = repère ACTUEL du plateau :
    /// les pixels suivent la caméra. Calés sur leur grille de blocs (pixel-perfect).
    /// </summary>
    public void DrawGround(SpriteBatch sb, Texture2D pixel, Vector2 boardOrigin, float tile)
    {
        // Grille calée sur le PLATEAU (pas l'écran) : les pixels ne tremblent pas quand la caméra défile.
        var ox = (int)MathF.Round(boardOrigin.X);
        var oy = (int)MathF.Round(boardOrigin.Y);
        foreach (var s in _stains)
        {
            // Taille ET position proportionnelles à la case : au zoom, le sang grossit avec le plateau
            // (case entière × facteur entier → taille entière, pixel-perfect).
            var size = Math.Max(1, (int)MathF.Round(s.SizeCells * tile));
            var local = s.Cells * tile;
            var x = ox + (int)MathF.Round(local.X / size) * size;
            var y = oy + (int)MathF.Round(local.Y / size) * size;
            DrawRound(sb, pixel, new Rectangle(x, y, size, size), s.Color);
        }
    }

    /// <summary>
    /// Carré aux coins rognés (pixel-art) : une croix pour 3 px, un rond pour 5 px et plus. Bande centrale
    /// pleine largeur + deux chapeaux plus étroits, sans recouvrement (pas de pixel doublé en transparence).
    /// </summary>
    private static void DrawRound(SpriteBatch sb, Texture2D pixel, Rectangle r, Color c)
    {
        var cut = Math.Max(1, r.Width / 4);
        if (r.Width < 3)
        {
            sb.Draw(pixel, r, c);
            return;
        }
        sb.Draw(pixel, new Rectangle(r.X, r.Y + cut, r.Width, r.Height - 2 * cut), c);
        sb.Draw(pixel, new Rectangle(r.X + cut, r.Y, r.Width - 2 * cut, cut), c);
        sb.Draw(pixel, new Rectangle(r.X + cut, r.Bottom - cut, r.Width - 2 * cut, cut), c);
    }

    public void Draw(SpriteBatch sb, Texture2D pixel)
    {
        if (_active.Count == 0)
            return;

        sb.Begin(blendState: BlendState.AlphaBlend, samplerState: SamplerState.PointClamp);
        foreach (var s in _active)
        {
            // OPAQUES la quasi-totalité de leur vie (visibilité), un SEUL palier de fondu sur la fin,
            // puis disparition nette (pas de transparence lisse qui les rend fades).
            var k = s.Life / s.MaxLife;                       // 1 → 0
            var alpha = k > 0.25f ? 1f : 0.5f;
            // Position calée sur la grille de blocs (carrés nets, pas de sous-pixel).
            var x = (int)MathF.Round(s.Position.X / s.Size) * s.Size;
            var y = (int)MathF.Round(s.Position.Y / s.Size) * s.Size;
            if (s.Outline)   // goutte de sang : ARRONDIE, contour sombre compris
            {
                DrawRound(sb, pixel, new Rectangle(x - 1, y - 1, s.Size + 2, s.Size + 2), BloodOutline * alpha);
                DrawRound(sb, pixel, new Rectangle(x, y, s.Size, s.Size), s.Color * alpha);
            }
            else
                sb.Draw(pixel, new Rectangle(x, y, s.Size, s.Size), s.Color * alpha);
        }
        sb.End();
    }
}
