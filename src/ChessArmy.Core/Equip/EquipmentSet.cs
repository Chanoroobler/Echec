using System.Collections.Generic;

namespace ChessArmy.Core.Equip;

/// <summary>
/// Aides sur un ENSEMBLE d'équipements portés (un pion peut en porter plusieurs depuis l'arbre du Marchand,
/// cf. <see cref="Campaign.Run.UnitEquipSlots"/>). Mêmes questions que sur un <see cref="Equipment"/> seul —
/// bonus de stat, traits octroyés — mais CUMULÉES, pour que l'UI et le moteur voient un porteur et non une
/// liste. Une liste nulle ou vide se comporte comme « aucun équipement ».
/// </summary>
public static class EquipmentSet
{
    /// <summary>Bonus CUMULÉ apporté à <paramref name="stat"/> par tous les équipements portés (0 si aucun).</summary>
    public static int BonusFor(this IReadOnlyList<Equipment>? items, EquipStat stat)
    {
        if (items == null)
            return 0;
        var total = 0;
        for (var i = 0; i < items.Count; i++)
            total += items[i].BonusFor(stat);
        return total;
    }

    /// <summary>Vrai si AU MOINS UN des équipements portés octroie ce trait.</summary>
    public static bool GrantsTrait(this IReadOnlyList<Equipment>? items, string trait)
    {
        if (items == null)
            return false;
        for (var i = 0; i < items.Count; i++)
            if (items[i].GrantsTrait(trait))
                return true;
        return false;
    }

    /// <summary>Traits octroyés par les équipements portés, dans l'ordre de pose (doublons possibles).</summary>
    public static IEnumerable<string> TraitsOf(this IReadOnlyList<Equipment>? items)
    {
        if (items == null)
            yield break;
        foreach (var item in items)
            foreach (var trait in item.Traits)
                yield return trait;
    }
}
