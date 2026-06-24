using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Echec.Engine.Input;

/// <summary>Direction de navigation (croix directionnelle / stick gauche / clavier).</summary>
public enum NavDir { Up, Down, Left, Right }

/// <summary>
/// Capture l'état clavier/souris/MANETTE et expose la détection de fronts
/// (touche tout juste pressée, clic tout juste effectué) ainsi qu'une navigation
/// directionnelle avec répétition automatique (menus, cartes, curseur de plateau).
/// Appeler <see cref="Update"/> une fois par frame, avant la logique de jeu.
/// </summary>
public sealed class InputManager
{
    private KeyboardState _previousKeyboard;
    private KeyboardState _currentKeyboard;
    private MouseState _previousMouse;
    private MouseState _currentMouse;
    private GamePadState _previousPad;
    private GamePadState _currentPad;

    // Navigation : répétition (premier appui immédiat, puis délai, puis cadence).
    private const float NavInitialDelay = 0.35f;
    private const float NavRepeat = 0.12f;
    private const float StickThreshold = 0.5f;
    private readonly bool[] _navWasDown = new bool[4];
    private readonly float[] _navTimer = new float[4];
    private readonly bool[] _nav = new bool[4];

    // Dernier périphérique utilisé : pilote l'affichage (curseur souris vs focus/curseur de case).
    private bool _usingGamepad;

    // Transformation écran réel → espace virtuel (letterbox du rendu pixel-perfect).
    private Point _viewOffset = Point.Zero;
    private float _viewScale = 1f;

    /// <summary>
    /// Déclare la zone d'affichage du rendu virtuel (décalage + échelle) pour que
    /// <see cref="MousePosition"/> soit exprimée dans l'espace virtuel, comme le rendu.
    /// </summary>
    public void SetViewport(Point offset, float scale)
    {
        _viewOffset = offset;
        _viewScale = scale <= 0f ? 1f : scale;
    }

    public void Update(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        _previousKeyboard = _currentKeyboard;
        _currentKeyboard = Keyboard.GetState();
        _previousMouse = _currentMouse;
        _currentMouse = Mouse.GetState();
        _previousPad = _currentPad;
        _currentPad = GamePad.GetState(PlayerIndex.One);

        UpdateNav(dt);
        UpdateActiveDevice();
    }

    public bool IsKeyDown(Keys key) => _currentKeyboard.IsKeyDown(key);

    /// <summary>Vrai uniquement à la frame où la touche passe de relâchée à pressée.</summary>
    public bool WasKeyPressed(Keys key) =>
        _currentKeyboard.IsKeyDown(key) && _previousKeyboard.IsKeyUp(key);

    /// <summary>Position de la souris en espace virtuel (annule décalage + échelle du letterbox).</summary>
    public Point MousePosition => new(
        (int)((_currentMouse.Position.X - _viewOffset.X) / _viewScale),
        (int)((_currentMouse.Position.Y - _viewOffset.Y) / _viewScale));

    /// <summary>Vrai uniquement à la frame du clic gauche.</summary>
    public bool WasLeftClicked =>
        _currentMouse.LeftButton == ButtonState.Pressed &&
        _previousMouse.LeftButton == ButtonState.Released;

    /// <summary>Vrai uniquement à la frame où le bouton gauche est relâché (fin de glisser).</summary>
    public bool WasLeftReleased =>
        _currentMouse.LeftButton == ButtonState.Released &&
        _previousMouse.LeftButton == ButtonState.Pressed;

    /// <summary>Vrai tant que le bouton gauche est maintenu (pour le retour visuel d'enfoncement).</summary>
    public bool IsLeftDown => _currentMouse.LeftButton == ButtonState.Pressed;

    /// <summary>Vrai uniquement à la frame du clic droit (annulation / désélection).</summary>
    public bool WasRightClicked =>
        _currentMouse.RightButton == ButtonState.Pressed &&
        _previousMouse.RightButton == ButtonState.Released;

    /// <summary>Variation de la molette depuis la frame précédente (&gt; 0 = vers le haut). 0 si immobile.</summary>
    public int ScrollDelta => _currentMouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;

