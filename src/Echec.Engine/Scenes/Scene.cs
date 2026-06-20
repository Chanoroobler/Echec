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
}
