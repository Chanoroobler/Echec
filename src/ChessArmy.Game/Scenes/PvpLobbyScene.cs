using System;
using System.Collections.Generic;
using ChessArmy.Engine;
using ChessArmy.Engine.Input;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Scenes;
using ChessArmy.Engine.UI;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Steamworks;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// POC multijoueur : écran d'entrée du mode. Deux façons de trouver un adversaire :
///  • RECHERCHE RAPIDE : n'importe quel joueur qui cherche en même temps (<see cref="PvpMatchmaker.Start"/>) ;
///  • DÉFIER UN AMI : partie privée + fenêtre d'invitation de l'overlay Steam (<see cref="PvpMatchmaker.StartFriendLobby"/>).
/// L'ami invité arrive ici par <see cref="PvpInvites"/> (mode « rejoindre »). Dès que les deux joueurs se sont
/// salués, on bascule sur le combat PvP (<see cref="GameplayScene"/>), qui prend possession de la session.
/// </summary>
public sealed class PvpLobbyScene : Scene
{
    private enum Mode { Choice, Search, FriendHost, JoinInvite }

    private const int PanelW = 380;
    private const int BtnH = 36;
    private const int Pad = 16;
    private const int Gap = 10;
    // Adversaire trouvé mais muet (jeu fermé, pare-feu…) : on abandonne la session.
    private const float HandshakeTimeout = 20f;

    private readonly List<(string Label, Action Action)> _buttons = new();
    private readonly List<Rectangle> _buttonRects = new();
    private MenuBackdrop _backdrop = null!;
    private PvpMatchmaker? _matchmaker;
    private Mode _mode;
    private bool _handedOff;
    private float _time;
    private float _handshake;
    private int _focus;
    private CSteamID _invite;

    /// <param name="invite">Lobby d'une invitation d'ami acceptée : on le rejoint directement.</param>
    public PvpLobbyScene(GameContext context, CSteamID? invite = null) : base(context)
    {
        _invite = invite ?? CSteamID.Nil;
    }

    public override void Load()
    {
        _backdrop = new MenuBackdrop(Context.GraphicsDevice, Context.Pixel);
        if (_invite != CSteamID.Nil)
            JoinInvite(_invite);
        else
            ShowChoice();
    }

    public override void Unload()
    {
        StopMatchmaking();
        _backdrop.Dispose();
    }

    // ── Transitions ──────────────────────────────────────────────────────────────

    private void ShowChoice()
    {
        StopMatchmaking();
        _mode = Mode.Choice;
        SetButtons(
            (Loc.T("pvp.quick"), StartSearch),
            (Loc.T("pvp.challenge"), StartFriend),
            (Loc.T("pvp.back"), BackToMenu));
    }

    private void StartSearch()
    {
        StopMatchmaking();
        _mode = Mode.Search;
        _matchmaker = new PvpMatchmaker();
        _matchmaker.Start();
        SetButtons((Loc.T("pvp.cancel"), ShowChoice));
    }

    private void StartFriend()
    {
        StopMatchmaking();
        _mode = Mode.FriendHost;
        _matchmaker = new PvpMatchmaker();
        _matchmaker.StartFriendLobby();
        SetButtons((Loc.T("pvp.invite_again"), () => _matchmaker?.ReopenInvite()), (Loc.T("pvp.cancel"), ShowChoice));
    }

    private void JoinInvite(CSteamID lobby)
    {
        StopMatchmaking();
        _mode = Mode.JoinInvite;
        _matchmaker = new PvpMatchmaker();
        _matchmaker.JoinInvite(lobby);
        SetButtons((Loc.T("pvp.cancel"), ShowChoice));
    }

    private void BackToMenu()
    {
        Context.Sounds.Play("menu_close");
        Context.Scenes.Change(new MainMenuScene(Context));
    }

    /// <summary>Arrête la recherche en cours ; une session pas encore passée au combat est quittée.</summary>
    private void StopMatchmaking()
    {
        if (_matchmaker is null)
            return;
        _matchmaker.Dispose();
        if (!_handedOff)
            _matchmaker.Session?.Dispose();
        _matchmaker = null;
        _handshake = 0f;
    }

    private void SetButtons(params (string Label, Action Action)[] buttons)
    {
        _buttons.Clear();
        _buttons.AddRange(buttons);
        _focus = 0;
    }

    // ── Mise à jour ──────────────────────────────────────────────────────────────

