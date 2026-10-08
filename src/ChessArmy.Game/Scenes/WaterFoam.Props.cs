using System;
using System.Collections.Generic;
using System.IO;
using ChessArmy.Engine.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Props flottants (Assets/Props/Water : feuilles, nénuphars, fleurs…) posés au hasard sur l'eau à chaque plateau,
/// à <see cref="PropClearance"/> pixels au moins de tout rocher / berge et sans se chevaucher : ~1 gros prop par
/// tuile en moyenne + une petite grappe de fleurs (<c>Fleur*.png</c>). Quand une vague de leur étendue passe, ils sont
/// poussés d'1 pixel d'art à l'opposé du rocher le plus proche puis reviennent (ressac) : la poussée arrive plus tard
/// sur un prop loin du rocher. Une OMBRE (silhouette sombre, 1 pixel dessous) les fait flotter.
/// <para>Tous les sprites ET leurs silhouettes sont rangés dans UNE planche : tous les props du plateau se dessinent
/// d'un seul lot GPU (aucun changement de texture), sans allocation par frame.</para>
/// </summary>
internal sealed partial class WaterFoam
{
    private const int PropClearance = 2;
    private const int ClearCap = 32;
    private const float PropDelayPerPixel = 0.06f;   // retard de la poussée par pixel d'éloignement au rocher
    private const int PropMaxDelayPixels = 16;       // au-delà, même retard (la vague doit encore être en cours)
    private const float PropPushTime = 0.35f, PropBackStart = 0.6f, PropBackEnd = 0.85f;
    private static readonly Color ShadowColor = new Color(14, 38, 64) * 0.4f;

    private struct Prop
    {
        public int Kind;          // indice dans _propSrc / _shadowSrc
        public Point At;          // coin haut-gauche, en pixels de la tuile
        public Point Push;        // direction de la poussée (±1 sur un axe)
        public float Delay;       // retard de la poussée sur l'âge de la vague
    }

    private Texture2D? _propAtlas;                             // sprites (rangée du haut) + silhouettes (dessous)
    private PropAtlas? _atlas;
    private readonly List<Rectangle> _propSrc = new();         // sprite de chaque sorte dans la planche
    private readonly List<Rectangle> _shadowSrc = new();       // sa silhouette blanche (teintée en ombre au rendu)
    private readonly List<int> _bigKinds = new(), _leafKinds = new(), _flowerKinds = new();
    private Rectangle _whitePx;                                // pixel blanc de la planche (sillage)
    private static readonly Color WakeColor = new(205, 234, 244);
    private readonly List<Prop> _props = new();
    private readonly List<Rectangle> _propSpots = new();       // placement : emprises déjà prises dans la case (tampon)

    /// <summary>Vrai si le plateau a des props : ils se dessinent à chaque frame, même sans vague.</summary>
    public bool HasProps => _props.Count > 0;

    /// <summary>
    /// Charge tous les PNG du dossier de props d'eau (un nouveau prop = un PNG de plus ; <c>Fleur*</c> = fleur,
    /// éparpillée ; <c>Feuille*</c> = feuille, au moins une par tuile) dans une seule planche (cf. <see cref="PropAtlas"/>).
    /// </summary>
    public void LoadProps(string dir)
    {
        _atlas = PropAtlas.Load(_gd, dir, Face - 2);
        if (_atlas.Texture == null)
            return;
        _propAtlas = _atlas.Texture;
        _whitePx = _atlas.WhitePx;
        for (var k = 0; k < _atlas.Count; k++)
        {
            (_atlas.NameStarts(k, "Fleur") ? _flowerKinds : _bigKinds).Add(k);
            if (_atlas.NameStarts(k, "Feuille"))
                _leafKinds.Add(k);
            _propSrc.Add(_atlas.Src[k]);
            _shadowSrc.Add(_atlas.ShadowSrc[k]);
        }
    }

    private void UnloadProps()
    {
        _atlas?.Dispose();
        _atlas = null;
        _propAtlas = null;
        _propSrc.Clear();
        _shadowSrc.Clear();
        _bigKinds.Clear();
        _leafKinds.Clear();
        _flowerKinds.Clear();
        _props.Clear();
    }

