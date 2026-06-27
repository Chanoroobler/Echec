using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Echec.Core.Equip;

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
        if (!Enum.TryParse<EquipmentKind>(e.Kind, ignoreCase: true, out var kind))
            throw new InvalidOperationException($"Type d'équipement inconnu pour '{e.Id}' : '{e.Kind}'.");

        if (kind == EquipmentKind.Trait)
        {
            if (string.IsNullOrWhiteSpace(e.Trait))
                throw new InvalidOperationException($"Équipement de trait '{e.Id}' sans champ 'trait'.");
            return Equipment.OfTrait(e.Id, e.Name, e.Trait!, rarity, e.Icon);
        }

        if (!Enum.TryParse<EquipStat>(e.Stat, ignoreCase: true, out var stat))
            throw new InvalidOperationException($"Stat inconnue pour l'équipement '{e.Id}' : '{e.Stat}'.");
        return Equipment.OfStat(e.Id, e.Name, stat, e.Amount, rarity, e.Icon);
    }
}
