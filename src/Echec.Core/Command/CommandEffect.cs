using Echec.Core.Equip;

namespace Echec.Core.Command;

/// <summary>
/// Nature d'un <see cref="CommandEffect"/> : ce que le nœud d'arbre modifie une fois acheté.
/// </summary>
public enum CommandEffectKind
{
    /// <summary>Bonus de STAT sur le commandant seul.</summary>
    CommanderStat,

    /// <summary>Octroi d'un TRAIT de combat au commandant.</summary>
    CommanderTrait,

    /// <summary>Bonus de STAT sur toutes les unités NON essentielles (commandant exclu).</summary>
    UnitStat,

    /// <summary>Octroi d'un TRAIT de combat à toutes les unités non essentielles.</summary>
    UnitTrait,

    /// <summary>Agrandit la réserve (plafond de pions hors commandant).</summary>
    ReserveSlots,

    /// <summary>Agrandit le déploiement (pions posables sur le plateau, commandant compris).</summary>
    DeploySlots,

    /// <summary>Chaque fusion recrute EN PLUS une unité tier 1 déjà découverte.</summary>
    FusionRecruit,
}

/// <summary>
/// Comment l'<see cref="CommandEffect.Amount"/> d'un effet de STAT est mis à l'échelle.
/// </summary>
public enum CommandScale
{
    /// <summary>Bonus plat : <c>Amount</c> tel quel.</summary>
    Flat,

    /// <summary>
    /// Bonus × le nombre de PAIRES DE CLASSES DISTINCTES du roster (hors commandant) : classes
    /// distinctes ÷ 2, arrondi vers le bas. Recalculé à chaque phase de placement — récompense la
    /// variété de l'armée. Cf. <see cref="Campaign.Run.DistinctPairs"/>.
    /// </summary>
    PerDistinctPair,
}

/// <summary>
/// UN effet d'un nœud d'arbre de commandement. Modelé sur <see cref="EquipEffect"/> : soit un bonus de
/// STAT (avec une éventuelle mise à l'échelle, cf. <see cref="Scale"/>), soit l'octroi d'un TRAIT, soit
/// un effet de MÉTA (slots de réserve/déploiement, bonus de fusion) qui ne touche aucune unité mais la
/// <see cref="Campaign.Run"/>. La stat visée réutilise <see cref="EquipStat"/> : mêmes noms côté JSON
/// que pour les équipements (<c>hp</c>, <c>damage</c>, <c>moveRange</c>, <c>attackRange</c>).
/// </summary>
public sealed class CommandEffect
{
    private CommandEffect(CommandEffectKind kind, EquipStat stat, int amount, string? trait, CommandScale scale)
    {
        Kind = kind;
        Stat = stat;
        Amount = amount;
        Trait = trait;
        Scale = scale;
    }

    public CommandEffectKind Kind { get; }

    /// <summary>Stat visée (effets <see cref="CommandEffectKind.CommanderStat"/> / <see cref="CommandEffectKind.UnitStat"/>).</summary>
    public EquipStat Stat { get; }

    /// <summary>Bonus de base, avant mise à l'échelle (ou nombre de slots / de recrues).</summary>
    public int Amount { get; }

    /// <summary>Nom canonique du trait octroyé (cf. <c>Battle.Trait</c>), pour les effets de trait.</summary>
    public string? Trait { get; }

    /// <summary>Mise à l'échelle du bonus de stat (plat par défaut).</summary>
    public CommandScale Scale { get; }

    /// <summary>Vrai si l'effet vise le COMMANDANT (stat ou trait).</summary>
    public bool TargetsCommander => Kind is CommandEffectKind.CommanderStat or CommandEffectKind.CommanderTrait;

    /// <summary>Vrai si l'effet vise les unités NON essentielles (stat ou trait).</summary>
    public bool TargetsUnits => Kind is CommandEffectKind.UnitStat or CommandEffectKind.UnitTrait;

    /// <summary>Valeur effective du bonus, connaissant le nombre de paires de classes distinctes du roster.</summary>
    public int AmountFor(int distinctPairs) =>
        Scale == CommandScale.PerDistinctPair ? Amount * distinctPairs : Amount;

    public static CommandEffect CommanderStat(EquipStat stat, int amount, CommandScale scale = CommandScale.Flat) =>
        new(CommandEffectKind.CommanderStat, stat, amount, null, scale);

    public static CommandEffect CommanderTrait(string trait) =>
        new(CommandEffectKind.CommanderTrait, default, 0, trait, CommandScale.Flat);

    public static CommandEffect UnitStat(EquipStat stat, int amount, CommandScale scale = CommandScale.Flat) =>
        new(CommandEffectKind.UnitStat, stat, amount, null, scale);

    public static CommandEffect UnitTrait(string trait) =>
        new(CommandEffectKind.UnitTrait, default, 0, trait, CommandScale.Flat);

    public static CommandEffect ReserveSlots(int amount) =>
        new(CommandEffectKind.ReserveSlots, default, amount, null, CommandScale.Flat);

    public static CommandEffect DeploySlots(int amount) =>
        new(CommandEffectKind.DeploySlots, default, amount, null, CommandScale.Flat);

    public static CommandEffect FusionRecruit(int amount = 1) =>
        new(CommandEffectKind.FusionRecruit, default, amount, null, CommandScale.Flat);
}
