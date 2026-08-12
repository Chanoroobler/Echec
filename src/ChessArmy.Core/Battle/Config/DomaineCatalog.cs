using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ChessArmy.Core.Battle.Config;

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

    /// <summary>Construit les COMMANDANTS (role = Commander) depuis le même JSON. Les boss ont leur propre
    /// chargeur (<see cref="BossesFromJson"/>) car leur format diffère (profils par phase).</summary>
    public static IReadOnlyList<CommandeDef> CommandesFromJson(string json) =>
        Deserialize(json).Commandes.Where(c => RoleOf(c) == CommandeRole.Commander).Select(ToCommande).ToList();

    /// <summary>Construit les BOSS (role = Boss) depuis le même JSON : identité + profils (stats/traits) par phase.</summary>
    public static IReadOnlyList<BossDef> BossesFromJson(string json) =>
        Deserialize(json).Commandes.Where(c => RoleOf(c) == CommandeRole.Boss).Select(ToBoss).ToList();

    private static UnitsConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<UnitsConfig>(json, Options)
            ?? throw new InvalidOperationException("Configuration d'unités vide ou illisible.");

    private static CommandeRole RoleOf(CommandeConfig c) =>
        Enum.TryParse<CommandeRole>(c.Role, ignoreCase: true, out var role)
            ? role
            : throw new InvalidOperationException($"Role de commande inconnu dans units.json : '{c.Role}'.");

    private static CommandeDef ToCommande(CommandeConfig c)
    {
        if (!Enum.TryParse<Domaine>(c.Domaine, ignoreCase: true, out var domaine))
            throw new InvalidOperationException($"Domaine de mouvement inconnu pour la commande '{c.Name}' : '{c.Domaine}'.");

        IReadOnlyList<Domaine>? starting = null;
        if (c.StartingUnits is { } list)
            starting = list.Select(d => Enum.TryParse<Domaine>(d, ignoreCase: true, out var sd)
                ? sd
                : throw new InvalidOperationException(
                    $"Domaine de pion de départ inconnu pour le commandant '{c.Name}' : '{d}'.")).ToList();

        return new CommandeDef(CommandeRole.Commander, domaine,
            new UnitClass(c.Name, c.Asset, tier: 1, c.Hp, c.Damage, c.MoveRange, c.AttackRange),
            c.Deployments ?? 5, c.ReserveSize ?? 8, c.Tree ?? "commandant", c.FusionPoints ?? 0,
            c.Id, starting, c.Unlocked ?? true, c.OnHitPoints ?? 0, c.OnHitCap ?? int.MaxValue,
            c.RangedHitPoints ?? 0, c.RangedHitCap ?? int.MaxValue);
    }

    private static BossDef ToBoss(CommandeConfig c)
    {
        if (!Enum.TryParse<Domaine>(c.Domaine, ignoreCase: true, out var domaine))
            throw new InvalidOperationException($"Domaine de mouvement inconnu pour le boss '{c.Name}' : '{c.Domaine}'.");

        var profiles = new Dictionary<int, UnitClass>();
        if (c.Phases is { Count: > 0 })
        {
            foreach (var (key, p) in c.Phases)
            {
                if (!int.TryParse(key, out var phase) || phase is < 1 or > 3)
                    throw new InvalidOperationException(
                        $"Phase de boss invalide pour '{c.Name}' : \"{key}\". Attendu \"1\" à \"3\".");
                if (!profiles.TryAdd(phase, new UnitClass(c.Name, c.Asset, tier: 1, p.Hp, p.Damage, p.MoveRange,
                        p.AttackRange, p.PiercesAllies, p.MinAttackRange ?? 1, p.Traits, ParseAttackDomaine(p.AttackDomaine, c.Name))))
                    throw new InvalidOperationException($"Phase de boss en double pour '{c.Name}' : {phase}.");
            }
        }
        else
        {
            // Repli HÉRITÉ : entrée à stats plates + champ « phase » (une seule phase, sans trait).
            if (c.Phase is < 0 or > 3)
                throw new InvalidOperationException($"Phase de boss invalide pour '{c.Name}' : {c.Phase}. Attendu 0 ou 1..3.");
            var phase = c.Phase is >= 1 and <= 3 ? c.Phase : 1;
            profiles[phase] = new UnitClass(c.Name, c.Asset, tier: 1, c.Hp, c.Damage, c.MoveRange, c.AttackRange);
        }

        return new BossDef(c.Name, c.Asset, domaine, profiles, c.UnlocksCommander);
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
            c.PiercesAllies, c.MinAttackRange ?? 1, c.Traits, ParseAttackDomaine(c.AttackDomaine, c.Name), evolutions);
    }

    /// <summary>Parse un domaine d'attaque optionnel (« attackDomaine »). Null si absent ; lève si invalide.</summary>
    private static Domaine? ParseAttackDomaine(string? value, string owner)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Enum.TryParse<Domaine>(value, ignoreCase: true, out var ad)
            ? ad
            : throw new InvalidOperationException($"Domaine d'attaque inconnu pour '{owner}' : '{value}'.");
    }
}
