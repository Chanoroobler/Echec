using System;
using System.Collections.Generic;
using ChessArmy.Core.Map;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Props de décor posés au hasard, à chaque plateau, sur une SURFACE de toutes les tuiles qui en montrent. Deux
/// instances : l'HERBE (Assets/Props/Herb : champignons, tronc, fleurs… ; herbe pleine, bords des chemins, berges,
/// plateaux) et la TERRE (Assets/Props/Ground : pierres ; chemins de terre et haut des murs). Purement cosmétique et
/// immobile.
/// <para>Surface = la couleur dominante d'une tuile de référence. Les petits détails peints dessus (points de moins
/// de <see cref="MinSolidPixels"/> pixels, en 8-voisinage) en font partie ; le tramage d'un bord de chemin, connecté
/// en diagonale, non. Les pans verticaux (masques <c>*_faces.png</c>) et les petites zones isolées (dessus d'un
/// rocher) sont exclus. Un prop pose TOUTE son emprise à <see cref="Clearance"/> pixels au moins de tout le reste et
/// du bord de la tuile.</para>
/// <para>Une planche (<see cref="PropAtlas"/>) pour tous les sprites : un seul lot GPU, rien d'alloué par
/// frame.</para>
/// </summary>
internal sealed class SurfaceProps
{
    private const int Face = 64;
    private const int Clearance = 2;
    private const int MinSolidPixels = 6;
    private const int SmallSize = 4;   // sprite de 4 pixels au plus (fleur, feuille) = PETIT prop, semé entre les gros

    private struct Prop
    {
        public int Kind;
        public int Board;  // indice de sa case (placement)
        public Cell Cell;  // sa case (le tableau des cases de placement est vidé ensuite)
        public Point At;   // coin haut-gauche, en pixels de la tuile
    }

    private struct GrassCell
    {
        public Cell Cell;
        public int PropStart, PropCount;
    }

    private readonly GraphicsDevice _gd;
    private readonly Random _rng = new();
    private PropAtlas? _atlas;
    private readonly List<int> _bigKinds = new(), _flowerKinds = new();
    private readonly Dictionary<Texture2D, Color[]> _sheetPixels = new();
    private readonly Dictionary<(Texture2D Sheet, Rectangle Src), byte[]?> _clear = new();   // par cellule de tileset
    private readonly List<GrassCell> _cells = new();   // seulement les cases qui ont reçu des props
    private readonly List<Prop> _props = new();
    private Color _surface;
    private bool _hasSurface;

    private readonly float _bigPerTile, _smallPerTile;
    private readonly int _minRegion;

    /// <param name="bigPerTile">gros props par tuile de surface pleine (au prorata de la surface réelle).</param>
    /// <param name="smallPerTile">idem pour les petits props (fleurs, feuilles : <see cref="SmallSize"/> pixels au plus).</param>
    /// <param name="minRegion">aire minimale (pixels, 4-voisinage) d'une zone de surface pour y poser : écarte les petites
    /// taches de la même couleur (dessus des rochers…). 0 = aucune.</param>
    public SurfaceProps(GraphicsDevice gd, float bigPerTile, float smallPerTile, int minRegion)
    {
        _gd = gd;
        _bigPerTile = bigPerTile;
        _smallPerTile = smallPerTile;
        _minRegion = minRegion;
    }

    /// <summary>Couleur de surface retenue au dernier <see cref="ResetBoard"/> (null si aucune).</summary>
    public Color? Surface => _hasSurface ? _surface : null;

    /// <summary>Case où un gros obstacle (<c>Tronc*</c>, <c>Branch*</c>) est posé : les bêtes d'ambiance (renards) l'évitent.</summary>
    public bool BlocksCritters(Cell cell) => _blockers.Contains(cell);

    /// <summary>Nombre total de props posés sur le plateau (cf. <see cref="PropAt"/>).</summary>
    public int PropCount => _props.Count;

    /// <summary>Case et emprise (pixels de la tuile) du prop n° <paramref name="i"/> (ex. ronds de pluie dans les flaques).</summary>
    public (Cell Cell, Rectangle Rect) PropAt(int i)
    {
        var p = _props[i];
        var s = _atlas!.Src[p.Kind];
        return (p.Cell, new Rectangle(p.At.X, p.At.Y, s.Width, s.Height));
    }

    public int Count => _cells.Count;
    public Cell CellAt(int i) => _cells[i].Cell;

    public void Load(string dir) => Classify(PropAtlas.Load(_gd, dir, Face - 2 * Clearance));

