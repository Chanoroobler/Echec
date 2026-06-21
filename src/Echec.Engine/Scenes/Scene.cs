using Microsoft.Xna.Framework;

namespace Echec.Engine.Scenes;

/// <summary>
/// Base commune des scènes : donne accès au <see cref="GameContext"/>
/// et fournit des implémentations vides surchargeables.
/// </summary>
public abstract class Scene : IScene
{
    protected Scene(GameContext context) => Context = context;

    protected GameContext Context { get; }

    public virtual void Load() { }

    public virtual void Unload() { }

    public abstract void Update(GameTime gameTime);

    public abstract void Draw(GameTime gameTime);

    /// <summary>
    /// Dessine un fond plein écran sur le BACKBUFFER réel, sous le blit du canvas virtuel —
    /// permet d'étendre les effets de fond (eau) dans les bandes noires du letterbox. Appelé par
    /// la couche Game après <c>SetRenderTarget(null)</c> et avant le blit du canvas.
    /// <paramref name="canvasOffset"/>/<paramref name="canvasScale"/> = position et facteur
    /// d'agrandissement ENTIER du canvas dans l'écran réel (pour raccorder le repère). Vide par défaut.
    /// </summary>
    public virtual void DrawLetterboxBackground(Point realScreen, Point canvasOffset, int canvasScale) { }
}
