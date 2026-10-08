using System;
using System.Collections.Generic;
using System.IO;
using ChessArmy.Core.Map;
using ChessArmy.Engine.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Vie de l'eau sur les tuiles d'eau dessinées (purement cosmétique) :
/// <list type="bullet">
/// <item>RESSAC sur les berges (ce qui touche le bord de la tuile) et les rochers dressés dans l'eau : une petite
/// vague claire claque contre eux puis s'éloigne par paliers de 1 pixel d'art (jusqu'à 5 pixels) avant de
/// s'éteindre. Par ÉTENDUE d'eau (cases d'eau qui se touchent), en SÉRIES de 2-3 vagues décroissantes puis un calme
/// (cf. WaveStrength) : berges et rochers d'une même étendue battent ensemble. Les feuilles et pierres moussues
/// flottantes (taches vertes isolées) ne moussent pas.</item>
/// <item>POISSONS : de temps en temps, en EAU LIBRE (loin de tout bord ou objet), un petit « bloop » : un point clair
/// puis un rond aplati (perspective) qui s'élargit en s'effaçant.</item>
/// </list>
/// Pour chaque cellule de tileset (planche + rectangle source), on calcule UNE fois la distance de chaque pixel
/// d'EAU à la berge (4-voisinage, face du dessus 64×64) et on en tire des masques blancs par distance (1 à 5), plus
/// la liste des points d'eau libre. Eau = pixel nettement BLEU ; les petites taches non bleues (fleurs, reflets)
/// comptent comme de l'eau pour ne pas mousser autour d'un détail de 1-2 pixels.
/// </summary>
internal sealed partial class WaterFoam
{
    private const int Face = 64;            // face du dessus de la tuile (l'épaisseur dessous est ignorée)
    private const int Bands = 5;            // la vague s'éloigne jusqu'à 5 pixels du bord
    private const int MinSolidPixels = 6;   // tache non bleue plus petite = détail peint sur l'eau, pas un bord
    private const int SideBands = 3;        // berges de CÔTÉ : ressac sur 3 pixels seulement…
    private const float SideWeight = 0.7f;  // … et un peu moins vif (berge du haut = plein, du bas = rien)

    // ── Forme de la vague ──
    // Chaque masque existe en VARIANTES (motifs d'émiettement différents) : chaque vague en tire une, donc deux
    // vagues successives ne se ressemblent jamais tout à fait. Le front s'ÉMIETTE en s'éloignant (part de pixels
    // gardés par bande), et après son passage une ÉCUME clairsemée traîne un instant contre la berge.
    private const int Variants = 3;
    private const int Layers = Bands + 1;                         // 5 bandes + l'écume résiduelle
    private static readonly float[] Keep = { 1f, 0.92f, 0.8f, 0.66f, 0.5f }; // pixels gardés, bande 1 → 5
    private const float LingerKeep = 0.42f;                       // écume résiduelle : 42 % des pixels de la bande 1

    // Chronologie d'une vague (s) : elle CLAQUE vite contre la berge puis ralentit en s'éloignant, et l'écume
    // s'éteint par paliers (pas de fondu lisse : pixel-art).
    // FrontEnd[i] = instant où le front quitte la bande i (durées croissantes : 0,14 / 0,20 / 0,26 / 0,32 / 0,38 s).
    private static readonly float[] FrontEnd = { 0.14f, 0.34f, 0.60f, 0.92f, 1.30f };
    private const float LingerTime = 0.6f;                                      // écume résiduelle, après le front
    private const float WaveTime = 1.30f + LingerTime;
    private static readonly float[] FrontAlpha = { 0.95f, 0.85f, 0.72f, 0.58f, 0.45f };   // le front pâlit en s'éloignant

    // Plus blanche contre la berge, plus bleue en s'éloignant (elle se mêle à l'eau).
    private static readonly Color[] BandColor =
    {
        new(240, 250, 250), new(215, 238, 244), new(192, 226, 238), new(172, 214, 232), new(155, 204, 226),
    };

    // ── Poissons ──
    private const int OpenClearance = 7;    // eau libre = à 7 pixels au moins de tout bord / objet (rond de rayon 4 compris)
    private const int MaxFish = 3;          // ronds de poisson à l'écran en même temps, au plus
    private const int MaxRingRadius = 4;
    private const float FishTime = 1.15f;
    private static readonly Color FishColor = new(200, 232, 242);