    /// <summary>Sprites générés par le code (ex. flaques de pluie) au lieu d'un dossier.</summary>
    public void Load(List<(Color[] Data, int W, int H, string Name)> sprites) => Classify(PropAtlas.FromSprites(_gd, sprites));

    private void Classify(PropAtlas atlas)
    {
        _atlas = atlas;
        for (var k = 0; k < _atlas.Count; k++)
            (Math.Max(_atlas.Src[k].Width, _atlas.Src[k].Height) <= SmallSize ? _flowerKinds : _bigKinds).Add(k);
    }

    public void Unload()
    {
        _atlas?.Dispose();
        _atlas = null;
        _bigKinds.Clear();
        _flowerKinds.Clear();
        _sheetPixels.Clear();
        _clear.Clear();
        _cells.Clear();
        _props.Clear();
    }

    /// <summary>Nouveau plateau. <paramref name="sheet"/>/<paramref name="src"/> = la tuile de référence : sa couleur
    /// dominante (hors <paramref name="exclude"/>) est la surface où poser.</summary>
    public void ResetBoard(Texture2D sheet, Rectangle src, Color? exclude = null)
    {
        _cells.Clear();
        _props.Clear();
        _board.Clear();
        var pixels = Pixels(sheet);
        var counts = new Dictionary<Color, int>();
        for (var y = 0; y < Math.Min(Face, src.Height); y++)
            for (var x = 0; x < Math.Min(Face, src.Width); x++)
            {
                var c = pixels[(src.Y + y) * sheet.Width + src.X + x];
                if (c.A == 255 && c != exclude)
                    counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;
            }
        var previous = _hasSurface ? _surface : (Color?)null;
        _hasSurface = false;
        var best = 0;
        foreach (var (c, n) in counts)
            if (n > best)
            {
                best = n;
                _surface = c;
                _hasSurface = true;
            }
        if (previous != Surface)
            _clear.Clear();   // couleur de référence changée : les zones calculées ne valent plus (sinon : cache gardé)
    }

    /// <summary>Case du plateau (sa face de tuile) : retenue si elle montre assez de surface (placement dans
    /// <see cref="FinishBoard"/>, sur tout le plateau d'un coup). <paramref name="isFace"/>(x, y) = pixel de la face
    /// sur un pan VERTICAL (masque <c>*_faces.png</c>) : jamais de prop dessus. Masque fixe par cellule de tileset (cache).</summary>
    public void AddCell(Cell cell, Texture2D sheet, Rectangle src, Func<int, int, bool>? isFace = null)
    {
        if (!_hasSurface || _atlas?.Texture == null || src.Width < Face || src.Height < Face)
            return;
        if (!_clear.TryGetValue((sheet, src), out var clear))
            _clear[(sheet, src)] = clear = Build(sheet, src, isFace);
        if (clear == null)
            return;
        var area = 0;
        foreach (var d in clear)
            if (d >= Clearance)
                area++;
        _board.Add(new BoardCell { Cell = cell, Clear = clear, Area = area });
    }

    /// <summary>Fin du plateau : pose tous les props (cf. <see cref="Place"/>), puis la copie CPU des planches n'a
    /// plus d'usage.</summary>
    public void FinishBoard()
    {
        Place();
        _board.Clear();
        _sheetPixels.Clear();
    }

    // ── Placement HOMOGÈNE sur tout le plateau ──
    // Densité par tuile de surface pleine (cf. constructeur) ; une tuile qui n'en a qu'un coin en reçoit au prorata.
    private const int Candidates = 10;        // « meilleur candidat » : positions valides comparées par prop
    private const float SameKindWeight = 0.6f; // deux props de même sorte se repoussent plus (variété)
    private const int FullArea = (Face - 2 * Clearance) * (Face - 2 * Clearance);

    private struct BoardCell
    {
        public Cell Cell;
        public byte[] Clear;
        public int Area;   // pixels d'herbe où un prop peut poser un coin (≈ place disponible)
    }

    private readonly List<BoardCell> _board = new();   // placement seulement
    private readonly List<int> _areaSum = new();        // sommes cumulées des aires (tirage d'une case pondéré)
    private readonly HashSet<Cell> _blockers = new();    // cases avec un tronc ou une branche : les renards n'y marchent pas

