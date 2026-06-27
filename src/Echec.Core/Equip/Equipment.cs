namespace Echec.Core.Equip;

/// <summary>Rareté d'un équipement : pool de coffre dont il provient (commun = coffre sans clé, rare = coffre à clé).</summary>
public enum EquipmentRarity { Common, Rare }

/// <summary>Nature d'un équipement : bonus de STAT, ou octroi d'un TRAIT de combat.</summary>
public enum EquipmentKind { Stat, Trait }

/// <summary>Stat augmentée par un équipement de type <see cref="EquipmentKind.Stat"/>.</summary>
public enum EquipStat { Hp, Damage, MoveRange, AttackRange }

/// <summary>
/// Équipement posable sur un pion (jamais le commandant) pendant la phase Équipement. UN SEUL par pion.
/// Donnée immuable et PARTAGÉE (flyweight, comme une <c>UnitClass</c>) : plusieurs pions ou emplacements
/// d'inventaire peuvent référencer la même instance. Deux familles : STAT (bonus plat sur une stat) et
/// TRAIT (ajoute un trait de combat, appliqué par le moteur via <c>Unit.HasTrait</c>). Résolu par
/// <see cref="Id"/> pour la sauvegarde et les tirages de coffre.
/// </summary>
public sealed class Equipment
{
    private Equipment(string id, string name, EquipmentRarity rarity, EquipmentKind kind,
        EquipStat stat, int amount, string? trait, string? icon)
    {
        Id = id;
        Name = name;
        Rarity = rarity;
        Kind = kind;
        Stat = stat;
        Amount = amount;
        Trait = trait;
        Icon = string.IsNullOrWhiteSpace(icon) ? id : icon!;
    }

    /// <summary>Identifiant stable : clé de sauvegarde et de tirage.</summary>
    public string Id { get; }
    public string Name { get; }
    public EquipmentRarity Rarity { get; }
    public EquipmentKind Kind { get; }

    /// <summary>Nom de l'icône (PNG 32×32 dans <c>Assets/Equipment/&lt;icon&gt;.png</c>). Par défaut = <see cref="Id"/>.</summary>
    public string Icon { get; }

    /// <summary>Stat visée (pertinent si <see cref="Kind"/> == <see cref="EquipmentKind.Stat"/>).</summary>
    public EquipStat Stat { get; }

    /// <summary>Bonus plat appliqué à <see cref="Stat"/> (0 pour un équipement de trait).</summary>
    public int Amount { get; }

    /// <summary>Nom canonique du trait octroyé (pertinent si <see cref="Kind"/> == Trait), sinon null.</summary>
    public string? Trait { get; }

    /// <summary>Crée un équipement de STAT (bonus plat sur une stat).</summary>
    public static Equipment OfStat(string id, string name, EquipStat stat, int amount,
        EquipmentRarity rarity = EquipmentRarity.Common, string? icon = null) =>
        new(id, name, rarity, EquipmentKind.Stat, stat, amount, null, icon);

    /// <summary>Crée un équipement de TRAIT (octroie un trait de combat, cf. <c>Battle.Trait</c>).</summary>
    public static Equipment OfTrait(string id, string name, string trait,
        EquipmentRarity rarity = EquipmentRarity.Common, string? icon = null) =>
        new(id, name, rarity, EquipmentKind.Trait, default, 0, trait, icon);

    /// <summary>Bonus apporté à <paramref name="stat"/> (0 si ce n'est pas un équipement de cette stat).</summary>
    public int BonusFor(EquipStat stat) => Kind == EquipmentKind.Stat && Stat == stat ? Amount : 0;

    /// <summary>Vrai si cet équipement octroie le trait <paramref name="trait"/>.</summary>
    public bool GrantsTrait(string trait) => Kind == EquipmentKind.Trait && Trait == trait;
}
