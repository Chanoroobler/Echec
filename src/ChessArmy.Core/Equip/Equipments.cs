using System;
using System.Collections.Generic;
using System.Linq;

namespace ChessArmy.Core.Equip;

/// <summary>
/// Registre des équipements connus, chargé depuis equipment.json via <see cref="Load"/> ; à défaut,
/// deux équipements de test codés servent de repli (fichier manquant / tests). Pendant de
/// <see cref="Battle.Domaines"/>. Sert à résoudre un équipement par Id (sauvegarde) et à tirer le
/// butin d'un coffre par rareté.
/// </summary>
public static class Equipments
{
    private static IReadOnlyList<Equipment> _all = Defaults();
    private static Dictionary<string, Equipment> _byId = Index(_all);

    /// <summary>Remplace les définitions (depuis le JSON). Ignoré si la liste est vide.</summary>
    public static void Load(IReadOnlyList<Equipment> defs)
    {
        if (defs.Count == 0)
            return;
        _all = defs;
        _byId = Index(defs);
    }

    /// <summary>Restaure le repli codé (utile pour réinitialiser l'état statique partagé après un test).</summary>
    public static void ResetToDefaults()
    {
        _all = Defaults();
        _byId = Index(_all);
    }

    public static IReadOnlyList<Equipment> All => _all;

    /// <summary>Équipement par id, ou null s'il est inconnu (sauvegarde plus à jour que le catalogue).</summary>
    public static Equipment? ById(string id) => _byId.TryGetValue(id, out var e) ? e : null;

    /// <summary>Équipements d'une rareté donnée : pool de tirage d'un coffre.</summary>
    public static IReadOnlyList<Equipment> OfRarity(EquipmentRarity rarity) =>
        _all.Where(e => e.Rarity == rarity).ToList();

    /// <summary>
    /// Tire un équipement dans le pool d'une rareté (null si le pool est vide). <paramref name="weight"/>
    /// optionnel pondère chaque candidat (poids relatif ≥ 0) — sert à réduire la chance d'un item déjà possédé
    /// en double (cf. <see cref="Campaign.Run.RollChestEquipment"/>). Sans pondération (ou tous poids nuls),
    /// tirage UNIFORME. Ne renvoie jamais null tant que le pool n'est pas vide.
    /// </summary>
    public static Equipment? Roll(EquipmentRarity rarity, Random rng, Func<Equipment, double>? weight = null)
    {
        var pool = OfRarity(rarity);
        if (pool.Count == 0)
            return null;
        if (weight is null)
            return pool[rng.Next(pool.Count)];

        var weights = new double[pool.Count];
        var total = 0.0;
        for (var i = 0; i < pool.Count; i++)
        {
            weights[i] = Math.Max(0, weight(pool[i]));
            total += weights[i];
        }
        if (total <= 0)
            return pool[rng.Next(pool.Count)];   // tous nuls → uniforme (jamais bloqué)

        var pick = rng.NextDouble() * total;
        for (var i = 0; i < pool.Count; i++)
        {
            pick -= weights[i];
            if (pick < 0)
                return pool[i];
        }
        return pool[^1];   // filet contre l'arrondi flottant
    }

    private static Dictionary<string, Equipment> Index(IReadOnlyList<Equipment> defs)
    {
        var dict = new Dictionary<string, Equipment>(StringComparer.Ordinal);
        foreach (var e in defs)
            dict[e.Id] = e;
        return dict;
    }

    // Repli codé (DOIT rester aligné avec Assets/Config/equipment.json) : 2 équipements de test.
    private static IReadOnlyList<Equipment> Defaults() => new[]
    {
        Equipment.OfStat("vigueur", "Amulette de vigueur", EquipStat.Hp, 5),
        Equipment.OfTrait("rempart", "Écu de rempart", Battle.Trait.Rempart),
    };
}
