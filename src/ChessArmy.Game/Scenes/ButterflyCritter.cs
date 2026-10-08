using System;
using System.Collections.Generic;
using System.IO;
using ChessArmy.Core.Map;
using ChessArmy.Engine.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Papillons d'ambiance (purement cosmétiques) : par VAGUES de 1 à 5, répartis en 1 à 3 petits GROUPES
/// indépendants (hauteur, sens et départ propres : deux en bas, trois en haut…), ils TRAVERSENT l'écran de jeu
/// d'un bord à l'autre, au-dessus du plateau et des pions. Spritesheets
/// <c>Assets/Anim/Butterfly.png</c> : 2 frames 16×16 tournées vers la DROITE (0 = ailes relevées, 1 = déployées).
/// Position en CASES relative à l'origine du plateau : ils suivent la caméra et le dézoom comme le reste.
/// </summary>
internal sealed class ButterflyCritter
{
    private const int MaxFlyers = 5;    // papillons par vague : 1 à 5
    private const int MaxGroups = 3;    // groupes par vague : 1 à 3

    private readonly Random _rng = new();
    private readonly Butterfly[] _flyers = { new(), new(), new(), new(), new() };
    // Groupes de la vague en cours (tampons réutilisés, pas d'alloc) : hauteur, sens, retard de départ.
    private readonly float[] _groupY = new float[MaxGroups];
    private readonly float[] _groupDir = new float[MaxGroups];
    private readonly float[] _groupLag = new float[MaxGroups];
    private int _activeCount;
    private readonly List<Texture2D> _sheets = new();   // une planche par couleur, tirée au hasard par papillon
    private readonly List<Color[]> _splashColors = new();   // couleurs de chaque planche (éclaboussure au clic)
    private float _cooldown;

    /// <summary>Charge TOUTES les planches <c>Butterfly*.png</c> du dossier : une nouvelle couleur = un PNG de plus.</summary>
    public void Load(GraphicsDevice gd, string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (var file in Directory.GetFiles(dir, "Butterfly*.png"))
            if (Textures.LoadPngOrNull(gd, file) is { } sheet)
            {
                _sheets.Add(sheet);
                _splashColors.Add(SheetColors(sheet));
            }
    }

    /// <summary>Couleurs CLAIRES de la planche (contour sombre exclu) : la gerbe d'un papillon écrasé.</summary>
    private static Color[] SheetColors(Texture2D sheet)
    {
        var data = new Color[sheet.Width * sheet.Height];
        sheet.GetData(data);
        var set = new HashSet<Color>();
        foreach (var c in data)
            if (c.A > 128 && c.R + c.G + c.B > 120)
                set.Add(new Color(c.R, c.G, c.B));
        var colors = new Color[set.Count];
        set.CopyTo(colors);
        return colors;
    }

    /// <summary>
    /// Clic sur un papillon en vol (repère <paramref name="layout"/>, celui où il est dessiné) : il est ÉCRASÉ et
    /// disparaît. Rend son centre écran, la taille d'un pixel d'art et ses couleurs pour l'éclaboussure.
    /// </summary>
    public bool TrySquash(Point mouse, GridLayout layout, out Vector2 center, out int pixelSize, out Color[] colors)
    {
        center = default;
        colors = Array.Empty<Color>();
        pixelSize = Math.Max(1, (int)MathF.Round(layout.TileSize / 64f));
        if (_activeCount == 0)
            return false;
        for (var i = _flyers.Length - 1; i >= 0; i--)   // dernier dessiné = au-dessus
        {
            var f = _flyers[i];
            if (!f.Active || !f.Hit(mouse, layout, pixelSize))
                continue;
            f.Active = false;
            _activeCount--;
            center = f.Center(layout);
            colors = _splashColors[f.SheetIndex];
            return true;
        }
        return false;
    }

    public void Unload()
    {
        foreach (var s in _sheets)
            s.Dispose();
        _sheets.Clear();
        _splashColors.Clear();
        foreach (var f in _flyers)
            f.Active = false;
        _activeCount = 0;
    }

    private int RandomSheet() => _rng.Next(_sheets.Count);

    private const float LandChance = 0.6f;   // part des vagues dont un papillon se pose (rocher / buisson)
    private bool _firstWave;                 // 1re vague du combat : la pose est GARANTIE (s'il y a un perchoir)
    private readonly List<(Vector2 Point, Cell Cell)> _perches = new();   // points de pose + leur case

