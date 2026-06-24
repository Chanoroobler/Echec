using Echec.Core.Campaign;
using Echec.Engine;
using Echec.Engine.Input;
using Echec.Engine.Persistence;
using Echec.Engine.Scenes;
using Echec.Engine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Echec.Game.Scenes;

/// <summary>
/// Menu principal : titre + <see cref="SaveService.SlotCount"/> slots de sauvegarde (continuer une
/// run ou en démarrer une), accès aux Options (réutilise le panneau du menu pause) et Quitter.
/// Un slot occupé peut être effacé (avec confirmation) pour libérer la place d'une nouvelle partie.
/// </summary>
public sealed class MainMenuScene : Scene
{
    // Mise en page (espace virtuel, px).
    private const int ColW = 380;
    private const int Pad = 16;
    private const int RowH = 52;
    private const int BtnH = 36;
    private const int Gap = 12;
    private const int DelW = 36;

    private PauseMenu _menu = null!;
    private PauseMenuRenderer _menuRenderer = null!;

    // État des 3 slots (null = vide), relu au chargement et après chaque effacement.
    private readonly RunSave?[] _slots = new RunSave?[SaveService.SlotCount];

    // Index du slot dont on demande confirmation d'effacement (-1 = aucune confirmation en cours).
    private int _confirmDelete = -1;

    // Navigation manette : focus dans la liste racine [slots…, Options, Quitter] et dans la confirmation.
    private int _focus;
    private bool _confirmYes;   // focus de la boîte de confirmation : true = EFFACER, false = ANNULER

    public MainMenuScene(GameContext context) : base(context) { }

    public override void Load()
    {
        var native = Context.GraphicsDevice.Adapter.CurrentDisplayMode;
        _menu = new PauseMenu(Context.Settings, new Point(native.Width, native.Height));
        _menuRenderer = new PauseMenuRenderer(Context.Pixel, Context.Font, Context.Style);
        RefreshSlots();
    }

    private void RefreshSlots()
    {
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = Context.Saves.LoadSlot(i);
    }

