using System.Collections.Generic;
using System.Linq;

namespace ChessArmy.Core.Command;

/// <summary>
/// Arbre de commandement d'UN commandant : une grille de <see cref="CommandNode"/> rangés en BRANCHES
/// (colonnes) × NIVEAUX (1..<see cref="MaxLevel"/>), achetés avec les points de commandement gagnés en
/// campagne (2 par mission réussie, + le bonus propre au commandant — cf. <see cref="Battle.CommandeDef.FusionPoints"/>).
///
/// Deux règles, et deux seulement :
/// <list type="bullet">
/// <item>le COÛT ne dépend que du niveau : <see cref="CostOf"/> = 2 / 4 / 6 / 8 ;</item>
/// <item>le PRÉREQUIS d'un nœud de niveau N est de posséder au moins un nœud de niveau N-1 de la MÊME
/// branche (les niveaux 1 sont libres). Les branches ne communiquent jamais.</item>
/// </list>
///
/// Chaque commandant a son propre arbre, identifié par <see cref="Id"/> et chargé depuis
/// <c>Assets/Config/commander_trees.json</c> (cf. <see cref="CommandTrees"/>). L'arbre est propre à la RUN :
/// les nœuds achetés sont sauvegardés avec elle et repartent à zéro à la campagne suivante.
/// </summary>
public sealed class CommandTree
{
    /// <summary>Niveau maximal d'une branche.</summary>
    public const int MaxLevel = 4;

    /// <summary>Coût, en points de commandement, d'un nœud du niveau donné : 2, 4, 6, 8.</summary>
    public static int CostOf(int level) => level * 2;

    private readonly Dictionary<string, CommandNode> _byId;

    public CommandTree(string id, IReadOnlyList<CommandNode> nodes)
    {
        Id = id;
        Nodes = nodes;
        _byId = nodes.ToDictionary(n => n.Id);
        BranchCount = nodes.Count == 0 ? 0 : nodes.Max(n => n.Branch) + 1;
    }

    /// <summary>Identifiant de l'arbre, référencé par <see cref="Battle.CommandeDef.TreeId"/>.</summary>
    public string Id { get; }

    public IReadOnlyList<CommandNode> Nodes { get; }

    /// <summary>Nombre de colonnes (branches) : sert au tracé de l'UI.</summary>
    public int BranchCount { get; }

    /// <summary>Nœud par identifiant, ou null s'il n'appartient pas à cet arbre (sauvegarde plus ancienne).</summary>
    public CommandNode? ById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Nœuds d'une branche et d'un niveau donnés, dans l'ordre du fichier (gauche → droite).</summary>
    public IReadOnlyList<CommandNode> At(int branch, int level) =>
        Nodes.Where(n => n.Branch == branch && n.Level == level).ToList();

    /// <summary>
    /// Vrai si le prérequis de <paramref name="node"/> est satisfait par les nœuds déjà possédés : niveau 1
    /// (toujours libre), ou au moins un nœud de niveau <c>Level - 1</c> DANS LA MÊME BRANCHE.
    /// </summary>
    public bool PrerequisiteMet(CommandNode node, IReadOnlyCollection<string> owned) =>
        node.Level <= 1
        || Nodes.Any(n => n.Branch == node.Branch && n.Level == node.Level - 1 && owned.Contains(n.Id));

    /// <summary>Effets cumulés des nœuds possédés (les ids inconnus de cet arbre sont ignorés).</summary>
    public IEnumerable<CommandEffect> EffectsOf(IEnumerable<string> owned) =>
        owned.Select(ById).Where(n => n != null).SelectMany(n => n!.Effects);
}