    /// <summary>
    /// Case de perchoir libre ? (fourni par la scène : un buisson où se cache un pion n'est pas un perchoir).
    /// Null = toujours libre. Un pion qui y entre fait aussi s'envoler le papillon posé ou en approche.
    /// </summary>
    public Func<Cell, bool>? PerchFree { get; set; }

    /// <summary>Un papillon vient de se POSER sur la case de perchoir donnée (la scène y réagit : lucioles d'un buisson
    /// la nuit…). Appelé depuis <see cref="Update"/>, une fois par pose.</summary>
    public Action<Cell>? Landed { get; set; }

    /// <summary>
    /// Points où un papillon peut se poser pour le combat courant (dessus des rochers, des buissons…), en cases
    /// depuis l'origine du plateau, au pixel d'art près (1/64 de case), avec leur case. Vide = jamais de pose.
    /// </summary>
    public void SetPerches(List<(Vector2 Point, Cell Cell)> perches)
    {
        _perches.Clear();
        _perches.AddRange(perches);
    }

    /// <summary>Un perchoir au hasard parmi les LIBRES (quelques tirages), ou -1.</summary>
    private int PickPerch()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var k = _rng.Next(_perches.Count);
            if (PerchFree?.Invoke(_perches[k].Cell) ?? true)
                return k;
        }
        return -1;
    }

    /// <summary>Nouveau combat : aucun papillon à l'écran, première vague après un court délai.</summary>
    public void Reset()
    {
        foreach (var f in _flyers)
            f.Active = false;
        _activeCount = 0;
        _cooldown = 3f + (float)_rng.NextDouble() * 5f;
        _firstWave = true;
    }

    /// <summary>
    /// <paramref name="leftEdge"/>/<paramref name="rightEdge"/> = bords de l'écran de jeu, en cases depuis
    /// l'origine du plateau ; <paramref name="rows"/> = hauteur du plateau (ils volent dans cette bande).
    /// </summary>
    public void Update(float dt, float leftEdge, float rightEdge, int rows)
    {
        if (_sheets.Count == 0)
            return;
        _activeCount = 0;
        foreach (var f in _flyers)
            if (f.Active)
            {
                f.Update(dt, rows, _rng, PerchFree);
                if (f.JustLanded)
                {
                    f.JustLanded = false;
                    Landed?.Invoke(f.PerchCell);
                }
                if (f.Active)
                    _activeCount++;
            }
        if (_activeCount > 0)
            return;   // la vague suivante attend que l'écran soit vide

        _cooldown -= dt;
        if (_cooldown > 0f)
            return;
        _cooldown = 8f + (float)_rng.NextDouble() * 12f;   // entre deux vagues : 8 à 20 s

        var count = 1 + _rng.Next(MaxFlyers);
        var groups = 1 + _rng.Next(Math.Min(count, MaxGroups));
        for (var g = 0; g < groups; g++)
        {
            _groupY[g] = -0.3f + (float)_rng.NextDouble() * (rows - 0.4f);
            _groupDir[g] = _rng.Next(2) == 0 ? 1f : -1f;              // les groupes peuvent se croiser
            _groupLag[g] = (float)_rng.NextDouble() * 2.5f;           // départs décalés entre groupes
        }
        // De temps en temps, UN papillon de la vague ira se poser sur un rocher / buisson de la map (s'il y en a).
        var perchIndex = _perches.Count > 0 && (_firstWave || _rng.NextDouble() < LandChance) ? PickPerch() : -1;
        var lander = perchIndex >= 0 ? _rng.Next(count) : -1;
        _firstWave = false;
        for (var i = 0; i < count; i++)
        {
            var g = i % groups;   // chaque groupe reçoit au moins un papillon
            var y = Math.Clamp(_groupY[g] + ((float)_rng.NextDouble() - 0.5f) * 1.2f, -0.5f, rows - 0.5f);
            var lag = _groupLag[g] + (float)_rng.NextDouble() * 1.2f;   // un peu égrenés dans le groupe
            var sheet = RandomSheet();
            Vector2? perch = i == lander ? _perches[perchIndex].Point : null;
            _flyers[i].Start(_sheets[sheet], sheet, _groupDir[g], leftEdge, rightEdge, y, lag, _rng, perch,
                i == lander ? _perches[perchIndex].Cell : default);
        }
        _activeCount = count;
    }

    /// <summary>Dessine les papillons (batch dédié, PointClamp), calés sur la grille de pixels d'art du plateau.</summary>
    public void Draw(SpriteBatch sb, GridLayout layout)
    {
        if (_activeCount == 0)
            return;
        sb.Begin(samplerState: SamplerState.PointClamp);
        foreach (var f in _flyers)
            if (f.Active)
                f.Draw(sb, layout);
        sb.End();
    }

    /// <summary>
    /// Ombres AU SOL des papillons (batch dédié), à tracer dans la passe d'ombres, SOUS les pions : petite tache
    /// pixel-art sombre, décalée sous le papillon (il vole) et vers la lumière comme les ombres des pions ; plus
    /// large ailes déployées. <paramref name="pixelScale"/> = taille d'un pixel d'art du papillon dans ce repère.
    /// </summary>
    public void DrawShadows(SpriteBatch sb, Texture2D pixel, GridLayout layout, float alpha, int pixelScale)
    {
        if (_activeCount == 0)
            return;
        sb.Begin(samplerState: SamplerState.PointClamp);
        var color = Engine.UI.Palette.Black1 * alpha;
        foreach (var f in _flyers)
            if (f.Active)
                f.DrawShadow(sb, pixel, layout, color, pixelScale);
        sb.End();
    }

    /// <summary>
    /// Un papillon. Vol « papillon » : battements irréguliers, chaque coup d'aile (relevées → déployées) donne
    /// une petite impulsion vers le haut que la pesanteur reprend aussitôt → trajectoire en dents de scie ; de
    /// courts PLANÉS ailes ouvertes où il descend doucement ; une altitude de croisière qui erre ; une vitesse
    /// qui ondule.
    /// </summary>
    private sealed class Butterfly
    {
        private const int FrameSize = 16;
        private const float Gravity = 2.4f;        // cases / s²
        private const float FlapImpulse = 0.75f;   // cases / s vers le haut à chaque coup d'aile
        private const float Drag = 3f;             // amortissement vertical
        private const float Cruise = 0.95f;        // vitesse horizontale moyenne, cases / s

        // POSE sur un rocher : il vole normalement jusqu'à ~2 cases du perchoir, s'en approche en ralentissant,
        // se pose (frame 2, ailes repliées), attend 3 à 8 s puis redécolle et finit sa traversée.
        private const int FramePerched = 2;
        private const float ApproachDist = 2.5f;     // cases avant le perchoir où il commence à viser
        private const float PerchCruiseAbove = 0.3f; // hauteur de croisière d'un futur posé : ~1/3 de case au-dessus

        private enum Mode { Fly, Approach, Perched }

        public bool Active;
        private Mode _mode;
        private bool _hasPerch;
        private Vector2 _target;        // CENTRE du papillon une fois posé (pattes sur le point du rocher)
        private Cell _perchCell;        // case du perchoir (un pion qui y entre le fait s'envoler)
        private float _perchTimer;
        private Texture2D _sheet = null!;   // sa couleur, tirée au départ
        private float _x, _y;          // centre, en cases depuis l'origine du plateau
        private float _vy;
        private float _dir;             // +1 vers la droite, -1 vers la gauche
        private float _endX;            // sortie de l'écran
        private float _cruiseY;         // altitude de croisière (erre au fil du vol)
        private float _cruiseTimer;
        private int _frame;
        private float _flapTimer;
        private float _glide;           // > 0 : plané en cours
        private float _time;

        /// <param name="lag">Retard en cases derrière le point d'entrée (départs égrenés dans la vague).</param>
        public int SheetIndex;   // couleur (indice de planche), pour l'éclaboussure

        /// <param name="perch">Point du rocher où poser les pattes (cases depuis l'origine du plateau), ou null.</param>
        public void Start(Texture2D sheet, int sheetIndex, float dir, float leftEdge, float rightEdge, float y, float lag,
            Random rng, Vector2? perch = null, Cell perchCell = default)
        {
            const float margin = 0.5f;   // part / sort juste hors champ
            _sheet = sheet;
            SheetIndex = sheetIndex;
            _dir = dir;
            _mode = Mode.Fly;
            _hasPerch = perch.HasValue;
            _perchCell = perchCell;
            if (perch is { } p)
            {
                // Pattes de la frame « posé » : pixel (7, 14) de la frame (8, 14 une fois retournée vers la gauche).
                // Centre du sprite = point du rocher + (8 - colonne des pattes, 8 - 14) pixels d'art (64 par case).
                var footCol = dir > 0f ? 7 : 8;
                _target = p + new Vector2((8 - footCol) / 64f, (8 - 14) / 64f);
            }
            _x = dir > 0f ? leftEdge - margin - lag : rightEdge + margin + lag;
            _endX = dir > 0f ? rightEdge + margin : leftEdge - margin;
            // Futur posé : il entre DÉJÀ à peu près à la hauteur de son rocher (un peu au-dessus), au lieu de
            // devoir plonger de loin au dernier moment.
            if (_hasPerch)
                y = _target.Y - PerchCruiseAbove + ((float)rng.NextDouble() - 0.5f) * 0.4f;
            _y = _cruiseY = y;
            _vy = 0f;
            _frame = rng.Next(2);                         // pas en phase avec son compagnon
            _flapTimer = 0.05f + (float)rng.NextDouble() * 0.1f;
            _glide = 0f;
            _cruiseTimer = 1f;
            _time = (float)rng.NextDouble() * 10f;
            Active = true;
        }

        public bool IsPerched => _mode == Mode.Perched;
        public bool JustLanded;                 // posé à cette frame (relevé puis remis à faux par le gestionnaire)
        public Cell PerchCell => _perchCell;

        public void Update(float dt, int rows, Random rng, Func<Cell, bool>? perchFree)
        {
            _time += dt;
            // Un pion arrive sur son perchoir (buisson) : il renonce à s'y poser, ou s'envole s'il y est déjà.
            var disturbed = _mode != Mode.Fly && _hasPerch && perchFree != null && !perchFree(_perchCell);
            if (disturbed && _mode == Mode.Approach)
            {
                _mode = Mode.Fly;
                _hasPerch = false;
                _cruiseY = _y - 0.4f;
            }
            if (_mode == Mode.Perched)
            {
                _perchTimer -= dt;
                if (_perchTimer <= 0f || disturbed)
                {
                    // Redécolle : petit bond vers le haut, puis reprend sa traversée dans le même sens.
                    _mode = Mode.Fly;
                    _hasPerch = false;
                    _frame = 1;
                    _flapTimer = 0.08f;
                    _vy = -1f;
                    _cruiseY = _y - 0.5f;
                    _cruiseTimer = 1f;
                }
                return;
            }
            if (_mode == Mode.Approach)
            {
                UpdateApproach(dt, rng);
                return;
            }
            if (_hasPerch)
            {
                var ahead = _dir > 0f ? _target.X - _x : _x - _target.X;
                if (ahead <= 0f)
                    _hasPerch = false;           // dépassé (ne devrait pas arriver) : il continue sa route
                else if (ahead < ApproachDist)
                {
                    _mode = Mode.Approach;
                    UpdateApproach(dt, rng);
                    return;
                }
            }
            if (_glide > 0f)
            {
                _glide -= dt;
                _frame = 1;                       // plané : ailes déployées
                _vy += Gravity * 0.25f * dt;      // descend doucement
            }
            else
            {
                _flapTimer -= dt;
                if (_flapTimer <= 0f)
                {
                    _frame ^= 1;
                    _flapTimer = 0.07f + (float)rng.NextDouble() * 0.06f;   // battement IRRÉGULIER
                    if (_frame == 1)
                    {
                        _vy -= FlapImpulse * (0.7f + (float)rng.NextDouble() * 0.6f);   // coup d'aile : remonte
                        if (rng.Next(14) == 0)
                            _glide = 0.25f + (float)rng.NextDouble() * 0.45f;           // de temps en temps, il plane
                    }
                }
                _vy += Gravity * dt;
            }

            // Altitude de croisière qui erre + rappel doux vers elle (il ne tombe ni ne s'envole).
            _cruiseTimer -= dt;
            if (_cruiseTimer <= 0f)
            {
                _cruiseTimer = 0.8f + (float)rng.NextDouble() * 1.4f;
                _cruiseY = _hasPerch
                    // Futur posé : il erre de PEU autour d'une hauteur juste au-dessus de son rocher.
                    ? _target.Y - PerchCruiseAbove + ((float)rng.NextDouble() - 0.5f) * 0.3f
                    : Math.Clamp(_cruiseY + ((float)rng.NextDouble() - 0.5f) * 1.4f, -0.5f, rows - 0.5f);
            }
            _vy += (_cruiseY - _y) * 2.5f * dt;
            _vy -= _vy * Drag * dt;
            _y += _vy * dt;

            // Avance : plus lent en plané, ondulation lente.
            var speed = Cruise * (_glide > 0f ? 0.8f : 1f) * (0.8f + 0.25f * MathF.Sin(_time * 2.3f));
            _x += _dir * speed * dt;

            if ((_dir > 0f && _x > _endX) || (_dir < 0f && _x < _endX))
                Active = false;
        }

        /// <summary>
        /// Approche du perchoir : il continue de battre des ailes, ralentit en arrivant et glisse en hauteur vers
        /// le point visé ; une fois dessus (au pixel près), il se pose et replie ses ailes.
        /// </summary>
        private void UpdateApproach(float dt, Random rng)
        {
            _flapTimer -= dt;
            if (_flapTimer <= 0f)
            {
                _frame ^= 1;
                _flapTimer = 0.06f + (float)rng.NextDouble() * 0.05f;   // battements plus serrés : il freine
            }
            var dx = _target.X - _x;
            var dist = MathF.Abs(dx);
            var step = Cruise * Math.Clamp(dist / 0.8f, 0.3f, 1f) * dt;   // ralentit à l'approche
            _x = dist <= step ? _target.X : _x + MathF.Sign(dx) * step;
            var dy = _target.Y - _y;
            // Descente en LIGNE DOUCE : vitesse verticale calée pour arriver à la bonne hauteur en même temps
            // qu'au-dessus du point (pas de chute à pic au dernier moment), plafonnée pour rester un vol calme.
            var timeLeft = MathF.Max(dist / Cruise, 0.3f);
            var climb = MathF.Min(MathF.Abs(dy), MathF.Min(MathF.Abs(dy) / timeLeft, 0.9f) * dt + 0.02f * dt);
            _y += MathF.Sign(dy) * climb;
            _vy = 0f;
            if (_x == _target.X && MathF.Abs(_target.Y - _y) < 0.004f)
            {
                _y = _target.Y;
                _mode = Mode.Perched;
                _frame = FramePerched;
                _perchTimer = 3f + (float)rng.NextDouble() * 5f;   // pause de 3 à 8 s
                JustLanded = true;
            }
        }

        private const float ShadowDrop = 0.55f;    // l'ombre tombe ~une demi-case sous le papillon (hauteur de vol)
        private const float ShadowShift = 0.12f;   // et un peu vers la lumière (droite), comme celles des pions

        public Vector2 Center(GridLayout layout) =>
            layout.Origin + new Vector2(_x * layout.TileSize, _y * layout.TileSize);

        /// <summary>Souris sur le papillon : son carré 16×16 (pixels d'art), élargi de 3 px (cible mouvante).</summary>
        public bool Hit(Point mouse, GridLayout layout, int scale)
        {
            var c = Center(layout);
            var half = FrameSize * scale / 2f + 3f;
            return MathF.Abs(mouse.X - c.X) <= half && MathF.Abs(mouse.Y - c.Y) <= half;
        }

        public void DrawShadow(SpriteBatch sb, Texture2D pixel, GridLayout layout, Color color, int scale)
        {
            if (_mode == Mode.Perched)
                return;   // posé sur le rocher : pas d'ombre « de vol » décalée au sol
            var tile = layout.TileSize;
            var w = (_frame == 1 ? 9 : 5) * scale;    // ailes déployées : ombre plus large
            var ox = (int)MathF.Round(layout.Origin.X);
            var oy = (int)MathF.Round(layout.Origin.Y);
            var x = ox + (int)MathF.Round(((_x + ShadowShift) * tile - w / 2f) / scale) * scale;
            var y = oy + (int)MathF.Round((_y + ShadowDrop) * tile / scale) * scale;
            // Ovale pixel-art sur 3 rangées : bande centrale pleine, rangées haute/basse rognées d'un pixel.
            sb.Draw(pixel, new Rectangle(x, y + scale, w, scale), color);
            sb.Draw(pixel, new Rectangle(x + scale, y, w - 2 * scale, scale), color);
            sb.Draw(pixel, new Rectangle(x + scale, y + 2 * scale, w - 2 * scale, scale), color);
        }

        public void Draw(SpriteBatch sb, GridLayout layout)
        {
            var tile = layout.TileSize;
            var scale = Math.Max(1, (int)MathF.Round(tile / 64f));   // 1 pixel d'art = 1 pixel de tuile native
            var ox = (int)MathF.Round(layout.Origin.X);
            var oy = (int)MathF.Round(layout.Origin.Y);
            var half = FrameSize * scale / 2f;
            var x = ox + (int)MathF.Round((_x * tile - half) / scale) * scale;
            var y = oy + (int)MathF.Round((_y * tile - half) / scale) * scale;
            sb.Draw(_sheet, new Rectangle(x, y, FrameSize * scale, FrameSize * scale),
                new Rectangle(_frame * FrameSize, 0, FrameSize, FrameSize), Color.White, 0f, Vector2.Zero,
                _dir < 0f ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
        }
    }
}
