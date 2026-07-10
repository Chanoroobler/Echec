using System.Collections.Generic;

namespace Echec.Core.Battle.Config;

/// <summary>Racine du fichier de configuration des unités (units.json).</summary>
public sealed class UnitsConfig
{
    public List<DomaineConfig> Domaines { get; set; } = new();

    /// <summary>Unités COMMANDE (commandant, boss) : rôle + domaine de mouvement + stats.</summary>
    public List<CommandeConfig> Commandes { get; set; } = new();
}

/// <summary>
/// Une unité COMMANDE : son rôle (Commander/Boss), le domaine dont elle emprunte le
/// déplacement, et ses stats (mêmes champs qu'une classe de base).
/// </summary>
public sealed class CommandeConfig
{
    public string Role { get; set; } = "";
    public string Domaine { get; set; } = "";
    public string Name { get; set; } = "";
    public string Asset { get; set; } = "";
    public int Hp { get; set; }
    public int Damage { get; set; }
    public int MoveRange { get; set; }
    public int AttackRange { get; set; }

    /// <summary>
    /// BOSS uniquement : phase de campagne (1..3) à laquelle ce boss est réservé, ou <c>0</c>/absent =
    /// « toutes phases » (repli). Le combat de boss d'une phase choisit d'abord le boss de CETTE phase,
    /// sinon un boss « toutes phases », sinon le premier boss défini. Ignoré pour un Commander.
    /// </summary>
    public int Phase { get; set; }

    /// <summary>COMMANDANT : pions posables au placement, commandant compris. Absent → 5.</summary>
    public int? Deployments { get; set; }

    /// <summary>COMMANDANT : taille de base de la réserve (hors commandant). Absent → 8.</summary>
    public int? ReserveSize { get; set; }

    /// <summary>COMMANDANT : id de son arbre dans commander_trees.json. Absent → « commandant ».</summary>
    public string? Tree { get; set; }

    /// <summary>COMMANDANT : points de commandement gagnés à chaque fusion (sa source de gain propre). Absent → 0.</summary>
    public int? FusionPoints { get; set; }
}

/// <summary>Un domaine et sa classe de base (le motif de déplacement reste en code).</summary>
public sealed class DomaineConfig
{
    public string Domaine { get; set; } = "";
    public ClassConfig BaseClass { get; set; } = new();
}

/// <summary>Stats d'une classe : asset + PV + dégâts + portées, et évolutions optionnelles.</summary>
public sealed class ClassConfig
{
    public string Name { get; set; } = "";
    public string Asset { get; set; } = "";
    public int Hp { get; set; }
    public int Damage { get; set; }
    public int MoveRange { get; set; }
    public int AttackRange { get; set; }

    /// <summary>Tire à travers ses alliés sans les toucher (Lancier). Absent/false = ligne bloquée par les alliés.</summary>
    public bool PiercesAllies { get; set; }

    /// <summary>Portée de tir MINIMALE (« X à Y » : X). Absent → 1 (peut frapper au contact).</summary>
    public int? MinAttackRange { get; set; }

    /// <summary>Traits/particularités (hors « Traverse allié » = piercesAllies). Absent → aucun.</summary>
    public List<string>? Traits { get; set; }

    /// <summary>
    /// Domaine du pattern d'ATTAQUE s'il diffère du déplacement (ex. cavalier monté : déplacement Cavalier
    /// en L, mais attaque « Dame » en lignes comme un archer). Absent → attaque = déplacement.
    /// </summary>
    public string? AttackDomaine { get; set; }

    /// <summary>Sous-classes (arbre d'évolution) ; null/absent = feuille.</summary>
    public List<ClassConfig>? Evolutions { get; set; }
}
