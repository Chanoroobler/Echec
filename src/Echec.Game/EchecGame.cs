using Echec.Engine;
using Echec.Engine.Input;
using Echec.Engine.Scenes;
using Echec.Game.Scenes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Echec.Game;

/// <summary>
/// Composition root : crée le périphérique graphique, instancie les services
/// du moteur (input, scènes) et délègue la boucle de jeu à la scène active.
/// </summary>
public class EchecGame : Microsoft.Xna.Framework.Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly SceneManager _scenes = new();
    private readonly InputManager _input = new();

    private SpriteBatch _spriteBatch = null!;
    private GameContext _context = null!;

    public EchecGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 512,
            PreferredBackBufferHeight = 512
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.Title = "Echec";
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _context = new GameContext(GraphicsDevice, _spriteBatch, Content, _input, _scenes, Window);
        _scenes.Change(new GameplayScene(_context));
    }

    protected override void Update(GameTime gameTime)
    {
        _input.Update();

        if (_input.IsKeyDown(Keys.Escape))
            Exit();

        _scenes.Update(gameTime);
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);
        _scenes.Draw(gameTime);
        base.Draw(gameTime);
    }
}
