using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Settings;
using Microsoft.Xna.Framework;

namespace ChessArmy.Engine.UI;

/// <summary>Action signalée à la scène de jeu après un clic dans le menu.</summary>
public enum MenuAction { None, Resume, Codex, RestartMission, MainMenu, Quit, GraphicsChanged, VolumeChanged, LanguageChanged }

/// <summary>Quel panneau du menu est affiché.</summary>
public enum MenuPanel { Root, Options }

/// <summary>
/// Élément interactif du menu sous un point. Sert au feedback audio (survol/clic) sans
/// dupliquer la mise en page : même source que le hit-test. <c>None</c> = aucun.
/// </summary>
public enum PauseElement
{
    None,
    Resume, Codex, Options, Restart, MainMenu, Quit,
    ResLeft, ResRight, ModeLeft, ModeRight,
    MasterLeft, MasterRight,
    MusicLeft, MusicRight,
    SfxLeft, SfxRight,
    LangLeft, LangRight,
    Back,
}

/// <summary>
/// Rectangles de tous les éléments cliquables du menu, calculés à partir de la taille
/// du viewport UI. Source de vérité UNIQUE partagée entre le rendu et le hit-test.
/// </summary>
public struct PauseLayout
{
    public Rectangle Panel;
    public Rectangle Title;
    // Racine
    public Rectangle Resume, Codex, Options, Restart, MainMenu, Quit;
    // Options : lignes (label à gauche, contrôle à droite)
    public Rectangle ResRow, ResLeft, ResValue, ResRight;
    public Rectangle ModeRow, ModeLeft, ModeValue, ModeRight;
    public Rectangle MasterRow, MasterLeft, MasterValue, MasterRight;
    public Rectangle MusicRow, MusicLeft, MusicValue, MusicRight;
    public Rectangle SfxRow, SfxLeft, SfxValue, SfxRight;
    public Rectangle LangRow, LangLeft, LangValue, LangRight;
    public Rectangle Back;
}

/// <summary>
/// État + géométrie + logique du menu pause. Pur modèle : le rendu vit dans
/// <see cref="PauseMenuRenderer"/>. Le menu met le jeu en pause tant qu'il est ouvert.
/// (Porté de CosyFarmer, adapté aux <see cref="GameSettings"/> de Chess Army.)
/// </summary>
public sealed class PauseMenu
{
    // ── Constantes de mise en page (espace UI, px) ─────────────────────────────
    private const int Pad = 18;
    private const int BtnH = 28;
    private const int Gap = 10;
    private const int TitleH = 16;
    private const int StepW = 22;   // largeur des boutons < / >
    private const int ValW = 100;   // largeur de la zone de valeur
    private const int CtrlW = StepW * 2 + ValW;

    private const int RootW = 220;
    private const int OptionsW = 330;

