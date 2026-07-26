using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ChessArmy.Core.Equip;

/// <summary>
/// Construit les <see cref="Equipment"/> à partir du JSON de configuration (equipment.json).
/// Domaine pur : prend du texte, rend des équipements ; aucun accès disque. Pendant de
/// <see cref="Battle.Config.DomaineCatalog"/> pour les équipements.
/// </summary>
public static class EquipmentCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<Equipment> FromJson(string json) =>
        (JsonSerializer.Deserialize<EquipmentConfig>(json, Options)
            ?? throw new InvalidOperationException("Configuration d'équipement vide ou illisible."))
        .Equipments.Select(ToEquipment).ToList();

    private static Equipment ToEquipment(EquipmentEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.Id))
            throw new InvalidOperationException("Équipement sans id dans equipment.json.");

        var rarity = Enum.TryParse<EquipmentRarity>(e.Rarity, ignoreCase: true, out var r)
            ? r : EquipmentRarity.Common;

        // Effets : tableau "effects" (un ou plusieurs) si présent, sinon la forme héritée à effet unique.
        var effects = e.Effects is { Count: > 0 }
            ? e.Effects.Select(ef => ToEffect(ef.Stat, ef.Amount, ef.Trait, e.Id)).ToList()
            : new List<EquipEffect> { ToEffect(e.Stat, e.Amount, e.Trait, e.Id) };

        return Equipment.Of(e.Id, e.Name, rarity, effects, e.Icon);
    }

    /// <summary>Un effet : TRAIT si <paramref name="trait"/> est renseigné, sinon bonus de la <paramref name="stat"/>.</summary>
    private static EquipEffect ToEffect(string? stat, int amount, string? trait, string id)
    {
        if (!string.IsNullOrWhiteSpace(trait))
            return EquipEffect.OfTrait(trait!);
        if (!Enum.TryParse<EquipStat>(stat, ignoreCase: true, out var s))
            throw new InvalidOperationException($"Effet d'équipement invalide pour '{id}' : ni 'trait' ni 'stat' valide ('{stat}').");
        return EquipEffect.OfStat(s, amount);
    }
}
