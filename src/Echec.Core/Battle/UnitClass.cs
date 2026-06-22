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
        int moveRange, int attackRange, bool piercesAllies = false, params UnitClass[] evolutions)
    {
        Name = name;
        Asset = asset;
        Tier = tier;
        MaxHp = maxHp;
        Damage = damage;
        MoveRange = moveRange;
        AttackRange = attackRange;
        PiercesAllies = piercesAllies;
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

    /// <summary>Distance de tir/attaque (le long des directions du domaine).</summary>
    public int AttackRange { get; }

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
