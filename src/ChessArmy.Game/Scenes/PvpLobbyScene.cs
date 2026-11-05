using System;
using System.Collections.Generic;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
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
/// Accueil du MULTIJOUEUR : profil (rang, cote, points, bilan), et accès à la partie classée, au défi entre
/// amis, aux armées (<see cref="PvpBuildScene"/>) et aux coffres (<see cref="PvpChestScene"/>).
///  • PARTIE CLASSÉE : recherche d'un joueur de cote proche (<see cref="PvpMatchmaker.Start"/>) ; personne au bout
///    de <c>BotAfterSeconds</c> → bot du niveau du joueur. Sans Steam, on joue directement contre un bot.
///  • DÉFIER UN AMI : partie privée + invitation de l'overlay Steam. L'ami invité arrive ici par <see cref="PvpInvites"/>.
/// Une fois l'adversaire trouvé, on passe au choix de l'armée devant la map (<see cref="PvpPrepScene"/>).
/// </summary>
public sealed class PvpLobbyScene : Scene
{
    private enum Mode { Hub, Search, FriendHost, JoinInvite }

    private const int PanelW = 400;
    private const int BtnH = 32;
    private const int Pad = 16;
    private const int Gap = 8;
    // Adversaire trouvé mais muet (jeu fermé, pare-feu…) : on abandonne la session.
    private const float HandshakeTimeout = 20f;

    private readonly List<(string Label, Action Action, bool Enabled)> _buttons = new();
    private readonly PvpFocus _focus = new();
    private MenuBackdrop _backdrop = null!;
    private PvpMatchmaker? _matchmaker;
    private Mode _mode;
    private bool _handedOff;
    private float _time;
    private float _handshake;
    private bool _tutorialOpen;
    private Rectangle _tutorialOk;
    private CSteamID _invite;

    /// <param name="invite">Lobby d'une invitation d'ami acceptée : on le rejoint directement.</param>
    public PvpLobbyScene(GameContext context, CSteamID? invite = null) : base(context)
    {
        _invite = invite ?? CSteamID.Nil;
    }

    private static PvpProfile Profile => PvpProfileStore.Current;

    public override void Load()
    {
        _backdrop = new MenuBackdrop(Context.GraphicsDevice, Context.Pixel);
        Context.Music.Play(Engine.Audio.MusicScene.Calm);
        if (_invite != CSteamID.Nil)
            JoinInvite(_invite);
        else
            ShowHub();
        _tutorialOpen = !Profile.TutorialDone && _invite == CSteamID.Nil;
    }

    public override void Unload()
    {
        StopMatchmaking();
        _backdrop.Dispose();
    }

    // ── Transitions ──────────────────────────────────────────────────────────────

    private void ShowHub()
    {
        StopMatchmaking();
        _mode = Mode.Hub;
        SetButtons(
            (Loc.T("pvp.ranked"), StartSearch, true),
            (Loc.T("pvp.challenge"), StartFriend, SteamService.IsActive),
            (Loc.T("pvp.armies"), () => Context.Scenes.Change(new PvpBuildScene(Context)), true),
            (Loc.T("pvp.chests"), () => Context.Scenes.Change(new PvpChestScene(Context)), true),
            (Loc.T("pvp.back"), BackToMenu, true));
    }

    private void StartSearch()
    {
        StopMatchmaking();
        if (!SteamService.IsActive)
        {
            StartBot();   // hors ligne : pas de recherche possible, directement contre un bot
            return;
        }
        _mode = Mode.Search;
        _matchmaker = new PvpMatchmaker(Profile.Rating);
        _matchmaker.Start();
        SetButtons((Loc.T("pvp.cancel"), ShowHub, true));
    }

    private void StartFriend()
    {
        StopMatchmaking();
        _mode = Mode.FriendHost;
        _matchmaker = new PvpMatchmaker(Profile.Rating);
        _matchmaker.StartFriendLobby();
        SetButtons((Loc.T("pvp.invite_again"), () => _matchmaker?.ReopenInvite(), true), (Loc.T("pvp.cancel"), ShowHub, true));
    }

    private void JoinInvite(CSteamID lobby)
    {
        StopMatchmaking();
        _mode = Mode.JoinInvite;
        _matchmaker = new PvpMatchmaker(Profile.Rating);
        _matchmaker.JoinInvite(lobby);
        SetButtons((Loc.T("pvp.cancel"), ShowHub, true));
    }

