using Echec.Engine.Input;
using Echec.Engine.Scenes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Engine;

/// <summary>
/// Conteneur de services partagés transmis à chaque scène.
/// Évite que les scènes dépendent directement de la classe Game.
/// </summary>
public sealed class GameContext
{
    public GameContext(
        GraphicsDevice graphicsDevice,
        SpriteBatch spriteBatch,
        ContentManager content,
        InputManager input,
        SceneManager scenes,
        GameWindow window)
    {
        GraphicsDevice = graphicsDevice;
        SpriteBatch = spriteBatch;
        Content = content;
        Input = input;
        Scenes = scenes;
        Window = window;
    }

    public GraphicsDevice GraphicsDevice { get; }
    public SpriteBatch SpriteBatch { get; }
    public ContentManager Content { get; }
    public InputManager Input { get; }
    public SceneManager Scenes { get; }
    public GameWindow Window { get; }
}
