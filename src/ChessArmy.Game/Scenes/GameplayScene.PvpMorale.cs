using System;
using ChessArmy.Core.Battle;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Combat multi : barre VERTICALE du moral (cf. <see cref="Match.Morale"/>) au milieu de la colonne de gauche,
/// entre le panneau de l'adversaire (en haut, comme ses pièces) et le nôtre (en bas). Elle part du centre et se
/// remplit vers le camp vers qui la balance penche : vers le bas (couleur joueur) quand on mène, vers le haut
/// (couleur ennemie) sinon. Remplie jusqu'au bout = victoire. Aplats unis.
///
/// Chaque pion tombé envoie une GEMME pixel art PAR POINT de moral, de sa case jusqu'à la barre (couleur du camp qui le gagne) ; à
/// l'arrivée la barre tressaute, un son, et l'écart « +3 » / « -3 » monte à côté d'elle (+ = vers nous). La barre
/// ne bouge qu'à l'arrivée de l'éclat. Le combat reste « non posé » pendant les vols (cf. BattleSettled) : la
/// pendule attend, et la fin de partie s'affiche après le dernier impact. Tampons fixes, aucune allocation par frame.
/// </summary>
public sealed partial class GameplayScene
{
    private const int MoraleBarW = 20;            // px canvas (creux de 16)
    private const int MoraleBarTop = 24;          // sous le libellé (le haut de la colonne est libre)
    private const float MoraleBarSpeed = 20f;     // points de moral par seconde (la barre glisse vers sa valeur)

    private const int MoraleQueueMax = 16;        // morts en attente de lancer
    private const int MoraleFlightMax = 96;       // gemmes en vol (une par point de moral)
    private const float MoraleFlightTime = 0.6f;  // durée d'un vol
    private const float MoraleGemGap = 0.08f;     // décalage entre deux gemmes (un pion à 5 points = 5 gemmes)
    private const int MoraleFlightRise = 40;      // hauteur de l'arc (px canvas), +14 selon la gemme
    private const int MoraleGemScale = 2;         // gemme 7×7 en ×2 (échelle entière, pixel-perfect)
    private const float MoraleShakeTime = 0.3f;
    private const int MoraleShakeAmp = 3;
    private const int MoraleDeltaMax = 8;
    private const float MoraleDeltaTime = 1.1f;   // écart chiffré : montée + fondu
    private const int MoraleDeltaRise = 22;

    private float _pvpMoraleShown;
    private int _moraleLanded;                    // balance « arrivée » à la barre (somme des éclats posés)
    private int _moraleDeathsSeen;                // morts du journal déjà traitées
    private int _moraleTextValue;
    private string _moraleText = "";
    private Rectangle _moraleBarRect;             // dernière position dessinée (cible des éclats)
    private float _moraleShake;

    /// <summary>Une gemme = UN point de moral.</summary>
    private struct MoraleFlight
    {
        public Vector2 From;
        public float T;       // < 0 : en attente de départ (décalage)
        public int Sign;      // +1 vers nous, -1 vers l'adversaire
        public int Rise;      // hauteur de son arc (les gemmes d'un même pion ne se superposent pas)
        public int Total;     // dernière gemme d'un pion : écart total à afficher (0 sinon)
    }

    private struct MoraleDelta
    {
        public float T;
        public int Y;
        public string Text;
        public bool Positive;
        public bool Active;
    }

    private readonly MoraleFlight[] _moraleFlights = new MoraleFlight[MoraleFlightMax];
    private readonly MoraleDelta[] _moraleDeltas = new MoraleDelta[MoraleDeltaMax];
    private int _moraleFlightCount;
    private readonly int[] _moraleQueue = new int[MoraleQueueMax];   // indices du journal des morts, pas encore lancées
    private int _moraleQueued;                    // morts relevées pas encore lancées (attendent la fin du coup)

    /// <summary>Vols d'éclats en cours ou en attente : le combat n'est pas « posé » (cf. BattleSettled).</summary>
    private bool MoraleFxBusy => _moraleFlightCount > 0 || _moraleQueued > 0 || _lonePending[0] + _lonePending[1] > 0
                                 || LoneTicksUnseen || MoraleWinPending || MoraleBurstActive;

    /// <summary>Tour fini en commandant isolé que la barre n'a pas encore relevé (il vient d'arriver dans la frame).</summary>
    private bool LoneTicksUnseen => _match.MoraleToWin > 0
        && (_loneSeen[0] != _match.LoneTurns(Faction.Player) || _loneSeen[1] != _match.LoneTurns(Faction.Enemy));

