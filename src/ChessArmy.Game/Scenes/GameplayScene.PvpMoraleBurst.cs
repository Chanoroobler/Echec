using System;
using ChessArmy.Core.Battle;
using ChessArmy.Engine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Victoire AU MORAL (barre totalement d'un côté) : la barre EXPLOSE en éclats de pixels (carrés unis de ses
/// couleurs), qui retombent un instant, puis filent se ranger sur le cadre de la fenêtre de fin — elle apparaît
/// quand ils y sont tous, avec un flash de son contour. Les éclats gardent la couleur du camp qui a gagné le moral
/// (victoire bleue, défaite rouge). La fin de partie (enregistrement, écran) attend la fin de l'explosion ;
/// l'animation de la fenêtre attend la fin de la reformation. Tampon fixe, aucune allocation par frame.
/// </summary>
public sealed partial class GameplayScene
{
    private const int BurstMax = 200;
    private const int BurstPiece = 4;             // côté d'un éclat (px canvas = 2 pixels d'art en ×2)
    private const float BurstTime = 0.55f;        // explosion
    private const float ReformTime = 0.55f;       // vol vers le cadre de la fenêtre
    private const float ReformStagger = 0.25f;    // les éclats ne partent pas tous ensemble
    private const float ReformFlash = 0.3f;       // contour de la fenêtre qui s'allume puis s'éteint
    private const float BurstGravity = 420f;      // px/s²
    private const float BurstDrag = 2.5f;         // freinage (1/s)

    private struct BurstFrag
    {
        public Vector2 Pos, Vel, Start, Target;
        public float Delay;
        public Color Color;
    }

    private readonly BurstFrag[] _burst = new BurstFrag[BurstMax];
    private int _burstCount;
    private float _burstT = -1f;                  // -1 : pas d'explosion
    private bool _burstTargets;                   // cibles de reformation posées (la fenêtre de fin existe)
    private Rectangle _burstPanel;

    private float ReformEnd => BurstTime + ReformTime + ReformStagger;

    private const float MoraleFullHold = 0.45f;   // barre pleine visible avant qu'elle explose
    private float _moraleFullHold;                // temps passé barre pleine, tout posé

    /// <summary>Victoire au moral décidée par le moteur mais pas encore montrée (barre qui n'a pas encore explosé) :
    /// la fin de partie attend (cf. MoraleFxBusy). Sans barre à l'écran, rien à attendre.</summary>
    private bool MoraleWinPending => _match.IsOver && _match.EndReason == MatchEndReason.Morale && _burstT < 0f
                                     && _moraleBarRect.Width > 0;

    /// <summary>
    /// Avance le compteur « barre pleine » : il ne court que quand le dernier coup a fini de se jouer, que toutes
    /// les gemmes (pions tombés, commandant isolé) ont touché la barre et que celle-ci a fini de glisser jusqu'au
    /// bout. Sinon il repart de zéro.
    /// </summary>
    private void UpdateMoraleFullHold(float dt)
    {
        var settled = MoraleWinPending && _moraleFlightCount == 0 && _moraleQueued == 0
                      && _lonePending[0] + _lonePending[1] == 0 && FxSettled && _pendingCollapses.Count == 0;
        if (!settled)
        {
            _moraleFullHold = 0f;
            return;
        }
        _moraleLanded = _match.Morale;   // tout est posé : la barre rejoint exactement la balance du moteur
        if (MathF.Abs(_pvpMoraleShown - _moraleLanded) > 0.01f)
        {
            _moraleFullHold = 0f;   // la barre glisse encore vers le bout
            return;
        }
        _moraleFullHold += dt;
    }

    /// <summary>Explosion en cours : la fin de partie attend (cf. MoraleFxBusy → BattleSettled).</summary>
    private bool MoraleBurstActive => _burstT >= 0f && _burstT < BurstTime;

    /// <summary>La fenêtre de fin est cachée tant que les éclats ne l'ont pas reformée.</summary>
    private bool MoraleReformHidesPanel => _burstT >= 0f && _burstT < ReformEnd;

    /// <summary>Barre pleine (victoire au moral) et toutes les gemmes posées : la barre explose.</summary>
    private void TryStartMoraleBurst()
    {
        if (_burstT >= 0f || !_match.IsOver || _match.EndReason != MatchEndReason.Morale || _moraleBarRect.Width == 0
            || _moraleFlightCount > 0 || _moraleQueued > 0 || _moraleFullHold < MoraleFullHold)
            return;   // le coup, les gemmes, la barre qui se remplit puis reste pleine un instant : tout se voit avant
        var bar = _moraleBarRect;
        var playerWon = _match.Winner == Faction.Player;
        Color body = playerWon ? Palette.Cyan1 : Palette.Purple5;
        Color light = playerWon ? Palette.White : Palette.Brown5;
        var columns = Math.Max(1, bar.Width / BurstPiece);
        var rows = Math.Max(1, Math.Min(bar.Height / BurstPiece, BurstMax / columns));
        var stepY = bar.Height / (float)rows;
        var rng = new Random(_match.Morale * 7919 + bar.Height);   // une fois par partie
        _burstCount = 0;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns && _burstCount < BurstMax; c++)
            {
                var pos = new Vector2(bar.X + c * BurstPiece + BurstPiece / 2f, bar.Y + r * stepY + BurstPiece / 2f);
                var angle = (float)(rng.NextDouble() * Math.PI * 2);
                var speed = 80f + (float)rng.NextDouble() * 220f;
                _burst[_burstCount++] = new BurstFrag
                {
                    Pos = pos,
                    Vel = new Vector2(MathF.Cos(angle), MathF.Sin(angle) - 0.6f) * speed,   // vers le haut surtout
                    Delay = (float)rng.NextDouble() * ReformStagger,
                    Color = rng.Next(4) switch { 0 => light, 1 => Palette.Black1, _ => body },
                };
            }
        _burstT = 0f;
        _burstTargets = false;
        Context.Sounds.Play("morale_burst");
    }

    private void UpdateMoraleBurst(float dt)
    {
        if (_burstT < 0f)
            return;
        // La reformation vise la fenêtre de fin : tant qu'elle n'existe pas, les éclats flottent (temps gelé).
        if (_burstT >= BurstTime && !_burstTargets)
        {
            if (_pvpEnd is not { } e)
            {
                StepBurstPhysics(dt);
                return;
            }
            _burstPanel = PvpEndPanelRect(VirtualViewport, e, out _);
            SetReformTargets(_burstPanel);
            _burstTargets = true;
        }
        _burstT += dt;
        if (_burstT < BurstTime)
            StepBurstPhysics(dt);
    }

    private void StepBurstPhysics(float dt)
    {
        var drag = MathF.Max(0f, 1f - BurstDrag * dt);
        for (var i = 0; i < _burstCount; i++)
        {
            ref var f = ref _burst[i];
            f.Vel.Y += BurstGravity * dt;
            f.Vel *= drag;
            f.Pos += f.Vel * dt;
        }
    }

    /// <summary>Chaque éclat reçoit sa place sur le contour de la fenêtre, répartis régulièrement.</summary>
    private void SetReformTargets(Rectangle panel)
    {
        var frame = new Rectangle(panel.X - 2, panel.Y - 2, panel.Width + 4, panel.Height + 4);
        var perimeter = 2f * (frame.Width + frame.Height);
        for (var i = 0; i < _burstCount; i++)
        {
            ref var f = ref _burst[i];
            f.Start = f.Pos;
            var d = perimeter * i / _burstCount;
            f.Target = d < frame.Width ? new Vector2(frame.X + d, frame.Y)
                : (d -= frame.Width) < frame.Height ? new Vector2(frame.Right, frame.Y + d)
                : (d -= frame.Height) < frame.Width ? new Vector2(frame.Right - d, frame.Bottom)
                : new Vector2(frame.X, frame.Bottom - (d - frame.Width));
        }
    }

    /// <summary>Éclats (explosion puis reformation) et flash du contour de la fenêtre, par-dessus tout le HUD.</summary>
    private void DrawMoraleBurst(SpriteBatch sb)
    {
        if (_burstT < 0f)
            return;
        if (_burstT < ReformEnd)
        {
            for (var i = 0; i < _burstCount; i++)
            {
                ref var f = ref _burst[i];
                var pos = f.Pos;
                if (_burstTargets && _burstT >= BurstTime)
                {
                    var p = Math.Clamp((_burstT - BurstTime - f.Delay) / ReformTime, 0f, 1f);
                    var e = p * p * (3f - 2f * p);   // part doucement, arrive doucement
                    pos = Vector2.Lerp(f.Start, f.Target, e);
                }
                DrawRect(sb, new Rectangle((int)pos.X - BurstPiece / 2, (int)pos.Y - BurstPiece / 2, BurstPiece, BurstPiece),
                    f.Color);
            }
            return;
        }
        // Fenêtre reformée : son contour s'allume (couleur du camp vainqueur du moral) puis s'éteint.
        var flash = 1f - (_burstT - ReformEnd) / ReformFlash;
        if (flash <= 0f)
            return;
        var color = (_match.Winner == Faction.Player ? Palette.Cyan1 : Palette.Purple5) * flash;
        var r = new Rectangle(_burstPanel.X - 4, _burstPanel.Y - 4, _burstPanel.Width + 8, _burstPanel.Height + 8);
        DrawRect(sb, new Rectangle(r.X, r.Y, r.Width, BurstPiece), color);
        DrawRect(sb, new Rectangle(r.X, r.Bottom - BurstPiece, r.Width, BurstPiece), color);
        DrawRect(sb, new Rectangle(r.X, r.Y, BurstPiece, r.Height), color);
        DrawRect(sb, new Rectangle(r.Right - BurstPiece, r.Y, BurstPiece, r.Height), color);
    }
}
