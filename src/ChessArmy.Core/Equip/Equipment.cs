using System.Collections.Generic;
using System.Linq;

namespace ChessArmy.Core.Equip;

/// <summary>
/// Rareté d'un équipement, du plus commun au plus rare (l'ordre compte : repli d'une rareté vers la
/// précédente si son pool est vide). Tirée à l'ouverture d'un coffre selon la phase + la « pitié »
/// (cf. <see cref="Campaign.Run.ResolveChestRarity"/>).
/// </summary>
public enum EquipmentRarity { Common, Rare, Legendary }

/// <summary>Stat augmentée par un effet d'équipement de type STAT.</summary>
public enum EquipStat { Hp, Damage, MoveRange, AttackRange }

/// <summary>
/// UN effet d'équipement : soit un bonus plat de STAT (<see cref="Stat"/> + <see cref="Amount"/>), soit
/// l'octroi d'un TRAIT de combat (<see cref="Trait"/>). Un <see cref="Equipment"/> en porte un ou plusieurs
/// (ex. « +1 portée » + trait « Balistique », ou « +5 Vie » + « +3 Puissance »).
/// </summary>
public sealed class EquipEffect
{
    private EquipEffect(EquipStat stat, int amount, string? trait)
    {
        Stat = stat;
        Amount = amount;
        Trait = trait;
    }

    /// <summary>Vrai si cet effet octroie un trait ; faux s'il augmente une stat.</summary>
    public bool IsTrait => Trait is not null;

    /// <summary>Stat visée (si <see cref="IsTrait"/> est faux).</summary>
    public EquipStat Stat { get; }

    /// <summary>Bonus plat appliqué à <see cref="Stat"/> (0 pour un effet de trait).</summary>
    public int Amount { get; }

    /// <summary>Nom canonique du trait octroyé (si <see cref="IsTrait"/>), sinon null.</summary>
    public string? Trait { get; }

    /// <summary>Crée un effet de STAT (bonus plat sur une stat).</summary>
    public static EquipEffect OfStat(EquipStat stat, int amount) => new(stat, amount, null);

    /// <summary>Crée un effet de TRAIT (octroie un trait de combat, cf. <c>Battle.Trait</c>).</summary>
    public static EquipEffect OfTrait(string trait) => new(default, 0, trait);
}

/// <summary>
/// Équipement posable sur un pion (jamais le commandant) pendant la phase Équipement. UN SEUL par pion.
/// Donnée immuable et PARTAGÉE (flyweight, comme une <c>UnitClass</c>) : plusieurs pions ou emplacements
/// d'inventaire peuvent référencer la même instance. Porte UN OU PLUSIEURS <see cref="EquipEffect"/>
/// (bonus de stat et/ou octroi de trait), tous appliqués au pion : les bonus de stat s'additionnent
/// (<see cref="BonusFor"/>), les traits s'ajoutent (<see cref="GrantsTrait"/>, appliqué par le moteur via
/// <c>Unit.HasTrait</c>). Résolu par <see cref="Id"/> pour la sauvegarde et les tirages de coffre.
/// </summary>
public sealed class Equipment
{
    private Equipment(string id, string name, EquipmentRarity rarity, IReadOnlyList<EquipEffect> effects, string? icon)
    {
        Id = id;
        Name = name;
        Rarity = rarity;
        Effects = effects;
        Icon = string.IsNullOrWhiteSpace(icon) ? id : icon!;
    }

    /// <summary>Identifiant stable : clé de sauvegarde et de tirage.</summary>
    public string Id { get; }
    public string Name { get; }
    public EquipmentRarity Rarity { get; }

    /// <summary>Nom de l'icône (PNG 32×32 dans <c>Assets/Equipment/&lt;icon&gt;.png</c>). Par défaut = <see cref="Id"/>.</summary>
    public string Icon { get; }

    /// <summary>Effets appliqués au pion (au moins un : bonus de stat et/ou octroi de trait).</summary>
    public IReadOnlyList<EquipEffect> Effects { get; }

    /// <summary>Bonus TOTAL apporté à <paramref name="stat"/> (somme des effets de stat qui la visent ; 0 sinon).</summary>
    public int BonusFor(EquipStat stat) =>
        Effects.Where(e => !e.IsTrait && e.Stat == stat).Sum(e => e.Amount);

    /// <summary>Vrai si l'un des effets octroie le trait <paramref name="trait"/>.</summary>
    public bool GrantsTrait(string trait) => Effects.Any(e => e.Trait == trait);

    /// <summary>Traits octroyés par cet équipement (0, 1 ou plusieurs).</summary>
    public IEnumerable<string> Traits => Effects.Where(e => e.IsTrait).Select(e => e.Trait!);

    /// <summary>Effets de STAT portés (pour l'affichage : « +N stat »).</summary>
    public IEnumerable<EquipEffect> StatBonuses => Effects.Where(e => !e.IsTrait);

    /// <summary>Vrai si l'équipement octroie au moins un trait (sert à la « couleur » d'affichage).</summary>
    public bool GrantsAnyTrait => Effects.Any(e => e.IsTrait);

    /// <summary>Crée un équipement à UN effet de STAT (bonus plat sur une stat).</summary>
    public static Equipment OfStat(string id, string name, EquipStat stat, int amount,
        EquipmentRarity rarity = EquipmentRarity.Common, string? icon = null) =>
        new(id, name, rarity, new[] { EquipEffect.OfStat(stat, amount) }, icon);

    /// <summary>Crée un équipement à UN effet de TRAIT (octroie un trait de combat, cf. <c>Battle.Trait</c>).</summary>
    public static Equipment OfTrait(string id, string name, string trait,
        EquipmentRarity rarity = EquipmentRarity.Common, string? icon = null) =>
        new(id, name, rarity, new[] { EquipEffect.OfTrait(trait) }, icon);

    /// <summary>Crée un équipement à PLUSIEURS effets (mélange libre de stats et de traits).</summary>
    public static Equipment Of(string id, string name, EquipmentRarity rarity,
        IReadOnlyList<EquipEffect> effects, string? icon = null) =>
        new(id, name, rarity, effects, icon);
}