    // Commandant isolé (cf. Match.LoneTurns) : tours déjà relevés, et pertes pas encore lancées vers la barre.
    private readonly int[] _loneSeen = new int[2];
    private readonly int[] _lonePending = new int[2];

    private void UpdatePvpMorale(float dt)
    {
        if (_match.MoraleToWin <= 0)
            return;

        // 1. Morts nouvelles (commandant exclu, comme le moteur) : en file jusqu'à la fin de l'animation du coup.
        var log = _match.DeathLog;
        for (; _moraleDeathsSeen < log.Count; _moraleDeathsSeen++)
        {
            if (log[_moraleDeathsSeen].Unit.IsEssential)
                continue;
            if (_moraleQueued < MoraleQueueMax)
                _moraleQueue[_moraleQueued++] = _moraleDeathsSeen;
            else
                LandMorale(MoraleDeltaOf(log[_moraleDeathsSeen].Unit));   // file pleine (coup énorme) : posé direct
        }
        // Tours finis en commandant isolé : relevés AVANT le filet du bas (sinon la barre sauterait sans gemme).
        for (var s = 0; s < 2; s++)
            for (var lone = _match.LoneTurns((Faction)s); _loneSeen[s] < lone; _loneSeen[s]++)
                _lonePending[s]++;

        // 2. Coup fini de jouer (attaque, dissolution, chute de tuile) : UNE gemme par point de moral, en rafale.
        if (_moraleQueued > 0 && FxSettled && _pendingCollapses.Count == 0)
        {
            var layout = BuildLayout();
            var gem = 0;
            for (var i = 0; i < _moraleQueued; i++)
            {
                var (cell, unit) = log[_moraleQueue[i]];
                var delta = MoraleDeltaOf(unit);
                var points = Math.Abs(delta);
                var from = layout.CellToScreen(cell.Column, cell.Row)
                           + new Vector2(layout.TileSize / 2f, layout.RowPitch / 2f);
                if (_moraleBarRect.Width == 0 || _moraleFlightCount + points > MoraleFlightMax)
                {
                    LandMorale(delta);   // pas de barre à l'écran (ou trop de gemmes) : on pose directement
                    continue;
                }
                for (var j = 0; j < points; j++, gem++)
                    _moraleFlights[_moraleFlightCount++] = new MoraleFlight
                    {
                        From = from + new Vector2(((j % 3) - 1) * 8, 0f),   // trois départs côte à côte
                        T = -gem * MoraleGemGap,
                        Sign = Math.Sign(delta),
                        Rise = MoraleFlightRise + (j % 3) * 14,
                        Total = j == points - 1 ? delta : 0,
                    };
            }
            _moraleQueued = 0;
        }
        if (_lonePending[0] + _lonePending[1] > 0 && FxSettled && _pendingCollapses.Count == 0)
            LaunchLoneMorale();

        // 3. Vols : chaque gemme arrivée = un point posé (la barre monte point par point) ; la dernière d'un
        // pion affiche son écart total.
        for (var i = 0; i < _moraleFlightCount; i++)
        {
            ref var f = ref _moraleFlights[i];
            f.T += dt;
            if (f.T < MoraleFlightTime)
                continue;
            LandMoralePoint(f.Sign);
            if (f.Total != 0)
                SpawnMoraleDelta(f.Total);
            _moraleFlights[i] = _moraleFlights[--_moraleFlightCount];   // retrait par échange (ordre sans importance)
            i--;
        }

        // 4. Barre pleine : elle explose, puis ses éclats reforment la fenêtre de fin (GameplayScene.PvpMoraleBurst.cs).
        UpdateMoraleFullHold(dt);
        TryStartMoraleBurst();
        UpdateMoraleBurst(dt);

        // Filet : plus rien en vol ni en attente, la barre rejoint la balance du moteur (aucun écart possible).
        if (!MoraleFxBusy)
            _moraleLanded = _match.Morale;

        var target = (float)_moraleLanded;
        var step = MoraleBarSpeed * dt;
        _pvpMoraleShown = MathF.Abs(target - _pvpMoraleShown) <= step
            ? target
            : _pvpMoraleShown + MathF.Sign(target - _pvpMoraleShown) * step;
        if (_moraleShake > 0f)
            _moraleShake -= dt;
        for (var i = 0; i < MoraleDeltaMax; i++)
            if (_moraleDeltas[i].Active && (_moraleDeltas[i].T += dt) >= MoraleDeltaTime)
                _moraleDeltas[i].Active = false;
    }

