using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;
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

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Avant le combat multi : la map tirée est montrée (aperçu des cases bloquantes, de l'eau, des buissons et des
/// deux zones de départ) et le joueur choisit l'une de ses 3 armées en <c>BuildChoiceSeconds</c> ; l'armée
/// adverse reste cachée. À l'échéance, l'armée sélectionnée part d'office. Contre un humain, on attend aussi la
/// sienne (<see cref="PvpSession.OpponentBuild"/>) ; contre un bot, son armée est déjà tirée.
/// </summary>
public sealed class PvpPrepScene : Scene
{
    private const int Pad = 16;
    private const int BtnH = 30;
    private const int Cell = 18;

    private readonly PvpSession? _session;
    private readonly PvpFocus _focus = new();
    private MenuBackdrop _backdrop = null!;
    private PvpMatchInfo? _botTemplate;
    private MapData? _map;
    private Battlefield? _preview;
    private List<Cell> _bushes = new();
    private int _seed;
    private int _selected;
    private float _timeLeft;
    private PvpBuild? _locked;
    private string? _error;
    private bool _handedOff;
    private Rectangle _readyRect;
    private readonly Rectangle[] _buildRects = new Rectangle[PvpProfile.BuildSlots];

    /// <param name="session">Adversaire humain (partie déjà « démarrée ») ; null = bot de secours.</param>
    public PvpPrepScene(GameContext context, PvpSession? session) : base(context) => _session = session;

    private static PvpProfile Profile => PvpProfileStore.Current;

    public override void Load()
    {
        _backdrop = new MenuBackdrop(Context.GraphicsDevice, Context.Pixel);
        if (_session == null)
        {
            _botTemplate = PvpMatchInfo.ForBot(PvpProfile.DefaultBuild(), Profile.Rating, new Random());
            _seed = _botTemplate.Seed;
        }
        else
            _seed = _session.Seed;

        _map = PvpSetup.PickMap(LoadMultiMaps(), _seed);
        if (_map != null)
            _preview = PvpSetup.BuildBattlefield(_map, isHost: _session?.IsHost ?? true, out _bushes);
        _selected = Math.Clamp(Profile.SelectedBuild, 0, PvpProfile.BuildSlots - 1);
        if (!IsPlayable(_selected))
            _selected = Enumerable.Range(0, PvpProfile.BuildSlots).FirstOrDefault(IsPlayable);
        _timeLeft = PvpConfig.Current.BuildChoiceSeconds;
    }

    public override void Unload()
    {
        if (!_handedOff)
            _session?.Dispose();
        _backdrop.Dispose();
    }

    /// <summary>Maps Multi livrées (même règle que le combat : brouillons exclus) pour tirer la même que lui.</summary>
    private static List<MapData> LoadMultiMaps()
    {
        var maps = new List<MapData>();
        try
        {
            var root = AppContext.BaseDirectory;
            var catalog = TileCatalog.FromJson(File.ReadAllText(Path.Combine(root, "Assets/Tiles/tiles.json")));
            foreach (var file in Directory.GetFiles(Path.Combine(root, "Assets/Maps"), "*.json"))
            {
                try
                {
                    var map = MapLoader.Parse(File.ReadAllText(file), catalog);
                    if (map.Type == CombatType.Multi && !map.IsDraft)
                        maps.Add(map);
                }
                catch (FormatException) { }
            }
        }
        catch (IOException) { }
        return maps;
    }

    private static bool IsPlayable(int slot) =>
        PvpCatalog.Problem(Profile.Builds[slot], Profile.Owns) is null;

    private void Lock()
    {
        if (_locked != null)
            return;
        _locked = IsPlayable(_selected) ? Profile.Builds[_selected].Clone() : PvpProfile.DefaultBuild();
        Profile.SelectedBuild = _selected;
        PvpProfileStore.Save();
        _session?.SendBuild(_locked);
        Context.Sounds.Play("unit_place");
    }

    public override void Update(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_error != null)
        {
            if (Context.Input.WasConfirmPressed || Context.Input.WasCancelPressed || Context.Input.WasLeftClicked
                || Context.Input.WasKeyPressed(Keys.Enter) || Context.Input.WasKeyPressed(Keys.Escape))
                Context.Scenes.Change(new PvpArmyScene(Context));
            return;
        }

        if (_session != null)
        {
            _session.Update(dt);
            if (_session.OpponentLeft || _session.Silence > 4f + PvpConfig.Current.ReconnectSeconds)
            {
                _error = Loc.T("pvp.opponent_left");
                return;
            }
        }

        _timeLeft -= dt;
        if (_timeLeft <= 0f)
            Lock();

        if (_locked == null)
        {
            _focus.Navigate(Context.Input);
            var hit = _focus.Activated(Context.Input);
            if (hit >= 0 && hit < PvpProfile.BuildSlots)
            {
                if (IsPlayable(hit))
                {
                    _selected = hit;
                    Context.Sounds.Play("menu_click");
                }
            }
            else if (hit == PvpProfile.BuildSlots)
                Lock();
            return;
        }

        // Armée verrouillée : on attend celle de l'adversaire (immédiate contre un bot).
        var opponent = _session == null ? _botTemplate!.OpponentBuild : _session.OpponentBuild;
        if (opponent == null)
            return;
        if (PvpCatalog.Problem(opponent) is { } problem)
        {
            _error = Loc.T("pvp.invalid_army", problem);
            return;
        }
        var info = _session == null
            ? _botTemplate!.WithLocalBuild(_locked)
            : PvpMatchInfo.ForSession(_session, _locked);
        _handedOff = true;
        Context.Scenes.Change(new GameplayScene(Context, info));
    }

    public override void Draw(GameTime gameTime)
    {
        var sb = Context.SpriteBatch;
        var w = Context.VirtualResolution.X;
        var h = Context.VirtualResolution.Y;
        var font = Context.Font;
        var lh = font.LineHeight(1) + 4;

        sb.Begin(samplerState: SamplerState.PointClamp);
        _backdrop.Draw(sb, w, h);

        var boardPx = PvpSetup.Size * Cell;
        var listW = 360;
        var panelW = Pad + boardPx + Pad * 2 + listW + Pad;
        var panelH = Pad + 21 + Pad + Math.Max(boardPx + lh * 2, PvpProfile.BuildSlots * (BtnH * 2 + 6) + BtnH + Pad) + Pad;
        var panel = new Rectangle((w - panelW) / 2, (h - panelH) / 2, panelW, panelH);
        Context.Style.DrawPanel(sb, panel);

        var opponentName = _session?.OpponentName ?? _botTemplate!.DisplayName;
        var title = Loc.T("pvp.versus", opponentName);
        font.DrawCentered(sb, title, new Rectangle(panel.X, panel.Y + Pad, panel.Width, 21), 2, Palette.Yellow2);

        // Aperçu de la map (repère local : le joueur en bas).
        var origin = new Point(panel.X + Pad, panel.Y + Pad + 21 + Pad);
        var bushes = _bushes.ToHashSet();
        for (var r = 0; r < PvpSetup.Size; r++)
            for (var c = 0; c < PvpSetup.Size; c++)
            {
                var rect = new Rectangle(origin.X + c * Cell, origin.Y + r * Cell, Cell - 1, Cell - 1);
                var color = Palette.Green1;
                if (_preview != null)
                {
                    var tile = _preview[new Cell(c, r)];
                    color = tile.BlocksMovement && tile.BlocksLineOfFire ? Palette.Brown1
                        : tile.BlocksMovement ? Palette.WaterMid2
                        : tile.Slippery ? Palette.Cyan1
                        : Palette.Green1;
                }
                if (bushes.Contains(new Cell(c, r)))
                    color = Palette.Green2;
                sb.Draw(Context.Pixel, rect, color);
                if (r <= 1)
                    sb.Draw(Context.Pixel, rect, Palette.Purple5 * 0.35f);
                else if (r >= PvpSetup.Size - 2)
                    sb.Draw(Context.Pixel, rect, Palette.Cyan1 * 0.35f);
            }
        var mapName = _map?.Name ?? Loc.T("pvp.map_default");
        font.DrawCentered(sb, mapName.ToUpperInvariant(), new Rectangle(origin.X, origin.Y + boardPx + 6, boardPx, 7), 1, Palette.White);

        // Armées (3) + bouton PRÊT.
        _focus.Clear();
        var x = origin.X + boardPx + Pad * 2;
        var y = origin.Y;
        var gp = Context.Input.UsingGamepad;
        for (var i = 0; i < PvpProfile.BuildSlots; i++)
        {
            var build = Profile.Builds[i];
            var rect = new Rectangle(x, y, listW, BtnH * 2);
            _buildRects[i] = rect;
            _focus.Add(rect);
            var playable = IsPlayable(i);
            var selected = i == _selected;
            var hover = gp ? _focus.IsFocused(i) : rect.Contains(Context.Input.MousePosition);
            Context.Style.DrawButton(sb, rect, UiStyle.StateOf(hover && _locked == null, false), selected);
            var head = Loc.T("pvp.army_n", i + 1) + "  " + PvpUi.CommanderName(build.CommanderId);
            var sub = playable
                ? Loc.T("pvp.army_summary", build.Slots.Count, PvpCatalog.Cost(build), PvpConfig.Current.Budget)
                : Loc.T("pvp.army_invalid");
            font.DrawCentered(sb, head, new Rectangle(rect.X, rect.Y + 8, rect.Width, 7), 1, selected ? Palette.Yellow2 : Palette.White);
            font.DrawCentered(sb, sub, new Rectangle(rect.X, rect.Y + 8 + lh, rect.Width, 7), 1, playable ? Palette.Blue1 : Palette.Purple5);
            y += BtnH * 2 + 6;
        }
        y += 4;
        _readyRect = new Rectangle(x, y, listW, BtnH);
        _focus.Add(_readyRect);
        var ready = _locked == null
            ? (gp ? "(A) " : "") + Loc.T("pvp.ready", PvpUi.Clock(Math.Max(0, _timeLeft)))
            : Loc.T("pvp.waiting_opponent");
        PvpUi.Button(Context, sb, _readyRect, ready, _focus.IsFocused(PvpProfile.BuildSlots), _locked == null);

        if (_error != null)
        {
            sb.Draw(Context.Pixel, new Rectangle(0, 0, w, h), Palette.Black1 * 0.62f);
            var ew = font.Measure(_error, 1) + 60;
            var box = new Rectangle((w - ew) / 2, h / 2 - 30, ew, 60);
            Context.Style.DrawPanel(sb, box);
            font.DrawCentered(sb, _error, box, 1, Palette.Purple5);
        }
        sb.End();
    }

    public override void DrawLetterboxBackground(Point realScreen, Point canvasOffset, int canvasScale) =>
        _backdrop.DrawBands(Context.SpriteBatch, realScreen, canvasOffset, canvasScale, Context.VirtualResolution);
}
