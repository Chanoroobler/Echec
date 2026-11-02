using System;
using ChessArmy.Engine.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Grenouille d'ambiance (Assets/Anim/Grenouille.png : 7 images de 64 × 16, la grenouille traverse la planche de gauche à
/// droite) sur un NÉNUPHAR qui lui est réservé. Une par plateau au plus, sur une case d'eau où tout son parcours (tête à
/// gauche du nénuphar, plongeon à droite) tombe sur de l'eau dégagée sans toucher un autre prop.
/// <para>Cycle : rien → la tête sort de l'eau (quelques secondes) → saut sur le nénuphar → assise (quelques secondes) →
/// saut à droite → plongeon → rien (un moment) → et ça recommence. Un rond dans l'eau quand elle sort et quand elle
/// plonge. Assise, elle suit le nénuphar quand une vague le pousse.</para>
/// </summary>
internal sealed partial class WaterFoam
{
    private const int FrogFrameW = 64, FrogFrameH = 16;
    private static readonly Point FrogOrigin = new(-21, -7);            // coin de l'image / coin du nénuphar
    private static readonly Rectangle FrogArea = new(-15, -7, 44, 20);  // eau dégagée nécessaire (relatif au nénuphar)
    private static readonly Point FrogHeadAt = new(-10, 6), FrogDiveAt = new(22, 7);   // ronds dans l'eau (idem)

    private enum FrogStep { Hidden, Head, Jump, Sit, Leap, Dive }

    private Texture2D? _frogSheet;
    private int _nenupharKind = -1;
    private bool _hasFrog;
    private int _frogCell;
    private Prop _frogPad;
    private FrogStep _frogStep;
    private float _frogTimer;

    public void LoadFrog(string path) => _frogSheet = Textures.LoadPngOrNull(_gd, path);

    private void UnloadFrog()
    {
        _frogSheet?.Dispose();
        _frogSheet = null;
        _hasFrog = false;
    }

    private readonly System.Collections.Generic.List<Point> _frogSpots = new();   // placement : positions valides (tampon)
    private readonly int[] _badSum = new int[(Face + 1) * (Face + 1)];            // placement : sommes cumulées (tampon)

