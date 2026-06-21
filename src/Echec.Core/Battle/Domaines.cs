using System.Collections.Generic;

namespace Echec.Core.Battle;

/// <summary>
/// Registre des domaines. Les classes (asset + stats) sont chargées depuis le JSON
/// de configuration via <see cref="Load"/> ; à défaut, des valeurs par défaut codées
/// servent de repli (tests, fichier manquant). Le motif de déplacement reste défini
/// en code par <see cref="Movement"/>.
/// </summary>
public static class Domaines
{
    private static IReadOnlyList<DomaineDef> _all = Defaults();
    private static Dictionary<Domaine, DomaineDef> _byId = Index(_all);

    /// <summary>Remplace les définitions (depuis le JSON). Ignoré si la liste est vide.</summary>
    public static void Load(IReadOnlyList<DomaineDef> defs)
    {
        if (defs.Count == 0)
            return;
        _all = defs;
        _byId = Index(defs);
    }

    public static IReadOnlyList<DomaineDef> All => _all;

    public static DomaineDef Of(Domaine domaine) =>
        _byId.TryGetValue(domaine, out var def) ? def : _all[0];

    // Raccourcis pratiques.
    public static DomaineDef Pion => Of(Domaine.Pion);
    public static DomaineDef Fou => Of(Domaine.Fou);
    public static DomaineDef Cavalier => Of(Domaine.Cavalier);
    public static DomaineDef Tour => Of(Domaine.Tour);
    public static DomaineDef Dame => Of(Domaine.Dame);

    private static Dictionary<Domaine, DomaineDef> Index(IReadOnlyList<DomaineDef> defs)
    {
        var dict = new Dictionary<Domaine, DomaineDef>();
        foreach (var def in defs)
            dict[def.Id] = def;
        return dict;
    }

    // Repli codé (doit rester aligné avec Assets/Config/units.json).
    private static IReadOnlyList<DomaineDef> Defaults() => new[]
    {
        new DomaineDef(Domaine.Pion, Cls("Soldat", "soldat", hp: 10, dmg: 4, move: 1, attack: 1)),
        new DomaineDef(Domaine.Fou, Cls("Eclaireur", "eclaireur", hp: 8, dmg: 4, move: 3, attack: 2)),
        new DomaineDef(Domaine.Cavalier, Cls("Cavalier", "cavalier", hp: 10, dmg: 5, move: 1, attack: 1)),
        new DomaineDef(Domaine.Tour, Cls("Lancier", "lancier", hp: 12, dmg: 4, move: 3, attack: 3)),
        new DomaineDef(Domaine.Dame, Cls("Archer", "archer", hp: 8, dmg: 5, move: 2, attack: 3)),
    };

    private static UnitClass Cls(string name, string asset, int hp, int dmg, int move, int attack) =>
        new(name, asset, tier: 1, maxHp: hp, damage: dmg, moveRange: move, attackRange: attack);
}
