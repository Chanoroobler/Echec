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
    public bool Persistent = true;     // sang : reste sur le terrain une fois posé (false = disparaît en touchant sol ou mur)
    public float GravityScale = 1f;    // < 0 : la particule MONTE (braises d'un pion qui brûle)
}

/// <summary>Pixel de sang posé au sol, ancré au PLATEAU (en cases depuis son origine) : suit la caméra.</summary>
internal readonly record struct BloodStain(Vector2 Cells, float SizeCells, Color Color);   // taille en fraction de case : suit le zoom

/// <summary>Goutte de sang tombée à l'eau : flotte en px CANVAS (l'eau est ancrée au canvas) et dérive.</summary>
internal readonly record struct FloatingDrop(Vector2 Position, int Size, Color Color, float Phase);

/// <summary>Goutte de sang qui GLISSE le long d'un mur vertical, sans traînée, jusqu'au sol (en cases depuis l'origine du plateau).</summary>
internal readonly record struct BloodDrip(Vector2 Cells, float SizeCells, Color Color, Vector2 BoardOrigin, float Tile,
    float Speed, float Travelled, bool Persistent);   // Persistent = false : sang de coupe, s'estompe au pied du mur

/// <summary>Ce que touche une goutte qui retombe : du sol (tache), un mur vertical (elle coule), ou rien (eau).</summary>
internal enum BloodSurface { None, Ground, Wall }

