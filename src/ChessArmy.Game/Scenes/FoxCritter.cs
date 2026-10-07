using System;
using ChessArmy.Core.Map;
using ChessArmy.Engine.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Petit renard d'ambiance (purement cosmétique) posé sur une case d'herbe. Assis la plupart du temps, il se
/// lève de temps en temps, trottine HORIZONTALEMENT vers la gauche ou la droite sur une distance aléatoire, puis
/// se rassoit. Il ne traverse jamais une case non libre (cf. prédicat <c>free</c> fourni par la scène : herbe,
/// sans objet ni pion). Spritesheet <c>Assets/Anim/fox.png</c> : 6 frames 32×16 tournées vers la DROITE
/// (0 = assis, 1 = debout, 2..5 = marche).
/// </summary>
internal sealed class FoxCritter
{
    private const int FrameW = 32;
    private const int FrameH = 16;
    private const int FrameIdle = 0;
    private const int FrameStand = 1;
    private const int FrameWalk = 2;
    private const int WalkFrames = 4;
    private const float WalkFps = 10f;
    private const float Speed = 0.7f;        // cases / seconde
    private const float HalfBody = 0.25f;    // demi-largeur du renard (16 px d'art sur 64), en cases
    private const float StandTime = 0.35f;   // pose « debout » avant de partir / avant de se rasseoir
    private const float MaxWalk = 3f;        // distance max d'une balade, en cases
    // FUITE (un pion s'est posé sur sa case) : il détale, plus vite et plus loin, sans traîner à se lever.
    private const float FleeSpeed = 2.2f;
    private const float FleeFps = 18f;
    private const float FleeRise = 0.08f;
    private const float FleeExtra = 1.5f;    // au moins ~1,5 case au-delà de la sortie de sa case (si la place le permet)
    private const float MaxFlee = 5f;

    // CARESSE (clic sur le renard) : il se dresse et fait deux petits bonds pendant que des cœurs s'envolent.
    private const float PetTime = 0.7f;
    private const int PetHops = 2;
    private const int PetHopHeight = 3;      // pixels d'art

    private enum State { Idle, Rising, Walking, Settling, Petted }

    private readonly Random _rng = new();
    private Texture2D? _sheet;
    private State _state;
    private float _timer;
    private float _x;            // centre du renard, en colonnes (2.5 = milieu de la colonne 2)
    private float _yInCell;      // position des pattes dans la case (fraction de case, depuis le haut)
    private float _targetX;
    private float _walkAnim;
    private bool _facingLeft;
    private bool _fleeing;       // balade en cours = fuite (vitesse + cadence de marche relevées)

    public bool Active { get; private set; }
    public int Row { get; private set; }
    public Cell Cell => new((int)MathF.Floor(_x), Row);

    private bool _ownsSheet;   // faux = planche PARTAGÉE avec un autre renard (pas libérée ici)

    public void Load(GraphicsDevice gd, string path)
    {
        _sheet = Textures.LoadPngOrNull(gd, path);
        _ownsSheet = true;
    }

    /// <summary>Même planche que <paramref name="other"/> (second renard du même terrain) : pas de double chargement.</summary>
    public void ShareSheet(FoxCritter other)
    {
        _sheet = other._sheet;
        _ownsSheet = false;
    }

    public void Unload()
    {
        if (_ownsSheet)
            _sheet?.Dispose();
        _sheet = null;
        Active = false;
    }

    public void Reset() => Active = false;

    /// <summary>
    /// Pose le renard sur une case libre ENCADRÉE de deux cases libres (gauche ET droite) : il a toujours de
    /// quoi se dégourdir au départ. Aucune case candidate → pas de renard pour ce combat.
    /// </summary>
    /// <param name="spot">Filtre en plus de <paramref name="free"/> pour SA case de départ (ex. pas dans un buisson,
    /// où il serait caché d'entrée).</param>
    public void Place(int columns, int rows, Func<Cell, bool> free, Func<Cell, bool>? spot = null)
    {
        Active = false;
        if (_sheet == null)
            return;
        // Tirage réservoir : une case au hasard parmi les candidates, sans liste temporaire.
        var seen = 0;
        var pick = default(Cell);
        for (var r = 0; r < rows; r++)
            for (var c = 1; c < columns - 1; c++)
            {
                if (!free(new Cell(c, r)) || !free(new Cell(c - 1, r)) || !free(new Cell(c + 1, r))
                    || (spot != null && !spot(new Cell(c, r))))
                    continue;
                if (_rng.Next(++seen) == 0)
                    pick = new Cell(c, r);
            }
        if (seen == 0)
            return;
        Active = true;
        Row = pick.Row;
        _x = pick.Column + 0.3f + (float)_rng.NextDouble() * 0.4f;   // pas forcément centré dans la case
        _yInCell = 0.4f + (float)_rng.NextDouble() * 0.3f;
        _facingLeft = _rng.Next(2) == 0;
        _state = State.Idle;
        _timer = IdleDelay();
    }