    /// <summary>
    /// Commandant isolé : chaque tour qu'il a fini seul envoie ses gemmes de SON commandant vers la barre (couleur du
    /// camp qui les gagne), avec l'écart « -1 » / « +1 » à l'arrivée : la perte se voit partir de lui.
    /// </summary>
    private void LaunchLoneMorale()
    {
        var layout = BuildLayout();
        var step = ChessArmy.Core.Pvp.PvpConfig.Current.LoneCommanderMorale;
        for (var s = 0; s < 2; s++)
        {
            var count = _lonePending[s];
            if (count == 0)
                continue;
            _lonePending[s] = 0;
            var side = (Faction)s;
            var delta = (side == Faction.Enemy ? step : -step) * count;
            Vector2? from = null;
            foreach (var (cell, unit) in _match.Units())
                if (unit.Faction == side && unit.IsEssential)
                {
                    from = layout.CellToScreen(cell.Column, cell.Row) + new Vector2(layout.TileSize / 2f, layout.RowPitch / 2f);
                    break;
                }
            var points = Math.Abs(delta);
            if (from is not { } start || _moraleBarRect.Width == 0 || _moraleFlightCount + points > MoraleFlightMax)
            {
                LandMorale(delta);
                continue;
            }
            for (var j = 0; j < points; j++)
                _moraleFlights[_moraleFlightCount++] = new MoraleFlight
                {
                    From = start + new Vector2(((j % 3) - 1) * 8, 0f),
                    T = -j * MoraleGemGap,
                    Sign = Math.Sign(delta),
                    Rise = MoraleFlightRise + (j % 3) * 14,
                    Total = j == points - 1 ? delta : 0,
                };
        }
    }

    /// <summary>Écart de moral d'un pion tombé, vu du joueur local (= règle du moteur, cf. Match.OnUnitDied).</summary>
    private static int MoraleDeltaOf(Unit dead)
    {
        var price = ChessArmy.Core.Pvp.PvpConfig.Current.CostOf(dead.Class);
        return dead.Faction == Faction.Enemy ? price : -price;
    }

    /// <summary>Pose d'un coup tout un écart (pas de gemmes : barre absente ou trop de gemmes en vol).</summary>
    private void LandMorale(int delta)
    {
        _moraleLanded += delta;
        _moraleShake = MoraleShakeTime;
        Context.Sounds.Play("morale_hit");
        SpawnMoraleDelta(delta);
    }

    /// <summary>Une gemme touche la barre : un point posé, petit tressaut, son.</summary>
    private void LandMoralePoint(int sign)
    {
        _moraleLanded += sign;
        _moraleShake = MoraleShakeTime;
        Context.Sounds.Play("morale_hit");
    }

    /// <summary>Écart « +3 » / « -3 » qui monte à côté de la barre (chaîne faite une fois, à l'impact).</summary>
    private void SpawnMoraleDelta(int delta)
    {
        for (var i = 0; i < MoraleDeltaMax; i++)
        {
            if (_moraleDeltas[i].Active)
                continue;
            _moraleDeltas[i] = new MoraleDelta
            {
                T = 0f, Text = (delta > 0 ? "+" : "-") + Math.Abs(delta), Positive = delta > 0, Active = true,
                // Départ au bout de la partie remplie, du côté où la barre penche après l'impact.
                Y = MoraleFillEnd(_moraleLanded),
            };
            return;
        }
    }

    /// <summary>Ordonnée (canvas) du bout de la partie remplie pour une balance donnée.</summary>
    private int MoraleFillEnd(float value)
    {
        var max = Math.Max(1, _match.MoraleToWin);
        var innerH = _moraleBarRect.Height - 4;
        var mid = _moraleBarRect.Y + 2 + innerH / 2;
        var fill = (int)MathF.Round(Math.Clamp(value, -max, max) / max * (innerH / 2));
        return mid + fill;
    }

