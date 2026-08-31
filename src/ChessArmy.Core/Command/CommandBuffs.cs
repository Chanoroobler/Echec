using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Command;

/// <summary>
/// Bonus AGRÉGÉS d'un arbre de commandement pour UNE cible (le commandant, ou les unités non essentielles) :
/// somme des effets de stat des nœuds achetés + traits octroyés. C'est la TROISIÈME source de bonus d'une
/// <see cref="Battle.Unit"/>, à côté de sa classe et de son <see cref="Equipment"/>.
///
/// Immuable et RECALCULÉ à chaque phase de placement (les effets « par paire » dépendent du roster du
/// moment, cf. <see cref="CommandScale.PerDistinctPair"/>), puis passé à <see cref="Campaign.UnitSpec.Spawn"/>.
/// Les ennemis n'en reçoivent jamais : ils spawnent avec <see cref="None"/>.
/// </summary>
public sealed class CommandBuffs
{
    /// <summary>Aucun bonus (ennemis, tests, commandant sans arbre acheté).</summary>
    public static readonly CommandBuffs None = new(new Dictionary<EquipStat, int>(), System.Array.Empty<string>());

    private readonly IReadOnlyDictionary<EquipStat, int> _stats;

    private CommandBuffs(IReadOnlyDictionary<EquipStat, int> stats, IReadOnlyList<string> traits)
    {
        _stats = stats;
        Traits = traits;
    }

    /// <summary>Traits de combat octroyés (cf. <c>Battle.Trait</c>).</summary>
    public IReadOnlyList<string> Traits { get; }

    /// <summary>Vrai si ces bonus ne changent rien (évite d'allouer / de recalculer côté appelant).</summary>
    public bool IsEmpty => _stats.Count == 0 && Traits.Count == 0;

    /// <summary>Bonus total apporté à <paramref name="stat"/> (0 si aucun).</summary>
    public int BonusFor(EquipStat stat) => _stats.GetValueOrDefault(stat, 0);

    /// <summary>Vrai si ces bonus octroient le trait <paramref name="trait"/>.</summary>
    public bool GrantsTrait(string trait) => Traits.Contains(trait);

    /// <summary>
    /// Agrège les effets qui visent la cible voulue. <paramref name="commander"/> vrai → on ne retient que
    /// les effets <see cref="CommandEffect.TargetsCommander"/> ; faux → <see cref="CommandEffect.TargetsUnits"/>.
    /// Les effets de méta (slots, fusion) sont ignorés ici (lus directement par la <see cref="Campaign.Run"/>).
    /// <paramref name="distinctPairs"/> met à l'échelle les bonus « par paire ». Pour une unité,
    /// <paramref name="targetDomaine"/> filtre les effets restreints à un domaine (cf. <see cref="CommandEffect.Domaine"/>) ;
    /// <paramref name="domaineCount"/> alimente l'échelle « par unité de domaine », <paramref name="deployedCount"/>
    /// l'échelle « par unité de domaine DÉPLOYÉE » (fournie par la scène, qui seule connaît le plateau).
    /// </summary>
    public static CommandBuffs From(IEnumerable<CommandEffect> effects, bool commander, int distinctPairs,
        Domaine? targetDomaine = null, Func<Domaine, int>? domaineCount = null,
        Func<Domaine, int>? deployedCount = null)
    {
        var stats = new Dictionary<EquipStat, int>();
        var traits = new List<string>();

        foreach (var e in effects)
        {
            if (commander ? !e.TargetsCommander : !e.TargetsUnits)
                continue;

            // Effets d'UNITÉ restreints à un domaine : ignorés pour une unité d'un autre domaine. Le
            // commandant, lui, n'est jamais filtré (son domaine sert seulement à l'échelle par domaine).
            if (!commander && e.Domaine is { } fd && targetDomaine != fd)
                continue;

            if (e.Kind is CommandEffectKind.CommanderTrait or CommandEffectKind.UnitTrait)
            {
                if (e.Trait is { } t && !traits.Contains(t))
                    traits.Add(t);
                continue;
            }

            var amount = e.AmountFor(distinctPairs, domaineCount, deployedCount);
            if (amount != 0)
                stats[e.Stat] = stats.GetValueOrDefault(e.Stat, 0) + amount;
        }

        return stats.Count == 0 && traits.Count == 0 ? None : new CommandBuffs(stats, traits);
    }
}