    /// <summary>
    /// Pose les props sur TOUT le plateau en « meilleur candidat » (Mitchell) : pour chaque prop, on tire
    /// <see cref="Candidates"/> positions valides (case tirée au prorata de son herbe) et on garde celle qui est la plus
    /// LOIN des props déjà posés (bords à bords, une même sorte comptant plus près). Résultat : un semis régulier, sans
    /// tas ni trous, quel que soit le découpage en tuiles. Gros props d'abord (ils structurent), fleurs entre eux.
    /// </summary>
    private void Place()
    {
        _props.Clear();
        _cells.Clear();
        _areaSum.Clear();
        _blockers.Clear();
        var total = 0;
        foreach (var b in _board)
            _areaSum.Add(total += b.Area);
        if (total == 0)
            return;
        var tiles = total / (float)FullArea;
        RollLimits();
        if (_bigKinds.Count > 0)
            for (int n = 0, count = (int)MathF.Round(tiles * _bigPerTile); n < count; n++)
                if (PickKind(_bigKinds) is var big and >= 0 && PlaceBest(big, total))
                    CountLimit(big);
        if (_flowerKinds.Count > 0)
            for (int n = 0, count = (int)MathF.Round(tiles * _smallPerTile); n < count; n++)
                if (PickKind(_flowerKinds) is var small and >= 0 && PlaceBest(small, total))
                    CountLimit(small);

        // Rangement par case (dessin case par case), de haut en bas dans la case.
        _props.Sort((a, b) => a.Board != b.Board ? a.Board.CompareTo(b.Board) : a.At.Y.CompareTo(b.At.Y));
        for (var i = 0; i < _props.Count;)
        {
            var start = i;
            var board = _props[i].Board;
            while (i < _props.Count && _props[i].Board == board)
                i++;
            _cells.Add(new GrassCell { Cell = _board[board].Cell, PropStart = start, PropCount = i - start });
            for (var j = start; j < i; j++)
                if (_atlas!.NameStarts(_props[j].Kind, "Tronc") || _atlas.NameStarts(_props[j].Kind, "Branch"))
                    _blockers.Add(_board[board].Cell);
        }
    }

    private bool PlaceBest(int kind, int totalArea)
    {
        var size = _atlas!.Src[kind];
        var spanX = Face - 2 * Clearance - size.Width;
        var spanY = Face - 2 * Clearance - size.Height;
        if (spanX < 0 || spanY < 0)
            return false;
        var radius = Math.Max(size.Width, size.Height) * 0.5f;
        var found = 0;
        var bestScore = float.MinValue;
        Prop best = default;
        for (var attempt = 0; attempt < Candidates * 8 && found < Candidates; attempt++)
        {
            var board = PickCell(totalArea);
            var clear = _board[board].Clear;
            var spot = new Rectangle(Clearance + _rng.Next(spanX + 1), Clearance + _rng.Next(spanY + 1), size.Width, size.Height);
            if (!FootprintClear(clear, spot) || Overlaps(board, spot))
                continue;
            found++;
            var cell = _board[board].Cell;
            var cx = cell.Column * Face + spot.X + size.Width * 0.5f;
            var cy = cell.Row * Face + spot.Y + size.Height * 0.5f;
            var score = float.MaxValue;
            foreach (var p in _props)
            {
                var ps = _atlas.Src[p.Kind];
                var pc = _board[p.Board].Cell;
                var dx = pc.Column * Face + p.At.X + ps.Width * 0.5f - cx;
                var dy = pc.Row * Face + p.At.Y + ps.Height * 0.5f - cy;
                var gap = MathF.Sqrt(dx * dx + dy * dy) - radius - Math.Max(ps.Width, ps.Height) * 0.5f;
                if (p.Kind == kind)
                    gap *= SameKindWeight;
                score = Math.Min(score, gap);
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = new Prop { Kind = kind, Board = board, Cell = cell, At = spot.Location };
            }
        }
        if (found == 0)
            return false;
        _props.Add(best);
        return true;
    }

    // ── Plafonds par plateau (ex. 1 à 2 branches, 3 feuillages au plus) ──
    private readonly List<(string Prefix, int Min, int Max)> _limits = new();
    private readonly List<int> _limitCap = new(), _limitUsed = new();

    /// <summary>
    /// Au plus <paramref name="min"/> à <paramref name="max"/> (tiré à chaque plateau) props dont le nom commence par
    /// <paramref name="prefix"/> (toutes variantes confondues, ex. « Feuillage » = Feuillage + Feuillage2).
    /// </summary>
    public void Limit(string prefix, int min, int max) => _limits.Add((prefix, min, max));

    // Groupes ÉQUILIBRÉS : plusieurs sortes (noms de fichier EXACTS) posées à peu près autant les unes que les autres
    // (écart d'1 au plus), ex. Feuille = Tronc = Feuillage. Les sortes hors groupe ne sont pas concernées.
    private readonly List<string[]> _groups = new();
    private readonly List<int[]> _groupCount = new();

