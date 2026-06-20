using Echec.Engine;
using Echec.Engine.Audio;
using Echec.Engine.Display;
using Echec.Engine.Input;
using Echec.Engine.Rendering;
using Echec.Engine.Scenes;
using Echec.Engine.Settings;
using Echec.Engine.UI;
using Echec.Engine.UI.Text;
using Echec.Game.Scenes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Game;

/// <summary>
/// Composition root : crée le périphérique graphique, instancie les services
/// du moteur (input, scènes, audio, réglages) et délègue la boucle de jeu à
/// la scène active. Implémente <see cref="IDisplayService"/> car il possède
/// le <see cref="GraphicsDeviceManager"/>.
/// </summary>
public class EchecGame : Microsoft.Xna.Framework.Game, IDisplayService
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly SceneManager _scenes = new();
    private readonly InputManager _input = new();
    private readonly GameSettings _settings = new();

    private SpriteBatch _spriteBatch = null!;
    private AudioManager _audio = null!;
    private GameContext _context = null!;

    public EchecGame()
    {
        _graphics = new GraphicsDeviceManager(this);
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.Title = "Echec";
    }

    protected override void Initialize()
    {
        // Démarre dans la résolution par défaut des réglages.
        Apply(_settings.Display);
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _audio = new AudioManager(_settings.Audio);

        // Ressources UI pixel-art partagées (aucun pipeline de contenu requis).
        var pixel = Textures.CreatePixel(GraphicsDevice);
        var font = new PixelFont(pixel);
        var ditherTile = Textures.CreateDitherTile(GraphicsDevice, 8, Palette.Black3, Palette.Black2);
        var style = new UiStyle(pixel, ditherTile);

        _context = new GameContext(
            GraphicsDevice, _spriteBatch, Content, pixel, font, style,
            _input, _scenes, Window, _settings, _audio, this, Exit);

        _scenes.Change(new GameplayScene(_context));
    }

    protected override void Update(GameTime gameTime)
    {
        _input.Update();
        _scenes.Update(gameTime);
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);
        _scenes.Draw(gameTime);
        base.Draw(gameTime);
    }

    /// <summary>Applique résolution + plein écran (IDisplayService).</summary>
    public void Apply(DisplaySettings settings)
    {
        _graphics.IsFullScreen = settings.Fullscreen;
        _graphics.PreferredBackBufferWidth = settings.Width;
        _graphics.PreferredBackBufferHeight = settings.Height;
        _graphics.ApplyChanges();
    }
}