    public override void Update(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _time += dt;

        // Invitation d'ami acceptée pendant qu'on est ici : elle prime sur ce qu'on faisait.
        if (PvpInvites.TryTake(out var invite))
        {
            JoinInvite(invite);
            return;
        }

        if (Context.Input.WasKeyPressed(Keys.Escape) || Context.Input.WasCancelPressed)
        {
            if (_mode == Mode.Choice) BackToMenu();
            else { Context.Sounds.Play("menu_close"); ShowChoice(); }
            return;
        }
        if (UpdateButtons())
            return;

        if (_matchmaker is not { } mm)
            return;
        mm.Update(dt);
        if (mm.Session is not { } session)
            return;

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
            mm.Restart();   // recherche : on cherche à nouveau ; défi / invitation : échec affiché
        }
    }

    /// <summary>Boutons : clic souris, ou haut/bas + A à la manette. Vrai si un bouton a été activé.</summary>
    private bool UpdateButtons()
    {
        if (_buttons.Count == 0)
            return false;
        if (Context.Input.Nav(NavDir.Up)) { _focus = (_focus - 1 + _buttons.Count) % _buttons.Count; Context.Sounds.Play("menu_click"); }
        if (Context.Input.Nav(NavDir.Down)) { _focus = (_focus + 1) % _buttons.Count; Context.Sounds.Play("menu_click"); }

        var index = -1;
        if (Context.Input.WasConfirmPressed)
            index = _focus;
        else if (Context.Input.WasLeftClicked)
            for (var i = 0; i < _buttonRects.Count && i < _buttons.Count; i++)
                if (_buttonRects[i].Contains(Context.Input.MousePosition))
                    index = i;
        if (index < 0)
            return false;
        Context.Sounds.Play("menu_click");
        _buttons[index].Action();
        return true;
    }

    // ── Rendu ────────────────────────────────────────────────────────────────────

    private string StatusText(out Color color)
    {
        color = Palette.White;
        if (_mode == Mode.Choice)
            return SteamService.IsActive ? "" : Loc.T("pvp.no_steam");
        var mm = _matchmaker!;
        if (mm.Failed)
        {
            color = Palette.Purple5;
            if (!SteamService.IsActive) return Loc.T("pvp.no_steam");
            return Loc.T(_mode == Mode.Search ? "pvp.failed" : "pvp.join_failed");
        }
        if (mm.Session is { } found)
            return Loc.T("pvp.found", found.OpponentName);
        var dots = new string('.', 1 + (int)(_time * 2f) % 3);
        return _mode switch
        {
            Mode.FriendHost => Loc.T("pvp.wait_friend") + dots,
            Mode.JoinInvite => Loc.T("pvp.joining") + dots,
            _ => Loc.T("pvp.searching") + dots,
        };
    }

    public override void Draw(GameTime gameTime)
    {
        var sb = Context.SpriteBatch;
        var w = Context.VirtualResolution.X;
        var h = Context.VirtualResolution.Y;
        var status = StatusText(out var color);
        var hint = _mode == Mode.FriendHost && _matchmaker is { Failed: false, Session: null } ? Loc.T("pvp.invite_hint") : "";
        var lh = Context.Font.LineHeight(1);

        var textRows = (status.Length > 0 ? 1 : 0) + (hint.Length > 0 ? 1 : 0);
        var panelH = Pad + 21 + Pad + textRows * (lh + Gap) + _buttons.Count * (BtnH + Gap) - Gap + Pad;
        var panel = new Rectangle((w - PanelW) / 2, (h - panelH) / 2, PanelW, panelH);

        sb.Begin(samplerState: SamplerState.PointClamp);
        _backdrop.Draw(sb, w, h);
        Context.Style.DrawPanel(sb, panel);
        var y = panel.Y + Pad;
        Context.Font.DrawCentered(sb, Loc.T("menu.multiplayer"), new Rectangle(panel.X, y, panel.Width, 21), 3, Palette.Yellow2);
        y += 21 + Pad;
        if (status.Length > 0)
        {
            Context.Font.DrawCentered(sb, status, new Rectangle(panel.X, y, panel.Width, lh), 1, color);
            y += lh + Gap;
        }
        if (hint.Length > 0)
        {
            Context.Font.DrawCentered(sb, hint, new Rectangle(panel.X, y, panel.Width, lh), 1, Palette.Blue1);
            y += lh + Gap;
        }

        _buttonRects.Clear();
        var gp = Context.Input.UsingGamepad;
        for (var i = 0; i < _buttons.Count; i++)
        {
            var r = new Rectangle(panel.X + Pad, y, PanelW - 2 * Pad, BtnH);
            _buttonRects.Add(r);
            var hover = gp ? i == _focus : r.Contains(Context.Input.MousePosition);
            var dy = Context.Style.DrawButton(sb, r, UiStyle.StateOf(hover, !gp && Context.Input.IsLeftDown));
            var label = r;
            label.Offset(0, dy);
            Context.Font.DrawCentered(sb, _buttons[i].Label, label, 1, Palette.White);
            y += BtnH + Gap;
        }
        sb.End();
    }

    public override void DrawLetterboxBackground(Point realScreen, Point canvasOffset, int canvasScale) =>
        _backdrop.DrawBands(Context.SpriteBatch, realScreen, canvasOffset, canvasScale, Context.VirtualResolution);
}