    /// <summary>Autant de chacune des sortes <paramref name="names"/> (noms de fichier exacts, sans extension) à un
    /// près : une sorte ne peut prendre plus d'une longueur d'avance sur la moins posée du groupe.</summary>
    public void Balance(params string[] names) => _groups.Add(names);

    private bool NameIs(int kind, string name) => string.Equals(_atlas!.Names[kind], name, StringComparison.OrdinalIgnoreCase);

    private void RollLimits()
    {
        _limitCap.Clear();
        _limitUsed.Clear();
        foreach (var l in _limits)
        {
            _limitCap.Add(_rng.Next(l.Min, l.Max + 1));
            _limitUsed.Add(0);
        }
        _groupCount.Clear();
        foreach (var g in _groups)
            _groupCount.Add(new int[g.Length]);   // placement seulement (une fois par plateau)
    }

    /// <summary>Plafond qui concerne la sorte <paramref name="kind"/> (-1 : aucun).</summary>
    private int LimitOf(int kind)
    {
        for (var i = 0; i < _limits.Count; i++)
            if (_atlas!.NameStarts(kind, _limits[i].Prefix))
                return i;
        return -1;
    }

    private bool Capped(int kind)
    {
        if (LimitOf(kind) is var l and >= 0 && _limitUsed[l] >= _limitCap[l])
            return true;
        for (var g = 0; g < _groups.Count; g++)
        {
            var names = _groups[g];
            var counts = _groupCount[g];
            for (var m = 0; m < names.Length; m++)
            {
                if (!NameIs(kind, names[m]))
                    continue;
                var least = int.MaxValue;
                foreach (var c in counts)
                    least = Math.Min(least, c);
                if (counts[m] > least)
                    return true;   // déjà une longueur d'avance : on attend les autres
            }
        }
        return false;
    }

    private void CountLimit(int kind)
    {
        if (LimitOf(kind) is var l and >= 0)
            _limitUsed[l]++;
        for (var g = 0; g < _groups.Count; g++)
            for (var m = 0; m < _groups[g].Length; m++)
                if (NameIs(kind, _groups[g][m]))
                    _groupCount[g][m]++;
    }

