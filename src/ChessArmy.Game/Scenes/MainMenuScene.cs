using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Campaign;
using ChessArmy.Engine;
using ChessArmy.Engine.Audio;
using ChessArmy.Game.UI;
using ChessArmy.Engine.Input;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Persistence;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.Scenes;
using ChessArmy.Engine.UI;
using ChessArmy.Engine.UI.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.Scenes;

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

    // Décor : pions découverts posés autour du panneau, animés d'un léger va-et-vient vertical.
    private const int DecorSlots = 5;       // au plus 5 emplacements (arc centré sur le panneau)
    private const int DecorDrop = 30;       // descend toute la lignée de pions de ce nombre de px
    private const float DecorBobAmp = 5f;   // amplitude du va-et-vient (px)
    private const float DecorBobSpeed = 1.6f;

    // Titre : plus imposant qu'avant, en dégradé jaune TRAMÉ (crème clair en haut → or ambré en bas).
    private const int TitleScale = 6;
    private const int DemoScale = 2;   // mention DÉMO sous le titre (même dégradé, plus petite)
    private static readonly Color[] TitleRamp = { Palette.Yellow2, Palette.Yellow1, Palette.Brown2 };

    private PauseMenu _menu = null!;
    private PauseMenuRenderer _menuRenderer = null!;

    /// <summary>Codex (bestiaire des pions + équipements), ouvert en overlay depuis le menu principal.</summary>
    private CodexView _codex = null!;

    /// <summary>Rendu partagé des sprites de pions (cache de textures), pour le décor animé du fond.</summary>
    private UnitCardRenderer _units = null!;

    /// <summary>
    /// Fond du menu : dégradé vertical tramé (dithering) régénéré si la taille du canevas change (le joueur
    /// peut changer de résolution depuis les Options). Disposé au <see cref="Unload"/>.
    /// </summary>
    private Texture2D? _background;

    /// <summary>
    /// Pions du décor : tirés AU HASARD parmi ceux déjà découverts par le joueur (méta-progression), une
    /// nouvelle sélection à chaque venue au menu. Vide tant que rien n'a été découvert (le fond reste nu).
    /// </summary>
    private readonly List<UnitClass> _decorUnits = new();

    /// <summary>Horloge du va-et-vient du décor (avance en continu, même sous un overlay semi-transparent).</summary>
    private float _decorTime;

    // État des 3 slots (null = vide), relu au chargement et après chaque effacement.
    private readonly RunSave?[] _slots = new RunSave?[SaveService.SlotCount];

    // Index du slot dont on demande confirmation d'effacement (-1 = aucune confirmation en cours).
    private int _confirmDelete = -1;

    // Confirmation d'effacement de la MÉTA-PROGRESSION (par-dessus le panneau d'options).
    private bool _confirmMetaReset;

    // Navigation manette : focus dans la liste racine [slots…, Options, Quitter] et dans la confirmation.
    private int _focus;
    private bool _confirmYes;   // focus de la boîte de confirmation : true = EFFACER, false = ANNULER

    public MainMenuScene(GameContext context) : base(context) { }

    public override void Load()
    {
        var native = Context.GraphicsDevice.Adapter.CurrentDisplayMode;
        _menu = new PauseMenu(Context.Settings, new Point(native.Width, native.Height));
        _menuRenderer = new PauseMenuRenderer(Context.Pixel, Context.Style);
        _codex = new CodexView(Context);
        _units = new UnitCardRenderer(Context);
        RefreshSlots();
        SelectDecor();
        Context.Music.Play(MusicScene.Calm);   // menu principal : piste « Relaxed » (continue dans le placement)
    }

    public override void Unload()
    {
        _codex.Unload();
        _units.Unload();
        _background?.Dispose();
        _background = null;
    }

    /// <summary>
    /// (Re)génère le fond dégradé si absent ou si le canevas a changé de taille. Paliers du HAUT vers le BAS :
    /// vert-nuit un peu plus clair en haut (derrière la lignée de pions et le titre), fondu vers le presque
    /// noir en bas pour asseoir le panneau.
    /// </summary>
    private void EnsureBackground(int w, int h)
    {
        if (_background != null && _background.Width == w && _background.Height == h)
            return;
        _background?.Dispose();
        _background = Textures.CreateVerticalDitherGradient(Context.GraphicsDevice, w, h,
            Palette.Black4, Palette.Navy2, Palette.Black1);
    }

    /// <summary>
    /// Choisit les pions du décor, mélangés, au plus <see cref="DecorSlots"/> :
    /// <list type="bullet">
    /// <item>les classes de pions DÉCOUVERTES (tous domaines, tous tiers) ;</item>
    /// <item>les COMMANDANTS jouables DÉBLOQUÉS (leur propre méta-progression, pas les « découvertes »).</item>
    /// </list>
    /// Le commandant de base étant toujours débloqué, la lignée n'est jamais vide — et un filet de sécurité le
    /// garantit même sous une config exotique. Doublons d'asset écartés.
    /// </summary>
    private void SelectDecor()
    {
        _decorUnits.Clear();
        var seen = new HashSet<string>();
        var pool = new List<UnitClass>();

        void TryAdd(UnitClass c)
        {
            if (seen.Add(c.Asset))
                pool.Add(c);
        }

        // Pions découverts.
        var classes = new List<UnitClass>();
        foreach (var d in Domaines.All)
            Flatten(d.BaseClass, classes);
        foreach (var c in classes)
            if (Context.Saves.IsUnitDiscovered(c.Asset))
                TryAdd(c);

        // Commandants jouables débloqués (le commandant de base l'est toujours via StartsUnlocked).
        foreach (var cmd in Commandes.Playable)
            if (cmd.StartsUnlocked || Context.Saves.IsCommanderUnlocked(cmd.Id))
                TryAdd(cmd.BaseClass);

        // Filet de sécurité : au moins le commandant de base, jamais un fond nu.
        if (pool.Count == 0)
            TryAdd(Commandes.Commander.BaseClass);

        // Mélange de Fisher-Yates : une composition différente à chaque retour au menu.
        var rng = new Random();
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        _decorUnits.AddRange(pool.Take(DecorSlots));
    }

    private static void Flatten(UnitClass c, List<UnitClass> acc)
    {
        acc.Add(c);
        foreach (var e in c.Evolutions)
            Flatten(e, acc);
    }

    private void RefreshSlots()
    {
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = Context.Saves.LoadSlot(i);
    }

    // ── Mise à jour ─────────────────────────────────────────────────────────────
    public override void Update(GameTime gameTime)
    {
        // Le va-et-vient du décor tourne en continu (même derrière un overlay semi-transparent).
        _decorTime += (float)gameTime.ElapsedGameTime.TotalSeconds;

        if (_codex.IsOpen)   // overlay par-dessus le menu : capte tout jusqu'à sa fermeture
        {
            _codex.Update(new Viewport(0, 0, Context.VirtualResolution.X, Context.VirtualResolution.Y),
                (float)gameTime.ElapsedGameTime.TotalSeconds);
            return;
        }
        if (_confirmMetaReset) { UpdateMetaConfirm(); return; }   // par-dessus les options
        if (_menu.IsOpen) { UpdateOptions(); return; }
        if (_confirmDelete >= 0) { UpdateConfirm(); return; }

        var w = Context.VirtualResolution.X;
        var h = Context.VirtualResolution.Y;
        var lay = BuildLayout(w, h);
        var count = _slots.Length + (Context.Settings.IsDemo ? 4 : 3);   // slots… + Codex + Options + Quitter (+ Wishlist en démo)
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

        if (lay.Codex.Contains(p)) { _codex.Open(); }
        else if (lay.Options.Contains(p)) { _menu.OpenOptions(); Context.Sounds.Play("menu_open"); }
        else if (lay.Quit.Contains(p)) { Context.Sounds.Play("menu_click"); Context.Quit(); }
        else if (Context.Settings.IsDemo && lay.Wishlist.Contains(p)) { Context.Sounds.Play("menu_click"); Store.OpenWishlist(); }
    }

    /// <summary>Active l'élément racine sous le focus (slot → démarrer, Options, Quitter).</summary>
    private void ActivateFocus()
    {
        if (_focus < _slots.Length) { Context.Sounds.Play("menu_click"); StartSlot(_focus); }
        else if (_focus == _slots.Length) { _codex.Open(); }
        else if (_focus == _slots.Length + 1) { _menu.OpenOptions(); Context.Sounds.Play("menu_open"); }
        else if (_focus == _slots.Length + 2) { Context.Sounds.Play("menu_click"); Context.Quit(); }
        else { Context.Sounds.Play("menu_click"); Store.OpenWishlist(); }   // wishlist (démo seulement)
    }

    /// <summary>Rectangle de l'élément racine focus (surbrillance / pointeur synthétique manette).</summary>
    private Rectangle FocusedRect(MenuLayout lay)
    {
        if (_focus < _slots.Length) return lay.Slots[_focus];
        if (_focus == _slots.Length) return lay.Codex;
        if (_focus == _slots.Length + 1) return lay.Options;
        return _focus == _slots.Length + 2 ? lay.Quit : lay.Wishlist;
    }

    /// <summary>
    /// Continue la run du slot si elle existe, sinon en démarre une nouvelle. Une NOUVELLE partie passe
    /// d'abord par l'écran de sélection du commandant (commandant + difficulté, figés pour toute la run) ;
    /// une reprise va directement au jeu, ces deux choix venant alors de la sauvegarde.
    /// </summary>
    private void StartSlot(int index)
    {
        if (_slots[index]?.ToRun() is { } run)
            Context.Scenes.Change(new GameplayScene(Context, index, run));
        else
            Context.Scenes.Change(new CommanderSelectScene(Context, index));
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
            var mp = Context.Input.MousePosition;
            // Bouton « effacer les découvertes » (méta-progression) : ouvre une confirmation explicative.
            if (_menu.Panel == MenuPanel.Options
                && MetaResetRect(Context.VirtualResolution.X, Context.VirtualResolution.Y).Contains(mp))
            {
                _confirmMetaReset = true; _confirmYes = false;
                Context.Sounds.Play("menu_click");
                return;
            }
            Context.Sounds.Play("menu_click");
            action = _menu.HandleClick(mp, Context.VirtualResolution.X, Context.VirtualResolution.Y);
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
            case MenuAction.LanguageChanged:
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

    private void UpdateMetaConfirm()
    {
        if (Context.Input.WasKeyPressed(Keys.Escape) || Context.Input.WasRightClicked || Context.Input.WasCancelPressed)
        {
            _confirmMetaReset = false;
            return;
        }

        if (Context.Input.Nav(NavDir.Left)) _confirmYes = true;
        if (Context.Input.Nav(NavDir.Right)) _confirmYes = false;
        if (Context.Input.WasConfirmPressed) { ApplyMetaConfirm(_confirmYes); return; }

        if (!Context.Input.WasLeftClicked)
            return;

        var (_, yes, no) = MetaConfirmLayout(Context.VirtualResolution.X, Context.VirtualResolution.Y);
        var p = Context.Input.MousePosition;
        if (yes.Contains(p)) ApplyMetaConfirm(true);
        else if (no.Contains(p)) ApplyMetaConfirm(false);
    }

    private void ApplyMetaConfirm(bool erase)
    {
        if (erase)
            Context.Saves.ResetMetaProgression();
        _confirmMetaReset = false;
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
        var overlay = _menu.IsOpen || _confirmDelete >= 0 || _codex.IsOpen;
        var bgPointer = overlay ? new Point(int.MinValue, int.MinValue)
            : (gp ? FocusedRect(lay).Center : mouse);
        var bgDown = !overlay && !gp && mouseDown;

        EnsureBackground(w, h);

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_background!, new Rectangle(0, 0, w, h), Color.White);   // EnsureBackground vient de le garantir

        // Lignée de pions du décor, DERRIÈRE le titre et le panneau (posée sur le fond, non interactive).
        DrawDecor(sb, lay.Panel);

        // Le logo est toujours le latin CHESS ARMY : on force la PixelFont (dégradé doré tramé) dans TOUTES
        // les langues, y compris le chinois, pour garder l'identité de marque (la police CJK n'a pas le dégradé).
        var titleArea = new Rectangle(0, 20, w, Fonts.Pixel.GlyphHeight * TitleScale + 12);
        Fonts.Pixel.DrawCenteredGradient(sb, Loc.T("game.title"), titleArea, TitleScale, TitleRamp);

        // Version DÉMO : même style doré tramé que le logo, aligné à DROITE sous le titre (bord droit du « ARMY »).
        if (Context.Settings.IsDemo)
        {
            var demo = Loc.T("menu.demo");
            var titleRight = (w + Fonts.Pixel.Measure(Loc.T("game.title"), TitleScale)) / 2;
            var demoPos = new Vector2(titleRight - Context.Font.Measure(demo, DemoScale), titleArea.Bottom - 4);
            Context.Font.DrawGradient(sb, demo, demoPos, DemoScale, TitleRamp);
        }

        Context.Style.DrawPanel(sb, lay.Panel);
        for (var i = 0; i < _slots.Length; i++)
            DrawSlot(sb, lay.Slots[i], lay.Dels[i], i, bgPointer, bgDown);

        Button(sb, lay.Codex, Loc.T("menu.codex"), bgPointer, bgDown);
        Button(sb, lay.Options, Loc.T("menu.options"), bgPointer, bgDown);
        Button(sb, lay.Quit, Loc.T("menu.quit"), bgPointer, bgDown);
        if (Context.Settings.IsDemo)
            Button(sb, lay.Wishlist, Loc.T("menu.wishlist_add"), bgPointer, bgDown);
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
            var dead = new Point(int.MinValue, int.MinValue);
            // Confirmation méta ouverte : on neutralise le survol des options derrière.
            var pointer = _confirmMetaReset ? dead.ToVector2() : (gp ? focusRect.Center.ToVector2() : mouse.ToVector2());
            var optDown = !_confirmMetaReset && !gp && mouseDown;
            sb.Begin(samplerState: SamplerState.PointClamp);
            _menuRenderer.Draw(sb, _menu, w, h, pointer, optDown, (!_confirmMetaReset && gp) ? focusRect : null);
            // Bouton « effacer les découvertes » sous le panneau (uniquement dans les options).
            if (_menu.Panel == MenuPanel.Options)
                Button(sb, MetaResetRect(w, h), Loc.T("mainmenu.reset_meta"),
                    (_confirmMetaReset || gp) ? dead : mouse, optDown);
            sb.End();

            // Boîte de confirmation explicative par-dessus le panneau d'options.
            if (_confirmMetaReset)
            {
                var (_, yc, nc) = MetaConfirmLayout(w, h);
                var cp = gp ? (_confirmYes ? yc.Center : nc.Center) : mouse;
                DrawMetaConfirm(sb, w, h, cp, !gp && mouseDown);
            }
        }

        // Codex par-dessus le menu (dessine son propre voile + panneau).
        if (_codex.IsOpen)
            _codex.Draw(sb, new Viewport(0, 0, w, h));
    }

    private void DrawSlot(SpriteBatch sb, Rectangle main, Rectangle del, int index, Point pointer, bool down)
    {
        var occupied = _slots[index] != null;

        var dy = Context.Style.DrawButton(sb, main, UiStyle.StateOf(main.Contains(pointer), down));
        var x = main.X + 12;
        var line = main.Y + 9 + dy;
        Context.Font.Draw(sb, Loc.T("mainmenu.slot", index + 1), new Vector2(x, line), 1, Palette.White);

        // Difficulté de la run, alignée à DROITE de la ligne de TITRE — la droite de la sous-ligne est
        // déjà prise par le chronomètre. Figée à la création de la run, donc jamais vide sur un slot
        // occupé (une sauvegarde antérieure au champ vaut Normal, cf. RunSave.Difficulty).
        if (occupied)
        {
            var diff = DifficultyName(_slots[index]!.Difficulty);
            Context.Font.Draw(sb, diff, new Vector2(main.Right - 12 - Context.Font.Measure(diff, 1), line),
                1, DifficultyColor(_slots[index]!.Difficulty));
        }

        var sub = occupied
            ? Loc.T("mainmenu.slot_progress", _slots[index]!.CombatNumber, Run.TotalCombats, _slots[index]!.UnitCount)
            : Loc.T("mainmenu.new_game");
        var subY = line + Context.Font.LineHeight() + 6;
        Context.Font.Draw(sb, sub, new Vector2(x, subY), 1, occupied ? Palette.Blue1 : Palette.Yellow2);

        // Chronomètre de la run, aligné à DROITE de la même ligne. Masqué sous la seconde : une sauvegarde
        // antérieure au chronomètre vaut 0, et afficher « 0:00 » ferait croire à une run vierge.
        if (occupied && _slots[index]!.PlayTimeSeconds >= 1)
        {
            var elapsed = TimeText.Duration(_slots[index]!.PlayTimeSeconds);
            Context.Font.Draw(sb, elapsed,
                new Vector2(main.Right - 12 - Context.Font.Measure(elapsed, 1), subY), 1, Palette.Cyan1);
        }

        if (occupied)
            Button(sb, del, "X", pointer, down);
    }

    /// <summary>Nom localisé d'une difficulté (mêmes clés que l'écran de sélection de commandant).</summary>
    private static string DifficultyName(Difficulty d) => Loc.T("difficulty." + d.ToString().ToLowerInvariant());

    /// <summary>Couleur d'une difficulté, par RÔLE de palette : accent d'info, libellé discret, danger.</summary>
    private static Color DifficultyColor(Difficulty d) => d switch
    {
        Difficulty.Facile => Palette.Cyan2,
        Difficulty.Difficile => Palette.Purple5,
        _ => Palette.Blue1,
    };

    private void Button(SpriteBatch sb, Rectangle r, string label, Point pointer, bool down)
    {
        var dy = Context.Style.DrawButton(sb, r, UiStyle.StateOf(r.Contains(pointer), down));
        var area = r; area.Offset(0, dy);
        Context.Font.DrawCentered(sb, label, area, 1, Palette.White);
    }

    /// <summary>
    /// Décor animé : jusqu'à cinq pions découverts posés en arc autour du panneau (le central en haut et,
    /// si le canevas le permet, agrandi ; puis les flancs ; puis deux dans les gouttières latérales), chacun
    /// animé d'un léger va-et-vient vertical déphasé. Les emplacements sont dérivés de la géométrie du
    /// panneau pour rester À L'ÉCART de l'UI — le titre et le panneau sont dessinés PAR-DESSUS. Rien de
    /// découvert → rien à dessiner.
    /// </summary>
    private void DrawDecor(SpriteBatch sb, Rectangle panel)
    {
        if (_decorUnits.Count == 0)
            return;

        var w = Context.VirtualResolution.X;
        var baseline = panel.Y;                        // haut du panneau : réf. pour l'échelle du central
        var centerScale = baseline >= 224 ? 3 : 2;     // pion central agrandi seulement si le canevas le permet
        var feet = baseline + DecorDrop;               // « sol » de l'arc, descendu d'un cran sous le panneau

        // Emplacements en ordre de PRIORITÉ (centre d'abord) : une sélection réduite garnit d'abord le centre.
        var slots = new[]
        {
            (X: w / 2,            FeetY: feet - 24, Scale: centerScale),  // centre (derrière le titre)
            (X: (int)(w * 0.30f), FeetY: feet - 6,  Scale: 2),           // flanc gauche
            (X: (int)(w * 0.70f), FeetY: feet - 6,  Scale: 2),           // flanc droit
            (X: (int)(w * 0.12f), FeetY: feet + 70, Scale: 2),           // gouttière gauche
            (X: (int)(w * 0.88f), FeetY: feet + 70, Scale: 2),           // gouttière droite
        };

        var count = Math.Min(_decorUnits.Count, slots.Length);
        // Du plus extérieur vers le centre : le pion central est tracé EN DERNIER, donc devant les autres.
        for (var i = count - 1; i >= 0; i--)
        {
            var s = slots[i];
            var bob = (int)Math.Round(Math.Sin(_decorTime * DecorBobSpeed + i * 1.1f) * DecorBobAmp);
            var center = new Point(s.X, s.FeetY - 32 * s.Scale + bob);
            _units.DrawScaled(sb, _decorUnits[i], center, s.Scale);
        }
    }

    private void DrawConfirm(SpriteBatch sb, int w, int h, Point pointer, bool down)
    {
        var (panel, yes, no) = ConfirmLayout(w, h);

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(Context.Pixel, new Rectangle(0, 0, w, h), Palette.Navy2 * 0.85f);
        Context.Style.DrawPanel(sb, panel);

        var textArea = new Rectangle(panel.X, panel.Y + Pad, panel.Width, 16);
        Context.Font.DrawCentered(sb, Loc.T("mainmenu.confirm_delete", _confirmDelete + 1), textArea, 1, Palette.White);

        Button(sb, yes, Loc.T("mainmenu.delete"), pointer, down);
        Button(sb, no, Loc.T("mainmenu.cancel"), pointer, down);
        sb.End();
    }

    // ── Mise en page ─────────────────────────────────────────────────────────────
    private MenuLayout BuildLayout(int w, int h)
    {
        var panelW = ColW + 2 * Pad;
        // Les trois actions tiennent désormais sur UNE rangée (Codex | Options | Quitter) sous les slots : le
        // panneau y gagne en hauteur, dégageant le haut de l'écran pour la lignée de pions du décor.
        var panelH = Pad + _slots.Length * (RowH + Gap) + BtnH + Pad
                   + (Context.Settings.IsDemo ? BtnH + Gap : 0);   // rangée wishlist en plus (démo)
        var panelX = (w - panelW) / 2;
        // Panneau dans la moitié basse : le titre et la lignée de pions occupent le haut (façon capture).
        var panelY = (h - panelH) / 2 + 56;

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

        // Rangée de trois boutons de largeur égale ; le dernier absorbe l'arrondi pour coller à ColW.
        var btnW = (ColW - 2 * Gap) / 3;
        lay.Codex = new Rectangle(x, y, btnW, BtnH);
        lay.Options = new Rectangle(x + btnW + Gap, y, btnW, BtnH);
        lay.Quit = new Rectangle(x + 2 * (btnW + Gap), y, ColW - 2 * (btnW + Gap), BtnH);
        if (Context.Settings.IsDemo)   // CTA pleine largeur sous la rangée d'actions
            lay.Wishlist = new Rectangle(x, y + BtnH + Gap, ColW, BtnH);
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

    /// <summary>Boîte de confirmation de l'effacement de la méta-progression (plus large : texte explicatif).</summary>
    private static (Rectangle panel, Rectangle yes, Rectangle no) MetaConfirmLayout(int w, int h)
    {
        const int cw = 480, ch = 210, pad = 16, btnH = 34, gap = 12;
        var panel = new Rectangle((w - cw) / 2, (h - ch) / 2, cw, ch);
        var btnW = (cw - 2 * pad - gap) / 2;
        var by = panel.Bottom - pad - btnH;
        var yes = new Rectangle(panel.X + pad, by, btnW, btnH);
        var no = new Rectangle(panel.Right - pad - btnW, by, btnW, btnH);
        return (panel, yes, no);
    }

    private void DrawMetaConfirm(SpriteBatch sb, int w, int h, Point pointer, bool down)
    {
        var (panel, yes, no) = MetaConfirmLayout(w, h);

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(Context.Pixel, new Rectangle(0, 0, w, h), Palette.Navy2 * 0.85f);
        Context.Style.DrawPanel(sb, panel);

        Context.Font.DrawCentered(sb, Loc.T("mainmenu.reset_meta_confirm"),
            new Rectangle(panel.X, panel.Y + Pad, panel.Width, 16), 1, Palette.Yellow2);

        var ty = panel.Y + Pad + 30;
        foreach (var key in new[] { "mainmenu.reset_meta_desc1", "mainmenu.reset_meta_desc2", "mainmenu.reset_meta_desc3" })
        {
            Context.Font.DrawCentered(sb, Loc.T(key), new Rectangle(panel.X, ty, panel.Width, 12), 1, Palette.White);
            ty += 18;
        }

        Button(sb, yes, Loc.T("mainmenu.delete"), pointer, down);
        Button(sb, no, Loc.T("mainmenu.cancel"), pointer, down);
        sb.End();
    }

    /// <summary>Bouton « effacer les découvertes » : juste sous le panneau d'options.</summary>
    private Rectangle MetaResetRect(int w, int h)
    {
        var panel = _menu.Layout(w, h).Panel;
        return new Rectangle(panel.X, panel.Bottom + 12, panel.Width, 28);
    }

    private struct MenuLayout
    {
        public Rectangle Panel;
        public Rectangle[] Slots;
        public Rectangle[] Dels;
        public Rectangle Codex;
        public Rectangle Options;
        public Rectangle Quit;
        public Rectangle Wishlist;   // démo seulement : bouton pleine largeur vers la page Steam
    }
}