    private void DrawPvpMorale(SpriteBatch sb, Rectangle column)
    {
        var max = _match.MoraleToWin;
        if (max <= 0)
            return;
        var font = Context.Font;
        // Hauteur : de sous la zone du haut jusqu'au-dessus de notre panneau dans sa taille la plus haute (bond compris).
        var bottom = column.Bottom - (HeroPanelH + 2 * CollapseTagH) - 12 - (int)TurnPopAmp;   // 2 lignes d'alerte possibles
        var top = column.Y + MoraleBarTop;
        if (bottom - top < 60)
        {
            _moraleBarRect = Rectangle.Empty;
            return;   // colonne trop courte : pas de barre plutôt qu'une barre illisible
        }
        var x = column.X + (column.Width - MoraleBarW) / 2;
        _moraleBarRect = new Rectangle(x, top, MoraleBarW, bottom - top);
        if (_burstT >= 0f)
            return;   // la barre a explosé : ses éclats sont dessinés à part (DrawMoraleBurst)
        // Tressaut à l'impact d'un éclat (créneau horizontal, px entiers).
        var shakeDx = _moraleShake > 0f ? ((int)(_moraleShake * 40f) % 2 == 0 ? -MoraleShakeAmp : MoraleShakeAmp) : 0;
        var bar = new Rectangle(x + shakeDx, top, MoraleBarW, bottom - top);

        font.DrawCentered(sb, Loc.T("pvp.morale"), new Rectangle(column.X, top - 14, column.Width, 7), 1, Palette.Blue1);

        // Cadre noir + creux tramé (le style des zones enfoncées de l'UI), puis remplissage depuis le centre.
        DrawRect(sb, bar, Palette.Black1);
        var inner = new Rectangle(bar.X + 2, bar.Y + 2, bar.Width - 4, bar.Height - 4);
        // Creux : trame seule + ombre d'un pixel en haut et à gauche (pas le biseau clair des zones enfoncées).
        Context.Style.FillDither(sb, inner);
        DrawRect(sb, new Rectangle(inner.X, inner.Y, inner.Width, 1), Palette.Black2);
        DrawRect(sb, new Rectangle(inner.X, inner.Y, 1, inner.Height), Palette.Black2);
        var mid = inner.Y + inner.Height / 2;
        var half = inner.Height / 2;
        EnsureMoraleFillTextures(inner.Width, half);
        var shown = Math.Clamp(_pvpMoraleShown, -max, max);
        var fill = (int)MathF.Round(MathF.Abs(shown) / max * half);
        if (fill > 0)
        {
            // Dégradé TRAMÉ fixe sur la demi-barre (sombre au centre, éclatant au bout) : plus la barre se
            // remplit, plus elle s'allume. On découvre la portion remplie, la trame ne « rampe » donc jamais.
            var player = shown > 0;
            var dest = player
                ? new Rectangle(inner.X, mid, inner.Width, fill)          // on mène : vers notre camp (bas)
                : new Rectangle(inner.X, mid - fill, inner.Width, fill);  // l'adversaire mène : vers le sien (haut)
            var src = player ? new Rectangle(0, 0, inner.Width, fill) : new Rectangle(0, half - fill, inner.Width, fill);
            sb.Draw(player ? _moraleFillPlayer! : _moraleFillEnemy!, dest, src, Color.White);

        }
        // Équilibre (0) : trait clair qui déborde de la barre de part et d'autre.
        DrawRect(sb, new Rectangle(bar.X - 3, mid - 1, bar.Width + 6, 2), Palette.Black1);
        DrawRect(sb, new Rectangle(bar.X - 2, mid - 1, bar.Width + 4, 1), Palette.White);

        // Écart chiffré à côté de la barre, du côté de celui qui mène (balance arrivée à la barre).
        var value = _moraleLanded;
        if (value != 0)
        {
            if (value != _moraleTextValue)
            {
                _moraleTextValue = value;   // chaîne refaite seulement quand la balance bouge (pas à chaque frame)
                _moraleText = $"{Math.Abs(value)}/{max}";
            }
            var ty = value > 0 ? mid + 6 : mid - 6 - font.LineHeight(1);
            font.Draw(sb, _moraleText, new Vector2(bar.Right + 6, ty), 1, value > 0 ? Palette.Cyan1 : Palette.Purple5);
        }

        // Écarts « +3 » / « -3 » de chaque impact, à GAUCHE de la barre : ils montent et s'effacent.
        for (var i = 0; i < MoraleDeltaMax; i++)
        {
            ref var d = ref _moraleDeltas[i];
            if (!d.Active)
                continue;
            var p = d.T / MoraleDeltaTime;
            var alpha = p < 0.6f ? 1f : 1f - (p - 0.6f) / 0.4f;
            var y = d.Y - (int)(MoraleDeltaRise * (1f - (1f - p) * (1f - p))) - font.LineHeight(2) / 2;
            var w = font.Measure(d.Text, 2);
            font.Draw(sb, d.Text, new Vector2(_moraleBarRect.X - 8 - w, y), 2,
                (d.Positive ? Palette.Cyan1 : Palette.Purple5) * alpha);
        }
    }

