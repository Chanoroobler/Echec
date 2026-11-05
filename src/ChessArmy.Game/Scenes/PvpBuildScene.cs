using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChessArmy.Core.Battle;
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
/// MES ARMÉES : composer et sauvegarder jusqu'à 3 armées pour le multi. Un commandant (gratuit, case fixe au
/// centre de la ligne du fond), puis des pions et des équipements pris dans ce qu'on a débloqué, posés sur les
/// deux lignes, le tout dans le budget. Prendre / poser au clic (ou A à la manette), poser sur la corbeille pour
/// retirer, cliquer l'équipement d'un pion pour le lui reprendre. Chaque changement est enregistré.
/// </summary>
public sealed class PvpBuildScene : Scene
{
    private enum Target { Tab, CommanderPrev, CommanderNext, Cell, Trash, PaletteUnits, PaletteEquip, Item, PagePrev, PageNext, Back, Badge }

    private const int CellSize = 64;
    private const int ItemW = 72, ItemH = 84;
    private const int EquipCell = 44;
    private const int Pad = 14;

    private readonly PvpFocus _focus = new();
    private readonly List<(Rectangle Rect, Target Kind, int Index)> _targets = new();
    private readonly Dictionary<string, Texture2D?> _icons = new();
    private MenuBackdrop _backdrop = null!;
    private UnitCardRenderer _units = null!;
    private int _tab;
    private bool _paletteEquip;
    private int _page;
    private string? _heldUnit;          // classe tenue (palette ou case)
    private string? _heldEquip;         // équipement tenu
    private (int Column, int Line)? _heldFrom;   // case d'origine d'un pion tenu (il y retourne si on annule)
    private string? _heldFromEquip;     // équipement que portait le pion tenu
    private float _deny;                // tremblement « pas assez de points »
    private string _hint = "";

    public PvpBuildScene(GameContext context) : base(context) { }

    private static PvpProfile Profile => PvpProfileStore.Current;
    private PvpBuild Build => Profile.Builds[_tab];
    private bool Holding => _heldUnit != null || _heldEquip != null;

    public override void Load()
    {
        _backdrop = new MenuBackdrop(Context.GraphicsDevice, Context.Pixel);
        _units = new UnitCardRenderer(Context);
        _tab = Math.Clamp(Profile.SelectedBuild, 0, PvpProfile.BuildSlots - 1);
    }

    public override void Unload()
    {
        ReturnHeld();
        PvpProfileStore.Save();
        _units.Unload();
        foreach (var tex in _icons.Values)
            tex?.Dispose();
        _backdrop.Dispose();
    }

    // ── Contenu ──────────────────────────────────────────────────────────────────

    private List<UnitClass> OwnedUnits() =>
        PvpCatalog.AllUnitClasses().Select(c => c.Class).Where(c => Profile.Owns(c.Asset))
            .OrderBy(c => c.Tier).ThenBy(c => c.Asset, StringComparer.Ordinal).ToList();

    private List<Equipment> OwnedEquipment() =>
        PvpCatalog.AllEquipment().Where(e => Profile.Owns(e.Id))
            .OrderBy(e => e.Rarity).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();

    private List<CommandeDef> OwnedCommanders() =>
        PvpCatalog.Commanders().Where(c => Profile.Owns(c.Id)).ToList();

    private int ItemsPerPage(Rectangle palette) =>
        _paletteEquip
            ? Math.Max(1, palette.Width / EquipCell) * Math.Max(1, palette.Height / EquipCell)
            : Math.Max(1, palette.Width / ItemW) * Math.Max(1, palette.Height / ItemH);

    private int Remaining => PvpConfig.Current.Budget - PvpCatalog.Cost(Build);

    private int CostOfUnit(string asset) =>
        PvpCatalog.FindClass(asset) is { } c ? PvpConfig.Current.CostOf(c.Class) : 0;

    private int CostOfEquip(string? id) =>
        id != null && Equipments.ById(id) is { } e ? PvpConfig.Current.CostOf(e) : 0;

    // ── Mise en page (partagée par Update et Draw) ───────────────────────────────

    private Rectangle _panel, _grid, _trash, _palette;