    /// <summary>
    /// Données d'une cellule de tileset : masques du rivage + points d'eau libre. Les <see cref="Variants"/> ×
    /// <see cref="Layers"/> masques sont rangés dans UNE seule planche (colonne = couche, rangée = variante) : une
    /// texture par tuile au lieu de 18, et les couches d'une même case se dessinent sans changer de texture (un seul
    /// appel GPU pour la case).
    /// </summary>
    private sealed class TileFoam
    {
        public Texture2D? Shore;
        public readonly List<Point> Open = new();   // centres possibles d'un rond de poisson (pixels de la tuile)
        public byte[]? Clear;                       // distance (4-voisinage) de chaque pixel au non-eau, plafonnée
        public int[]? Origin;                       // pixel non-eau le plus proche (sens de la poussée des props)
    }

    /// <summary>Case d'eau du plateau courant, résolue UNE fois au placement (tuile, étendue) : le rendu et les
    /// poissons n'ont plus aucune recherche à faire par frame.</summary>
    private struct WaterCell
    {
        public Cell Cell;
        public TileFoam? Foam;
        public int Region;
        public int PropStart, PropCount;   // tranche de _props posée sur cette case (placée une fois, ne bouge plus)
    }

    private struct Fish
    {
        public int CellIndex;   // indice dans _cells
        public Point At;        // centre, en pixels de la tuile
        public float Age;
    }

    private readonly GraphicsDevice _gd;
    private readonly Random _rng = new();
    private readonly Dictionary<Texture2D, Color[]> _sheetPixels = new();
    private readonly Dictionary<(Texture2D Sheet, Rectangle Src), TileFoam?> _tiles = new();
    private readonly Texture2D[] _rings;                                 // rond aplati de rayon 1..4 (blanc)
    private readonly List<WaterCell> _cells = new();                     // cases d'eau du plateau
    private readonly List<int> _openCells = new();                       // indices des cases avec de l'eau libre
    private readonly Fish[] _fish = new Fish[MaxFish];
    private int _fishCount;
    private float _fishCooldown;

    /// <summary>Faux = rien à dessiner cette frame (aucune vague en cours, aucun poisson) : la scène saute tout.</summary>
    public bool Active { get; private set; }

    public int Count => _cells.Count;
    public Cell CellAt(int i) => _cells[i].Cell;

    public WaterFoam(GraphicsDevice gd)
    {
        _gd = gd;
        _rings = new Texture2D[MaxRingRadius + 1];
        for (var r = 1; r <= MaxRingRadius; r++)
            _rings[r] = BuildRing(r);
    }

    public void Unload()
    {
        foreach (var foam in _tiles.Values)
            foam?.Shore?.Dispose();
        foreach (var t in _rings)
            t?.Dispose();
        UnloadProps();
        _tiles.Clear();
        _sheetPixels.Clear();
        _cells.Clear();
        _openCells.Clear();
        _fishCount = 0;
        Active = false;
    }

    /// <summary>Nouveau plateau : on oublie ses étendues d'eau, ses vagues et ses poissons.</summary>
    public void ResetBoard()
    {
        _cells.Clear();
        _regionOf.Clear();
        _regions.Clear();
        _openCells.Clear();
        _props.Clear();
        _fishCount = 0;
        Active = false;
        _fishCooldown = 1f + (float)_rng.NextDouble() * 3f;
    }

    /// <summary>Déclare une case d'eau du plateau (calcule ses masques au besoin).</summary>
    public void AddCell(Cell cell, Texture2D sheet, Rectangle src)
    {
        var foam = FoamFor(sheet, src);
        if (foam is { Open.Count: > 0 })
            _openCells.Add(_cells.Count);
        _regionOf[cell] = -1;
        var start = _props.Count;
        if (foam != null)
            PlaceProps(foam);
        _cells.Add(new WaterCell { Cell = cell, Foam = foam, Region = -1, PropStart = start, PropCount = _props.Count - start });
    }


