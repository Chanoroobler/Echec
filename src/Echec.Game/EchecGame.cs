using Echec.Engine;
using Echec.Engine.Audio;
using Echec.Engine.Display;
using Echec.Engine.Input;
using Echec.Engine.Rendering;
using Echec.Engine.Scenes;
using Echec.Engine.Settings;
using Echec.Engine.UI;
using Echec.Engine.UI.Text;
using Echec.Core.Battle;
using Echec.Core.Battle.Config;
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

    // Rendu pixel-perfect : les scènes dessinent dans un canvas virtuel, agrandi d'un
    // facteur ENTIER vers une zone 16:9 de l'écran (aucun asset n'est jamais déformé).
    // Hauteur de référence visée pour le canvas (taille à laquelle l'UI est calibrée).
    private const int DesignHeight = 720;
    private RenderTarget2D? _virtualTarget;
    private Rectangle _virtualDest;

    public EchecGame()
    {
        _graphics = new GraphicsDeviceManager(this);
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.Title = "Echec";
    }

    protected override void Initialize()
    {
        LoadUnitConfig();
        // Démarre dans la résolution par défaut des réglages.
        Apply(_settings.Display);
        base.Initialize();
    }

    /// <summary>Charge les classes depuis Assets/Config/units.json (repli sur les défauts si absent/invalide).</summary>
    private static void LoadUnitConfig()
    {
        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets/Config/units.json");
        if (!System.IO.File.Exists(path))
            return;
        try
        {
            var json = System.IO.File.ReadAllText(path);
            Domaines.Load(DomaineCatalog.FromJson(json));
            Commandes.Load(DomaineCatalog.CommandesFromJson(json));
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"units.json ignoré (invalide) : {ex.Message}");
        }
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

        ConfigureVirtualScreen();
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
        // 1. Les scènes dessinent dans la cible virtuelle (résolution logique fixe).
        GraphicsDevice.SetRenderTarget(_virtualTarget);
        GraphicsDevice.Clear(Color.Black);
        _scenes.Draw(gameTime);

        // 2. On agrandit la cible vers l'écran réel (échelle entière, PointClamp = net).
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Color.Black);
        _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
        _spriteBatch.Draw(_virtualTarget, _virtualDest, Color.White);
        _spriteBatch.End();

        base.Draw(gameTime);
    }

    /// <summary>Applique résolution + plein écran (IDisplayService).</summary>
    public void Apply(DisplaySettings settings)
    {
        _graphics.IsFullScreen = settings.Fullscreen;
        _graphics.PreferredBackBufferWidth = settings.Width;
        _graphics.PreferredBackBufferHeight = settings.Height;
        _graphics.ApplyChanges();
        ConfigureVirtualScreen();
    }

    /// <summary>
    /// (Re)calcule le canvas virtuel et sa zone d'affichage. On cale d'abord une zone
    /// 16:9 dans l'écran (bandes noires UNIQUEMENT hors 16:9), puis on choisit un facteur
    /// d'agrandissement ENTIER (canvas visé ≈ 720 de haut) et on définit le canvas comme
    /// zone ÷ facteur : le blit ×facteur remplit alors EXACTEMENT la zone 16:9 — donc
    /// aucune bande en 16:9 — tout en restant strictement entier (pixel-perfect).
    /// </summary>
    private void ConfigureVirtualScreen()
    {
        // Avant LoadContent (premier Apply via Initialize) : rien à configurer encore.
        if (_context == null)
            return;

        var pp = GraphicsDevice.PresentationParameters;
        int realW = pp.BackBufferWidth;
        int realH = pp.BackBufferHeight;

        // 1. Zone d'affichage en 16:9 (pilier si plus large, letterbox si plus haut).
        const float targetAspect = 16f / 9f;
        int stageW, stageH;
        if ((float)realW / realH > targetAspect)
        {
            stageH = realH;
            stageW = (int)(realH * targetAspect);
        }
        else
        {
            stageW = realW;
            stageH = (int)(realW / targetAspect);
        }

        // 2. Facteur d'agrandissement entier, canvas visé ≈ DesignHeight de haut.
        int scale = System.Math.Max(1, (int)System.Math.Round(
            stageH / (float)DesignHeight, System.MidpointRounding.AwayFromZero));

        // 3. Canvas = zone ÷ facteur → le blit ×facteur recouvre la zone 16:9.
        int canvasW = System.Math.Max(1, stageW / scale);
        int canvasH = System.Math.Max(1, stageH / scale);

        if (_virtualTarget == null || _virtualTarget.Width != canvasW || _virtualTarget.Height != canvasH)
        {
            _virtualTarget?.Dispose();
            _virtualTarget = new RenderTarget2D(GraphicsDevice, canvasW, canvasH);
        }

        int dispW = canvasW * scale;
        int dispH = canvasH * scale;
        var offset = new Point((realW - dispW) / 2, (realH - dispH) / 2);
        _virtualDest = new Rectangle(offset.X, offset.Y, dispW, dispH);

        _context.VirtualResolution = new Point(canvasW, canvasH);
        _input.SetViewport(offset, scale);
    }
}
