using Echec.Engine.Audio;
using Echec.Engine.Display;
using Echec.Engine.Input;
using Echec.Engine.Persistence;
using Echec.Engine.Scenes;
using Echec.Engine.Settings;
using Echec.Engine.UI;
using Echec.Engine.UI.Text;
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
        Texture2D pixel,
        PixelFont font,
        UiStyle style,
        InputManager input,
        SceneManager scenes,
        GameWindow window,
        GameSettings settings,
        AudioManager audio,
        SoundBank sounds,
        IDisplayService display,
        SaveService saves,
        Action quit)
    {
        GraphicsDevice = graphicsDevice;
        SpriteBatch = spriteBatch;
        Content = content;
        Pixel = pixel;
        Font = font;
        Style = style;
        Input = input;
        Scenes = scenes;
        Window = window;
        Settings = settings;
        Audio = audio;
        Sounds = sounds;
        Display = display;
        Saves = saves;
        Quit = quit;
        VirtualResolution = new Point(1280, 720);
    }

    /// <summary>
    /// Résolution logique dans laquelle les scènes dessinent (rendu pixel-perfect mis
    /// à l'échelle vers l'écran réel par la couche Game). L'UI s'y réfère au lieu du
    /// viewport réel pour rester cohérente quelle que soit la résolution d'écran.
    /// </summary>
    public Point VirtualResolution { get; set; }

    public GraphicsDevice GraphicsDevice { get; }
    public SpriteBatch SpriteBatch { get; }
    public ContentManager Content { get; }

    /// <summary>Texture 1×1 blanche partagée (rectangles, police pixel).</summary>
    public Texture2D Pixel { get; }

    /// <summary>Police bitmap pixel-art partagée.</summary>
    public PixelFont Font { get; }

    /// <summary>Style d'UI partagé (panneaux/boutons en relief).</summary>
    public UiStyle Style { get; }

    public InputManager Input { get; }
    public SceneManager Scenes { get; }
    public GameWindow Window { get; }
    public GameSettings Settings { get; }
    public AudioManager Audio { get; }

    /// <summary>Banque d'effets sonores (clé d'action → WAV), pilotée par Assets/Config/sounds.json.</summary>
    public SoundBank Sounds { get; }

    public IDisplayService Display { get; }

    /// <summary>Persistance disque : réglages + slots de progression (%AppData%\Echec).</summary>
    public SaveService Saves { get; }

    /// <summary>Ferme l'application (fourni par la couche Game).</summary>
    public Action Quit { get; }
}