    /// <summary>Sorte au hasard parmi <paramref name="kinds"/> qui n'a pas atteint son plafond (-1 : toutes plafonnées).</summary>
    private int PickKind(List<int> kinds)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var k = kinds[_rng.Next(kinds.Count)];
            if (!Capped(k))
                return k;
        }
        foreach (var k in kinds)
            if (!Capped(k))
                return k;
        return -1;
    }

    /// <summary>Case tirée au prorata de son herbe disponible.</summary>
    private int PickCell(int totalArea)
    {
        var r = _rng.Next(totalArea);
        int lo = 0, hi = _areaSum.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_areaSum[mid] > r) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>Touche (à 1 pixel près) un prop déjà posé dans la même case ? (Ils ne débordent jamais de leur case.)</summary>
    private bool Overlaps(int board, Rectangle spot)
    {
        var inflated = new Rectangle(spot.X - 1, spot.Y - 1, spot.Width + 2, spot.Height + 2);
        foreach (var p in _props)
        {
            if (p.Board != board)
                continue;
            var s = _atlas!.Src[p.Kind];
            if (inflated.Intersects(new Rectangle(p.At.X, p.At.Y, s.Width, s.Height)))
                return true;
        }
        return false;
    }

    private static bool FootprintClear(byte[] clear, Rectangle spot)
    {
        for (var y = spot.Top; y < spot.Bottom; y++)
            for (var x = spot.Left; x < spot.Right; x++)
                if (clear[y * Face + x] < Clearance)
                    return false;
        return true;
    }

    private Color[] Pixels(Texture2D sheet)
    {
        if (!_sheetPixels.TryGetValue(sheet, out var pixels))
        {
            pixels = new Color[sheet.Width * sheet.Height];
            sheet.GetData(pixels);
            _sheetPixels[sheet] = pixels;
        }
        return pixels;
    }

    /// <summary>
    /// Distance (4-voisinage, plafonnée à <see cref="Clearance"/>) de chaque pixel d'herbe de la face au non-herbe et
    /// au bord de la tuile ; null si la tuile n'a aucun coin d'herbe assez grand.
    /// </summary>
    private byte[]? Build(Texture2D sheet, Rectangle src, Func<int, int, bool>? isFace)
    {
        var pixels = Pixels(sheet);
        var solid = new bool[Face * Face];
        for (var i = 0; i < solid.Length; i++)
            solid[i] = pixels[(src.Y + i / Face) * sheet.Width + src.X + i % Face] != _surface;

        // Petits détails peints sur l'herbe (8-voisinage : le tramage d'un bord reste un bord) = herbe.
        var seen = new bool[solid.Length];
        var stack = new Stack<int>();
        var comp = new List<int>();
        for (var i = 0; i < solid.Length; i++)
        {
            if (!solid[i] || seen[i])
                continue;
            comp.Clear();
            stack.Push(i);
            seen[i] = true;
            while (stack.Count > 0)
            {
                var k = stack.Pop();
                comp.Add(k);
                int kx = k % Face, ky = k / Face;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int x = kx + dx, y = ky + dy;
                        if (x < 0 || y < 0 || x >= Face || y >= Face)
                            continue;
                        var j = y * Face + x;
                        if (solid[j] && !seen[j])
                        {
                            seen[j] = true;
                            stack.Push(j);
                        }
                    }
            }
            if (comp.Count < MinSolidPixels)
                foreach (var k in comp)
                    solid[k] = false;
        }

        // Pans verticaux (briques des murs, flancs de rochers) : jamais de prop, même de la bonne couleur.
        if (isFace != null)
            for (var i = 0; i < solid.Length; i++)
                if (isFace(i % Face, i / Face))
                    solid[i] = true;

        // Petites zones de surface (4-voisinage) : taches de la même couleur hors du vrai sol (dessus d'un rocher…).
        if (_minRegion > 0)
        {
            Array.Clear(seen);
            for (var i = 0; i < solid.Length; i++)
            {
                if (solid[i] || seen[i])
                    continue;
                comp.Clear();
                stack.Push(i);
                seen[i] = true;
                while (stack.Count > 0)
                {
                    var k = stack.Pop();
                    comp.Add(k);
                    int kx = k % Face, ky = k / Face;
                    Visit(kx - 1, ky); Visit(kx + 1, ky); Visit(kx, ky - 1); Visit(kx, ky + 1);
                }
                if (comp.Count < _minRegion)
                    foreach (var k in comp)
                        solid[k] = true;
            }

            void Visit(int x, int y)
            {
                if (x < 0 || y < 0 || x >= Face || y >= Face)
                    return;
                var j = y * Face + x;
                if (!solid[j] && !seen[j])
                {
                    seen[j] = true;
                    stack.Push(j);
                }
            }
        }

        // Distance au non-surface ET au bord de la tuile (hors tuile = inconnu : on ne déborde pas).
        var clear = new byte[solid.Length];
        var queue = new Queue<int>();
        var any = false;
        for (var i = 0; i < clear.Length; i++)
        {
            int x = i % Face, y = i / Face;
            if (solid[i])
            {
                clear[i] = 0;
                queue.Enqueue(i);
            }
            else
            {
                clear[i] = (byte)Math.Min(Clearance, Math.Min(Math.Min(x, y), Math.Min(Face - 1 - x, Face - 1 - y)));
                any = true;
            }
        }
        if (!any)
            return null;
        while (queue.Count > 0)
        {
            var k = queue.Dequeue();
            var d = clear[k] + 1;
            if (d > Clearance)
                continue;
            int kx = k % Face, ky = k / Face;
            Relax(kx - 1, ky); Relax(kx + 1, ky); Relax(kx, ky - 1); Relax(kx, ky + 1);

            void Relax(int x, int y)
            {
                if (x < 0 || y < 0 || x >= Face || y >= Face)
                    return;
                var j = y * Face + x;
                if (clear[j] > d)
                {
                    clear[j] = (byte)d;
                    queue.Enqueue(j);
                }
            }
        }
        for (var i = 0; i < clear.Length; i++)
            if (clear[i] >= Clearance)
                return clear;
        return null;   // aucun pixel assez loin du non-herbe
    }

    /// <summary>Props de la case n° <paramref name="index"/> (cf. <see cref="CellAt"/>) dans le batch OUVERT :
    /// les sprites tels quels (leur ombre est peinte dans le PNG). <paramref name="dest"/> = face du dessus à l'écran.</summary>
    public void DrawCell(SpriteBatch sb, int index, Rectangle dest, float alpha)
    {
        var atlas = _atlas!;
        var tex = atlas.Texture!;
        var entry = _cells[index];
        var px = Math.Max(1, dest.Width / Face);
        var end = entry.PropStart + entry.PropCount;
        var tint = Color.White * alpha;
        for (var i = entry.PropStart; i < end; i++)
        {
            var p = _props[i];
            var s = atlas.Src[p.Kind];
            sb.Draw(tex, new Rectangle(dest.X + p.At.X * px, dest.Y + p.At.Y * px, s.Width * px, s.Height * px), s, tint);
        }
    }
}
