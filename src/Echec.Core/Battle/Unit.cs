namespace Echec.Core.Battle;

/// <summary>
/// Unité jouable. Deux axes : le <see cref="Domaine"/> (qui fournit le style de
/// déplacement) et la <see cref="Class"/> (asset + stats : PV, dégâts, portée).
/// Le level-up n'est pas encore défini : l'unité reste sur la classe qu'on lui donne.
/// </summary>
public sealed class Unit
{
    public Unit(Domaine domaine, Faction faction, UnitClass unitClass)
    {
        Domaine = domaine;
        Faction = faction;
        Class = unitClass;
        Hp = unitClass.MaxHp;
    }

    public Domaine Domaine { get; }
    public Faction Faction { get; }
    public UnitClass Class { get; }
    public int Hp { get; private set; }

    /// <summary>
    /// Unité « pivot » dont la mort décide la partie : le commandant (joueur) ou le
    /// boss (ennemi). Voir <see cref="Match"/> pour les conditions de victoire.
    /// </summary>
    public bool IsEssential { get; init; }

    public MovementKind MovementKind => Movement.Kind(Domaine);
    public int MaxHp => Class.MaxHp;
    public int Damage => Class.Damage;
    public int MoveRange => Class.MoveRange;
    public int AttackRange => Class.AttackRange;

    public bool IsAlive => Hp > 0;

    public void TakeDamage(int amount) => Hp = System.Math.Max(0, Hp - amount);
}

/// <summary>Fabrique d'unités : démarre sur la classe de base du domaine.</summary>
public static class Units
{
    public static Unit Of(Domaine domaine, Faction faction) =>
        new(domaine, faction, Domaines.Of(domaine).BaseClass);

    public static Unit Pion(Faction faction) => Of(Domaine.Pion, faction);
}