    /// <summary>
    /// Props de la case : parfois (45 %) un gros prop en plus (feuille ou nénuphar, posé d'abord : il lui faut de la
    /// place), puis TOUJOURS une feuille (<c>Feuille*</c>), puis 3 à 6 fleurs éparpillées sur toute la tuile.
    /// Emprise entièrement dans la tuile, à <see cref="PropClearance"/> pixels du non-eau, sans toucher un autre prop
    /// de la case (une tuile presque pleine de rochers peut donc en avoir moins).
    /// </summary>
    private void PlaceProps(TileFoam foam)
    {
        if (_propAtlas == null || foam.Clear is not { } clear)
            return;
        _propSpots.Clear();
        if (_bigKinds.Count > 0 && _rng.NextDouble() < 0.45)
            TryPlace(_bigKinds[_rng.Next(_bigKinds.Count)], 30);
        if (_leafKinds.Count > 0)
            TryPlace(_leafKinds[_rng.Next(_leafKinds.Count)], 80);   // au moins une feuille : on insiste
        if (_flowerKinds.Count > 0)
        {
            var flowers = 3 + _rng.Next(4);
            for (var n = 0; n < flowers; n++)
                TryPlace(_flowerKinds[_rng.Next(_flowerKinds.Count)], 20);
        }

        // Quelques essais au hasard sur la tuile : pas de liste de candidats à construire (placement seulement).
        void TryPlace(int kind, int attempts)
        {
            var size = _propSrc[kind];
            var spanX = Face - 2 - size.Width;
            var spanY = Face - 2 - size.Height;
            if (spanX < 0 || spanY < 0)
                return;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var spot = new Rectangle(1 + _rng.Next(spanX + 1), 1 + _rng.Next(spanY + 1), size.Width, size.Height);
                if (!FootprintClear(clear, spot) || Overlaps(spot))
                    continue;
                _propSpots.Add(spot);
                _props.Add(MakeProp(foam, kind, spot));
                return;
            }
        }

