using Microsoft.Xna.Framework;

namespace Echec.Engine.Scenes;

/// <summary>
/// Gère la scène active et les transitions entre scènes.
/// </summary>
public sealed class SceneManager
{
    private IScene? _current;

    public IScene? Current => _current;

    /// <summary>Remplace la scène courante (décharge l'ancienne, charge la nouvelle).</summary>
    public void Change(IScene scene)
    {
        _current?.Unload();
        _current = scene;
        _current.Load();
    }

    public void Update(GameTime gameTime) => _current?.Update(gameTime);

    public void Draw(GameTime gameTime) => _current?.Draw(gameTime);
}