    // ── Rythme du rivage : des SÉRIES, comme une vraie houle ──
    // Par ÉTENDUE d'eau (cases d'eau qui se touchent), indépendante des autres : une série de 2 ou 3 vagues espacées
    // de ~1,6-2 s (la précédente a fini de s'éloigner quand la suivante claque), chacune plus faible et moins
    // longue que la précédente, puis un vrai CALME. Une grande étendue a des séries plus fréquentes et plus pleines ;
    // une petite mare, des séries rares et discrètes.
    private static readonly float[] WaveStrength = { 1f, 0.72f, 0.5f };   // 1re, 2e, 3e vague de la série
    private const float SeriesSpacingMin = 1.6f, SeriesSpacingMax = 2.0f;

    /// <summary>Une vague en cours (Age &lt; 0 = aucune).</summary>
    private struct Wave
    {
        public float Age;
        public int Variant;     // motif d'émiettement
        public float Strength;  // intensité (0..1)
        public int Reach;       // pixels de portée (bandes), ≤ Bands
    }

    private struct Region
    {
        public int Size;        // nombre de cases d'eau de l'étendue
        public bool HasShore;   // au moins une berge dessinée : sinon ni vague ni clapotis (rien à voir)
        public float Calm;      // attente avant la prochaine série (ne court qu'une fois l'eau redevenue calme)
        public int WaveIndex;   // rang de la prochaine vague dans la série en cours
        public int WavesLeft;   // vagues encore à venir dans la série
        public float NextIn;    // délai avant la vague suivante de la série
        public Wave Current, Previous;   // la précédente finit de s'éteindre pendant que la suivante claque
    }

    private readonly Dictionary<Cell, int> _regionOf = new();   // placement seulement (regroupement des étendues)
    private readonly List<Region> _regions = new();

    private bool SmallPond(in Region r) => r.Size <= 2;

    /// <summary>Calme entre deux séries : plus long pour une petite mare (eau abritée), court pour un grand plan d'eau.</summary>
    private float NextCalm(in Region r) => r.Size <= 2 ? Rand(9f, 14f) : r.Size <= 8 ? Rand(5f, 9f) : Rand(4f, 7f);

    private float Rand(float min, float max) => min + (float)_rng.NextDouble() * (max - min);

    private Wave NewWave(in Region r, int index)
    {
        var small = SmallPond(r);
        var strength = WaveStrength[index] * (small ? 0.7f : 1f);
        return new Wave
        {
            Age = 0f,
            Variant = _rng.Next(Variants),
            Strength = strength,
            Reach = Math.Max(2, Bands - index - (small ? 1 : 0)),   // chaque vague porte un pixel moins loin
        };
    }

