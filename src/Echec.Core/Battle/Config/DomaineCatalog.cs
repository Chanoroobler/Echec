using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Echec.Core.Battle.Config;

/// <summary>
/// Construit les <see cref="DomaineDef"/> à partir du JSON de configuration des unités.
/// Le motif de déplacement (directions/saut) reste défini en code par le <see cref="Domaine"/> ;
/// le JSON ne porte que les classes (asset + stats).
/// </summary>
public static class DomaineCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<DomaineDef> FromJson(string json) =>
        Deserialize(json).Domaines.Select(ToDef).ToList();

    /// <summary>Construit les unités COMMANDE (commandant/boss) depuis le même JSON.</summary>
    public static IReadOnlyList<CommandeDef> CommandesFromJson(string json) =>
        Deserialize(json).Commandes.Select(ToCommande).ToList();

    private static UnitsConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<UnitsConfig>(json, Options)
            ?? throw new InvalidOperationException("Configuration d'unités vide ou illisible.");

    private static CommandeDef ToCommande(CommandeConfig c)
    {
        if (!Enum.TryParse<CommandeRole>(c.Role, ignoreCase: true, out var role))
            throw new InvalidOperationException($"Role de commande inconnu dans units.json : '{c.Role}'.");
        if (!Enum.TryParse<Domaine>(c.Domaine, ignoreCase: true, out var domaine))
            throw new InvalidOperationException($"Domaine de mouvement inconnu pour la commande '{c.Name}' : '{c.Domaine}'.");

        return new CommandeDef(role, domaine,
            new UnitClass(c.Name, c.Asset, tier: 1, c.Hp, c.Damage, c.MoveRange, c.AttackRange));
    }

    private static DomaineDef ToDef(DomaineConfig dc)
    {
        if (!Enum.TryParse<Domaine>(dc.Domaine, ignoreCase: true, out var domaine))
            throw new InvalidOperationException($"Domaine inconnu dans units.json : '{dc.Domaine}'.");

        return new DomaineDef(domaine, ToClass(dc.BaseClass, tier: 1));
    }

    private static UnitClass ToClass(ClassConfig c, int tier)
    {
        var evolutions = (c.Evolutions ?? new List<ClassConfig>())
            .Select(e => ToClass(e, tier + 1))
            .ToArray();

        return new UnitClass(c.Name, c.Asset, tier, c.Hp, c.Damage, c.MoveRange, c.AttackRange,
            c.PiercesAllies, evolutions);
    }
}
