namespace Echec.Core.Battle;

/// <summary>
/// Unité jouable : type, camp, points de vie et dégâts. État mutable (les PV
/// baissent au combat). Pour le POC, seules ces stats existent.
/// </summary>
public sealed class Unit
{
    public Unit(UnitType type, Faction faction, int maxHp, int damage)
    {
        Type = type;
        Faction = faction;
        MaxHp = maxHp;
        Hp = maxHp;
        Damage = damage;
    }

    public UnitType Type { get; }
    public Faction Faction { get; }
    public int MaxHp { get; }
    public int Damage { get; }
    public int Hp { get; private set; }

    public bool IsAlive => Hp > 0;

    public void TakeDamage(int amount) => Hp = System.Math.Max(0, Hp - amount);
}

/// <summary>Fabrique d'unités avec leurs stats par défaut (POC).</summary>
public static class Units
{
    public static Unit Soldier(Faction faction) =>
        new(UnitType.Soldier, faction, maxHp: 10, damage: 4);
}
