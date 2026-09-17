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
/// <summary>
/// CIBLE d'une agrégation de bonus d'arbre. Les trois sont EXCLUSIVES : un effet <c>commanderStat</c> ne
/// touche pas le compagnon d'un DUO, et réciproquement — c'est ce qui permet à l'arbre du commandant DUO
/// d'améliorer Basile ou l'artisan séparément.
/// </summary>
public enum BuffTarget
{
    /// <summary>Les pions NON essentiels (l'armée).</summary>
    Units,

    /// <summary>Le commandant lui-même.</summary>
    Commander,

    /// <summary>Le SECOND meneur d'un commandant DUO (cf. <see cref="Campaign.UnitSpec.Companion"/>).</summary>
    Companion,
}

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
    /// Copie de ces bonus avec <paramref name="amount"/> EN PLUS sur <paramref name="stat"/> (rendue telle
    /// quelle si le montant est nul). Sert aux bonus qui ne viennent pas d'un nœud mais de ce que la run a
    /// ramassé — les PV max gagnés sur le terrain par les meneurs du DUO (cf. <c>Run.LeaderBonusHp</c>).
    /// </summary>
    public CommandBuffs Plus(EquipStat stat, int amount)
    {
        if (amount == 0)
            return this;
        var stats = new Dictionary<EquipStat, int>(_stats);
        stats[stat] = stats.GetValueOrDefault(stat, 0) + amount;
        return new CommandBuffs(stats, Traits);
    }

    /// <summary>
    /// Agrège les effets qui visent la cible voulue (<paramref name="target"/> : les troupes, le commandant,
    /// ou le second meneur d'un DUO — les trois jeux d'effets sont disjoints).
    /// Les effets de méta (slots, fusion) sont ignorés ici (lus directement par la <see cref="Campaign.Run"/>).
    /// <paramref name="distinctPairs"/> met à l'échelle les bonus « par paire ». Pour une unité,
    /// <paramref name="targetDomaine"/> filtre les effets restreints à un domaine (cf. <see cref="CommandEffect.Domaine"/>) ;
    /// <paramref name="domaineCount"/> alimente l'échelle « par unité de domaine », <paramref name="deployedCount"/>
    /// l'échelle « par unité de domaine DÉPLOYÉE » (fournie par la scène, qui seule connaît le plateau).
    /// </summary>
    /// <param name="equippedItems">
    /// Nombre d'équipements POSSÉDÉS par l'armée (portés ou en réserve) : échelle
    /// <see cref="CommandScale.PerEquippedItem"/> du commandant MARCHAND. 0 par défaut → ces bonus valent 0.
    /// </param>
    /// <param name="ownEquippedItems">
    /// Nombre d'équipements que porte la CIBLE elle-même : échelle <see cref="CommandScale.PerOwnEquippedItem"/>.
    /// 0 par défaut → ces bonus valent 0 (cas de tous les pions et des commandants sans emplacement).
    /// </param>
    public static CommandBuffs From(IEnumerable<CommandEffect> effects, BuffTarget target, int distinctPairs,
        Domaine? targetDomaine = null, Func<Domaine, int>? domaineCount = null,
        Func<Domaine, int>? deployedCount = null, int equippedItems = 0, int ownEquippedItems = 0)
    {
        var stats = new Dictionary<EquipStat, int>();
        var traits = new List<string>();

        foreach (var e in effects)
        {
            var kept = target switch
            {
                BuffTarget.Commander => e.TargetsCommander,
                BuffTarget.Companion => e.TargetsCompanion,
                _ => e.TargetsUnits,
            };
            if (!kept)
                continue;

            // Effets d'UNITÉ restreints à un domaine : ignorés pour une unité d'un autre domaine. Les MENEURS,
            // eux, ne sont jamais filtrés (leur domaine sert seulement à l'échelle par domaine).
            if (target == BuffTarget.Units && e.Domaine is { } fd && targetDomaine != fd)
                continue;

            if (e.Kind is CommandEffectKind.CommanderTrait or CommandEffectKind.UnitTrait
                or CommandEffectKind.CompanionTrait)
            {
                if (e.Trait is { } t && !traits.Contains(t))
                    traits.Add(t);
                continue;
            }

            var amount = e.AmountFor(distinctPairs, domaineCount, deployedCount, equippedItems, ownEquippedItems);
            if (amount != 0)
                stats[e.Stat] = stats.GetValueOrDefault(e.Stat, 0) + amount;
        }

        return stats.Count == 0 && traits.Count == 0 ? None : new CommandBuffs(stats, traits);
    }
}
