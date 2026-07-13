using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Echec.Core.Equip;

namespace Echec.Core.Command.Config;

/// <summary>
/// Construit les <see cref="CommandTree"/> à partir du JSON de configuration (commander_trees.json).
/// Domaine pur : prend du texte, rend des arbres ; aucun accès disque. Pendant de
/// <see cref="Battle.Config.DomaineCatalog"/> et de <see cref="EquipmentCatalog"/>.
/// </summary>
public static class CommandTreeCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<CommandTree> FromJson(string json) =>
        (JsonSerializer.Deserialize<CommandTreeConfig>(json, Options)
            ?? throw new InvalidOperationException("Configuration d'arbres de commandement vide ou illisible."))
        .Trees.Select(ToTree).ToList();

    private static CommandTree ToTree(TreeEntry t)
    {
        if (string.IsNullOrWhiteSpace(t.Id))
            throw new InvalidOperationException("Arbre de commandement sans id dans commander_trees.json.");

        var nodes = t.Nodes.Select(n => ToNode(n, t.Id)).ToList();
        var duplicate = nodes.GroupBy(n => n.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"Nœud '{duplicate.Key}' défini deux fois dans l'arbre '{t.Id}'.");

        return new CommandTree(t.Id, nodes);
    }

    private static CommandNode ToNode(NodeEntry n, string treeId)
    {
        if (string.IsNullOrWhiteSpace(n.Id))
            throw new InvalidOperationException($"Nœud sans id dans l'arbre '{treeId}'.");
        if (n.Level is < 1 or > CommandTree.MaxLevel)
            throw new InvalidOperationException(
                $"Niveau invalide pour le nœud '{n.Id}' : {n.Level}. Attendu 1..{CommandTree.MaxLevel}.");
        if (n.Branch < 0)
            throw new InvalidOperationException($"Branche invalide pour le nœud '{n.Id}' : {n.Branch}.");
        if (n.Effects.Count == 0)
            throw new InvalidOperationException($"Nœud '{n.Id}' sans effet.");

        var icon = string.IsNullOrWhiteSpace(n.Icon) ? n.Id : n.Icon!;
        return new CommandNode(n.Id, n.Branch, n.Level, icon, n.Effects.Select(e => ToEffect(e, n.Id)).ToList());
    }

    private static CommandEffect ToEffect(EffectEntry e, string nodeId)
    {
        var amount = e.Amount ?? 1;
        return e.Kind?.ToLowerInvariant() switch
        {
            "commanderstat" => CommandEffect.CommanderStat(Stat(e, nodeId), amount, Scale(e, nodeId)),
            "unitstat" => CommandEffect.UnitStat(Stat(e, nodeId), amount, Scale(e, nodeId)),
            "commandertrait" => CommandEffect.CommanderTrait(TraitOf(e, nodeId)),
            "unittrait" => CommandEffect.UnitTrait(TraitOf(e, nodeId)),
            "reserveslots" => CommandEffect.ReserveSlots(amount),
            "deployslots" => CommandEffect.DeploySlots(amount),
            "fusionrecruit" => CommandEffect.FusionRecruit(amount),
            "elitedeathrecruit" => CommandEffect.EliteDeathRecruit(amount),
            _ => throw new InvalidOperationException($"Type d'effet inconnu pour le nœud '{nodeId}' : '{e.Kind}'."),
        };
    }

    private static EquipStat Stat(EffectEntry e, string nodeId) =>
        Enum.TryParse<EquipStat>(e.Stat, ignoreCase: true, out var s)
            ? s
            : throw new InvalidOperationException($"Stat invalide pour le nœud '{nodeId}' : '{e.Stat}'.");

    private static CommandScale Scale(EffectEntry e, string nodeId) =>
        string.IsNullOrWhiteSpace(e.Scale) ? CommandScale.Flat
        : Enum.TryParse<CommandScale>(e.Scale, ignoreCase: true, out var s) ? s
        : throw new InvalidOperationException($"Échelle invalide pour le nœud '{nodeId}' : '{e.Scale}'.");

    private static string TraitOf(EffectEntry e, string nodeId) =>
        !string.IsNullOrWhiteSpace(e.Trait) && Battle.Trait.All.Contains(e.Trait)
            ? e.Trait!
            : throw new InvalidOperationException($"Trait inconnu pour le nœud '{nodeId}' : '{e.Trait}'.");
}