    // ── Manette ────────────────────────────────────────────────────────────────

    public bool GamepadConnected => _currentPad.IsConnected;

    /// <summary>Vrai si la manette est le dernier périphérique utilisé (sinon souris/clavier).</summary>
    public bool UsingGamepad => _usingGamepad;

    /// <summary>Navigation directionnelle (front + répétition auto) : croix directionnelle ou stick gauche.</summary>
    public bool Nav(NavDir dir) => _nav[(int)dir];

    /// <summary>Bouton de validation : A de la manette.</summary>
    public bool WasConfirmPressed => PadEdge(Buttons.A);

    /// <summary>Bouton d'annulation / retour : B de la manette.</summary>
    public bool WasCancelPressed => PadEdge(Buttons.B);

    /// <summary>Bouton menu/pause : Start de la manette.</summary>
    public bool WasMenuPressed => PadEdge(Buttons.Start);

    /// <summary>Bouton d'action secondaire : X de la manette (ex. effacer un slot).</summary>
    public bool WasTertiaryPressed => PadEdge(Buttons.X);

    /// <summary>Bouton d'action quaternaire : Y de la manette (ex. lancer le combat).</summary>
    public bool WasQuaternaryPressed => PadEdge(Buttons.Y);

    /// <summary>Gâchettes/boutons de tranche (cycle d'onglets, inventaire…).</summary>
    public bool WasLeftShoulderPressed => PadEdge(Buttons.LeftShoulder);
    public bool WasRightShoulderPressed => PadEdge(Buttons.RightShoulder);

    /// <summary>Gâchette droite MAINTENUE (analogique &gt; 0,5) — ex. révéler les zones de danger.</summary>
    public bool IsRightTriggerDown => _currentPad.Triggers.Right > 0.5f;

    private bool PadEdge(Buttons b) =>
        _currentPad.IsButtonDown(b) && _previousPad.IsButtonUp(b);

    private void UpdateNav(float dt)
    {
        for (var i = 0; i < 4; i++)
        {
            var down = DirDown((NavDir)i);
            var fire = false;
            if (down)
            {
                if (!_navWasDown[i]) { fire = true; _navTimer[i] = NavInitialDelay; }
                else { _navTimer[i] -= dt; if (_navTimer[i] <= 0f) { fire = true; _navTimer[i] = NavRepeat; } }
            }
            _navWasDown[i] = down;
            _nav[i] = fire;
        }
    }

    private bool DirDown(NavDir dir)
    {
        var d = _currentPad.DPad;
        var s = _currentPad.ThumbSticks.Left;
        return dir switch
        {
            NavDir.Up => d.Up == ButtonState.Pressed || s.Y > StickThreshold,
            NavDir.Down => d.Down == ButtonState.Pressed || s.Y < -StickThreshold,
            NavDir.Left => d.Left == ButtonState.Pressed || s.X < -StickThreshold,
            NavDir.Right => d.Right == ButtonState.Pressed || s.X > StickThreshold,
            _ => false,
        };
    }

    /// <summary>Bascule souris ↔ manette selon l'activité de la frame (pour l'affichage du curseur).</summary>
    private void UpdateActiveDevice()
    {
        if (PadActivity())
            _usingGamepad = true;
        else if (MouseActivity())
            _usingGamepad = false;
    }

    private bool PadActivity()
    {
        if (!_currentPad.IsConnected)
            return false;
        var b = _currentPad.Buttons;
        var anyButton = b.A == ButtonState.Pressed || b.B == ButtonState.Pressed ||
            b.X == ButtonState.Pressed || b.Y == ButtonState.Pressed ||
            b.Start == ButtonState.Pressed || b.Back == ButtonState.Pressed ||
            b.LeftShoulder == ButtonState.Pressed || b.RightShoulder == ButtonState.Pressed;
        var anyDir = _navWasDown[0] || _navWasDown[1] || _navWasDown[2] || _navWasDown[3];
        return anyButton || anyDir;
    }

    private bool MouseActivity() =>
        _currentMouse.Position != _previousMouse.Position ||
        WasLeftClicked || WasRightClicked || ScrollDelta != 0;
}
