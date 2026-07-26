using System.Collections.Generic;

namespace ChessArmy.Core.Battle.Config;

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
    /// BOSS, format HÉRITÉ (une seule variante) : phase de campagne (1..3) à laquelle ce boss est réservé,
    /// ou <c>0</c>/absent. Sert uniquement quand <see cref="Phases"/> est absent (repli sans traits). Ignoré
    /// pour un Commander et pour un boss qui déclare <see cref="Phases"/>.
    /// </summary>
    public int Phase { get; set; }

    /// <summary>
    /// BOSS : profils par phase — clé <c>"1"</c>..<c>"3"</c> → stats + traits propres à cette phase. C'est le
    /// format recommandé : un même boss peut être plus fort (et gagner des traits) selon la phase où il tombe.
    /// Un boss n'est éligible qu'aux phases qu'il déclare ici. Prioritaire sur les champs plats
    /// (<see cref="Hp"/>/<see cref="Damage"/>/…). Ignoré pour un Commander.
    /// </summary>
    public Dictionary<string, BossPhaseConfig>? Phases { get; set; }

    /// <summary>COMMANDANT : pions posables au placement, commandant compris. Absent → 5.</summary>
    public int? Deployments { get; set; }

    /// <summary>COMMANDANT : taille de base de la réserve (hors commandant). Absent → 8.</summary>
    public int? ReserveSize { get; set; }

    /// <summary>COMMANDANT : id de son arbre dans commander_trees.json. Absent → « commandant ».</summary>
    public string? Tree { get; set; }

    /// <summary>COMMANDANT : points de commandement gagnés à chaque fusion (une source de gain propre). Absent → 0.</summary>
    public int? FusionPoints { get; set; }

    /// <summary>COMMANDANT : points gagnés à chaque coup REÇU en combat (source alternative). Absent → 0.</summary>
    public int? OnHitPoints { get; set; }

    /// <summary>COMMANDANT : plafond de coups reçus comptabilisés par combat pour <c>onHitPoints</c>. Absent → illimité.</summary>
    public int? OnHitCap { get; set; }

    /// <summary>
    /// COMMANDANT : identifiant stable, persisté dans la sauvegarde (indépendant du nom et de l'asset).
    /// Absent → l'asset fait office d'id. Ignoré pour un boss.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// COMMANDANT : pions de départ, en plus de lui-même — une liste de DOMAINES dont la classe de base est
    /// recrutée (ex. <c>[ "Dame", "Dame" ]</c> = deux Soldats). Absent → deux Soldats. Liste vide = seul.
    /// </summary>
    public List<string>? StartingUnits { get; set; }

    /// <summary>
    /// COMMANDANT : disponible dès le départ. <c>false</c> = verrouillé (silhouette noire dans le carrousel,
    /// impossible à lancer). Absent → <c>true</c>. Le déblocage se fait en battant le boss lié (cf.
    /// <see cref="UnlocksCommander"/>) en dernière phase, mémorisé dans le profil.
    /// </summary>
    public bool? Unlocked { get; set; }

    /// <summary>
    /// BOSS : id du COMMANDANT débloqué en battant ce boss en dernière phase (cf. <see cref="Id"/> d'un
    /// Commander). Absent → ce boss ne débloque personne (ex. la Brute). Ignoré pour un Commander.
    /// </summary>
    public string? UnlocksCommander { get; set; }
}

/// <summary>
/// Stats + traits d'UN boss pour UNE phase (mêmes champs qu'une classe, hors évolutions). Le nom, l'asset
/// (sprite) et le domaine de déplacement restent portés par le <see cref="CommandeConfig"/> parent : seules
/// les stats et les traits changent d'une phase à l'autre.
/// </summary>
public sealed class BossPhaseConfig
{
    public int Hp { get; set; }
    public int Damage { get; set; }
    public int MoveRange { get; set; }
    public int AttackRange { get; set; }

    /// <summary>Portée de tir MINIMALE (« X à Y » : X). Absent → 1 (peut frapper au contact).</summary>
    public int? MinAttackRange { get; set; }

    /// <summary>Tire à travers ses alliés sans les toucher (comme le Lancier). Absent/false = ligne bloquée.</summary>
    public bool PiercesAllies { get; set; }

    /// <summary>Traits de combat actifs sur ce boss à cette phase (cf. Trait). Absent → aucun.</summary>
    public List<string>? Traits { get; set; }

    /// <summary>Domaine du pattern d'ATTAQUE s'il diffère du déplacement. Absent → attaque = déplacement.</summary>
    public string? AttackDomaine { get; set; }
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