    /// <summary>À appeler après les <see cref="AddCell"/> : regroupe les cases d'eau en étendues indépendantes.</summary>
    public void FinishBoard()
    {
        var stack = new Stack<Cell>();
        foreach (var entry in _cells)
        {
            if (_regionOf[entry.Cell] >= 0)
                continue;
            var region = _regions.Count;
            _regions.Add(new Region { Current = { Age = -1f }, Previous = { Age = -1f } });
            _regionOf[entry.Cell] = region;
            stack.Push(entry.Cell);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                Visit(new Cell(c.Column - 1, c.Row)); Visit(new Cell(c.Column + 1, c.Row));
                Visit(new Cell(c.Column, c.Row - 1)); Visit(new Cell(c.Column, c.Row + 1));
            }

            void Visit(Cell n)
            {
                if (_regionOf.TryGetValue(n, out var r) && r < 0)
                {
                    _regionOf[n] = region;
                    stack.Push(n);
                }
            }
        }
        // Chaque case retient son étendue : plus de recherche dans un dictionnaire au rendu.
        for (var i = 0; i < _cells.Count; i++)
        {
            var e = _cells[i];
            e.Region = _regionOf[e.Cell];
            _cells[i] = e;
            var reg = _regions[e.Region];
            reg.Size++;
            reg.HasShore |= e.Foam?.Shore != null;
            _regions[e.Region] = reg;
        }
        // Première série : décalée au hasard pour chaque étendue (pas toutes en même temps à l'arrivée).
        for (var i = 0; i < _regions.Count; i++)
        {
            var reg = _regions[i];
            reg.Calm = Rand(0.5f, NextCalm(reg));
            _regions[i] = reg;
        }
        // Masques du plateau calculés : la copie CPU des planches n'a plus d'usage (relue au besoin pour une autre
        // map) — on ne garde pas ~0,5-1 Mo de pixels en mémoire pour rien.
        _sheetPixels.Clear();
    }

    public void Update(float dt)
    {
        UpdateCore(dt);
        var active = _fishCount > 0;
        for (var i = 0; i < _regions.Count && !active; i++)
            active = _regions[i].Current.Age >= 0f || _regions[i].Previous.Age >= 0f;
        Active = active;
    }

    private static void Age(ref Wave w, float dt)
    {
        if (w.Age < 0f)
            return;
        w.Age += dt;
        if (w.Age >= WaveTime)
            w.Age = -1f;
    }

    private void UpdateCore(float dt)
    {
        // Étendues d'eau : calme → série de 2-3 vagues décroissantes → calme…
        for (var i = 0; i < _regions.Count; i++)
        {
            var r = _regions[i];
            if (!r.HasShore)
                continue;   // pleine eau sans berge : pas de ressac (ni de clapotis invisible)
            Age(ref r.Current, dt);
            Age(ref r.Previous, dt);
            if (r.WavesLeft > 0)
            {
                // Série en cours : la vague suivante claque pendant que la précédente finit de s'éteindre.
                if ((r.NextIn -= dt) <= 0f)
                {
                    r.Previous = r.Current;
                    r.Current = NewWave(r, r.WaveIndex++);
                    r.WavesLeft--;
                    r.NextIn = Rand(SeriesSpacingMin, SeriesSpacingMax);
                }
            }
            else if (r.Current.Age < 0f && r.Previous.Age < 0f && (r.Calm -= dt) <= 0f)
            {
                // Nouvelle série : 2 ou 3 vagues (3 plus souvent sur un grand plan d'eau).
                var threeChance = r.Size <= 2 ? 0.15 : r.Size <= 8 ? 0.35 : 0.55;
                var count = _rng.NextDouble() < threeChance ? 3 : 2;
                r.WaveIndex = 0;
                r.Current = NewWave(r, r.WaveIndex++);
                r.WavesLeft = count - 1;
                r.NextIn = Rand(SeriesSpacingMin, SeriesSpacingMax);
                r.Calm = NextCalm(r);   // décompté une fois la dernière vague éteinte
            }
            _regions[i] = r;
        }

        // Poissons : un « bloop » en eau libre toutes les 1 à 4 s, parfois un second juste à côté (le poisson
        // remonte deux fois).
        for (var i = _fishCount - 1; i >= 0; i--)
        {
            _fish[i].Age += dt;
            if (_fish[i].Age >= FishTime)
                _fish[i] = _fish[--_fishCount];
        }
        if (_openCells.Count == 0)
            return;
        _fishCooldown -= dt;
        if (_fishCooldown > 0f || _fishCount >= MaxFish)
            return;
        _fishCooldown = 1f + (float)_rng.NextDouble() * 3f;
        var cell = _openCells[_rng.Next(_openCells.Count)];
        var foam = _cells[cell].Foam!;
        var at = foam.Open[_rng.Next(foam.Open.Count)];
        _fish[_fishCount++] = new Fish { CellIndex = cell, At = at, Age = 0f };
        if (_fishCount < MaxFish && _rng.Next(3) == 0)
        {
            // Second bloop, 2 pixels plus loin, un peu après : le poisson file sous la surface (pas pair : reste
            // sur la grille d'échantillonnage de l'eau libre).
            var near = foam.Open[_rng.Next(foam.Open.Count)];
            var dx = Math.Clamp(near.X - at.X, -2, 2);
            var dy = Math.Clamp(near.Y - at.Y, -2, 2);
            var second = new Point(at.X + dx, at.Y + dy);
            if (foam.Open.Contains(second))
                _fish[_fishCount++] = new Fish { CellIndex = cell, At = second, Age = -0.35f - (float)_rng.NextDouble() * 0.25f };
        }
    }

    /// <summary>
    /// Dessine la vie de l'eau de la case d'eau n° <paramref name="index"/> (cf. <see cref="CellAt"/>) dans le batch
    /// OUVERT : ressac de son étendue + ronds de poissons. <paramref name="dest"/> = face du dessus à l'écran (carrée).
    /// </summary>
    public void DrawCell(SpriteBatch sb, int index, Rectangle dest, float alpha)
    {
        var entry = _cells[index];
        if (entry.Foam is not { } foam)
            return;
        if (!Active)
            return;
        if (foam.Shore != null && entry.Region >= 0)
        {
            var r = _regions[entry.Region];
            if (r.Previous.Age >= 0f)
                DrawWave(sb, foam.Shore, r.Previous, dest, alpha);   // la précédente s'éteint dessous
            if (r.Current.Age >= 0f)
                DrawWave(sb, foam.Shore, r.Current, dest, alpha);
        }
        for (var i = 0; i < _fishCount; i++)
        {
            ref readonly var f = ref _fish[i];
            if (f.CellIndex == index && f.Age >= 0f)
                DrawFish(sb, f, dest, alpha);
        }
    }

    /// <summary>
    /// Une vague à l'âge <paramref name="age"/> (s) : front qui claque contre la berge (bande 1) puis s'éloigne en
    /// ralentissant jusqu'à la bande 5 en pâlissant, chaque bande laissant une traînée plus pâle derrière elle. Dès
    /// que le front a quitté la berge, une écume clairsemée y reste et s'éteint par paliers francs.
    /// </summary>
    private static void DrawWave(SpriteBatch sb, Texture2D atlas, in Wave wave, Rectangle dest, float alpha)
    {
        var age = wave.Age;
        var variant = wave.Variant;
        var reach = wave.Reach;
        alpha *= wave.Strength;
        var front = 0;
        while (front < reach && age >= FrontEnd[front])
            front++;
        if (front < reach)
            Layer(front, FrontAlpha[front]);
        if (front > 0 && (front < reach || age < FrontEnd[reach - 1] + 0.15f))
            Layer(front - 1, FrontAlpha[front - 1] * 0.5f);   // traînée juste derrière le front
        if (age >= FrontEnd[1])
        {
            // Écume résiduelle contre la berge : 3 paliers francs jusqu'à extinction.
            var k = (age - FrontEnd[1]) / (WaveTime - FrontEnd[1]);
            var a = k < 0.33f ? 0.55f : k < 0.66f ? 0.35f : 0.16f;
            sb.Draw(atlas, dest, Cell(Bands), BandColor[0] * (a * alpha));
        }

        void Layer(int band, float a) => sb.Draw(atlas, dest, Cell(band), BandColor[band] * (a * alpha));
        Rectangle Cell(int layer) => new(layer * Face, variant * Face, Face, Face);   // case de la planche
    }

    /// <summary>
    /// « Bloop » de poisson : un point clair (il touche la surface), puis un rond aplati qui s'élargit (rayon 1 → 4)
    /// en s'effaçant par paliers francs.
    /// </summary>
    private void DrawFish(SpriteBatch sb, in Fish f, Rectangle dest, float alpha)
    {
        var px = Math.Max(1, dest.Width / Face);   // taille d'un pixel d'art à l'écran
        var age = f.Age;
        if (age < 0.12f)
        {
            sb.Draw(_rings[1], new Rectangle(dest.X + f.At.X * px, dest.Y + f.At.Y * px, px, px),
                new Rectangle(1, 1, 1, 1), FishColor * (0.8f * alpha));   // point central (pixel milieu du rond 1)
            return;
        }
        var t = (age - 0.12f) / (FishTime - 0.12f);                       // 0 → 1
        var r = Math.Min(1 + (int)(t * MaxRingRadius), MaxRingRadius);
        var a = r switch { 1 => 0.6f, 2 => 0.45f, 3 => 0.3f, _ => 0.18f };
        var ring = _rings[r];
        var half = new Point(ring.Width / 2, ring.Height / 2);
        sb.Draw(ring, new Rectangle(dest.X + (f.At.X - half.X) * px, dest.Y + (f.At.Y - half.Y) * px,
            ring.Width * px, ring.Height * px), FishColor * (a * alpha));
    }

    /// <summary>
    /// Rond de rayon <paramref name="r"/> pixels, APLATI (hauteur ≈ 0,6 × largeur : l'eau est vue en plongée), d'un
    /// pixel d'épaisseur. Rayon 1 : une croix de 4 pixels + son centre réservé au point d'impact.
    /// </summary>
    private Texture2D BuildRing(int r)
    {
        var ry = Math.Max(1, (int)MathF.Round(r * 0.6f));
        int w = 2 * r + 1, h = 2 * ry + 1;
        var data = new Color[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                float dx = (x - r) / (float)r, dy = (y - ry) / (float)ry;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                // Épaisseur ~1 pixel autour de l'ellipse (seuil relatif au rayon).
                if (MathF.Abs(d - 1f) <= 0.5f / Math.Min(r, ry + 0.5f))
                    data[y * w + x] = Color.White;
            }
        if (r == 1)
            data[ry * w + r] = Color.White;   // le point central sert à l'impact (cf. DrawFish)
        var tex = new Texture2D(_gd, w, h);
        tex.SetData(data);
        return tex;
    }

    /// <summary>Bruit déterministe 0..1 par pixel / variante / couche (émiettement du front de vague).</summary>
    private static float Noise(int x, int y, int variant, int layer)
    {
        var h = (uint)(x * 374761393 + y * 668265263 + variant * 2147483647 + layer * 1013904223);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFF) / 65535f;
    }

    private TileFoam? FoamFor(Texture2D sheet, Rectangle src)
    {
        if (_tiles.TryGetValue((sheet, src), out var cached))
            return cached;
        var foam = Build(sheet, src);
        _tiles[(sheet, src)] = foam;
        return foam;
    }

    private TileFoam? Build(Texture2D sheet, Rectangle src)
    {
        if (!_sheetPixels.TryGetValue(sheet, out var pixels))
        {
            pixels = new Color[sheet.Width * sheet.Height];
            sheet.GetData(pixels);
            _sheetPixels[sheet] = pixels;
        }
        var w = Math.Min(Face, src.Width);
        var h = Math.Min(Face, src.Height);
        Color At(int i) => pixels[(src.Y + i / w) * sheet.Width + src.X + i % w];

        // 1) Eau / non-eau, puis composantes connexes du non-eau : celles qui touchent le bord de la tuile sont
        //    la BERGE (source du ressac) ; les autres (rochers, feuilles au milieu de l'eau) ne moussent pas.
        var solid = new bool[w * h];
        for (var i = 0; i < solid.Length; i++)
        {
            var c = At(i);
            solid[i] = !(c.A > 0 && c.B > c.R + 25);
        }
        var shore = new bool[w * h];
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        var comp = new List<int>();
        for (var i = 0; i < solid.Length; i++)
        {
            if (!solid[i] || seen[i])
                continue;
            comp.Clear();
            stack.Push(i);
            seen[i] = true;
            var touchesEdge = false;
            long red = 0, green = 0;
            while (stack.Count > 0)
            {
                var k = stack.Pop();
                comp.Add(k);
                int kx = k % w, ky = k / w;
                if (kx == 0 || ky == 0 || kx == w - 1 || ky == h - 1)
                    touchesEdge = true;
                var c = At(k);
                red += c.R;
                green += c.G;
                Push(kx - 1, ky); Push(kx + 1, ky); Push(kx, ky - 1); Push(kx, ky + 1);
            }
            if (comp.Count < MinSolidPixels)
                foreach (var k in comp) solid[k] = false;   // détail peint : c'est de l'eau
            // Source du ressac : la BERGE (touche le bord de la tuile) et les ROCHERS dressés dans l'eau (pas verts) ;
            // les feuilles / pierres moussues flottantes (taches vertes isolées) restent sans ressac.
            else if (touchesEdge || green < red)
                foreach (var k in comp) shore[k] = true;
        }

        void Push(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h)
                return;
            var k = y * w + x;
            if (solid[k] && !seen[k])
            {
                seen[k] = true;
                stack.Push(k);
            }
        }

        var foam = new TileFoam { Shore = ShoreBands() };

        // 2) Eau libre (poissons) : à OpenClearance pixels au moins de tout non-eau ET du bord de la tuile (le rond
        //    ne déborde jamais sur une tuile voisine). Échantillonnée tous les 2 pixels.
        var clear = Distances(k => solid[k], OpenClearance);
        for (var y = OpenClearance; y < h - OpenClearance; y += 2)
            for (var x = OpenClearance; x < w - OpenClearance; x += 2)
                if (clear[y * w + x] >= OpenClearance)
                    foam.Open.Add(new Point(x, y));

        // 3) Props : distance de chaque pixel au non-eau (rochers, berges) et pixel non-eau le plus proche.
        var anyWater = false;
        if (w == Face && h == Face)
        {
            var origin = new int[w * h];
            Array.Fill(origin, -1);
            var dist = Distances(k => solid[k], ClearCap, origin);
            var clearMap = new byte[w * h];
            for (var i = 0; i < clearMap.Length; i++)
            {
                clearMap[i] = (byte)Math.Min(dist[i], ClearCap);
                anyWater |= !solid[i];
            }
            if (anyWater)
            {
                foam.Clear = clearMap;
                foam.Origin = origin;
            }
        }

        return foam.Shore == null && foam.Open.Count == 0 && !anyWater ? null : foam;

        // Distance (4-voisinage, sur l'eau) aux sources, plafonnée à max (au-delà : int.MaxValue). origin = pixel
        // source d'où vient la distance (direction de la berge), si demandé.
        int[] Distances(Func<int, bool> isSource, int max, int[]? origin = null)
        {
            var dist = new int[w * h];
            var queue = new Queue<int>();
            for (var i = 0; i < dist.Length; i++)
            {
                if (isSource(i))
                {
                    dist[i] = 0;
                    if (origin != null) origin[i] = i;
                    queue.Enqueue(i);
                }
                else dist[i] = int.MaxValue;
            }
            while (queue.Count > 0)
            {
                var k = queue.Dequeue();
                var d = dist[k] + 1;
                if (d > max)
                    continue;
                int kx = k % w, ky = k / w;
                Relax(kx - 1, ky); Relax(kx + 1, ky); Relax(kx, ky - 1); Relax(kx, ky + 1);

                void Relax(int x, int y)
                {
                    if (x < 0 || y < 0 || x >= w || y >= h)
                        return;
                    var j = y * w + x;
                    if (!solid[j] && dist[j] > d)   // ne court que sur l'EAU
                    {
                        dist[j] = d;
                        if (origin != null) origin[j] = origin[k];
                        queue.Enqueue(j);
                    }
                }
            }
            return dist;
        }

        // Masques du ressac (1..Bands). Hors tuile = eau (pas de mousse sur les raccords entre deux tuiles d'eau).
        // PERSPECTIVE : la vue plonge du haut vers le bas de l'écran. La berge du HAUT montre sa paroi, l'eau vient
        // la battre → ressac plein. Berges de CÔTÉ : vue de biais → atténué et plus court. Berge du BAS : son rebord,
        // au premier plan, cache la ligne d'eau → aucun ressac. Null si aucune bande.
        Texture2D? ShoreBands()
        {
            var origin = new int[w * h];
            var dist = Distances(k => shore[k], Bands, origin);
            var any = false;
            const int atlasW = Layers * Face;
            var data = new Color[atlasW * Variants * Face];   // planche : colonne = couche, rangée = variante
            for (var v = 0; v < Variants; v++)
                for (var layer = 0; layer < Layers; layer++)
                {
                    // Couche « écume » = pixels de la bande 1, très clairsemés.
                    var b = layer < Bands ? layer : 0;
                    var keep = layer < Bands ? Keep[layer] : LingerKeep;
                    var cellOrigin = v * Face * atlasW + layer * Face;
                    for (var y = 0; y < h; y++)
                        for (var x = 0; x < w; x++)
                        {
                            var k = y * w + x;
                            if (dist[k] != b + 1 || Noise(x, y, v, layer) > keep)
                                continue;
                            var oy = origin[k] / w;
                            var weight = oy < y ? 1f                       // berge du haut : paroi battue
                                : oy > y ? 0f                              // berge du bas : ligne d'eau cachée
                                : b + 1 <= SideBands ? SideWeight : 0f;    // côté : atténué, plus court
                            if (weight <= 0f)
                                continue;
                            data[cellOrigin + y * atlasW + x] = Color.White * weight;
                            any = true;
                        }
                }
            if (!any)
                return null;
            var atlas = new Texture2D(_gd, atlasW, Variants * Face);
            atlas.SetData(data);
            return atlas;
        }
    }
}
