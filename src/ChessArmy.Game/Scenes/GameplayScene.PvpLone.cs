using System;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.UI;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Fins de partie à commandants (multi), mises en évidence :
///  • COMMANDANT ISOLÉ (un camp n'a plus de pion) : bandeau au centre une fois, puis une ligne qui reste dans le
///    panneau de ce camp ; chaque tour qu'il finit seul envoie sa gemme « -1 » vers la barre (GameplayScene.PvpMorale.cs) ;
///  • PLUS QUE DES COMMANDANTS : grand bandeau « ÉGALITÉ » tenu quelques secondes avant la fenêtre de fin.
/// Aplats unis, textes faits une fois par langue.
/// </summary>
public sealed partial class GameplayScene
{
    private const float LoneBannerTime = 2.6f;   // bandeau « commandant seul » : apparition, tenue, fondu
    private const float LoneBannerFade = 0.2f;
    private const float DrawHoldTime = 2.5f;     // bandeau d'égalité avant la fenêtre de fin

    // Par camp : 0 rien, 1 à annoncer (dès que le plateau est posé), 2 annoncé (ligne affichée dans son panneau).
    private readonly byte[] _loneState = new byte[2];
    private float _loneBanner = -1f;
    private Faction _loneBannerSide;
    private float _drawHold = -1f;
    private Language _loneTextLang = (Language)(-1);
    private string _loneBannerMe = "", _loneBannerThem = "", _loneTag = "", _drawTitle = "", _drawSub = "";

    private bool LoneTagShown(Faction side) => _loneState[(int)side] == 2;

    private void RefreshLoneTexts()
    {
        if (_loneTextLang == Loc.Current)
            return;
        _loneTextLang = Loc.Current;
        var step = PvpConfig.Current.LoneCommanderMorale;
        _loneBannerMe = Loc.T("pvp.lone.banner_me", step);
        _loneBannerThem = Loc.T("pvp.lone.banner_them", step);
        _loneTag = Loc.T("pvp.lone.tag", step);
        _drawTitle = Loc.T("pvp.draw");
        _drawSub = Loc.T("pvp.end.reason_only_commanders");
    }

    private void UpdatePvpLone(float dt)
    {
        if (_match.MoraleToWin <= 0)
            return;
        if (_drawHold >= 0f)
            _drawHold += dt;
        if (_loneBanner >= 0f && (_loneBanner += dt) >= LoneBannerTime)
            _loneBanner = -1f;
        if (_match.IsOver)
            return;
        for (var s = 0; s < 2; s++)
        {
            var side = (Faction)s;
            if (_loneState[s] == 0 && !_match.HasPawns(side) && _match.HasPawns(side.Opponent()))
                _loneState[s] = 1;
        }
        if (_loneBanner >= 0f || !BattleSettled)
            return;   // le coup qui a tué le dernier pion finit d'abord de se jouer
        for (var s = 0; s < 2; s++)
            if (_loneState[s] == 1)
            {
                _loneState[s] = 2;
                _loneBannerSide = (Faction)s;
                _loneBanner = 0f;
                Context.Sounds.Play(_loneBannerSide == Faction.Player ? "defeat" : "morale_hit");
                return;
            }
    }

    /// <summary>Plus que des commandants : la fenêtre de fin attend la fin du bandeau d'égalité (vrai = attendre).</summary>
    private bool HoldOnlyCommandersDraw()
    {
        if (_match.EndReason != MatchEndReason.OnlyCommanders)
            return false;
        if (_drawHold < 0f)
        {
            _drawHold = 0f;
            Context.Sounds.Play("morale_burst");
        }
        return _drawHold < DrawHoldTime;
    }

    /// <summary>Bandeaux de fin à commandants, par-dessus le plateau et le HUD.</summary>
    private void DrawPvpLoneBanners(SpriteBatch sb, Viewport viewport)
    {
        if (_loneBanner < 0f && _drawHold < 0f)
            return;
        RefreshLoneTexts();
        var font = Context.Font;
        if (_drawHold >= 0f)
        {
            // Égalité : deux lignes, « ÉGALITÉ » en grand puis la raison, sur une bande pleine largeur.
            var a = MathHelper.Clamp(_drawHold / LoneBannerFade, 0f, 1f);
            var titleScale = font.Measure(_drawTitle, 4) <= viewport.Width - 32 ? 4 : 2;
            var subScale = font.Measure(_drawSub, 2) <= viewport.Width - 32 ? 2 : 1;
            var th = font.LineHeight(titleScale);
            var sh = font.LineHeight(subScale);
            var h = th + 10 + sh + 32;
            var band = new Rectangle(0, (viewport.Height - h) / 2, viewport.Width, h);
            DrawRect(sb, band, Palette.Black1 * (0.92f * a));
            font.DrawCentered(sb, _drawTitle, new Rectangle(0, band.Y + 16, viewport.Width, th), titleScale, Palette.White * a);
            font.DrawCentered(sb, PvpArt.Fit(font, _drawSub, viewport.Width - 32, subScale),
                new Rectangle(0, band.Y + 16 + th + 10, viewport.Width, sh), subScale, Palette.Yellow2 * a);
            return;
        }

        // Commandant isolé : bande au centre, couleur du camp qui en profite (violet si c'est le nôtre).
        var mine = _loneBannerSide == Faction.Player;
        var text = mine ? _loneBannerMe : _loneBannerThem;
        var color = mine ? Palette.Purple5 : Palette.Cyan1;
        var t = _loneBanner;
        var alpha = MathHelper.Clamp(t / LoneBannerFade, 0f, 1f) * MathHelper.Clamp((LoneBannerTime - t) / LoneBannerFade, 0f, 1f);
        var scale = font.Measure(text, 2) + 40 <= viewport.Width - 16 ? 2 : 1;
        var w = Math.Min(viewport.Width - 16, font.Measure(text, scale) + 40);
        var bh = font.LineHeight(scale) + 24;
        var rect = new Rectangle((viewport.Width - w) / 2, (viewport.Height - bh) / 2, w, bh);
        DrawRect(sb, rect, Palette.Black1 * (0.92f * alpha));
        font.DrawCentered(sb, PvpArt.Fit(font, text, rect.Width - 16, scale), rect, scale, color * alpha);
    }
}
