using Echec.Core.Equip;

namespace Echec.Core.Battle;

/// <summary>
/// Unité jouable. Deux axes : le <see cref="Domaine"/> (qui fournit le style de
/// déplacement) et la <see cref="Class"/> (asset + stats : PV, dégâts, portée).
/// Le level-up n'est pas encore défini : l'unité reste sur la classe qu'on lui donne.
/// Un éventuel <see cref="Equipment"/> ajoute un bonus de stat OU un trait (cf. <see cref="HasTrait"/>).
/// </summary>
public sealed class Unit
{
    public Unit(Domaine domaine, Faction faction, UnitClass unitClass, Equipment? equipment = null)
    {
        Domaine = domaine;
        Faction = faction;
        Class = unitClass;
        Equipment = equipment;
        Hp = MaxHp;   // PV pleins, bonus d'équipement inclus
    }

    public Domaine Domaine { get; }
    public Faction Faction { get; }
    public UnitClass Class { get; }

    /// <summary>Équipement porté (collé au pion), ou null. Stat ou trait, jamais sur le commandant.</summary>
    public Equipment? Equipment { get; }

    public int Hp { get; private set; }

    /// <summary>
    /// Unité « pivot » dont la mort décide la partie : le commandant (joueur) ou le
    /// boss (ennemi). Voir <see cref="Match"/> pour les conditions de victoire.
    /// </summary>
    public bool IsEssential { get; init; }

    public MovementKind MovementKind => Movement.Kind(Domaine);
    public int MaxHp => Class.MaxHp + EquipBonus(EquipStat.Hp);
    public int Damage => Class.Damage + EquipBonus(EquipStat.Damage);
    public int MoveRange => Class.MoveRange + EquipBonus(EquipStat.MoveRange);
    public int AttackRange => Class.AttackRange + EquipBonus(EquipStat.AttackRange);

    /// <summary>
    /// Portée d'attaque MINIMALE effective : le trait « Zone morte » interdit de frapper au contact
    /// (min = 2). Sinon la valeur de la classe (1 par défaut). Appliquée en ligne droite seulement
    /// (cf. <see cref="Match.AttackTargets"/>) : le tir diagonal au contact reste possible.
    /// </summary>
    public int MinAttackRange =>
        System.Math.Max(Class.MinAttackRange, HasTrait(Battle.Trait.ZoneMorte) ? 2 : 1);

    /// <summary>Bonus de l'équipement porté sur une stat (0 si aucun, ou si l'équipement vise une autre stat).</summary>
    private int EquipBonus(EquipStat stat) => Equipment?.BonusFor(stat) ?? 0;

    public bool IsAlive => Hp > 0;

    public void TakeDamage(int amount) => Hp = System.Math.Max(0, Hp - amount);

    /// <summary>Soigne l'unité (borné à ses PV max).</summary>
    public void Heal(int amount) => Hp = System.Math.Min(MaxHp, Hp + amount);

    /// <summary>
    /// Vrai si l'unité porte ce <paramref name="trait"/> (cf. <see cref="Trait"/>) — par sa classe OU par
    /// son <see cref="Equipment"/>. « Traverse allié » est porté par <see cref="UnitClass.PiercesAllies"/>
    /// et non par la liste de traits.
    /// </summary>
    public bool HasTrait(string trait)
    {
        if (Equipment is { } e && e.GrantsTrait(trait))
            return true;
        if (trait == Battle.Trait.TraverseAllie)
            return Class.PiercesAllies;
        foreach (var t in Class.Traits)
            if (t == trait)
                return true;
        return false;
    }
}

/// <summary>Fabrique d'unités : démarre sur la classe de base du domaine.</summary>
public static class Units
{
    public static Unit Of(Domaine domaine, Faction faction) =>
        new(domaine, faction, Domaines.Of(domaine).BaseClass);

    /// <summary>Soldat (base du domaine Dame) — l'unité de troupe élémentaire du joueur.</summary>
    public static Unit Soldat(Faction faction) => Of(Domaine.Dame, faction);
}