        bool Overlaps(Rectangle spot)
        {
            var inflated = new Rectangle(spot.X - 1, spot.Y - 1, spot.Width + 2, spot.Height + 2);
            foreach (var s in _propSpots)
                if (s.Intersects(inflated))
                    return true;
            return false;
        }
    }

    private static bool FootprintClear(byte[] clear, Rectangle spot)
    {
        for (var y = spot.Top; y < spot.Bottom; y++)
            for (var x = spot.Left; x < spot.Right; x++)
                if (clear[y * Face + x] < PropClearance)
                    return false;
        return true;
    }

    /// <summary>Sens et retard de la poussée : à l'opposé du rocher le plus proche du centre du prop.</summary>
    private static Prop MakeProp(TileFoam foam, int kind, Rectangle spot)
    {
        var center = spot.Center;
        var k = center.Y * Face + center.X;
        int dist = foam.Clear![k];
        var push = new Point(0, 1);   // eau libre (aucun rocher dans la tuile) : poussé vers le bas, comme le ressac
        var delay = PropMaxDelayPixels * PropDelayPerPixel;
        if (dist < ClearCap && foam.Origin![k] >= 0)
        {
            var o = foam.Origin[k];
            int dx = center.X - o % Face, dy = center.Y - o / Face;
            push = Math.Abs(dx) > Math.Abs(dy) ? new Point(Math.Sign(dx), 0) : new Point(0, dy >= 0 ? 1 : -1);
            delay = Math.Min(dist, PropMaxDelayPixels) * PropDelayPerPixel;
        }
        return new Prop { Kind = kind, At = spot.Location, Push = push, Delay = delay };
    }

    /// <summary>
    /// Props de la case d'eau n° <paramref name="index"/> dans le batch OUVERT (ombres puis sprites, même planche) :
    /// poussés d'1 pixel quand la vague de leur étendue les atteint, ramenés d'1 pixel par le ressac si elle est
    /// forte. À dessiner pour TOUTES les cases avant l'écume (un seul lot GPU, l'écume passe par-dessus).
    /// </summary>
    public void DrawCellProps(SpriteBatch sb, int index, Rectangle dest, float alpha)
    {
        var entry = _cells[index];
        if (entry.PropCount == 0 || _propAtlas == null)
            return;
        var px = Math.Max(1, dest.Width / Face);
        Wave current = default, previous = default;
        current.Age = previous.Age = -1f;
        if (entry.Region >= 0)
        {
            current = _regions[entry.Region].Current;
            previous = _regions[entry.Region].Previous;
        }
        var end = entry.PropStart + entry.PropCount;
        var shadow = ShadowColor * alpha;
        var tint = Color.White * alpha;
        // Passe 0 : ombres + sillages (sur l'eau) ; passe 1 : les sprites par-dessus (une ombre ne recouvre jamais une
        // fleur voisine).
        for (var pass = 0; pass < 2; pass++)
            for (var i = entry.PropStart; i < end; i++)
            {
                var p = _props[i];
                var step = Bob(current, p.Delay);
                if (step == 0)
                    step = Bob(previous, p.Delay);
                var src = pass == 0 ? _shadowSrc[p.Kind] : _propSrc[p.Kind];
                var at = new Point(p.At.X + p.Push.X * step, p.At.Y + p.Push.Y * step);
                var x = dest.X + at.X * px;
                var y = dest.Y + (at.Y + (pass == 0 ? 1 : 0)) * px;
                sb.Draw(_propAtlas, new Rectangle(x, y, src.Width * px, src.Height * px), src, pass == 0 ? shadow : tint);
                if (pass == 0 && step != 0)
                    DrawWake(sb, entry.Foam!.Clear!, at, src.Width, src.Height,
                        new Point(p.Push.X * step, p.Push.Y * step), dest, px, alpha);
            }
    }

    /// <summary>
    /// Petit sillage en V derrière un prop qui bouge (sens <paramref name="move"/>) : une ligne claire juste derrière,
    /// un peu plus étroite que lui, puis ses deux bouts écartés un pixel plus loin. Jamais sur un rocher ni hors tuile.
    /// </summary>
    private void DrawWake(SpriteBatch sb, byte[] clear, Point at, int w, int h, Point move, Rectangle dest, int px, float alpha)
    {
        var back = new Point(-move.X, -move.Y);
        var perp = new Point(Math.Abs(move.Y), Math.Abs(move.X));   // le long du bord arrière
        var len = move.Y != 0 ? w : h;
        // Premier pixel de la rangée juste derrière le bord arrière.
        var start = new Point(back.X > 0 ? at.X + w : back.X < 0 ? at.X - 1 : at.X,
                              back.Y > 0 ? at.Y + h : back.Y < 0 ? at.Y - 1 : at.Y);
        var near = WakeColor * (0.6f * alpha);
        var far = WakeColor * (0.32f * alpha);
        if (len < 3)
            Pixel(start.X + perp.X * (len / 2), start.Y + perp.Y * (len / 2), near);
        else
            for (var i = 1; i < len - 1; i++)
                Pixel(start.X + perp.X * i, start.Y + perp.Y * i, near);
        Pixel(start.X + back.X, start.Y + back.Y, far);
        Pixel(start.X + back.X + perp.X * (len - 1), start.Y + back.Y + perp.Y * (len - 1), far);

        void Pixel(int x, int y, Color color)
        {
            if (x < 0 || y < 0 || x >= Face || y >= Face || clear[y * Face + x] == 0)
                return;
            sb.Draw(_propAtlas!, new Rectangle(dest.X + x * px, dest.Y + y * px, px, px), _whitePx, color);
        }
    }

    /// <summary>Décalage (+1 poussé, -1 ressac, 0 repos) d'un prop sous la vague <paramref name="w"/>.</summary>
    private static int Bob(in Wave w, float delay)
    {
        if (w.Age < 0f)
            return 0;
        var t = w.Age - delay;
        if (t < 0f)
            return 0;
        if (t < PropPushTime)
            return 1;
        if (w.Strength >= 0.7f && t >= PropBackStart && t < PropBackEnd)
            return -1;
        return 0;
    }
}