    private float IdleDelay() => 3f + (float)_rng.NextDouble() * 6f;

    /// <summary>Avance le renard. Vrai s'il vient de se faire ÉCRASER (pion posé sur lui, aucune issue) : il
    /// disparaît, à la scène de jouer le son et la giclée (cf. <see cref="FootPosition"/>).</summary>
    public bool Update(float dt, int columns, Func<Cell, bool> free)
    {
        if (!Active)
            return false;
        switch (_state)
        {
            case State.Idle:
                _timer -= dt;
                // Un pion vient se poser sur sa case : il se lève tout de suite pour lui laisser la place.
                var crushed = !free(Cell);
                if (_timer <= 0f || crushed)
                {
                    if (PickTarget(columns, free))
                    {
                        _state = State.Rising;
                        _timer = _fleeing ? FleeRise : StandTime;
                    }
                    else if (crushed)
                    {
                        Active = false;         // aucune case de fuite : écrasé
                        return true;
                    }
                    else
                        _timer = IdleDelay();   // coincé : retente plus tard
                }
                break;
            case State.Petted:
                _timer -= dt;
                if (_timer <= 0f)
                {
                    _state = State.Idle;
                    _timer = IdleDelay();   // content : il se rassoit un moment
                }
                break;
            case State.Rising:
                _timer -= dt;
                if (_timer <= 0f)
                {
                    _state = State.Walking;
                    _walkAnim = 0f;
                }
                break;
            case State.Walking:
                _walkAnim += dt;
                var dir = _facingLeft ? -1f : 1f;
                var next = _x + dir * (_fleeing ? FleeSpeed : Speed) * dt;
                // Case où arrive le museau : un pion a pu s'y poser entre-temps → il s'arrête net. SA case à lui
                // n'est pas un obstacle : il doit pouvoir en sortir quand un pion s'y pose (sinon il boucle).
                var nose = new Cell((int)MathF.Floor(next + dir * HalfBody), Row);
                if (nose.Column < 0 || nose.Column >= columns || (nose != Cell && !free(nose)))
                {
                    Settle();
                    break;
                }
                _x = next;
                if ((_facingLeft && _x <= _targetX) || (!_facingLeft && _x >= _targetX))
                {
                    _x = _targetX;
                    Settle();
                }
                break;
            case State.Settling:
                _timer -= dt;
                if (_timer <= 0f)
                {
                    _state = State.Idle;
                    _timer = IdleDelay();
                }
                break;
        }
        return false;
    }

    /// <summary>Souris sur le renard (son cadre 32×16 dans <paramref name="layout"/>, élargi de 2 px).</summary>
    public bool Hit(Point mouse, GridLayout layout)
    {
        if (!TryGetFrame(layout, 0, out _, out var dest, out _, out _, withHop: false))
            return false;
        dest.Inflate(2, 2);
        return dest.Contains(mouse);
    }

    /// <summary>Vrai pendant les bonds de la caresse (le clic ne la relance pas avant la fin).</summary>
    public bool IsPetted => Active && _state == State.Petted;

    /// <summary>Caresse : il s'arrête (même en pleine balade), se dresse et fait deux petits bonds.</summary>
    public void Pet()
    {
        if (!Active)
            return;
        _state = State.Petted;
        _timer = PetTime;
        _fleeing = false;
    }

    /// <summary>Position écran de sa tête (d'où partent les cœurs) dans <paramref name="layout"/>.</summary>
    public Vector2 HeadPosition(GridLayout layout)
    {
        var dx = (_facingLeft ? -0.12f : 0.12f) * layout.TileSize;   // la tête est à l'avant du corps
        return FootPosition(layout) + new Vector2(dx, -layout.TileSize * 0.22f);
    }

    /// <summary>Position écran de ses pattes (centre du corps, au sol) dans <paramref name="layout"/>.</summary>
    public Vector2 FootPosition(GridLayout layout) =>
        layout.CellToScreen(0, Row) + new Vector2(_x * layout.TileSize, _yInCell * layout.TileSize);

    private void Settle()
    {
        _state = State.Settling;
        _timer = StandTime;
    }