    private static readonly Point[] BaseResolutions =
    {
        // 1280×800 est en 16:10 : le rendu 16:9 s'y affiche en letterbox (bandes haut/bas), comme prévu.
        new(1280, 720), new(1280, 800), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160),
    };

    private readonly GameSettings _s;
    private readonly List<Point> _resolutions;
    private readonly bool _allowRestart;
    private int _resIndex;

    public bool IsOpen { get; private set; }
    public MenuPanel Panel { get; private set; }

    /// <param name="allowRestart">
    /// Affiche « Recommencer la mission » dans la racine. <c>false</c> (difficulté sans filet) retire
    /// entièrement le bouton : la liste raccourcit et hauteur/focus/hit-test suivent automatiquement.
    /// </param>
    public PauseMenu(GameSettings settings, Point nativeRes, bool allowRestart = true)
    {
        _s = settings;
        _allowRestart = allowRestart;

        var set = new SortedSet<(int w, int h)>();
        foreach (var r in BaseResolutions) set.Add((r.X, r.Y));
        set.Add((nativeRes.X, nativeRes.Y));
        _resolutions = set.Select(t => new Point(t.w, t.h)).ToList();

        _resIndex = _resolutions.FindIndex(r => r.X == _s.Display.Width && r.Y == _s.Display.Height);
        if (_resIndex < 0) _resIndex = 0;
    }

    public void Open() { IsOpen = true; Panel = MenuPanel.Root; _focus = 0; }
    public void OpenOptions() { IsOpen = true; Panel = MenuPanel.Options; _focus = 0; }
    public void Close() => IsOpen = false;
    public void Toggle() { if (IsOpen) Close(); else Open(); }

    /// <summary>Retour arrière : Options → Racine, Racine → fermeture (reprise).</summary>
    public void Back()
    {
        if (Panel == MenuPanel.Options) { Panel = MenuPanel.Root; _focus = 0; }
        else Close();
    }

    // ── Navigation au focus (manette / clavier) ──────────────────────────────────
    // Pendant clic, le focus suit l'élément choisi ; en manette, haut/bas le déplace, gauche/droite
    // règle les pas/bascules (Options), A valide, B = retour. Le rendu surligne l'élément focus via un
    // « pointeur synthétique » = centre de FocusedRect (réutilise le hit-test/surbrillance existants).
    private int _focus;

    public int Focus => _focus;
    private int FocusCount => Panel == MenuPanel.Root ? RootItems.Count : 7;
    public void MoveFocus(int delta)
    {
        var n = FocusCount;
        _focus = ((_focus + delta) % n + n) % n;
    }

    /// <summary>Rectangle de l'élément actuellement focus (pour la surbrillance / le pointeur synthétique).</summary>
    public Rectangle FocusedRect(int vpW, int vpH)
    {
        var l = Layout(vpW, vpH);
        if (Panel == MenuPanel.Root)
            return RectFor(RootItems[_focus], l);
        return _focus switch
        {
            0 => l.ResRow, 1 => l.ModeRow,
            2 => l.MasterRow, 3 => l.MusicRow, 4 => l.SfxRow, 5 => l.LangRow, _ => l.Back,
        };
    }

    /// <summary>Rectangle du bouton racine <paramref name="item"/> dans la mise en page <paramref name="l"/>.</summary>
    private static Rectangle RectFor(PauseElement item, PauseLayout l) => item switch
    {
        PauseElement.Resume => l.Resume,
        PauseElement.Codex => l.Codex,
        PauseElement.Options => l.Options,
        PauseElement.Restart => l.Restart,
        PauseElement.MainMenu => l.MainMenu,
        _ => l.Quit,
    };

    /// <summary>Valide l'élément focus (bouton A). Équivaut au clic sur cet élément.</summary>
    public MenuAction ActivateFocused()
    {
        if (Panel == MenuPanel.Root)
            return RootItems[_focus] switch
            {
                PauseElement.Resume => CloseReturning(MenuAction.Resume),
                PauseElement.Codex => MenuAction.Codex,          // ouvre le codex par-dessus, sans fermer la pause
                PauseElement.Options => OpenOptionsPanel(),
                PauseElement.Restart => CloseReturning(MenuAction.RestartMission),
                PauseElement.MainMenu => CloseReturning(MenuAction.MainMenu),
                _ => MenuAction.Quit,
            };
        return _focus switch
        {
            6 => BackAction(),
            _ => MenuAction.None,   // les pas (résolution, mode, volumes, langue) se règlent avec gauche/droite
        };
    }

    /// <summary>Règle l'élément focus avec gauche (-1) / droite (+1).</summary>
    public MenuAction AdjustFocused(int dir)
    {
        if (Panel != MenuPanel.Options)
            return MenuAction.None;
        switch (_focus)
        {
            case 0: StepResolution(dir); return MenuAction.GraphicsChanged;
            case 1: StepMode(dir); return MenuAction.GraphicsChanged;
            case 2: _s.Audio.Master = Step(_s.Audio.Master, dir * 10); return MenuAction.VolumeChanged;
            case 3: _s.Audio.Music = Step(_s.Audio.Music, dir * 10); return MenuAction.VolumeChanged;
            case 4: _s.Audio.Sfx = Step(_s.Audio.Sfx, dir * 10); return MenuAction.VolumeChanged;
            case 5: StepLanguage(dir); return MenuAction.LanguageChanged;
            default: return MenuAction.None;
        }
    }

    private MenuAction CloseReturning(MenuAction a) { Close(); return a; }
    private MenuAction OpenOptionsPanel() { Panel = MenuPanel.Options; _focus = 0; return MenuAction.None; }
    private MenuAction BackAction() { Back(); return MenuAction.None; }

    // ── Valeurs affichées ──────────────────────────────────────────────────────
    public string ResolutionText => $"{_resolutions[_resIndex].X} X {_resolutions[_resIndex].Y}";

    /// <summary>Libellé du mode d'affichage courant (Fenêtré / Sans bordure / Plein écran).</summary>
    public string ModeText => _s.Display.Mode switch
    {
        WindowMode.Fullscreen => Loc.T("display.fullscreen"),
        WindowMode.Borderless => Loc.T("display.borderless"),
        _ => Loc.T("display.windowed"),
    };

    public string MasterVolumeText => $"{_s.Audio.Master}%";
    public string MusicVolumeText => $"{_s.Audio.Music}%";
    public string SfxVolumeText => $"{_s.Audio.Sfx}%";

    /// <summary>Nom de la langue active, affiché dans sa propre langue (endonyme).</summary>
    public string LanguageText => _s.Language switch
    {
        Language.English => Loc.T("lang.english"),
        Language.Italiano => Loc.T("lang.italiano"),
        Language.Deutsch => Loc.T("lang.deutsch"),
        Language.Espanol => Loc.T("lang.espanol"),
        Language.Polski => Loc.T("lang.polski"),
        Language.Turkce => Loc.T("lang.turkce"),
        Language.ChineseSimplified => Loc.T("lang.chinese"),
        _ => Loc.T("lang.francais"),
    };

    // ── Mise en page ────────────────────────────────────────────────────────────
    public PauseLayout Layout(int vpW, int vpH)
        => Panel == MenuPanel.Root ? RootLayout(vpW, vpH) : OptionsLayout(vpW, vpH);

    /// <summary>
    /// Boutons de la racine, dans l'ordre d'affichage — source UNIQUE pour la hauteur du panneau, le focus,
    /// la mise en page et le hit-test. « Recommencer la mission » disparaît quand la run l'interdit
    /// (<see cref="_allowRestart"/> = false), et tout ce qui en dépend suit sans resynchronisation.
    /// </summary>
    private IReadOnlyList<PauseElement> RootItems => _allowRestart
        ? new[]
        {
            PauseElement.Resume, PauseElement.Codex, PauseElement.Options,
            PauseElement.Restart, PauseElement.MainMenu, PauseElement.Quit,
        }
        : new[]
        {
            PauseElement.Resume, PauseElement.Codex, PauseElement.Options,
            PauseElement.MainMenu, PauseElement.Quit,
        };

    private PauseLayout RootLayout(int vpW, int vpH)
    {
        var items = RootItems;
        int n = items.Count;
        int h = Pad + TitleH + Gap + (n * BtnH + (n - 1) * Gap) + Pad;
        var panel = Centered(vpW, vpH, RootW, h);

        var l = new PauseLayout { Panel = panel };
        l.Title = new Rectangle(panel.X, panel.Y + Pad, panel.Width, TitleH);

        int bx = panel.X + Pad;
        int bw = panel.Width - 2 * Pad;
        int y = panel.Y + Pad + TitleH + Gap;
        // Recommencer (quand présent) est groupé avec « Menu principal » : ce sont les sorties de la mission.
        foreach (var item in items)
        {
            var r = new Rectangle(bx, y, bw, BtnH);
            switch (item)
            {
                case PauseElement.Resume: l.Resume = r; break;
                case PauseElement.Codex: l.Codex = r; break;
                case PauseElement.Options: l.Options = r; break;
                case PauseElement.Restart: l.Restart = r; break;
                case PauseElement.MainMenu: l.MainMenu = r; break;
                case PauseElement.Quit: l.Quit = r; break;
            }
            y += BtnH + Gap;
        }
        return l;
    }

    private PauseLayout OptionsLayout(int vpW, int vpH)
    {
        int h = Pad + TitleH + Gap + (6 * BtnH + 5 * Gap) + Gap + BtnH + Pad;
        var panel = Centered(vpW, vpH, OptionsW, h);

        var l = new PauseLayout { Panel = panel };
        l.Title = new Rectangle(panel.X, panel.Y + Pad, panel.Width, TitleH);

        int ctrlX = panel.Right - Pad - CtrlW;
        int y = panel.Y + Pad + TitleH + Gap;

        l.ResRow = new Rectangle(panel.X, y, panel.Width, BtnH);
        (l.ResLeft, l.ResValue, l.ResRight) = Stepper(ctrlX, y);
        y += BtnH + Gap;

        l.ModeRow = new Rectangle(panel.X, y, panel.Width, BtnH);
        (l.ModeLeft, l.ModeValue, l.ModeRight) = Stepper(ctrlX, y);
        y += BtnH + Gap;

        l.MasterRow = new Rectangle(panel.X, y, panel.Width, BtnH);
        (l.MasterLeft, l.MasterValue, l.MasterRight) = Stepper(ctrlX, y);
        y += BtnH + Gap;

        l.MusicRow = new Rectangle(panel.X, y, panel.Width, BtnH);
        (l.MusicLeft, l.MusicValue, l.MusicRight) = Stepper(ctrlX, y);
        y += BtnH + Gap;

        l.SfxRow = new Rectangle(panel.X, y, panel.Width, BtnH);
        (l.SfxLeft, l.SfxValue, l.SfxRight) = Stepper(ctrlX, y);
        y += BtnH + Gap;

        l.LangRow = new Rectangle(panel.X, y, panel.Width, BtnH);
        (l.LangLeft, l.LangValue, l.LangRight) = Stepper(ctrlX, y);
        y += BtnH + Gap;

        int backW = 130;
        l.Back = new Rectangle(panel.X + (panel.Width - backW) / 2, y, backW, BtnH);
        return l;
    }

    private static (Rectangle left, Rectangle value, Rectangle right) Stepper(int x, int y)
        => (new Rectangle(x, y, StepW, BtnH),
            new Rectangle(x + StepW, y, ValW, BtnH),
            new Rectangle(x + StepW + ValW, y, StepW, BtnH));

    private static Rectangle Centered(int vpW, int vpH, int w, int h)
        => new((vpW - w) / 2, (vpH - h) / 2, w, h);

    // ── Clics ────────────────────────────────────────────────────────────────────
    public MenuAction HandleClick(Point p, int vpW, int vpH)
    {
        var l = Layout(vpW, vpH);
        return Panel == MenuPanel.Root ? HandleRootClick(p, l) : HandleOptionsClick(p, l);
    }

    private MenuAction HandleRootClick(Point p, PauseLayout l)
    {
        if (l.Resume.Contains(p)) { Close(); return MenuAction.Resume; }
        if (l.Codex.Contains(p)) return MenuAction.Codex;   // ouvre le codex ; la pause reste ouverte derrière
        if (l.Options.Contains(p)) { Panel = MenuPanel.Options; _focus = 0; return MenuAction.None; }
        if (l.Restart.Contains(p)) { Close(); return MenuAction.RestartMission; }
        if (l.MainMenu.Contains(p)) { Close(); return MenuAction.MainMenu; }
        if (l.Quit.Contains(p)) return MenuAction.Quit;
        return MenuAction.None;
    }

    private MenuAction HandleOptionsClick(Point p, PauseLayout l)
    {
        if (l.ResLeft.Contains(p)) { StepResolution(-1); return MenuAction.GraphicsChanged; }
        if (l.ResRight.Contains(p)) { StepResolution(+1); return MenuAction.GraphicsChanged; }
        if (l.ModeLeft.Contains(p)) { StepMode(-1); return MenuAction.GraphicsChanged; }
        if (l.ModeRight.Contains(p)) { StepMode(+1); return MenuAction.GraphicsChanged; }

        if (l.MasterLeft.Contains(p)) { _s.Audio.Master = Step(_s.Audio.Master, -10); return MenuAction.VolumeChanged; }
        if (l.MasterRight.Contains(p)) { _s.Audio.Master = Step(_s.Audio.Master, +10); return MenuAction.VolumeChanged; }
        if (l.MusicLeft.Contains(p)) { _s.Audio.Music = Step(_s.Audio.Music, -10); return MenuAction.VolumeChanged; }
        if (l.MusicRight.Contains(p)) { _s.Audio.Music = Step(_s.Audio.Music, +10); return MenuAction.VolumeChanged; }
        if (l.SfxLeft.Contains(p)) { _s.Audio.Sfx = Step(_s.Audio.Sfx, -10); return MenuAction.VolumeChanged; }
        if (l.SfxRight.Contains(p)) { _s.Audio.Sfx = Step(_s.Audio.Sfx, +10); return MenuAction.VolumeChanged; }

        if (l.LangLeft.Contains(p)) { StepLanguage(-1); return MenuAction.LanguageChanged; }
        if (l.LangRight.Contains(p)) { StepLanguage(+1); return MenuAction.LanguageChanged; }

        if (l.Back.Contains(p)) { Back(); return MenuAction.None; }
        return MenuAction.None;
    }

    /// <summary>
    /// Élément interactif sous le point (espace UI), ou <see cref="PauseElement.None"/>.
    /// Réutilise la même mise en page que le hit-test : alimente le feedback audio
    /// (survol &amp; clic) sans dupliquer la géométrie.
    /// </summary>
    public PauseElement ElementAt(Point p, int vpW, int vpH)
    {
        var l = Layout(vpW, vpH);
        if (Panel == MenuPanel.Root)
        {
            if (l.Resume.Contains(p)) return PauseElement.Resume;
            if (l.Codex.Contains(p)) return PauseElement.Codex;
            if (l.Options.Contains(p)) return PauseElement.Options;
            if (l.Restart.Contains(p)) return PauseElement.Restart;
            if (l.MainMenu.Contains(p)) return PauseElement.MainMenu;
            if (l.Quit.Contains(p)) return PauseElement.Quit;
            return PauseElement.None;
        }

        if (l.ResLeft.Contains(p)) return PauseElement.ResLeft;
        if (l.ResRight.Contains(p)) return PauseElement.ResRight;
        if (l.ModeLeft.Contains(p)) return PauseElement.ModeLeft;
        if (l.ModeRight.Contains(p)) return PauseElement.ModeRight;
        if (l.MasterLeft.Contains(p)) return PauseElement.MasterLeft;
        if (l.MasterRight.Contains(p)) return PauseElement.MasterRight;
        if (l.MusicLeft.Contains(p)) return PauseElement.MusicLeft;
        if (l.MusicRight.Contains(p)) return PauseElement.MusicRight;
        if (l.SfxLeft.Contains(p)) return PauseElement.SfxLeft;
        if (l.SfxRight.Contains(p)) return PauseElement.SfxRight;
        if (l.LangLeft.Contains(p)) return PauseElement.LangLeft;
        if (l.LangRight.Contains(p)) return PauseElement.LangRight;
        if (l.Back.Contains(p)) return PauseElement.Back;
        return PauseElement.None;
    }

    private void StepResolution(int delta)
    {
        _resIndex = MathHelper.Clamp(_resIndex + delta, 0, _resolutions.Count - 1);
        _s.Display.Width = _resolutions[_resIndex].X;
        _s.Display.Height = _resolutions[_resIndex].Y;
    }

    /// <summary>Fait défiler le mode d'affichage (sans bouclage : borné aux extrêmes).</summary>
    private void StepMode(int dir)
    {
        var values = (WindowMode[])Enum.GetValues(typeof(WindowMode));
        var i = Array.IndexOf(values, _s.Display.Mode);
        i = MathHelper.Clamp(i + dir, 0, values.Length - 1);
        _s.Display.Mode = values[i];
    }

    private static int Step(int value, int delta) => MathHelper.Clamp(value + delta, 0, 100);

    /// <summary>
    /// Fait défiler la langue active (sans bouclage : bornée aux extrêmes) et synchronise
    /// <see cref="Loc.Current"/> pour que tout le texte se mette à jour immédiatement.
    /// </summary>
    private void StepLanguage(int dir)
    {
        var values = (Language[])Enum.GetValues(typeof(Language));
        var i = Array.IndexOf(values, _s.Language);
        i = MathHelper.Clamp(i + dir, 0, values.Length - 1);
        _s.Language = values[i];
        Loc.Current = _s.Language;
    }
}
