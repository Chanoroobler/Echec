using Echec.Core.Command;
using Echec.Core.Equip;

namespace Echec.Core.Battle;

/// <summary>
/// Unité jouable. Deux axes : le <see cref="Domaine"/> (qui fournit le style de
/// déplacement) et la <see cref="Class"/> (asset + stats : PV, dégâts, portée).
/// Le level-up n'est pas encore défini : l'unité reste sur la classe qu'on lui donne.
///
/// Trois sources de stats et de traits, cumulatives : la <see cref="Class"/>, un éventuel
/// <see cref="Equipment"/> (collé au pion) et les <see cref="Buffs"/> de l'arbre de commandement
/// (bonus d'armée du joueur, figés au placement — les ennemis n'en ont jamais).
/// </summary>
public sealed class Unit
{
    public Unit(Domaine domaine, Faction faction, UnitClass unitClass, Equipment? equipment = null,
        CommandBuffs? buffs = null)
    {
        Domaine = domaine;
        Faction = faction;
        Class = unitClass;
        Equipment = equipment;
        Buffs = buffs ?? CommandBuffs.None;
        Hp = MaxHp;   // PV pleins, bonus d'équipement et d'arbre inclus
    }

    public Domaine Domaine { get; }
    public Faction Faction { get; }
    public UnitClass Class { get; }

    /// <summary>
    /// Domaine du pattern d'ATTAQUE (directions + glissé/sauté) : celui de la classe s'il diffère, sinon le
    /// domaine de DÉPLACEMENT. Cavalier monté (archer) : déplacement en L (<see cref="Domaine"/> Cavalier),
    /// mais tir en lignes (attaque = Dame). Voir <see cref="UnitClass.AttackDomaine"/> et <see cref="Match"/>.
    /// </summary>
    public Domaine AttackDomaine => Class.AttackDomaine ?? Domaine;

    /// <summary>Équipement porté (collé au pion), ou null. Stat ou trait, jamais sur le commandant.</summary>
    public Equipment? Equipment { get; }

    /// <summary>
    /// Bonus de l'arbre de commandement applicables à CETTE unité (ceux du commandant, ou ceux des troupes).
    /// <see cref="CommandBuffs.None"/> pour tout ennemi. Calculés au placement par <see cref="Campaign.Run"/>.
    /// </summary>
    public CommandBuffs Buffs { get; }

    public int Hp { get; private set; }

    /// <summary>
    /// Unité « pivot » dont la mort décide la partie : le commandant (joueur) ou le
    /// boss (ennemi). Voir <see cref="Match"/> pour les conditions de victoire.
    /// </summary>
    public bool IsEssential { get; init; }

    /// <summary>
    /// Comportement d'IA de cette unité (ennemis seulement). <see cref="AiKind.Normal"/> par défaut :
    /// fonce sur le joueur. <see cref="AiKind.Defensif"/> : garde une position (mission spéciale),
    /// posé à la pose de la vague selon la case de spawn de la map. Voir <see cref="EnemyAi"/>.
    /// </summary>
    public AiKind AiKind { get; set; } = AiKind.Normal;

    public MovementKind MovementKind => Movement.Kind(Domaine);
    public int MaxHp => Stat(EquipStat.Hp, Class.MaxHp);
    public int Damage => Stat(EquipStat.Damage, Class.Damage);
    public int MoveRange => Stat(EquipStat.MoveRange, Class.MoveRange);
    public int AttackRange => Stat(EquipStat.AttackRange, Class.AttackRange);

    /// <summary>Stat effective : valeur de la classe + bonus d'équipement + bonus d'arbre. Jamais négative.</summary>
    private int Stat(EquipStat stat, int fromClass) =>
        System.Math.Max(0, fromClass + EquipBonus(stat) + Buffs.BonusFor(stat));

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
    /// Vrai si l'unité porte ce <paramref name="trait"/> (cf. <see cref="Trait"/>) — par sa classe, par son
    /// <see cref="Equipment"/> OU par les <see cref="Buffs"/> de l'arbre de commandement. « Traverse allié »
    /// est porté par <see cref="UnitClass.PiercesAllies"/> et non par la liste de traits.
    /// </summary>
    public bool HasTrait(string trait)
    {
        if (Equipment is { } e && e.GrantsTrait(trait))
            return true;
        if (Buffs.GrantsTrait(trait))
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
