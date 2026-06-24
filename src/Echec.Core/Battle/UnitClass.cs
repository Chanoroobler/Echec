using System.Collections.Generic;

namespace Echec.Core.Battle;

/// <summary>
/// Nœud de l'arbre de classes. Porte l'asset et les stats. Deux portées distinctes :
/// <see cref="MoveRange"/> (distance de déplacement) et <see cref="AttackRange"/>
/// (distance de tir). Le motif (directions) vient du domaine. Jusqu'à 2 évolutions.
/// </summary>
public sealed class UnitClass
{
    public UnitClass(string name, string asset, int tier, int maxHp, int damage,
        int moveRange, int attackRange, bool piercesAllies = false,
        int minAttackRange = 1, IReadOnlyList<string>? traits = null, params UnitClass[] evolutions)
    {
        Name = name;
        Asset = asset;
        Tier = tier;
        MaxHp = maxHp;
        Damage = damage;
        MoveRange = moveRange;
        AttackRange = attackRange;
        PiercesAllies = piercesAllies;
        MinAttackRange = minAttackRange;
        Traits = traits ?? System.Array.Empty<string>();
        Evolutions = evolutions;
    }

    public string Name { get; }

    /// <summary>Identifiant d'asset (nom de sprite à charger plus tard).</summary>
    public string Asset { get; }

    public int Tier { get; }
    public int MaxHp { get; }
    public int Damage { get; }

    /// <summary>Distance de déplacement (le long des directions du domaine).</summary>
    public int MoveRange { get; }

    /// <summary>Distance de tir/attaque MAXIMALE (le long des directions du domaine).</summary>
    public int AttackRange { get; }

    /// <summary>
    /// Distance de tir MINIMALE (notée « X à Y » dans le tableau des unités : X = ce min, Y =
    /// <see cref="AttackRange"/>). 1 par défaut (peut frapper au contact). &gt; 1 pour les archers
    /// (zone morte de près). Appliquée en combat, mais SEULEMENT en ligne droite (haut/bas/gauche/
    /// droite) : en DIAGONALE on peut tirer dès la distance 1. Le corps d'une cible plus proche borne
    /// quand même la ligne de tir (cf. <see cref="Match.AttackTargets"/>).
    /// </summary>
    public int MinAttackRange { get; }

    /// <summary>
    /// Traits/particularités de la classe (Rempart, Soin, Dégâts de zone, Franchissement,
    /// Transpercement, Interception…), HORS « Traverse allié » qui est porté par
    /// <see cref="PiercesAllies"/>. DONNÉE seule pour l'instant : les mécaniques ne sont pas encore
    /// implémentées (les unités concernées ne sont pas en jeu ; à faire avec les tiers 2).
    /// </summary>
    public IReadOnlyList<string> Traits { get; }

    /// <summary>
    /// Si vrai, la classe tire À TRAVERS ses propres alliés sans les toucher : ses unités amies ne
    /// bornent pas sa ligne de tir et ne sont jamais ciblées (les ennemis bornent toujours).
    /// Particularité du Lancier. Réglé par classe dans <c>units.json</c> (champ <c>piercesAllies</c>).
    /// </summary>
    public bool PiercesAllies { get; }

    /// <summary>Classes vers lesquelles évoluer (0 = feuille, 2 sinon).</summary>
    public IReadOnlyList<UnitClass> Evolutions { get; }

    public bool IsLeaf => Evolutions.Count == 0;
}
