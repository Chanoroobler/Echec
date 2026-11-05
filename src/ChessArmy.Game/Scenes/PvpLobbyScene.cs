using System;
using ChessArmy.Engine;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Scenes;
using ChessArmy.Engine.UI;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// POC multijoueur : recherche automatique d'un adversaire sur Steam (cf. <see cref="PvpMatchmaker"/>). Dès
/// que les deux joueurs se sont trouvés et salués, on bascule sur le combat PvP (<see cref="GameplayScene"/>),
/// qui prend possession de la session. Annuler (bouton, Échap ou B) ramène au menu principal.
/// </summary>
public sealed class PvpLobbyScene : Scene
{
    private const int PanelW = 380;
    private const int BtnH = 36;
    private const int Pad = 16;
    // Adversaire trouvé mais muet (jeu fermé, pare-feu…) : on abandonne la session et on recherche à nouveau.
    private const float HandshakeTimeout = 20f;

    private MenuBackdrop _backdrop = null!;
    private PvpMatchmaker _matchmaker = null!;
    private bool _handedOff;
    private float _time;
    private float _handshake;
    private Rectangle _cancelRect;

    public PvpLobbyScene(GameContext context) : base(context) { }

    public override void Load()
    {
        _backdrop = new MenuBackdrop(Context.GraphicsDevice, Context.Pixel);
        _matchmaker = new PvpMatchmaker();
        _matchmaker.Start();
    }

    public override void Unload()
    {
        _matchmaker.Dispose();
        if (!_handedOff)
            _matchmaker.Session?.Dispose();   // pas encore passée au combat : on quitte le lobby
        _backdrop.Dispose();
    }

    public override void Update(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _time += dt;

        if (Context.Input.WasKeyPressed(Keys.Escape) || Context.Input.WasCancelPressed
            || (Context.Input.WasLeftClicked && _cancelRect.Contains(Context.Input.MousePosition)))
        {
            Context.Sounds.Play("menu_close");
            Context.Scenes.Change(new MainMenuScene(Context));
            return;
        }

        _matchmaker.Update(dt);
        if (_matchmaker.Session is not { } session)
        {
            _handshake = 0f;
            return;
        }

        session.Update(dt);
        if (session.Started)
        {
            _handedOff = true;
            Context.Sounds.Play("battle_start");
            Context.Scenes.Change(new GameplayScene(Context, session));
            return;
        }
        _handshake += dt;
        if (session.OpponentLeft || _handshake > HandshakeTimeout)
        {
            _handshake = 0f;
            _matchmaker.Restart();
        }
    }

    public override void Draw(GameTime gameTime)
    {
        var sb = Context.SpriteBatch;
        var w = Context.VirtualResolution.X;
        var h = Context.VirtualResolution.Y;

        string status;
        var color = Palette.White;
        if (_matchmaker.Failed)
        {
            status = Loc.T(SteamService.IsActive ? "pvp.failed" : "pvp.no_steam");
            color = Palette.Purple5;
        }
        else if (_matchmaker.Session is { } found)
            status = Loc.T("pvp.found", found.OpponentName);
        else
            status = Loc.T("pvp.searching") + new string('.', 1 + (int)(_time * 2f) % 3);

        var title = Loc.T("menu.multiplayer");
        var cancel = (Context.Input.UsingGamepad ? "(B) " : "") + Loc.T("pvp.cancel");
        var lh = Context.Font.LineHeight(1);
        var panelH = Pad + 21 + Pad + lh + Pad + BtnH + Pad;
        var panel = new Rectangle((w - PanelW) / 2, (h - panelH) / 2, PanelW, panelH);
        _cancelRect = new Rectangle(panel.X + Pad, panel.Bottom - Pad - BtnH, PanelW - 2 * Pad, BtnH);

        sb.Begin(samplerState: SamplerState.PointClamp);
        _backdrop.Draw(sb, w, h);
        Context.Style.DrawPanel(sb, panel);
        var y = panel.Y + Pad;
        Context.Font.DrawCentered(sb, title, new Rectangle(panel.X, y, panel.Width, 21), 3, Palette.Yellow2);
        y += 21 + Pad;
        Context.Font.DrawCentered(sb, status, new Rectangle(panel.X, y, panel.Width, lh), 1, color);

        var pointer = Context.Input.UsingGamepad ? _cancelRect.Center : Context.Input.MousePosition;
        var dy = Context.Style.DrawButton(sb, _cancelRect,
            UiStyle.StateOf(_cancelRect.Contains(pointer), !Context.Input.UsingGamepad && Context.Input.IsLeftDown));
        var label = _cancelRect;
        label.Offset(0, dy);
        Context.Font.DrawCentered(sb, cancel, label, 1, Palette.White);
        sb.End();
    }

    public override void DrawLetterboxBackground(Point realScreen, Point canvasOffset, int canvasScale) =>
        _backdrop.DrawBands(Context.SpriteBatch, realScreen, canvasOffset, canvasScale, Context.VirtualResolution);
}