/// <summary>Tache de sang de COUPE : ancrée au plateau comme les autres, mais éphémère (s'estompe puis disparaît).</summary>
internal readonly record struct FadingStain(Vector2 Cells, float SizeCells, Color Color, float Life);

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
    // TAMPON CIRCULAIRE de taille fixe (aucune allocation après la construction) : plein, une nouvelle tache
    // écrase la plus ancienne en O(1) au lieu de décaler toute une liste.
    private const int MaxStains = 4000;
    private readonly BloodStain[] _stains = new BloodStain[MaxStains];
    private int _stainStart;   // indice de la plus ancienne tache
    private int _stainCount;

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

    public bool HasActive => _active.Count > 0 || _floating.Count > 0 || _drips.Count > 0 || _fading.Count > 0;

    /// <summary>
    /// Surface au point donné (en cases depuis l'origine du plateau) : sol, mur vertical ou eau. Fourni par la
    /// scène (tuiles, masques de faces peints dans l'éditeur, épaisseur du bord). Null = tout est sol.
    /// </summary>
    public Func<Vector2, BloodSurface>? SurfaceAt { get; set; }

    // Coulées le long des murs verticaux.
    private readonly List<BloodDrip> _drips = new();
    private const int MaxDrips = 600;

    // Sang de coupe (non persistant) : tache brève qui reste FadeHold s puis s'estompe en FadeTime s.
    private readonly List<FadingStain> _fading = new();
    private const int MaxFading = 800;
    private const float FadeHold = 0.6f;
    private const float FadeTime = 0.6f;
    private const float DripSpeed = 0.15f;      // vitesse de départ de la glissade (cases/s)
    private const float DripAccel = 1.2f;       // accélération : la goutte part doucement puis « lâche »
    private const float DripMaxSpeed = 1.5f;    // vitesse maximale de glissade (cases/s)
    private const float MaxDripCells = 1.5f;    // garde-fou : une coulée s'arrête au plus après 1,5 case

    /// <summary>
    /// Giclée de SANG depuis <paramref name="origin"/> (le corps du pion touché) : projetée vers
    /// <paramref name="direction"/> (normalisée ; zéro = vers le haut) dans un cône large, puis retombe
    /// sous la gravité et se POSE au sol, entre <paramref name="floorMin"/> et <paramref name="floorMax"/>
    /// (Y canvas), où elle reste jusqu'à la fin du combat (cf. <see cref="DrawGround"/>).
    /// <paramref name="boardOrigin"/>/<paramref name="tile"/> = repère du plateau à l'émission, pour ancrer
    /// la tache au plateau. <paramref name="strength"/> 0..1 accélère la giclée (coup puissant = gicle plus loin).
    /// </summary>
    public void EmitBlood(Vector2 origin, int count, float pixel, Vector2 direction, float strength,
        float floorMin, float floorMax, Vector2 boardOrigin, float tile, bool persistent = true)
    {
        var spread = Math.Max(2, (int)pixel);
        var size = 2 * Math.Max(1, (int)MathF.Round(tile / 64f));   // 2×2 pixels d'art (sprite 64 px = une case) : sang pixel-art
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
            s.GravityScale = 1f;   // recyclé : une braise avait pu le mettre en négatif (cf. EmitEmbers)
            var ang = baseAng + (float)(_rng.NextDouble() - 0.5) * 1.9f;   // cône ±~55°
            var speed = (60f + (float)_rng.NextDouble() * 140f) * (0.7f + strength * 1.3f);
            s.Position = origin + new Vector2((float)(_rng.NextDouble() - 0.5) * spread * 4, 0f);
            s.Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * speed;
            s.Velocity.X *= lateral;
            s.MaxLife = 3f;         // filet : la goutte se pose bien avant (cf. Update)
            s.Life = s.MaxLife;
            s.Size = size;
            s.Color = BloodColors[_rng.Next(BloodColors.Length)];
            s.Outline = false;   // pixel nu, sans contour ni arrondi
            s.FloorY = MathHelper.Lerp(floorMin, floorMax, (float)_rng.NextDouble())
                       + reachY * (float)_rng.NextDouble();
            s.BoardOrigin = boardOrigin;
            s.Tile = tile;
            s.Persistent = persistent;
            _active.Add(s);
        }
    }

    /// <summary>Vide les particules en cours (au démarrage d'un nouveau combat) et les rend au pool.</summary>
    public void Clear()
    {
        foreach (var s in _active)
            _pool.Return(s);
        _active.Clear();
        _stainStart = _stainCount = 0;
        _floating.Clear();
        _drips.Clear();
        _fading.Clear();
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
            s.GravityScale = 1f;   // recyclé : une braise avait pu le mettre en négatif (cf. EmitEmbers)
            var ang = (float)(_rng.NextDouble() * Math.PI * 2);          // tout autour
            var speed = 120f + (float)_rng.NextDouble() * 220f;
            s.Position = origin;
            s.Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * speed;
            s.MaxLife = 0.35f + (float)_rng.NextDouble() * 0.30f;
            s.Life = s.MaxLife;
            s.Size = _rng.Next(4) == 0 ? size * 2 : size;               // quelques grosses braises
            s.Outline = false;
            s.FloorY = float.NaN;   // recyclé du pool : ne se pose pas comme une goutte de sang
            s.Persistent = true;
            var palette = withRed ? FireworkColors : FireworkColorsNoRed;   // sans rouge : ne se confond pas avec le sang
            s.Color = palette[_rng.Next(palette.Length)];
            _active.Add(s);
        }
    }

    // Teintes BRAISE (jaune, orange, rouge brique, gris cendre) d'un pion qui brûle.
    private static readonly Color[] EmberColors =
    {
        new(255, 220, 110), new(255, 150, 50), new(220, 80, 40), new(120, 110, 105),
    };

    /// <summary>
    /// BRAISES qui s'élèvent d'un pion qui brûle : nées le long de la ligne de feu (de <paramref name="left"/> à
    /// <paramref name="left"/> + <paramref name="width"/>, à la hauteur <paramref name="y"/>), elles MONTENT en
    /// dérivant (gravité inversée) et s'éteignent vite. À émettre par petites salves pendant la combustion.
    /// </summary>
    public void EmitEmbers(float left, float width, float y, int count, float pixel)
    {
        var size = Math.Max(2, (int)pixel);
        for (var i = 0; i < count; i++)
        {
            var s = _pool.Get();
            s.Position = new Vector2(left + (float)_rng.NextDouble() * width, y);
            s.Velocity = new Vector2((float)(_rng.NextDouble() - 0.5) * 40f, -30f - (float)_rng.NextDouble() * 50f);
            s.GravityScale = -0.12f;   // poussée de l'air chaud : elles accélèrent doucement vers le haut
            s.MaxLife = 0.35f + (float)_rng.NextDouble() * 0.40f;
            s.Life = s.MaxLife;
            s.Size = size;
            s.Outline = false;
            s.FloorY = float.NaN;
            s.Persistent = true;
            s.Color = EmberColors[_rng.Next(EmberColors.Length)];
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
            s.GravityScale = 1f;   // recyclé : une braise avait pu le mettre en négatif (cf. EmitEmbers)
            var ang = (float)(-Math.PI / 2 + (_rng.NextDouble() - 0.5) * Math.PI);   // vers le haut, ±90°
            var speed = 70f + (float)_rng.NextDouble() * 150f;
            s.Position = origin;
            s.Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * speed;
            s.MaxLife = 0.30f + (float)_rng.NextDouble() * 0.35f;
            s.Life = s.MaxLife;
            s.Size = size;
            s.Outline = false;
            s.FloorY = float.NaN;   // recyclé du pool : ne se pose pas comme une goutte de sang
            s.Persistent = true;
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
                SwapRemove(_active, i);
                _pool.Return(s);
                continue;
            }
            s.Velocity.Y += Gravity * s.GravityScale * dt;
            s.Position += s.Velocity * dt;

            // Goutte de sang qui RETOMBE devant un mur vertical (pixel de masque) : elle s'y écrase et coule,
            // sans attendre d'atteindre son sol (le mur est entre elle et la caméra).
            if (!float.IsNaN(s.FloorY) && s.Velocity.Y > 0f && s.Tile > 0f && SurfaceAt is { } surfaceAt)
            {
                var here = (s.Position - s.BoardOrigin) / s.Tile;
                if (surfaceAt(here) == BloodSurface.Wall)
                {
                    var c = s.Color;
                    var dk = new Color((int)(c.R * 0.7f), (int)(c.G * 0.7f), (int)(c.B * 0.7f));
                    StartDrip(here, s.Size / s.Tile, dk, s.BoardOrigin, s.Tile, s.Persistent);   // glisse, même éphémère
                    SwapRemove(_active, i);
                    _pool.Return(s);
                    continue;
                }
            }

            // Goutte de sang qui retombe sur son sol : elle s'y pose en pixel PERMANENT (tout le combat),
            // plus sombre, ancré au plateau. Les pixels s'accumulent au fil des coups.
            if (!float.IsNaN(s.FloorY) && s.Velocity.Y > 0f && s.Position.Y >= s.FloorY && s.Tile > 0f)
            {
                var landed = new Vector2(s.Position.X, s.FloorY);
                var dark = new Color((int)(s.Color.R * 0.7f), (int)(s.Color.G * 0.7f), (int)(s.Color.B * 0.7f));
                var cells = (landed - s.BoardOrigin) / s.Tile;
                Settle(cells, s.Size / s.Tile, dark, s.BoardOrigin, s.Tile, s.Persistent);
                SwapRemove(_active, i);
                _pool.Return(s);
            }
        }

        // Gouttes qui COULENT le long d'un mur vertical : descendent lentement en laissant une traînée, jusqu'au
        // pied du mur (tache au sol) ou jusque dans l'eau (elles y dérivent ensuite).
        for (var i = _drips.Count - 1; i >= 0; i--)
        {
            var d = _drips[i];
            // La goutte GLISSE le long du mur, sans traînée : elle part lentement puis accélère (elle « lâche »),
            // et seule sa tache finale reste, au pied du mur.
            var speed = MathF.Min(d.Speed + DripAccel * dt, DripMaxSpeed);
            var step = speed * dt;
            var pos = d.Cells + new Vector2(0f, step);
            var travelled = d.Travelled + step;
            var surface = SurfaceAt?.Invoke(pos) ?? BloodSurface.Ground;
            if (surface == BloodSurface.Wall && travelled < MaxDripCells)
            {
                _drips[i] = d with { Cells = pos, Speed = speed, Travelled = travelled };
                continue;
            }
            SwapRemove(_drips, i);
            if (surface == BloodSurface.None)
            {
                if (d.Persistent)   // tombe dans l'eau : dérive (l'éphémère y disparaît)
                    Float(d.BoardOrigin + pos * d.Tile, d.SizeCells * d.Tile, d.Color);
            }
            else
                Rest(pos, d.SizeCells, d.Color, d.Persistent);   // pied du mur (ou coulée trop longue)
        }

        // Sang de coupe : vieillit puis disparaît (retrait par échange, à l'envers : aucune allocation).
        for (var i = _fading.Count - 1; i >= 0; i--)
        {
            var life = _fading[i].Life - dt;
            if (life <= 0f)
                SwapRemove(_fading, i);
            else
                _fading[i] = _fading[i] with { Life = life };
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
    /// Pose une goutte retombée à <paramref name="cells"/> selon la surface : tache sur le sol, coulée sur un mur
    /// vertical, flottaison sur l'eau. Sang éphémère (<paramref name="persistent"/> = false, coupe) : il glisse
    /// pareil sur les murs, mais finit en tache qui s'estompe, et disparaît dans l'eau au lieu d'y dériver.
    /// </summary>
    private void Settle(Vector2 cells, float sizeCells, Color color, Vector2 boardOrigin, float tile, bool persistent)
    {
        switch (SurfaceAt?.Invoke(cells) ?? BloodSurface.Ground)
        {
            case BloodSurface.Ground:
                Rest(cells, sizeCells, color, persistent);
                break;
            case BloodSurface.Wall:
                StartDrip(cells, sizeCells, color, boardOrigin, tile, persistent);
                break;
            default:
                if (persistent)
                    Float(boardOrigin + cells * tile, sizeCells * tile, color);
                break;
        }
    }

    /// <summary>Goutte arrêtée au sol : tache permanente, ou brève qui s'estompe (sang éphémère).</summary>
    private void Rest(Vector2 cells, float sizeCells, Color color, bool persistent)
    {
        if (persistent)
            AddStain(new BloodStain(cells, sizeCells, color));
        else
            AddFading(cells, sizeCells, color);
    }

    /// <summary>Goutte collée à un mur vertical : elle va glisser vers le bas (cf. Update).</summary>
    private void StartDrip(Vector2 cells, float sizeCells, Color color, Vector2 boardOrigin, float tile, bool persistent)
    {
        if (_drips.Count < MaxDrips)
            _drips.Add(new BloodDrip(cells, sizeCells, color, boardOrigin, tile, DripSpeed, 0f, persistent));
        else
            Rest(cells, sizeCells, color, persistent);   // trop de coulées en cours : reste sur place
    }

    /// <summary>Ajoute une tache au tampon circulaire ; plein, elle remplace la plus ancienne (O(1), sans allocation).</summary>
    private void AddStain(BloodStain stain)
    {
        if (_stainCount < MaxStains)
        {
            _stains[(_stainStart + _stainCount) % MaxStains] = stain;
            _stainCount++;
        }
        else
        {
            _stains[_stainStart] = stain;
            _stainStart = (_stainStart + 1) % MaxStains;
        }
    }

    /// <summary>
    /// Retrait en O(1) : le dernier élément prend la place de l'élément retiré. L'ordre n'importe pas ici, et les
    /// boucles qui l'appellent parcourent la liste à l'ENVERS : l'élément déplacé a déjà été traité.
    /// </summary>
    private static void SwapRemove<T>(List<T> list, int i)
    {
        var last = list.Count - 1;
        list[i] = list[last];
        list.RemoveAt(last);
    }

    /// <summary>Goutte tombée à l'eau : FLOTTE et dérive avec le courant. L'eau est ancrée au canvas : px canvas.</summary>
    private void Float(Vector2 canvasPos, float sizePx, Color color)
    {
        if (_floating.Count < MaxFloating)
            _floating.Add(new FloatingDrop(canvasPos, Math.Max(1, (int)MathF.Round(sizePx)), color,
                (float)_rng.NextDouble() * MathF.Tau));
    }

    /// <summary>
    /// Sang qui flotte sur l'eau du fond, en px canvas, dans son PROPRE batch : à tracer juste après l'eau,
    /// AVANT le plateau (les tuiles passent par-dessus). Les gouttes sorties du canvas (<paramref name="width"/>
    /// × <paramref name="height"/>) disparaissent.
    /// </summary>
    public void DrawFloating(SpriteBatch sb, Texture2D pixel, int width, int height)
    {
        // Retrait des gouttes sorties du canvas : boucle manuelle + retrait par échange (pas de lambda capturante,
        // donc aucune allocation par frame), à l'envers pour ne sauter aucun élément.
        for (var i = _floating.Count - 1; i >= 0; i--)
        {
            var p = _floating[i].Position;
            var size = _floating[i].Size;
            if (p.X < -size || p.Y < -size || p.X > width || p.Y > height)
                SwapRemove(_floating, i);
        }
        if (_floating.Count == 0)
            return;
        sb.Begin(samplerState: SamplerState.PointClamp);
        for (var i = 0; i < _floating.Count; i++)
        {
            var f = _floating[i];
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
        for (var i = 0; i < _stainCount; i++)   // de la plus ancienne à la plus récente
        {
            ref readonly var s = ref _stains[(_stainStart + i) % MaxStains];
            DrawBoardDrop(sb, pixel, ox, oy, tile, s.Cells, s.SizeCells, s.Color);
        }
        for (var i = 0; i < _drips.Count; i++)   // tête des coulées en cours (leur traînée est déjà dans les taches)
        {
            var d = _drips[i];
            DrawBoardDrop(sb, pixel, ox, oy, tile, d.Cells, d.SizeCells, d.Color);
        }
        for (var i = 0; i < _fading.Count; i++)   // sang de coupe : fondu POSTÉRISÉ (paliers francs, pixel-art)
        {
            var f = _fading[i];
            var alpha = f.Life > FadeTime * 0.66f ? 1f : f.Life > FadeTime * 0.33f ? 0.6f : 0.3f;
            DrawBoardDrop(sb, pixel, ox, oy, tile, f.Cells, f.SizeCells, f.Color * alpha);
        }
    }

    /// <summary>Tache de sang de coupe : posée puis estompée sur <see cref="FadeHold"/> + <see cref="FadeTime"/>.</summary>
    private void AddFading(Vector2 cells, float sizeCells, Color color)
    {
        if (_fading.Count < MaxFading)
            _fading.Add(new FadingStain(cells, sizeCells, color, FadeHold + FadeTime));
    }

    /// <summary>
    /// Goutte ancrée au plateau. Taille ET position proportionnelles à la case : au zoom, le sang grossit avec le
    /// plateau (case entière × facteur entier → taille entière, pixel-perfect).
    /// </summary>
    private static void DrawBoardDrop(SpriteBatch sb, Texture2D pixel, int ox, int oy, float tile, Vector2 cells,
        float sizeCells, Color color)
    {
        var size = Math.Max(1, (int)MathF.Round(sizeCells * tile));
        var local = cells * tile;
        var x = ox + (int)MathF.Round(local.X / size) * size;
        var y = oy + (int)MathF.Round(local.Y / size) * size;
        DrawRound(sb, pixel, new Rectangle(x, y, size, size), color);
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
