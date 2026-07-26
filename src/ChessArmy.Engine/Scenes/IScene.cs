using Microsoft.Xna.Framework;

namespace ChessArmy.Engine.Scenes;

/// <summary>
/// Une scène = un écran logique du jeu (menu, partie, pause...).
/// </summary>
public interface IScene
{
    void Load();
    void Unload();
    void Update(GameTime gameTime);
    void Draw(GameTime gameTime);
}