    /// <summary>
    /// Fin du plateau : la grenouille est PRIORITAIRE dès qu'il y a de l'eau. Cases d'eau dans un ordre au hasard ; pour
    /// chacune, TOUTES les positions du nénuphar sont testées (sommes cumulées des pixels « pas assez dégagés » : un
    /// test par position en temps constant), on en tire une parmi les valides. Les props flottants qui gênent à cet
    /// endroit sont retirés (cf. <see cref="Prop.Kind"/> = −1).
    /// </summary>
    private void PlaceFrog()
    {
        _hasFrog = false;
        if (_frogSheet == null || _nenupharKind < 0 || _cells.Count == 0)
            return;
        var pad = _propSrc[_nenupharKind];
        // Le parcours entier reste dans la tuile, à 1 pixel du bord.
        int minX = 1 - FrogArea.X, maxX = Face - 1 - FrogArea.Right;
        int minY = 1 - FrogArea.Y, maxY = Face - 1 - FrogArea.Bottom;
        if (maxX < minX || maxY < minY)
            return;
        var start = _rng.Next(_cells.Count);
        // Marge de 2 pixels avec l'eau d'abord ; si aucune case ne convient (rivière étroite…), on retente à 1.
        for (var need = PropClearance; need >= 1; need--)
        for (var n = 0; n < _cells.Count; n++)
        {
            var index = (start + n) % _cells.Count;
            if (_cells[index].Foam is not { Clear: { } clear } foam)
                continue;
            // Sommes cumulées des pixels trop près d'un rocher / d'une berge : zone libre ⇔ somme nulle.
            const int stride = Face + 1;
            for (var y = 0; y < Face; y++)
                for (var x = 0; x < Face; x++)
                    _badSum[(y + 1) * stride + x + 1] = (clear[y * Face + x] < need ? 1 : 0)
                        + _badSum[y * stride + x + 1] + _badSum[(y + 1) * stride + x] - _badSum[y * stride + x];
            _frogSpots.Clear();
            for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    int x0 = x + FrogArea.X, y0 = y + FrogArea.Y, x1 = x0 + FrogArea.Width, y1 = y0 + FrogArea.Height;
                    if (_badSum[y1 * stride + x1] - _badSum[y0 * stride + x1] - _badSum[y1 * stride + x0] + _badSum[y0 * stride + x0] == 0)
                        _frogSpots.Add(new Point(x, y));
                }
            if (_frogSpots.Count == 0)
                continue;
            var at = _frogSpots[_rng.Next(_frogSpots.Count)];
            ClearPropsAround(index, new Rectangle(at.X + FrogArea.X, at.Y + FrogArea.Y, FrogArea.Width, FrogArea.Height));
            _frogPad = MakeProp(foam, _nenupharKind, new Rectangle(at.X, at.Y, pad.Width, pad.Height));
            _frogCell = index;
            _hasFrog = true;
            SetFrogStep(FrogStep.Hidden, Rand(1f, 4f));
            return;
        }
    }

    /// <summary>Retire (Kind = −1 : plus dessinés) les props flottants de la case qui touchent la zone de la grenouille.</summary>
    private void ClearPropsAround(int index, Rectangle area)
    {
        var entry = _cells[index];
        var inflated = new Rectangle(area.X - 1, area.Y - 1, area.Width + 2, area.Height + 2);
        for (var i = entry.PropStart; i < entry.PropStart + entry.PropCount; i++)
        {
            var p = _props[i];
            if (p.Kind < 0)
                continue;
            var s = _propSrc[p.Kind];
            if (!inflated.Intersects(new Rectangle(p.At.X, p.At.Y, s.Width, s.Height)))
                continue;
            p.Kind = -1;
            _props[i] = p;
        }
    }

    // Durées des mouvements (s) : assez longues pour que le dessin glisse pixel par pixel (cf. DrawFrog).
    private const float FrogEmerge = 0.35f;   // la tête monte hors de l'eau (début de Head)
    private const float FrogJumpTime = 0.45f, FrogLeapTime = 0.4f, FrogDiveTime = 0.4f;

    private void UpdateFrog(float dt)
    {
        if (_frogPet >= 0f && (_frogPet += dt) >= FrogPetTime)
            _frogPet = -1f;
        if (!_hasFrog || (_frogTimer -= dt) > 0f)
            return;
        switch (_frogStep)
        {
            case FrogStep.Hidden:
                SetFrogStep(FrogStep.Head, Rand(2.5f, 4f));   // la tête hors de l'eau, elle regarde
                FrogRipple(FrogHeadAt);
                break;
            case FrogStep.Head:
                SetFrogStep(FrogStep.Jump, FrogJumpTime);
                break;
            case FrogStep.Jump:
                SetFrogStep(FrogStep.Sit, Rand(6f, 10f));     // assise sur le nénuphar (un bon moment)
                break;
            case FrogStep.Sit:
                SetFrogStep(FrogStep.Leap, FrogLeapTime);
                break;
            case FrogStep.Leap:
                SetFrogStep(FrogStep.Dive, FrogDiveTime);
                FrogRipple(FrogDiveAt);                       // elle touche l'eau
                break;
            default:
                SetFrogStep(FrogStep.Hidden, Rand(3f, 7f));   // sous l'eau un moment, puis recommence
                break;
        }
    }

    private float _frogStepTime;   // durée totale de l'étape en cours (avancée = 1 − timer / durée)

    private void SetFrogStep(FrogStep step, float time)
    {
        _frogStep = step;
        _frogTimer = _frogStepTime = time;
    }

    /// <summary>Avancée (0 → 1) de l'étape en cours.</summary>
    private float FrogProgress => _frogStepTime <= 0f ? 1f : Math.Clamp(1f - _frogTimer / _frogStepTime, 0f, 1f);

    /// <summary>Rond dans l'eau (le même que les poissons) au point <paramref name="offset"/> du nénuphar.</summary>
    private void FrogRipple(Point offset)
    {
        if (_fishCount >= MaxFish)
            return;
        _fish[_fishCount++] = new Fish
        {
            CellIndex = _frogCell,
            At = new Point(_frogPad.At.X + offset.X, _frogPad.At.Y + offset.Y),
            Age = 0f,
        };
    }

    /// <summary>Nénuphar de la grenouille (ombre + sprite, poussé par les vagues comme les autres) puis la grenouille.</summary>
    private void DrawFrog(SpriteBatch sb, Rectangle dest, int px, in Wave current, in Wave previous, float alpha)
    {
        var p = _frogPad;
        var step = Bob(current, p.Delay);
        if (step == 0)
            step = Bob(previous, p.Delay);
        var at = new Point(p.At.X + p.Push.X * step, p.At.Y + p.Push.Y * step);
        var shadow = _shadowSrc[p.Kind];
        var sprite = _propSrc[p.Kind];
        sb.Draw(_propAtlas!, new Rectangle(dest.X + at.X * px, dest.Y + (at.Y + 1) * px, shadow.Width * px, shadow.Height * px),
            shadow, ShadowColor * alpha);
        if (step != 0 && _cells[_frogCell].Foam?.Clear is { } clear)   // poussé par une vague : son sillage, comme les autres
            DrawWake(sb, clear, at, p.Kind, new Point(p.Push.X * step, p.Push.Y * step), dest, px, alpha);
        sb.Draw(_propAtlas!, new Rectangle(dest.X + at.X * px, dest.Y + at.Y * px, sprite.Width * px, sprite.Height * px),
            sprite, Color.White * alpha);

        var frame = _frogStep switch
        {
            FrogStep.Head => 1, FrogStep.Jump => 2, FrogStep.Sit => 3, FrogStep.Leap => 4, FrogStep.Dive => 5, _ => -1,
        };
        if (frame < 0)
            return;
        var anchor = _frogStep == FrogStep.Sit ? at : p.At;   // assise : elle suit le nénuphar poussé par la vague
        var k = FrogProgress;
        var offset = Point.Zero;   // décalage (pixels d'art) de l'image par rapport à sa place dans la planche
        var sink = 0;              // rangées du bas cachées « sous l'eau » (l'image descend d'autant)
        switch (_frogStep)
        {
            case FrogStep.Head:
                // La tête MONTE hors de l'eau (4 pixels) au lieu d'apparaître d'un coup.
                sink = (int)MathF.Round(4f * (1f - Math.Min(1f, (_frogStepTime - _frogTimer) / FrogEmerge)));
                break;
            case FrogStep.Jump:
                offset = Arc(JumpFrom, JumpVia, JumpTo, k);   // de la tête jusqu'au nénuphar, en arc
                break;
            case FrogStep.Leap:
                offset = Arc(LeapFrom, LeapVia, LeapTo, k);   // du nénuphar jusqu'au point de plongeon
                break;
            case FrogStep.Dive:
                sink = (int)MathF.Round(6f * k);              // elle s'enfonce dans l'eau
                break;
        }
        offset.Y -= FrogPetHop;   // caresse : petit bond sur place
        if (sink >= FrogFrameH)
            return;
        sb.Draw(_frogSheet!,
            new Rectangle(dest.X + (anchor.X + FrogOrigin.X + offset.X) * px, dest.Y + (anchor.Y + FrogOrigin.Y + offset.Y + sink) * px,
                FrogFrameW * px, (FrogFrameH - sink) * px),
            new Rectangle(frame * FrogFrameW, 0, FrogFrameW, FrogFrameH - sink), Color.White * alpha);
    }

    // ── Caresse (clic), comme le renard : petit bond sur place + cœurs (la scène les émet) ──
    private const float FrogPetTime = 0.4f;
    private float _frogPet = -1f;   // âge du petit bond (< 0 : aucun)
    // Silhouettes cliquables (pixels de l'image de 64 × 16) : tête hors de l'eau, et assise sur le nénuphar.
    private static readonly Rectangle FrogHeadBox = new(7, 11, 10, 5), FrogSitBox = new(23, 6, 11, 9);

    /// <summary>Case d'eau (indice) où vit la grenouille, ou −1.</summary>
    public int FrogCellIndex => _hasFrog ? _frogCell : -1;

    /// <summary>
    /// Clic sur la grenouille (visible : tête hors de l'eau ou assise) dont la case d'eau a sa face en
    /// <paramref name="dest"/> ? Si oui : petit bond, elle reste plus longtemps, et <paramref name="hearts"/> = d'où
    /// partent les cœurs (au-dessus de sa tête, écran).
    /// </summary>
    public bool TryPetFrog(Point mouse, Rectangle dest, out Vector2 hearts)
    {
        hearts = default;
        if (!_hasFrog || _frogPet >= 0f || _frogStep is not (FrogStep.Head or FrogStep.Sit))
            return false;
        var px = Math.Max(1, dest.Width / Face);
        var box = _frogStep == FrogStep.Sit ? FrogSitBox : FrogHeadBox;
        var ox = dest.X + (_frogPad.At.X + FrogOrigin.X) * px;
        var oy = dest.Y + (_frogPad.At.Y + FrogOrigin.Y) * px;
        var hit = new Rectangle(ox + (box.X - 2) * px, oy + (box.Y - 2) * px, (box.Width + 4) * px, (box.Height + 4) * px);
        if (!hit.Contains(mouse))
            return false;
        _frogPet = 0f;
        _frogTimer = MathF.Max(_frogTimer, _frogStep == FrogStep.Sit ? 4f : 2.5f);   // contente : elle reste encore un peu
        hearts = new Vector2(ox + (box.X + box.Width / 2f) * px, oy + box.Y * px);
        return true;
    }

    /// <summary>Hauteur (pixels d'art) du petit bond de la caresse.</summary>
    private int FrogPetHop => _frogPet < 0f ? 0 : (int)MathF.Round(3f * MathF.Sin(MathF.PI * (_frogPet / FrogPetTime)));

    // Sauts : l'image de saut (posée au milieu de son vol dans la planche) suit une courbe de Bézier qui part de la
    // pose précédente, PASSE par sa place dans la planche à mi-course et arrive sur la pose suivante. Points = écart
    // entre le centre de la pose voisine et celui de l'image de saut (mesurés sur la planche).
    //   tête (11.5, 13) → saut (19, 7) → assise (28, 10) ; assise (28, 10) → saut (37, 7) → plongeon (42.5, 11).
    // Point de contrôle choisi pour que B(0.5) = (0, 0) : P1 = −(P0 + P2) / 2.
    private static readonly Vector2 JumpFrom = new(-7.5f, 6f), JumpTo = new(9f, 3f), JumpVia = -(JumpFrom + JumpTo) / 2f;
    private static readonly Vector2 LeapFrom = new(-9f, 3f), LeapTo = new(5.5f, 4f), LeapVia = -(LeapFrom + LeapTo) / 2f;

    /// <summary>Point de la courbe (Bézier quadratique) à l'avancée <paramref name="t"/>, arrondi au pixel d'art.</summary>
    private static Point Arc(Vector2 from, Vector2 via, Vector2 to, float t)
    {
        var u = 1f - t;
        var p = u * u * from + 2f * u * t * via + t * t * to;
        return new Point((int)MathF.Round(p.X), (int)MathF.Round(p.Y));
    }
}