    /// <summary>Éclats en vol, PAR-DESSUS le plateau et le HUD (appelé après le HUD multi).</summary>
    private void DrawPvpMoraleFlights(SpriteBatch sb)
    {
        if (_moraleFlightCount == 0 || _moraleBarRect.Width == 0)
            return;
        _moraleGemPlayer ??= MakeMoraleGem(Palette.White, Palette.Cyan1, Palette.Blue2);
        _moraleGemEnemy ??= MakeMoraleGem(Palette.Brown5, Palette.Purple5, Palette.Purple2);
        var size = MoraleGem.Length * MoraleGemScale;
        for (var i = 0; i < _moraleFlightCount; i++)
        {
            ref var f = ref _moraleFlights[i];
            if (f.T < 0f)
                continue;   // pas encore partie (décalage entre gemmes)
            var pos = MoraleFlightPos(f, f.T);
            sb.Draw(f.Sign > 0 ? _moraleGemPlayer : _moraleGemEnemy,
                new Rectangle((int)pos.X - size / 2, (int)pos.Y - size / 2, size, size), Color.White);
        }
    }

    // Gemme de moral, 7×7 : # contour, h reflet, b corps, s ombre (aplats de la palette, aucun dégradé).
    private static readonly string[] MoraleGem =
    {
        "...#...",
        "..#h#..",
        ".#hbb#.",
        "#hbbbs#",
        ".#bss#.",
        "..#s#..",
        "...#...",
    };

    private Texture2D? _moraleGemPlayer, _moraleGemEnemy;

    private Texture2D MakeMoraleGem(Color light, Color body, Color shade)
    {
        var n = MoraleGem.Length;
        var pixels = new Color[n * n];
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                pixels[y * n + x] = MoraleGem[y][x] switch
                {
                    '#' => Palette.Black1,
                    'h' => light,
                    'b' => body,
                    's' => shade,
                    _ => Color.Transparent,
                };
        var tex = new Texture2D(Context.GraphicsDevice, n, n);
        tex.SetData(pixels);
        return tex;
    }

    // Remplissage de la barre : dégradés tramés (Bayer 4×4) d'une demi-barre, refaits seulement si sa taille change.
    private Texture2D? _moraleFillPlayer, _moraleFillEnemy;
    private Point _moraleFillSize;

    private void EnsureMoraleFillTextures(int width, int half)
    {
        if (_moraleFillPlayer != null && _moraleFillSize == new Point(width, half))
            return;
        DisposeMoraleFill();
        var device = Context.GraphicsDevice;
        var h = Math.Max(1, half);
        // Joueur : la demi-barre du BAS, du centre (haut de la texture) vers le bout (bas).
        _moraleFillPlayer = ChessArmy.Engine.Rendering.Textures.CreateVerticalDitherGradient(device, width, h,
            Palette.Cyan1, Palette.Cyan1, Palette.Cyan1, Palette.White);   // couleur pleine dès le centre
        // Adversaire : la demi-barre du HAUT, du bout (haut) vers le centre (bas).
        _moraleFillEnemy = ChessArmy.Engine.Rendering.Textures.CreateVerticalDitherGradient(device, width, h,
            Palette.Brown5, Palette.Purple5, Palette.Purple5, Palette.Purple5);   // couleur pleine dès le centre
        _moraleFillSize = new Point(width, half);
    }

    private void DisposeMoraleFill()
    {
        _moraleFillPlayer?.Dispose();
        _moraleFillEnemy?.Dispose();
        _moraleFillPlayer = _moraleFillEnemy = null;
    }

    private void UnloadMoraleFx()
    {
        _moraleGemPlayer?.Dispose();
        _moraleGemEnemy?.Dispose();
        _moraleGemPlayer = _moraleGemEnemy = null;
        DisposeMoraleFill();
    }

    /// <summary>Arc de la case du pion jusqu'au centre de la barre, qui accélère en arrivant.</summary>
    private Vector2 MoraleFlightPos(in MoraleFlight f, float t)
    {
        var p = Math.Clamp(t / MoraleFlightTime, 0f, 1f);
        var e = p * p;   // accélère
        var to = new Vector2(_moraleBarRect.Center.X, MoraleFillEnd(_moraleLanded));
        var pos = Vector2.Lerp(f.From, to, e);
        pos.Y -= f.Rise * 4f * p * (1f - p);
        return pos;
    }
}
