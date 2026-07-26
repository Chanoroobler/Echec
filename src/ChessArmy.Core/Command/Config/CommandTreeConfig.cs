using System.Collections.Generic;

namespace ChessArmy.Core.Command.Config;

/// <summary>Racine du fichier de configuration des arbres de commandement (commander_trees.json).</summary>
public sealed class CommandTreeConfig
{
    public List<TreeEntry> Trees { get; set; } = new();
}

/// <summary>Un arbre : son id (référencé par le champ <c>tree</c> d'un commandant) et ses nœuds.</summary>
public sealed class TreeEntry
{
    public string Id { get; set; } = "";
    public List<NodeEntry> Nodes { get; set; } = new();
}

/// <summary>Un nœud : position (branche, niveau), icône et effets. Le coût dérive du niveau.</summary>
public sealed class NodeEntry
{
    public string Id { get; set; } = "";

    /// <summary>Colonne de l'arbre (0-based).</summary>
    public int Branch { get; set; }

    /// <summary>Niveau 1..<see cref="CommandTree.MaxLevel"/>, du bas vers le haut.</summary>
    public int Level { get; set; }

    /// <summary>PNG 32×32 <c>Assets/CommandTree/&lt;icon&gt;.png</c>. Absent → l'<see cref="Id"/> du nœud.</summary>
    public string? Icon { get; set; }

    public List<EffectEntry> Effects { get; set; } = new();
}

/// <summary>
/// Un effet. <c>kind</c> nomme la cible (<c>commanderStat</c>, <c>commanderTrait</c>, <c>unitStat</c>,
/// <c>unitTrait</c>, <c>reserveSlots</c>, <c>deploySlots</c>, <c>fusionRecruit</c>, <c>eliteDeathRecruit</c>).
/// </summary>
public sealed class EffectEntry
{
    public string Kind { get; set; } = "";

    /// <summary>Stat visée pour un effet de stat : <c>hp</c>, <c>damage</c>, <c>moveRange</c>, <c>attackRange</c>.</summary>
    public string? Stat { get; set; }

    /// <summary>Bonus de base (ou nombre de slots/recrues). Absent → 1.</summary>
    public int? Amount { get; set; }

    /// <summary>Trait de combat octroyé (cf. <c>Battle.Trait</c>), pour les effets de trait.</summary>
    public string? Trait { get; set; }

    /// <summary>Mise à l'échelle du bonus : <c>flat</c> (défaut), <c>perDistinctPair</c> ou <c>perDomaineUnit</c>.</summary>
    public string? Scale { get; set; }

    /// <summary>
    /// Domaine ciblé, optionnel (<c>Dame</c>, <c>Tour</c>, <c>Cavalier</c>, <c>Fou</c>…). Sur un effet
    /// d'unité : restreint le bonus à ce domaine. Sur une recrue (fusion/relève) : désigne la classe de base
    /// recrutée. Avec l'échelle <c>perDomaineUnit</c> : le domaine dont on compte les unités.
    /// </summary>
    public string? Domaine { get; set; }
}
