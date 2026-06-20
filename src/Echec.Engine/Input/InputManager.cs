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

    public Point MousePosition => _currentMouse.Position;

    /// <summary>Vrai uniquement à la frame du clic gauche.</summary>
    public bool WasLeftClicked =>
        _currentMouse.LeftButton == ButtonState.Pressed &&
        _previousMouse.LeftButton == ButtonState.Released;
}
