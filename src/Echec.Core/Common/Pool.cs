using System;
using System.Collections.Generic;

namespace Echec.Core.Common;

/// <summary>
/// Pool d'objets générique réutilisable : recycle des instances de référence pour éviter des
/// allocations répétées (et la pression GC) sur les chemins chauds. Pattern acquérir/rendre.
///
/// <para>À utiliser quand un même type d'objet de RÉFÉRENCE est créé en grand nombre et de façon
/// transitoire (ex. futures particules, projectiles, effets). Inutile pour les <c>struct</c>
/// (Rectangle, Vector2, Cell…) qui ne touchent pas le GC, et déconseillé pour les objets de
/// domaine immuables (ex. <see cref="Battle.Unit"/>) ou les listes conservées entre frames.</para>
///
/// Non thread-safe (un pool par contexte d'usage, mono-thread comme la boucle de jeu).
/// </summary>
public sealed class Pool<T> where T : class
{
    private readonly Stack<T> _free = new();
    private readonly Func<T> _factory;
    private readonly Action<T>? _reset;

    /// <param name="factory">Crée une instance neuve quand le pool est vide.</param>
    /// <param name="reset">Optionnel : remet l'objet à un état propre au moment de le rendre.</param>
    /// <param name="prewarm">Nombre d'instances créées d'avance.</param>
    public Pool(Func<T> factory, Action<T>? reset = null, int prewarm = 0)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _reset = reset;
        for (var i = 0; i < prewarm; i++)
            _free.Push(_factory());
    }

    /// <summary>Nombre d'instances disponibles (déjà allouées, en attente de réutilisation).</summary>
    public int FreeCount => _free.Count;

    /// <summary>Fournit une instance : recyclée si disponible, sinon créée à la demande.</summary>
    public T Get() => _free.Count > 0 ? _free.Pop() : _factory();

    /// <summary>Rend une instance au pool (réinitialisée si un <c>reset</c> a été fourni).</summary>
    public void Return(T item)
    {
        if (item == null)
            throw new ArgumentNullException(nameof(item));
        _reset?.Invoke(item);
        _free.Push(item);
    }
}