    private void StartBot()
    {
        StopMatchmaking();
        Context.Scenes.Change(new PvpPrepScene(Context, session: null));
    }

    private void BackToMenu()
    {
        Context.Sounds.Play("menu_close");
        Context.Scenes.Change(new MainMenuScene(Context));
    }

    /// <summary>Arrête la recherche en cours ; une session pas encore passée au choix d'armée est quittée.</summary>
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

    private void SetButtons(params (string Label, Action Action, bool Enabled)[] buttons)
    {
        _buttons.Clear();
        _buttons.AddRange(buttons);
        _focus.Index = 0;
    }

    // ── Mise à jour ──────────────────────────────────────────────────────────────

    public override void Update(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _time += dt;

        if (_tutorialOpen)
        {
            if (Context.Input.WasConfirmPressed || Context.Input.WasKeyPressed(Keys.Enter)
                || (Context.Input.WasLeftClicked && _tutorialOk.Contains(Context.Input.MousePosition)))
            {
                _tutorialOpen = false;
                Profile.TutorialDone = true;
                PvpProfileStore.Save();
                Context.Sounds.Play("menu_click");
            }
            return;
        }

        // Invitation d'ami acceptée pendant qu'on est ici : elle prime sur ce qu'on faisait.
        if (PvpInvites.TryTake(out var invite))
        {
            JoinInvite(invite);
            return;
        }

        if (Context.Input.WasKeyPressed(Keys.Escape) || Context.Input.WasCancelPressed)
        {
            if (_mode == Mode.Hub) BackToMenu();
            else { Context.Sounds.Play("menu_close"); ShowHub(); }
            return;
        }

        _focus.Navigate(Context.Input);
        var hit = _focus.Activated(Context.Input);
        if (hit >= 0 && hit < _buttons.Count && _buttons[hit].Enabled)
        {
            Context.Sounds.Play("menu_click");
            _buttons[hit].Action();
            return;
        }

        if (_matchmaker is not { } mm)
            return;
        mm.Update(dt);
        if (_mode == Mode.Search && mm.Session == null
            && (mm.Failed || mm.SearchTime >= PvpConfig.Current.BotAfterSeconds))
        {
            StartBot();   // personne à notre niveau (ou Steam indisponible) : un bot prend le relais
            return;
        }
        if (mm.Session is not { } session)
            return;

        session.Update(dt);
        if (session.Started)
        {
            _handedOff = true;
            Context.Sounds.Play("battle_start");
            Context.Scenes.Change(new PvpPrepScene(Context, session));
            return;
        }
        _handshake += dt;
        if (session.OpponentLeft || _handshake > HandshakeTimeout)
        {
            _handshake = 0f;
            mm.Restart();   // recherche : on cherche à nouveau ; défi / invitation : échec affiché
        }
    }

    // ── Rendu ────────────────────────────────────────────────────────────────────

    private List<(string Text, Color Color)> InfoLines()
    {
        var lines = new List<(string, Color)>();
        if (_mode == Mode.Hub)
        {
            var p = Profile;
            lines.Add((PvpUi.RankName(p.Rank), Palette.Yellow2));
            lines.Add((Loc.T("pvp.profile_rating", p.Rating), Palette.White));
            lines.Add((Loc.T("pvp.profile_points", p.Points), Palette.White));
            lines.Add((Loc.T("pvp.profile_record", p.Wins, p.Losses, p.Draws), Palette.Blue1));
            if (PvpProfileStore.WasReset)
                lines.Add((Loc.T("pvp.profile_reset"), Palette.Purple5));
            if (!SteamService.IsActive)
                lines.Add((Loc.T("pvp.offline"), Palette.Blue1));
            return lines;
        }

        var mm = _matchmaker!;
        if (mm.Failed)
        {
            lines.Add((Loc.T(SteamService.IsActive ? "pvp.join_failed" : "pvp.no_steam"), Palette.Purple5));
            return lines;
        }
        if (mm.Session is { } found)
        {
            lines.Add((Loc.T("pvp.found", found.OpponentName), Palette.White));
            return lines;
        }
        var dots = new string('.', 1 + (int)(_time * 2f) % 3);
        switch (_mode)
        {
            case Mode.FriendHost:
                lines.Add((Loc.T("pvp.wait_friend") + dots, Palette.White));
                lines.Add((Loc.T("pvp.invite_hint"), Palette.Blue1));
                break;
            case Mode.JoinInvite:
                lines.Add((Loc.T("pvp.joining") + dots, Palette.White));
                break;
            default:
                lines.Add((Loc.T("pvp.searching") + dots, Palette.White));
                var left = PvpConfig.Current.BotAfterSeconds - mm.SearchTime;
                lines.Add((Loc.T("pvp.bot_in", Math.Max(0, (int)Math.Ceiling(left))), Palette.Blue1));
                break;
        }
        return lines;
    }

