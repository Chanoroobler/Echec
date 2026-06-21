using System.Collections.Generic;

namespace Echec.Core.Battle.Config;

/// <summary>Racine du fichier de configuration des unités (units.json).</summary>
public sealed class UnitsConfig
{
    public List<DomaineConfig> Domaines { get; set; } = new();
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

    /// <summary>Sous-classes (pour un futur arbre) ; null/absent = feuille.</summary>
    public List<ClassConfig>? Evolutions { get; set; }
}
