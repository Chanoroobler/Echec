using System.Collections.Generic;

namespace ChessArmy.Core.Command;

/// <summary>
/// Un nœud d'<see cref="CommandTree"/> : une amélioration achetable contre des points de commandement.
///
/// Position dans l'arbre = (<see cref="Branch"/>, <see cref="Level"/>). Les branches sont ÉTANCHES :
/// pour acheter un nœud de niveau N, il faut posséder au moins un nœud de niveau N-1 de la MÊME branche
/// (cf. <see cref="CommandTree.PrerequisiteMet"/>). Le coût ne dépend que du niveau
/// (cf. <see cref="CommandTree.CostOf"/>).
///
/// L'UI n'affiche pas de texte dans le nœud, seulement l'<see cref="Icon"/> (PNG 32×32 dans
/// <c>Assets/CommandTree/&lt;icon&gt;.png</c>) ; le libellé et la description sont localisés sous les clés
/// <c>tree.&lt;id&gt;</c> et <c>tree.&lt;id&gt;.desc</c> et n'apparaissent qu'en infobulle.
/// </summary>
public sealed class CommandNode
{
    public CommandNode(string id, int branch, int level, string icon, IReadOnlyList<CommandEffect> effects)
    {
        Id = id;
        Branch = branch;
        Level = level;
        Icon = icon;
        Effects = effects;
    }

    /// <summary>Identifiant stable : clé de sauvegarde et racine des clés de localisation.</summary>
    public string Id { get; }

    /// <summary>Colonne de l'arbre (0-based). Les prérequis ne traversent jamais une branche.</summary>
    public int Branch { get; }

    /// <summary>Niveau (1..<see cref="CommandTree.MaxLevel"/>), du bas de la colonne vers le haut.</summary>
    public int Level { get; }

    /// <summary>Nom du PNG 32×32 (<c>Assets/CommandTree/&lt;icon&gt;.png</c>).</summary>
    public string Icon { get; }

    /// <summary>Ce que le nœud fait une fois acheté (au moins un effet).</summary>
    public IReadOnlyList<CommandEffect> Effects { get; }

    /// <summary>Coût en points de commandement (dérivé du niveau).</summary>
    public int Cost => CommandTree.CostOf(Level);

    /// <summary>Clé de localisation du libellé affiché en infobulle.</summary>
    public string NameKey => $"tree.{Id}";

    /// <summary>Clé de localisation de la description affichée en infobulle.</summary>
    public string DescKey => $"tree.{Id}.desc";
}