    private void Layout()
    {
        var w = Context.VirtualResolution.X;
        var h = Context.VirtualResolution.Y;
        _panel = new Rectangle(20, 16, w - 40, h - 32);
        _targets.Clear();
        _focus.Clear();

        var x = _panel.X + Pad;
        var y = _panel.Y + Pad + 24;
        for (var i = 0; i < PvpProfile.BuildSlots; i++)
            AddTarget(new Rectangle(x + i * 128, y, 120, 28), Target.Tab, i);
        var cx = x + 3 * 128 + 24;
        AddTarget(new Rectangle(cx, y, 28, 28), Target.CommanderPrev, 0);
        AddTarget(new Rectangle(cx + 28 + 200, y, 28, 28), Target.CommanderNext, 0);

        y += 28 + Pad;
        _grid = new Rectangle(_panel.X + (_panel.Width - PvpBuild.Columns * CellSize - CellSize - Pad) / 2, y,
            PvpBuild.Columns * CellSize, 2 * CellSize);
        // Ligne de DEVANT en haut, ligne du FOND en bas (comme sur le plateau, le joueur est en bas).
        for (var line = 0; line < 2; line++)
            for (var c = 0; c < PvpBuild.Columns; c++)
            {
                var row = line == PvpBuild.FrontLine ? 0 : 1;
                var rect = new Rectangle(_grid.X + c * CellSize, _grid.Y + row * CellSize, CellSize, CellSize);
                AddTarget(rect, Target.Cell, line * PvpBuild.Columns + c);
                if (Build.SlotAt(c, line) is { EquipmentId: not null })
                    _targets.Add((new Rectangle(rect.Right - 22, rect.Y + 2, 20, 20), Target.Badge, line * PvpBuild.Columns + c));
            }
        _trash = new Rectangle(_grid.Right + Pad, _grid.Y, CellSize, _grid.Height);
        AddTarget(_trash, Target.Trash, 0);

        y = _grid.Bottom + Pad + 6;
        AddTarget(new Rectangle(x, y, 140, 26), Target.PaletteUnits, 0);
        AddTarget(new Rectangle(x + 148, y, 160, 26), Target.PaletteEquip, 0);
        AddTarget(new Rectangle(_panel.Right - Pad - 2 * 34, y, 30, 26), Target.PagePrev, 0);
        AddTarget(new Rectangle(_panel.Right - Pad - 30, y, 30, 26), Target.PageNext, 0);
        y += 26 + 8;
        _palette = new Rectangle(x, y, _panel.Width - 2 * Pad, _panel.Bottom - Pad - 30 - 8 - y);

        var count = _paletteEquip ? OwnedEquipment().Count : OwnedUnits().Count;
        var perPage = ItemsPerPage(_palette);
        _page = Math.Clamp(_page, 0, Math.Max(0, (count - 1) / perPage));
        var cellW = _paletteEquip ? EquipCell : ItemW;
        var cellH = _paletteEquip ? EquipCell : ItemH;
        var perRow = Math.Max(1, _palette.Width / cellW);
        for (var k = 0; k < perPage && _page * perPage + k < count; k++)
            AddTarget(new Rectangle(_palette.X + k % perRow * cellW, _palette.Y + k / perRow * cellH, cellW - 4, cellH - 4),
                Target.Item, _page * perPage + k);

        AddTarget(new Rectangle(_panel.X + Pad, _panel.Bottom - Pad - 28, 160, 28), Target.Back, 0);
    }

    private void AddTarget(Rectangle r, Target kind, int index)
    {
        _targets.Add((r, kind, index));
        _focus.Add(r);
    }

    // ── Mise à jour ──────────────────────────────────────────────────────────────

    public override void Update(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_deny > 0f) _deny -= dt;
        Layout();

        if (Context.Input.WasKeyPressed(Keys.Escape) || Context.Input.WasCancelPressed)
        {
            if (Holding) { ReturnHeld(); Context.Sounds.Play("unit_deselect"); }
            else Leave();
            return;
        }
        if (Context.Input.ScrollDelta != 0 && _palette.Contains(Context.Input.MousePosition))
            _page = Math.Max(0, _page - Math.Sign(Context.Input.ScrollDelta));

        _focus.Navigate(Context.Input);
        var pointer = _focus.Pointer(Context.Input);
        _hint = HintAt(pointer);