    public override void Draw(GameTime gameTime)
    {
        var sb = Context.SpriteBatch;
        var w = Context.VirtualResolution.X;
        var h = Context.VirtualResolution.Y;
        var lines = InfoLines();
        var lh = Context.Font.LineHeight(1) + 5;
        var panelH = Pad + 21 + Pad + lines.Count * lh + Pad + _buttons.Count * (BtnH + Gap) - Gap + Pad;
        var panel = new Rectangle((w - PanelW) / 2, (h - panelH) / 2, PanelW, panelH);

        sb.Begin(samplerState: SamplerState.PointClamp);
        _backdrop.Draw(sb, w, h);
        Context.Style.DrawPanel(sb, panel);
        var y = panel.Y + Pad;
        Context.Font.DrawCentered(sb, Loc.T("menu.multiplayer"), new Rectangle(panel.X, y, panel.Width, 21), 3, Palette.Yellow2);
        y += 21 + Pad;
        foreach (var (text, color) in lines)
        {
            Context.Font.DrawCentered(sb, text, new Rectangle(panel.X, y, panel.Width, lh - 5), 1, color);
            y += lh;
        }
        y += Pad - 5;

        _focus.Clear();
        var gp = Context.Input.UsingGamepad;
        for (var i = 0; i < _buttons.Count; i++)
        {
            var r = new Rectangle(panel.X + Pad, y, PanelW - 2 * Pad, BtnH);
            _focus.Add(r);
            var label = (gp && _buttons[i].Action == (Action)ShowHub ? "(B) " : "") + _buttons[i].Label;
            PvpUi.Button(Context, sb, r, label, _focus.IsFocused(i), _buttons[i].Enabled);
            y += BtnH + Gap;
        }

        if (_tutorialOpen)
            DrawTutorial(sb, w, h);
        sb.End();
    }

    /// <summary>Tuto multi (première visite) : les points de départ ouvrent un coffre de chaque type.</summary>
    private void DrawTutorial(SpriteBatch sb, int w, int h)
    {
        sb.Draw(Context.Pixel, new Rectangle(0, 0, w, h), Palette.Black1 * 0.62f);
        var lines = new[] { Loc.T("pvp.tuto_1"), Loc.T("pvp.tuto_2", Profile.Points), Loc.T("pvp.tuto_3"), Loc.T("pvp.tuto_4") };
        var lh = Context.Font.LineHeight(1) + 6;
        var pw = 0;
        foreach (var l in lines)
            pw = Math.Max(pw, Context.Font.Measure(l, 1));
        pw = Math.Max(pw + 2 * Pad + 16, 360);
        var ph = Pad + 21 + Pad + lines.Length * lh + Pad + BtnH + Pad;
        var panel = new Rectangle((w - pw) / 2, (h - ph) / 2, pw, ph);
        Context.Style.DrawPanel(sb, panel);
        var y = panel.Y + Pad;
        Context.Font.DrawCentered(sb, Loc.T("pvp.tuto_title"), new Rectangle(panel.X, y, panel.Width, 21), 3, Palette.Yellow2);
        y += 21 + Pad;
        foreach (var l in lines)
        {
            Context.Font.DrawCentered(sb, l, new Rectangle(panel.X, y, panel.Width, lh - 6), 1, Palette.White);
            y += lh;
        }
        _tutorialOk = new Rectangle(panel.X + (pw - 160) / 2, panel.Bottom - Pad - BtnH, 160, BtnH);
        PvpUi.Button(Context, sb, _tutorialOk, (Context.Input.UsingGamepad ? "(A) " : "") + Loc.T("pvp.ok"), true);
    }

    public override void DrawLetterboxBackground(Point realScreen, Point canvasOffset, int canvasScale) =>
        _backdrop.DrawBands(Context.SpriteBatch, realScreen, canvasOffset, canvasScale, Context.VirtualResolution);
}
