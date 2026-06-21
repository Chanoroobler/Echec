using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Echec.Engine.Input;

/// <summary>
/// Capture l'état clavier/souris et expose la détection de fronts
/// (touche tout juste pressée, clic tout juste effectué).
/// Appeler <see cref="Update"/> une fois par frame, avant la logique de jeu.
/// </summary>
public sealed class InputManager
{
    private KeyboardState _previousKeyboard;
    private KeyboardState _currentKeyboard;
    private MouseState _previousMouse;
    private MouseState _currentMouse;

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

    public void Update()
    {
        _previousKeyboard = _currentKeyboard;
        _currentKeyboard = Keyboard.GetState();
        _previousMouse = _currentMouse;
        _currentMouse = Mouse.GetState();
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
}