        if (!(Context.Input.UsingGamepad ? Context.Input.WasConfirmPressed : Context.Input.WasLeftClicked))
            return;
        // Les badges d'équipement passent avant la case qu'ils recouvrent.
        var hit = _targets.Where(t => t.Rect.Contains(pointer)).OrderByDescending(t => t.Kind == Target.Badge).FirstOrDefault();
        if (hit.Rect == Rectangle.Empty)
        {
            if (Holding) { ReturnHeld(); Context.Sounds.Play("unit_deselect"); }
            return;
        }
        OnActivate(hit.Kind, hit.Index);
    }

    private void OnActivate(Target kind, int index)
    {
        switch (kind)
        {
            case Target.Tab:
                ReturnHeld();
                _tab = index;
                Profile.SelectedBuild = index;
                Context.Sounds.Play("menu_click");
                break;
            case Target.CommanderPrev:
            case Target.CommanderNext:
            {
                var owned = OwnedCommanders();
                if (owned.Count == 0) break;
                var i = Math.Max(0, owned.FindIndex(c => c.Id == Build.CommanderId));
                i = (i + (kind == Target.CommanderNext ? 1 : owned.Count - 1)) % owned.Count;
                Build.CommanderId = owned[i].Id;
                Context.Sounds.Play("menu_click");
                Changed();
                break;
            }
            case Target.PaletteUnits:
                _paletteEquip = false;
                _page = 0;
                Context.Sounds.Play("menu_click");
                break;
            case Target.PaletteEquip:
                _paletteEquip = true;
                _page = 0;
                Context.Sounds.Play("menu_click");
                break;
            case Target.PagePrev:
                _page = Math.Max(0, _page - 1);
                break;
            case Target.PageNext:
                _page++;
                break;
            case Target.Item:
                ReturnHeld();
                if (_paletteEquip) _heldEquip = OwnedEquipment()[index].Id;
                else _heldUnit = OwnedUnits()[index].Asset;
                Context.Sounds.Play("unit_pick");
                break;
            case Target.Trash:
                if (Holding)
                {
                    _heldUnit = _heldEquip = _heldFromEquip = null;
                    _heldFrom = null;
                    Context.Sounds.Play("unit_deselect");
                    Changed();
                }
                break;
            case Target.Badge:
            {
                var (c, line) = (index % PvpBuild.Columns, index / PvpBuild.Columns);
                if (!Holding && Build.SlotAt(c, line) is { EquipmentId: { } eq } slot)
                {
                    Build.Set(c, line, slot.ClassAsset, null);   // l'équipement est repris en main
                    _heldEquip = eq;
                    Context.Sounds.Play("unit_pick");
                    Changed();
                }
                else
                    OnActivate(Target.Cell, index);
                break;
            }
            case Target.Cell:
                OnCell(index % PvpBuild.Columns, index / PvpBuild.Columns);
                break;
            case Target.Back:
                Leave();
                break;
        }
    }

    private void OnCell(int column, int line)
    {
        if (PvpBuild.IsKingCell(column, line))
            return;   // case du commandant : fixe
        var slot = Build.SlotAt(column, line);

        if (_heldEquip != null)
        {
            if (slot == null) return;
            var extra = CostOfEquip(_heldEquip) - CostOfEquip(slot.EquipmentId);
            if (extra > Remaining) { Deny(); return; }
            var previous = slot.EquipmentId;
            Build.Set(column, line, slot.ClassAsset, _heldEquip);
            _heldEquip = previous;   // l'ancien équipement passe en main (à reposer ou à jeter)
            Context.Sounds.Play("unit_place");
            Changed();
            return;
        }

        if (_heldUnit != null)
        {
            // Pion venu de la palette : il coûte ; venu d'une case : son coût est déjà hors du build.
            var cost = CostOfUnit(_heldUnit) + CostOfEquip(_heldFromEquip)
                       - (slot == null ? 0 : CostOfUnit(slot.ClassAsset) + CostOfEquip(slot.EquipmentId));
            if (cost > Remaining) { Deny(); return; }
            Build.Set(column, line, _heldUnit, _heldFromEquip);
            // Échange : l'occupant prend la case d'origine du pion tenu (s'il venait d'une case).
            if (slot != null && _heldFrom is { } from)
                Build.Set(from.Column, from.Line, slot.ClassAsset, slot.EquipmentId);
            _heldUnit = _heldFromEquip = null;
            _heldFrom = null;
            Context.Sounds.Play("unit_place");
            Changed();
            return;
        }

        if (slot != null)
        {
            _heldUnit = slot.ClassAsset;
            _heldFromEquip = slot.EquipmentId;
            _heldFrom = (column, line);
            Build.Remove(column, line);
            Context.Sounds.Play("unit_pick");
            Changed();
        }
    }

    /// <summary>Annule la prise en main : un pion pris sur une case y retourne ; un élément de palette est lâché.</summary>
    private void ReturnHeld()
    {
        if (_heldUnit != null && _heldFrom is { } from && Build.SlotAt(from.Column, from.Line) == null)
            Build.Set(from.Column, from.Line, _heldUnit, _heldFromEquip);
        _heldUnit = _heldEquip = _heldFromEquip = null;
        _heldFrom = null;
    }

    private void Deny()
    {
        _deny = 0.35f;
        Context.Sounds.Play("menu_close");
    }

    private void Changed() => PvpProfileStore.Save();

    private void Leave()
    {
        ReturnHeld();
        PvpProfileStore.Save();
        Context.Sounds.Play("menu_close");
        Context.Scenes.Change(new PvpLobbyScene(Context));
    }

    private string HintAt(Point p)
    {
        foreach (var (rect, kind, index) in _targets)
        {
            if (!rect.Contains(p))
                continue;
            switch (kind)
            {
                case Target.Item when !_paletteEquip:
                    var u = OwnedUnits()[index];
                    return $"{PvpUi.UnitName(u)}  T{u.Tier}  {Loc.T("pvp.cost", PvpConfig.Current.CostOf(u))}";
                case Target.Item:
                    var e = OwnedEquipment()[index];
                    return $"{EquipmentNames.Localized(e).ToUpperInvariant()}  {Loc.T("pvp.cost", PvpConfig.Current.CostOf(e))}";
                case Target.Cell:
                    var (c, line) = (index % PvpBuild.Columns, index / PvpBuild.Columns);
                    if (PvpBuild.IsKingCell(c, line))
                        return PvpUi.CommanderName(Build.CommanderId);
                    if (Build.SlotAt(c, line) is { } s && PvpCatalog.FindClass(s.ClassAsset) is { } cls)
                        return PvpUi.UnitName(cls.Class) + (s.EquipmentId != null && Equipments.ById(s.EquipmentId) is { } eq
                            ? "  +  " + EquipmentNames.Localized(eq).ToUpperInvariant() : "");
                    break;
                case Target.Trash:
                    return Loc.T("pvp.trash");
            }
        }
        return "";
    }

    // ── Rendu ────────────────────────────────────────────────────────────────────

    private Texture2D? Icon(Equipment e)
    {
        if (_icons.TryGetValue(e.Icon, out var tex))
            return tex;
        tex = Textures.LoadPngOrNull(Context.GraphicsDevice, Path.Combine(AppContext.BaseDirectory, $"Assets/Equipment/{e.Icon}.png"));
        _icons[e.Icon] = tex;
        return tex;
    }

    public override void Draw(GameTime gameTime)
    {
        Layout();
        var sb = Context.SpriteBatch;
        var font = Context.Font;
        var w = Context.VirtualResolution.X;
        var h = Context.VirtualResolution.Y;
        var gp = Context.Input.UsingGamepad;
        var pointer = _focus.Pointer(Context.Input);

        sb.Begin(samplerState: SamplerState.PointClamp);
        _backdrop.Draw(sb, w, h);
        Context.Style.DrawPanel(sb, _panel);
        font.DrawCentered(sb, Loc.T("pvp.armies"), new Rectangle(_panel.X, _panel.Y + 8, _panel.Width, 14), 2, Palette.Yellow2);

        var focusIndex = 0;
        foreach (var (rect, kind, index) in _targets)
        {
            var focused = gp && _focus.IsFocused(focusIndex);
            if (kind != Target.Badge)
                focusIndex++;
            switch (kind)
            {
                case Target.Tab:
                    Context.Style.DrawButton(sb, rect, UiStyle.StateOf(focused || (!gp && rect.Contains(pointer)), false), index == _tab);
                    font.DrawCentered(sb, Loc.T("pvp.army_n", index + 1), rect, 1, index == _tab ? Palette.Yellow2 : Palette.White);
                    break;
                case Target.CommanderPrev:
                    PvpUi.Button(Context, sb, rect, "<", focused);
                    font.DrawCentered(sb, PvpUi.CommanderName(Build.CommanderId), new Rectangle(rect.Right, rect.Y, 200, rect.Height), 1, Palette.Yellow2);
                    break;
                case Target.CommanderNext:
                    PvpUi.Button(Context, sb, rect, ">", focused);
                    break;
                case Target.Cell:
                    DrawCell(sb, rect, index % PvpBuild.Columns, index / PvpBuild.Columns, focused);
                    break;
                case Target.Badge:
                    break;
                case Target.Trash:
                    sb.Draw(Context.Pixel, rect, (Holding ? Palette.Purple5 : Palette.Black1) * (focused || rect.Contains(pointer) ? 0.6f : 0.35f));
                    font.DrawCentered(sb, Loc.T("pvp.trash"), rect, 1, Palette.White);
                    break;
                case Target.PaletteUnits:
                    PvpUi.Button(Context, sb, rect, Loc.T("pvp.palette_units"), focused || !_paletteEquip);
                    break;
                case Target.PaletteEquip:
                    PvpUi.Button(Context, sb, rect, Loc.T("pvp.palette_equipment"), focused || _paletteEquip);
                    break;
                case Target.PagePrev:
                    PvpUi.Button(Context, sb, rect, "<", focused);
                    break;
                case Target.PageNext:
                    PvpUi.Button(Context, sb, rect, ">", focused);
                    break;
                case Target.Item:
                    DrawItem(sb, rect, index, focused || (!gp && rect.Contains(pointer)));
                    break;
                case Target.Back:
                    PvpUi.Button(Context, sb, rect, (gp ? "(B) " : "") + Loc.T("pvp.back"), focused);
                    break;
            }
        }

        // Budget (tremble quand un ajout est refusé).
        var shake = _deny > 0f ? (int)(MathF.Sin(_deny * 80f) * 3f) : 0;
        var budget = Loc.T("pvp.budget", PvpCatalog.Cost(Build), PvpConfig.Current.Budget);
        font.DrawCentered(sb, budget, new Rectangle(_panel.Right - Pad - 220 + shake, _panel.Y + 8, 220, 14), 2,
            _deny > 0f || Remaining < 0 ? Palette.Purple5 : Palette.White);

        if (_hint.Length > 0)
            font.DrawCentered(sb, _hint, new Rectangle(_panel.X, _panel.Bottom - Pad - 24, _panel.Width, 14), 1, Palette.Blue1);

        // Élément tenu : suit le pointeur.
        if (_heldUnit != null && PvpCatalog.FindClass(_heldUnit) is { } held)
            _units.DrawScaled(sb, held.Class, pointer, 1, Color.White * 0.85f);
        else if (_heldEquip != null && Equipments.ById(_heldEquip) is { } he && Icon(he) is { } it)
            sb.Draw(it, new Rectangle(pointer.X - it.Width / 2, pointer.Y - it.Height / 2, it.Width, it.Height), Color.White);
        sb.End();
    }

    private void DrawCell(SpriteBatch sb, Rectangle rect, int column, int line, bool focused)
    {
        var dark = (column + line) % 2 == 0;
        sb.Draw(Context.Pixel, rect, (dark ? Palette.Green2 : Palette.Green1) * 0.9f);
        if (focused)
            DrawBorder(sb, rect, Palette.Yellow2);
        if (PvpBuild.IsKingCell(column, line))
        {
            if (PvpCatalog.CommanderClass(Build.CommanderId) is { } king)
                _units.DrawScaled(sb, king.Class, rect.Center, 1);
            DrawBorder(sb, rect, Palette.Yellow1);
            return;
        }
        if (Build.SlotAt(column, line) is not { } slot || PvpCatalog.FindClass(slot.ClassAsset) is not { } cls)
            return;
        _units.DrawScaled(sb, cls.Class, rect.Center, 1);
        if (slot.EquipmentId != null && Equipments.ById(slot.EquipmentId) is { } eq && Icon(eq) is { } icon)
            sb.Draw(icon, new Rectangle(rect.Right - 22, rect.Y + 2, 20, 20), new Rectangle(6, 6, 20, 20), Color.White);
    }

    private void DrawItem(SpriteBatch sb, Rectangle rect, int index, bool hover)
    {
        sb.Draw(Context.Pixel, rect, Palette.Black1 * (hover ? 0.55f : 0.3f));
        if (_paletteEquip)
        {
            var e = OwnedEquipment()[index];
            if (Icon(e) is { } icon)
                sb.Draw(icon, new Rectangle(rect.Center.X - icon.Width / 2, rect.Center.Y - icon.Height / 2, icon.Width, icon.Height), Color.White);
            return;
        }
        var u = OwnedUnits()[index];
        _units.DrawScaled(sb, u, new Point(rect.Center.X, rect.Y + 34), 1);
        Context.Font.DrawCentered(sb, PvpConfig.Current.CostOf(u).ToString(), new Rectangle(rect.X, rect.Bottom - 12, rect.Width, 7), 1, Palette.Yellow2);
    }

    private void DrawBorder(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(Context.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(Context.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(Context.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(Context.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }

    public override void DrawLetterboxBackground(Point realScreen, Point canvasOffset, int canvasScale) =>
        _backdrop.DrawBands(Context.SpriteBatch, realScreen, canvasOffset, canvasScale, Context.VirtualResolution);
}