    // ── Mise à jour ─────────────────────────────────────────────────────────────
    public override void Update(GameTime gameTime)
    {
        if (_menu.IsOpen) { UpdateOptions(); return; }
        if (_confirmDelete >= 0) { UpdateConfirm(); return; }

        var w = Context.VirtualResolution.X;
        var h = Context.VirtualResolution.Y;
        var lay = BuildLayout(w, h);
        var count = _slots.Length + 2;   // slots… + Options + Quitter
        _focus = System.Math.Clamp(_focus, 0, count - 1);

        // Manette : navigation haut/bas, A valide, X efface un slot occupé.
        if (Context.Input.Nav(NavDir.Up)) { _focus = (_focus - 1 + count) % count; Context.Sounds.Play("menu_click"); }
        if (Context.Input.Nav(NavDir.Down)) { _focus = (_focus + 1) % count; Context.Sounds.Play("menu_click"); }
        if (Context.Input.WasConfirmPressed) { ActivateFocus(); return; }
        if (Context.Input.WasTertiaryPressed && _focus < _slots.Length && _slots[_focus] != null)
        {
            _confirmDelete = _focus; _confirmYes = false; Context.Sounds.Play("menu_click"); return;
        }

        // Souris : clic direct.
        if (!Context.Input.WasLeftClicked)
            return;

        var p = Context.Input.MousePosition;
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i] != null && lay.Dels[i].Contains(p))
            {
                _confirmDelete = i; _confirmYes = false;
                Context.Sounds.Play("menu_click");
                return;
            }
            if (lay.Slots[i].Contains(p))
            {
                Context.Sounds.Play("menu_click");
                StartSlot(i);
                return;
            }
        }

        if (lay.Options.Contains(p)) { _menu.OpenOptions(); Context.Sounds.Play("menu_open"); }
        else if (lay.Quit.Contains(p)) { Context.Sounds.Play("menu_click"); Context.Quit(); }
    }

    /// <summary>Active l'élément racine sous le focus (slot → démarrer, Options, Quitter).</summary>
    private void ActivateFocus()
    {
        if (_focus < _slots.Length) { Context.Sounds.Play("menu_click"); StartSlot(_focus); }
        else if (_focus == _slots.Length) { _menu.OpenOptions(); Context.Sounds.Play("menu_open"); }
        else { Context.Sounds.Play("menu_click"); Context.Quit(); }
    }

    /// <summary>Rectangle de l'élément racine focus (surbrillance / pointeur synthétique manette).</summary>
    private Rectangle FocusedRect(MenuLayout lay)
    {
        if (_focus < _slots.Length) return lay.Slots[_focus];
        return _focus == _slots.Length ? lay.Options : lay.Quit;
    }

    /// <summary>Continue la run du slot si elle existe, sinon en démarre une nouvelle dans ce slot.</summary>
    private void StartSlot(int index)
    {
        var run = _slots[index]?.ToRun();   // null → GameplayScene crée une nouvelle campagne
        Context.Scenes.Change(new GameplayScene(Context, index, run));
    }

    private void UpdateOptions()
    {
        if (Context.Input.WasKeyPressed(Keys.Escape) || Context.Input.WasCancelPressed) { CloseOptions(); return; }

        var action = MenuAction.None;

        // Manette : navigation au focus + réglages.
        if (Context.Input.Nav(NavDir.Up)) { _menu.MoveFocus(-1); Context.Sounds.Play("menu_click"); }
        if (Context.Input.Nav(NavDir.Down)) { _menu.MoveFocus(+1); Context.Sounds.Play("menu_click"); }
        if (Context.Input.Nav(NavDir.Left)) action = _menu.AdjustFocused(-1);
        if (Context.Input.Nav(NavDir.Right)) action = _menu.AdjustFocused(+1);
        if (Context.Input.WasConfirmPressed) { Context.Sounds.Play("menu_click"); action = _menu.ActivateFocused(); }

        // Souris : clic direct.
        if (Context.Input.WasLeftClicked)
        {
            Context.Sounds.Play("menu_click");
            action = _menu.HandleClick(Context.Input.MousePosition, Context.VirtualResolution.X, Context.VirtualResolution.Y);
        }

        switch (action)
        {
            case MenuAction.GraphicsChanged:
                Context.Display.Apply(Context.Settings.Display);
                Context.Saves.SaveSettings(Context.Settings);
                break;
            case MenuAction.VolumeChanged:
                Context.Audio.Apply();
                Context.Saves.SaveSettings(Context.Settings);
                break;
        }

        // « Retour » depuis Options ramène à la racine du menu pause : ici, la racine = le menu
        // principal lui-même, donc on referme simplement l'overlay.
        if (_menu.Panel == MenuPanel.Root)
            CloseOptions();
    }

    private void CloseOptions() { _menu.Close(); Context.Sounds.Play("menu_close"); }

    private void UpdateConfirm()
    {
        if (Context.Input.WasKeyPressed(Keys.Escape) || Context.Input.WasRightClicked || Context.Input.WasCancelPressed)
        {
            _confirmDelete = -1;
            return;
        }

        // Manette : gauche/droite choisit EFFACER/ANNULER, A valide.
        if (Context.Input.Nav(NavDir.Left)) _confirmYes = true;
        if (Context.Input.Nav(NavDir.Right)) _confirmYes = false;
        if (Context.Input.WasConfirmPressed) { ConfirmDelete(_confirmYes); return; }

        if (!Context.Input.WasLeftClicked)
            return;

        var (_, yes, no) = ConfirmLayout(Context.VirtualResolution.X, Context.VirtualResolution.Y);
        var p = Context.Input.MousePosition;
        if (yes.Contains(p)) ConfirmDelete(true);
        else if (no.Contains(p)) ConfirmDelete(false);
    }

    private void ConfirmDelete(bool erase)
    {
        if (erase)
        {
            Context.Saves.DeleteSlot(_confirmDelete);
            RefreshSlots();
        }
        _confirmDelete = -1;
        _focus = System.Math.Clamp(_focus, 0, _slots.Length + 1);
        Context.Sounds.Play("menu_click");
    }

    // ── Rendu ───────────────────────────────────────────────────────────────────
    public override void Draw(GameTime gameTime)
    {
        var sb = Context.SpriteBatch;
        var w = Context.VirtualResolution.X;
        var h = Context.VirtualResolution.Y;
        var lay = BuildLayout(w, h);

        var mouse = Context.Input.MousePosition;
        var mouseDown = Context.Input.IsLeftDown;
        var gp = Context.Input.UsingGamepad;

        // Quand un overlay est actif (Options / confirmation), l'arrière-plan ne doit PAS réagir au
        // survol — sinon les boutons s'allument à travers l'overlay semi-transparent (fausse navigation).
        // L'overlay lui-même, en revanche, utilise la vraie position souris. En manette : pointeur
        // synthétique = centre de l'élément focus (réutilise la surbrillance de survol).
        var overlay = _menu.IsOpen || _confirmDelete >= 0;
        var bgPointer = overlay ? new Point(int.MinValue, int.MinValue)
            : (gp ? FocusedRect(lay).Center : mouse);
        var bgDown = !overlay && !gp && mouseDown;

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(Context.Pixel, new Rectangle(0, 0, w, h), Palette.Navy2);

        var titleArea = new Rectangle(0, lay.Panel.Y - 64, w, 44);
        Context.Font.DrawCentered(sb, "ECHEC", titleArea, 4, Palette.Yellow2);

        Context.Style.DrawPanel(sb, lay.Panel);
        for (var i = 0; i < _slots.Length; i++)
            DrawSlot(sb, lay.Slots[i], lay.Dels[i], i, bgPointer, bgDown);

        Button(sb, lay.Options, "OPTIONS", bgPointer, bgDown);
        Button(sb, lay.Quit, "QUITTER", bgPointer, bgDown);
        sb.End();

        if (_confirmDelete >= 0)
        {
            var (_, yes, no) = ConfirmLayout(w, h);
            var pointer = gp ? (_confirmYes ? yes.Center : no.Center) : mouse;
            DrawConfirm(sb, w, h, pointer, !gp && mouseDown);
        }
        else if (_menu.IsOpen)
        {
            var focusRect = _menu.FocusedRect(w, h);
            var pointer = gp ? focusRect.Center.ToVector2() : mouse.ToVector2();
            sb.Begin(samplerState: SamplerState.PointClamp);
            _menuRenderer.Draw(sb, _menu, w, h, pointer, !gp && mouseDown, gp ? focusRect : null);
            sb.End();
        }
    }

    private void DrawSlot(SpriteBatch sb, Rectangle main, Rectangle del, int index, Point pointer, bool down)
    {
        var occupied = _slots[index] != null;

        var dy = Context.Style.DrawButton(sb, main, UiStyle.StateOf(main.Contains(pointer), down));
        var x = main.X + 12;
        var line = main.Y + 9 + dy;
        Context.Font.Draw(sb, $"SLOT {index + 1}", new Vector2(x, line), 1, Palette.White);

        var sub = occupied
            ? $"COMBAT {_slots[index]!.CombatNumber}/{Run.TotalCombats}   {_slots[index]!.UnitCount} UNITES"
            : "NOUVELLE PARTIE";
        Context.Font.Draw(sb, sub, new Vector2(x, line + Context.Font.LineHeight() + 6),
            1, occupied ? Palette.Blue1 : Palette.Yellow2);

        if (occupied)
            Button(sb, del, "X", pointer, down);
    }

    private void Button(SpriteBatch sb, Rectangle r, string label, Point pointer, bool down)
    {
        var dy = Context.Style.DrawButton(sb, r, UiStyle.StateOf(r.Contains(pointer), down));
        var area = r; area.Offset(0, dy);
        Context.Font.DrawCentered(sb, label, area, 1, Palette.White);
    }

    private void DrawConfirm(SpriteBatch sb, int w, int h, Point pointer, bool down)
    {
        var (panel, yes, no) = ConfirmLayout(w, h);

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(Context.Pixel, new Rectangle(0, 0, w, h), Palette.Navy2 * 0.85f);
        Context.Style.DrawPanel(sb, panel);

        var textArea = new Rectangle(panel.X, panel.Y + Pad, panel.Width, 16);
        Context.Font.DrawCentered(sb, $"EFFACER LE SLOT {_confirmDelete + 1} ?", textArea, 1, Palette.White);

        Button(sb, yes, "EFFACER", pointer, down);
        Button(sb, no, "ANNULER", pointer, down);
        sb.End();
    }

    // ── Mise en page ─────────────────────────────────────────────────────────────
    private MenuLayout BuildLayout(int w, int h)
    {
        var panelW = ColW + 2 * Pad;
        var panelH = Pad + _slots.Length * (RowH + Gap) + (BtnH + Gap) + BtnH + Pad;
        var panelX = (w - panelW) / 2;
        var panelY = (h - panelH) / 2 + 28;   // décalé vers le bas pour laisser la place au titre

        var lay = new MenuLayout
        {
            Panel = new Rectangle(panelX, panelY, panelW, panelH),
            Slots = new Rectangle[_slots.Length],
            Dels = new Rectangle[_slots.Length],
        };

        var x = panelX + Pad;
        var y = panelY + Pad;
        for (var i = 0; i < _slots.Length; i++)
        {
            var occupied = _slots[i] != null;
            var mainW = occupied ? ColW - DelW - 8 : ColW;
            lay.Slots[i] = new Rectangle(x, y, mainW, RowH);
            lay.Dels[i] = occupied ? new Rectangle(x + ColW - DelW, y, DelW, RowH) : Rectangle.Empty;
            y += RowH + Gap;
        }

        lay.Options = new Rectangle(x, y, ColW, BtnH); y += BtnH + Gap;
        lay.Quit = new Rectangle(x, y, ColW, BtnH);
        return lay;
    }

    private static (Rectangle panel, Rectangle yes, Rectangle no) ConfirmLayout(int w, int h)
    {
        const int cw = 320, ch = 116, pad = 16, btnH = 34, gap = 12;
        var panel = new Rectangle((w - cw) / 2, (h - ch) / 2, cw, ch);
        var btnW = (cw - 2 * pad - gap) / 2;
        var by = panel.Bottom - pad - btnH;
        var yes = new Rectangle(panel.X + pad, by, btnW, btnH);
        var no = new Rectangle(panel.Right - pad - btnW, by, btnW, btnH);
        return (panel, yes, no);
    }

    private struct MenuLayout
    {
        public Rectangle Panel;
        public Rectangle[] Slots;
        public Rectangle[] Dels;
        public Rectangle Options;
        public Rectangle Quit;
    }
}