    /// <summary>
    /// Choisit une direction et une distance au hasard dans la portion LIBRE de sa rangée (cases contiguës
    /// libres autour de lui). Faux s'il n'a de place d'aucun côté.
    /// </summary>
    private bool PickTarget(int columns, Func<Cell, bool> free)
    {
        var col = (int)MathF.Floor(_x);
        var left = col;
        while (left - 1 >= 0 && free(new Cell(left - 1, Row)))
            left--;
        var right = col;
        while (right + 1 < columns && free(new Cell(right + 1, Row)))
            right++;
        var minX = left + HalfBody + 0.05f;
        var maxX = right + 1f - HalfBody - 0.05f;
        // Un pion s'est posé sur SA case : il doit en sortir ENTIÈREMENT (corps dans la case voisine).
        var flee = !free(Cell);
        var minLeft = flee ? _x - (col - HalfBody - 0.05f) : 0.3f;
        var minRight = flee ? (col + 1f + HalfBody + 0.05f) - _x : 0.3f;
        var maxDist = flee ? MaxFlee : MaxWalk;
        var roomLeft = MathF.Min(_x - minX, MathF.Max(maxDist, minLeft));
        var roomRight = MathF.Min(maxX - _x, MathF.Max(maxDist, minRight));
        var canLeft = roomLeft >= minLeft;
        var canRight = roomRight >= minRight;
        if (!canLeft && !canRight)
            return false;
        // Fuite : il part du côté où il a le plus de place (il s'éloigne vraiment), sinon au hasard.
        _facingLeft = flee
            ? canLeft && (!canRight || roomLeft > roomRight || (roomLeft == roomRight && _rng.Next(2) == 0))
            : canLeft && (!canRight || _rng.Next(2) == 0);
        _fleeing = flee;
        var room = _facingLeft ? roomLeft : roomRight;
        var minStep = _facingLeft ? minLeft : minRight;
        if (flee)
            minStep = MathF.Min(minStep + FleeExtra, room);   // plus loin que juste la case voisine
        var dist = minStep + (float)_rng.NextDouble() * (room - minStep);
        _targetX = _x + (_facingLeft ? -dist : dist);
        return true;
    }

    /// <summary>
    /// Dessine le renard dans le batch OUVERT. <paramref name="offsetY"/>/<paramref name="alpha"/> = émergence
    /// de sa case (assemblage du plateau). Taille = 1 pixel d'art par pixel de tuile native (×TileSize/64),
    /// position calée sur cette grille (pixel-perfect).
    /// </summary>
    public void Draw(SpriteBatch sb, GridLayout layout, int offsetY, float alpha)
    {
        if (!TryGetFrame(layout, offsetY, out var sheet, out var dest, out var src, out var fx))
            return;
        sb.Draw(sheet, dest, src, Color.White * alpha, 0f, Vector2.Zero, fx, 0f);
    }

    /// <summary>
    /// Frame courante : planche, rectangle écran, rectangle source et miroir. Sert au dessin du renard ET à
    /// son ombre projetée (même silhouette que les pions, cf. <c>GameplayScene.DrawCastShadows</c>).
    /// </summary>
    /// <param name="withHop">Faux pour l'ombre : elle reste au sol pendant les bonds du renard caressé.</param>
    public bool TryGetFrame(GridLayout layout, int offsetY, out Texture2D sheet, out Rectangle dest,
        out Rectangle src, out SpriteEffects fx, bool withHop = true)
    {
        sheet = _sheet!;
        dest = src = default;
        fx = SpriteEffects.None;
        if (!Active || _sheet == null)
            return false;
        var tile = layout.TileSize;
        var scale = Math.Max(1, (int)MathF.Round(tile / 64f));
        var top = layout.CellToScreen(0, Row);
        var ox = (int)MathF.Round(top.X);
        var oy = (int)MathF.Round(top.Y) + offsetY;
        var x = ox + (int)MathF.Round((_x * tile - FrameW / 2f * scale) / scale) * scale;
        var y = oy + (int)MathF.Round((_yInCell * tile - FrameH * scale) / scale) * scale;
        if (withHop && _state == State.Petted)
        {
            // Caressé : deux petits bonds joyeux (pixels d'art entiers, pas d'étirement du sprite).
            var t = 1f - _timer / PetTime;                                   // 0 → 1
            var hop = MathF.Abs(MathF.Sin(t * MathF.PI * PetHops));
            y -= (int)MathF.Round(hop * PetHopHeight) * scale;
        }
        var frame = _state switch
        {
            State.Idle => FrameIdle,
            State.Walking => FrameWalk + (int)(_walkAnim * (_fleeing ? FleeFps : WalkFps)) % WalkFrames,
            _ => FrameStand,
        };
        dest = new Rectangle(x, y, FrameW * scale, FrameH * scale);
        src = new Rectangle(frame * FrameW, 0, FrameW, FrameH);
        fx = _facingLeft ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        return true;
    }
}
