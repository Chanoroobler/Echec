using System;
using System.IO;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.Scenes;
using ChessArmy.Engine.UI;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// COFFRES du multi : un coffre de PIONS (T2, T3, commandants) et un coffre d'ÉQUIPEMENTS, payés en points. Le
/// contenu est tiré parmi ce que le joueur n'a PAS encore (jamais de doublon) ; un coffre vidé est grisé et les
/// points restent. Le dernier objet obtenu est montré en grand.
/// </summary>
public sealed class PvpChestScene : Scene
{
    private const int Pad = 16;
    private const int BtnH = 32;

    private readonly PvpFocus _focus = new();
    private readonly Random _rng = new();
    private MenuBackdrop _backdrop = null!;
    private UnitCardRenderer _units = null!;
    private Texture2D? _rewardIcon;
    private string? _lastReward;
    private bool _lastWasUnit;
    private float _revealTime;

    public PvpChestScene(GameContext context) : base(context) { }

    private static PvpProfile Profile => PvpProfileStore.Current;

    public override void Load()
    {
        _backdrop = new MenuBackdrop(Context.GraphicsDevice, Context.Pixel);
        _units = new UnitCardRenderer(Context);
    }

    public override void Unload()
    {
        _units.Unload();
        _rewardIcon?.Dispose();
        _backdrop.Dispose();
    }

    private static bool CanOpen(bool units) =>
        (units ? Profile.LockedUnits().Count : Profile.LockedEquipment().Count) > 0
        && Profile.Points >= (units ? PvpConfig.Current.UnitChestCost : PvpConfig.Current.EquipmentChestCost);

    public override void Update(GameTime gameTime)
    {
        _revealTime += (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (Context.Input.WasKeyPressed(Keys.Escape) || Context.Input.WasCancelPressed)
        {
            Back();
            return;
        }
        _focus.Navigate(Context.Input);
        switch (_focus.Activated(Context.Input))
        {
            case 0: Open(units: true); break;
            case 1: Open(units: false); break;
            case 2: Back(); break;
        }
    }

    private void Open(bool units)
    {
        if (!CanOpen(units))
        {
            Context.Sounds.Play("menu_close");
            return;
        }
        var item = Profile.OpenChest(units, _rng);
        if (item == null)
            return;
        PvpProfileStore.Save();
        _lastReward = item;
        _lastWasUnit = units;
        _revealTime = 0f;
        _rewardIcon?.Dispose();
        _rewardIcon = !units && Equipments.ById(item) is { } e
            ? Textures.LoadPngOrNull(Context.GraphicsDevice, Path.Combine(AppContext.BaseDirectory, $"Assets/Equipment/{e.Icon}.png"))
            : null;
        Context.Sounds.Play("reward");
    }

    private void Back()
    {
        Context.Sounds.Play("menu_close");
        Context.Scenes.Change(new PvpLobbyScene(Context));
    }

    public override void Draw(GameTime gameTime)
    {
        var sb = Context.SpriteBatch;
        var font = Context.Font;
        var w = Context.VirtualResolution.X;
        var h = Context.VirtualResolution.Y;
        var gp = Context.Input.UsingGamepad;
        const int ChestW = 260, PanelH = 360;
        var panelW = 2 * ChestW + 3 * Pad;
        var panel = new Rectangle((w - panelW) / 2, (h - PanelH) / 2, panelW, PanelH);

        sb.Begin(samplerState: SamplerState.PointClamp);
        _backdrop.Draw(sb, w, h);
        Context.Style.DrawPanel(sb, panel);
        font.DrawCentered(sb, Loc.T("pvp.chests"), new Rectangle(panel.X, panel.Y + Pad, panel.Width, 21), 3, Palette.Yellow2);
        font.DrawCentered(sb, Loc.T("pvp.profile_points", Profile.Points),
            new Rectangle(panel.X, panel.Y + Pad + 28, panel.Width, 7), 1, Palette.White);

        _focus.Clear();
        var y = panel.Y + Pad + 50;
        for (var i = 0; i < 2; i++)
        {
            var units = i == 0;
            var box = new Rectangle(panel.X + Pad + i * (ChestW + Pad), y, ChestW, 120);
            sb.Draw(Context.Pixel, box, Palette.Black1 * 0.3f);
            var left = units ? Profile.LockedUnits().Count : Profile.LockedEquipment().Count;
            var cost = units ? PvpConfig.Current.UnitChestCost : PvpConfig.Current.EquipmentChestCost;
            font.DrawCentered(sb, Loc.T(units ? "pvp.chest_units" : "pvp.chest_equipment"),
                new Rectangle(box.X, box.Y + 12, box.Width, 7), 1, Palette.Yellow2);
            font.DrawCentered(sb, left > 0 ? Loc.T("pvp.chest_left", left) : Loc.T("pvp.chest_empty"),
                new Rectangle(box.X, box.Y + 34, box.Width, 7), 1, left > 0 ? Palette.Blue1 : Palette.Grey);
            var btn = new Rectangle(box.X + 20, box.Bottom - BtnH - 14, box.Width - 40, BtnH);
            _focus.Add(btn);
            PvpUi.Button(Context, sb, btn, Loc.T("pvp.chest_open", cost), _focus.IsFocused(i), CanOpen(units));
        }

        // Dernier objet obtenu.
        var reveal = new Rectangle(panel.X + Pad, y + 120 + Pad, panel.Width - 2 * Pad, 110);
        if (_lastReward != null)
        {
            var name = _lastWasUnit
                ? PvpCatalog.FindClass(_lastReward) is { } cls ? PvpUi.UnitName(cls.Class) : PvpUi.CommanderName(_lastReward)
                : Equipments.ById(_lastReward) is { } eq ? EquipmentNames.Localized(eq).ToUpperInvariant() : _lastReward;
            font.DrawCentered(sb, Loc.T("pvp.chest_got", name), new Rectangle(reveal.X, reveal.Y, reveal.Width, 7), 1, Palette.Yellow2);
            var center = new Point(reveal.Center.X, reveal.Y + 56);
            if (_lastWasUnit)
            {
                var cls2 = PvpCatalog.FindClass(_lastReward)?.Class ?? PvpCatalog.CommanderClass(_lastReward)?.Class;
                if (cls2 != null)
                    _units.DrawScaled(sb, cls2, center, _revealTime < 0.15f ? 1 : 2);
            }
            else if (_rewardIcon != null)
            {
                var scale = _revealTime < 0.15f ? 1 : 2;
                sb.Draw(_rewardIcon, new Rectangle(center.X - _rewardIcon.Width * scale / 2, center.Y - _rewardIcon.Height * scale / 2,
                    _rewardIcon.Width * scale, _rewardIcon.Height * scale), Color.White);
            }
        }

        var back = new Rectangle(panel.X + (panel.Width - 180) / 2, panel.Bottom - Pad - BtnH, 180, BtnH);
        _focus.Add(back);
        PvpUi.Button(Context, sb, back, (gp ? "(B) " : "") + Loc.T("pvp.back"), _focus.IsFocused(2));
        sb.End();
    }

    public override void DrawLetterboxBackground(Point realScreen, Point canvasOffset, int canvasScale) =>
        _backdrop.DrawBands(Context.SpriteBatch, realScreen, canvasOffset, canvasScale, Context.VirtualResolution);
}
