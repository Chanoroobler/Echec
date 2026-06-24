using System.Collections.Generic;
using System.Linq;
using Echec.Engine.Settings;
using Microsoft.Xna.Framework;

namespace Echec.Engine.UI;

/// <summary>Action signalée à la scène de jeu après un clic dans le menu.</summary>
public enum MenuAction { None, Resume, MainMenu, Quit, GraphicsChanged, VolumeChanged }

/// <summary>Quel panneau du menu est affiché.</summary>
public enum MenuPanel { Root, Options }

/// <summary>
/// Élément interactif du menu sous un point. Sert au feedback audio (survol/clic) sans
/// dupliquer la mise en page : même source que le hit-test. <c>None</c> = aucun.
/// </summary>
public enum PauseElement
{
    None,
    Resume, Options, MainMenu, Quit,
    ResLeft, ResRight, FsToggle, BdToggle,
    MasterLeft, MasterRight,
    MusicLeft, MusicRight,
    SfxLeft, SfxRight,
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
    public Rectangle Resume, Options, MainMenu, Quit;
    // Options : lignes (label à gauche, contrôle à droite)
    public Rectangle ResRow, ResLeft, ResValue, ResRight;
    public Rectangle FsRow, FsToggle;
    public Rectangle BdRow, BdToggle;
    public Rectangle MasterRow, MasterLeft, MasterValue, MasterRight;
    public Rectangle MusicRow, MusicLeft, MusicValue, MusicRight;
    public Rectangle SfxRow, SfxLeft, SfxValue, SfxRight;
    public Rectangle Back;
}

