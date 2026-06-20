using Microsoft.Xna.Framework;

namespace Echec.Engine.Scenes;

/// <summary>
/// Pile de scènes. La scène du sommet reçoit les <see cref="Update"/> ;
/// toutes les scènes sont dessinées de bas en haut (overlay = la pause se
/// dessine par-dessus le jeu, qui reste visible mais figé).
/// </summary>
public sealed class SceneManager
{
    private readonly List<IScene> _stack = new();

    public IScene? Current => _stack.Count > 0 ? _stack[^1] : null;
    public int Count => _stack.Count;

    /// <summary>Vide la pile et démarre une nouvelle scène (changement d'écran complet).</summary>
    public void Change(IScene scene)
    {
        while (_stack.Count > 0)
            Pop();
        Push(scene);
    }

    /// <summary>Empile une scène par-dessus l'actuelle (overlay).</summary>
    public void Push(IScene scene)
    {
        _stack.Add(scene);
        scene.Load();
    }

    /// <summary>Dépile la scène du sommet.</summary>
    public void Pop()
    {
        if (_stack.Count == 0)
            return;

        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        top.Unload();
    }

    public void Update(GameTime gameTime) => Current?.Update(gameTime);

    public void Draw(GameTime gameTime)
    {
        foreach (var scene in _stack)
            scene.Draw(gameTime);
    }
}