/// <summary>
/// État + géométrie + logique du menu pause. Pur modèle : le rendu vit dans
/// <see cref="PauseMenuRenderer"/>. Le menu met le jeu en pause tant qu'il est ouvert.
/// (Porté de CosyFarmer, adapté aux <see cref="GameSettings"/> d'Echec.)
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
        new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160),
    };

    private readonly GameSettings _s;
    private readonly List<Point> _resolutions;
    private int _resIndex;

    public bool IsOpen { get; private set; }
    public MenuPanel Panel { get; private set; }

    public PauseMenu(GameSettings settings, Point nativeRes)
    {
        _s = settings;

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
    private int FocusCount => Panel == MenuPanel.Root ? 4 : 7;
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
            return _focus switch { 0 => l.Resume, 1 => l.Options, 2 => l.MainMenu, _ => l.Quit };
        return _focus switch
        {
            0 => l.ResRow, 1 => l.FsRow, 2 => l.BdRow,
            3 => l.MasterRow, 4 => l.MusicRow, 5 => l.SfxRow, _ => l.Back,
        };
    }

    /// <summary>Valide l'élément focus (bouton A). Équivaut au clic sur cet élément.</summary>
    public MenuAction ActivateFocused()
    {
        if (Panel == MenuPanel.Root)
            return _focus switch
            {
                0 => CloseReturning(MenuAction.Resume),
                1 => OpenOptionsPanel(),
                2 => CloseReturning(MenuAction.MainMenu),
                _ => MenuAction.Quit,
            };
        return _focus switch
        {
            1 => ToggleFullscreen(),
            2 => ToggleBorderless(),
            6 => BackAction(),
            _ => MenuAction.None,   // les pas (résolution, volumes) se règlent avec gauche/droite
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
            case 1: _s.Display.Fullscreen = !_s.Display.Fullscreen; return MenuAction.GraphicsChanged;
            case 2: _s.Display.Borderless = !_s.Display.Borderless; return MenuAction.GraphicsChanged;
            case 3: _s.Audio.Master = Step(_s.Audio.Master, dir * 10); return MenuAction.VolumeChanged;
            case 4: _s.Audio.Music = Step(_s.Audio.Music, dir * 10); return MenuAction.VolumeChanged;
            case 5: _s.Audio.Sfx = Step(_s.Audio.Sfx, dir * 10); return MenuAction.VolumeChanged;
            default: return MenuAction.None;
        }
    }

    private MenuAction CloseReturning(MenuAction a) { Close(); return a; }
    private MenuAction OpenOptionsPanel() { Panel = MenuPanel.Options; _focus = 0; return MenuAction.None; }
    private MenuAction BackAction() { Back(); return MenuAction.None; }
    private MenuAction ToggleFullscreen() { _s.Display.Fullscreen = !_s.Display.Fullscreen; return MenuAction.GraphicsChanged; }
    private MenuAction ToggleBorderless() { _s.Display.Borderless = !_s.Display.Borderless; return MenuAction.GraphicsChanged; }

    // ── Valeurs affichées ──────────────────────────────────────────────────────
    public string ResolutionText => $"{_resolutions[_resIndex].X} X {_resolutions[_resIndex].Y}";
    public string FullscreenText => _s.Display.Fullscreen ? "OUI" : "NON";
    public string BorderlessText => _s.Display.Borderless ? "OUI" : "NON";
    public string MasterVolumeText => $"{_s.Audio.Master}%";
    public string MusicVolumeText => $"{_s.Audio.Music}%";
    public string SfxVolumeText => $"{_s.Audio.Sfx}%";

    // ── Mise en page ────────────────────────────────────────────────────────────
    public PauseLayout Layout(int vpW, int vpH)
        => Panel == MenuPanel.Root ? RootLayout(vpW, vpH) : OptionsLayout(vpW, vpH);

    private PauseLayout RootLayout(int vpW, int vpH)
    {
        int h = Pad + TitleH + Gap + (4 * BtnH + 3 * Gap) + Pad;
        var panel = Centered(vpW, vpH, RootW, h);

        var l = new PauseLayout { Panel = panel };
        l.Title = new Rectangle(panel.X, panel.Y + Pad, panel.Width, TitleH);

        int bx = panel.X + Pad;
        int bw = panel.Width - 2 * Pad;
        int y = panel.Y + Pad + TitleH + Gap;
        l.Resume = new Rectangle(bx, y, bw, BtnH); y += BtnH + Gap;
        l.Options = new Rectangle(bx, y, bw, BtnH); y += BtnH + Gap;
        l.MainMenu = new Rectangle(bx, y, bw, BtnH); y += BtnH + Gap;
        l.Quit = new Rectangle(bx, y, bw, BtnH);
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

        l.FsRow = new Rectangle(panel.X, y, panel.Width, BtnH);
        l.FsToggle = new Rectangle(ctrlX, y, CtrlW, BtnH);
        y += BtnH + Gap;

        l.BdRow = new Rectangle(panel.X, y, panel.Width, BtnH);
        l.BdToggle = new Rectangle(ctrlX, y, CtrlW, BtnH);
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
        if (l.Options.Contains(p)) { Panel = MenuPanel.Options; _focus = 0; return MenuAction.None; }
        if (l.MainMenu.Contains(p)) { Close(); return MenuAction.MainMenu; }
        if (l.Quit.Contains(p)) return MenuAction.Quit;
        return MenuAction.None;
    }

    private MenuAction HandleOptionsClick(Point p, PauseLayout l)
    {
        if (l.ResLeft.Contains(p)) { StepResolution(-1); return MenuAction.GraphicsChanged; }
        if (l.ResRight.Contains(p)) { StepResolution(+1); return MenuAction.GraphicsChanged; }
        if (l.FsToggle.Contains(p)) { _s.Display.Fullscreen = !_s.Display.Fullscreen; return MenuAction.GraphicsChanged; }
        if (l.BdToggle.Contains(p)) { _s.Display.Borderless = !_s.Display.Borderless; return MenuAction.GraphicsChanged; }

        if (l.MasterLeft.Contains(p)) { _s.Audio.Master = Step(_s.Audio.Master, -10); return MenuAction.VolumeChanged; }
        if (l.MasterRight.Contains(p)) { _s.Audio.Master = Step(_s.Audio.Master, +10); return MenuAction.VolumeChanged; }
        if (l.MusicLeft.Contains(p)) { _s.Audio.Music = Step(_s.Audio.Music, -10); return MenuAction.VolumeChanged; }
        if (l.MusicRight.Contains(p)) { _s.Audio.Music = Step(_s.Audio.Music, +10); return MenuAction.VolumeChanged; }
        if (l.SfxLeft.Contains(p)) { _s.Audio.Sfx = Step(_s.Audio.Sfx, -10); return MenuAction.VolumeChanged; }
        if (l.SfxRight.Contains(p)) { _s.Audio.Sfx = Step(_s.Audio.Sfx, +10); return MenuAction.VolumeChanged; }

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
            if (l.Options.Contains(p)) return PauseElement.Options;
            if (l.MainMenu.Contains(p)) return PauseElement.MainMenu;
            if (l.Quit.Contains(p)) return PauseElement.Quit;
            return PauseElement.None;
        }

        if (l.ResLeft.Contains(p)) return PauseElement.ResLeft;
        if (l.ResRight.Contains(p)) return PauseElement.ResRight;
        if (l.FsToggle.Contains(p)) return PauseElement.FsToggle;
        if (l.BdToggle.Contains(p)) return PauseElement.BdToggle;
        if (l.MasterLeft.Contains(p)) return PauseElement.MasterLeft;
        if (l.MasterRight.Contains(p)) return PauseElement.MasterRight;
        if (l.MusicLeft.Contains(p)) return PauseElement.MusicLeft;
        if (l.MusicRight.Contains(p)) return PauseElement.MusicRight;
        if (l.SfxLeft.Contains(p)) return PauseElement.SfxLeft;
        if (l.SfxRight.Contains(p)) return PauseElement.SfxRight;
        if (l.Back.Contains(p)) return PauseElement.Back;
        return PauseElement.None;
    }

    private void StepResolution(int delta)
    {
        _resIndex = MathHelper.Clamp(_resIndex + delta, 0, _resolutions.Count - 1);
        _s.Display.Width = _resolutions[_resIndex].X;
        _s.Display.Height = _resolutions[_resIndex].Y;
    }

    private static int Step(int value, int delta) => MathHelper.Clamp(value + delta, 0, 100);
}
