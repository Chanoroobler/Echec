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

    /// <summary>Restaure les définitions codées par défaut (arbre complet, évolutions incluses).
    /// Utile pour réinitialiser l'état statique partagé après un <see cref="Load"/> de test.</summary>
    public static void ResetToDefaults()
    {
        _all = Defaults();
        _byId = Index(_all);
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

    // Repli codé (DOIT rester aligné avec Assets/Config/units.json) : 5 arbres de 3 classes.
    private static IReadOnlyList<DomaineDef> Defaults() => new[]
    {
        new DomaineDef(Domaine.Pion, Cls("Soldat", "soldat", 12, 10, 1, 1, evolutions: new[]
        {
            Leaf("Garde", "garde", 20, 8, 1, 1, traits: new[] { "Rempart" }),
            Leaf("Spadassin", "spadassin", 12, 14, 2, 1),
        })),
        new DomaineDef(Domaine.Fou, Cls("Mage", "mage", 6, 14, 2, 4, evolutions: new[]
        {
            Leaf("Clerc", "clerc", 8, 7, 4, 4, traits: new[] { "Soin" }),
            Leaf("Sorcier", "sorcier", 6, 14, 4, 5, traits: new[] { "Dégâts de zone" }),
        })),
        new DomaineDef(Domaine.Cavalier, Cls("Cavalier", "cavalier", 14, 10, 3, 3, pierces: true, traits: new[] { "Franchissement" }, evolutions: new[]
        {
            Leaf("Chevalier", "chevalier", 18, 16, 3, 3, pierces: true, traits: new[] { "Franchissement" }),
            Leaf("Archer monté", "archer_monte", 14, 10, 3, 4, minAtt: 2),
        })),
        new DomaineDef(Domaine.Tour, Cls("Lancier", "lancier", 10, 10, 2, 2, pierces: true, evolutions: new[]
        {
            Leaf("Empaleur", "empaleur", 14, 14, 3, 2, pierces: true, traits: new[] { "Transpercement" }),
            Leaf("Hallebardier", "hallebardier", 18, 10, 3, 2, pierces: true, traits: new[] { "Interception" }),
        })),
        new DomaineDef(Domaine.Dame, Cls("Archer", "archer", 8, 6, 3, 3, minAtt: 2, evolutions: new[]
        {
            Leaf("Rôdeur", "rodeur", 10, 12, 4, 3),
            Leaf("Maître archer", "maitre_archer", 12, 10, 3, 4, minAtt: 2),
        })),
    };

    /// <summary>Classe de BASE (tier 1) avec ses évolutions (tier 2).</summary>
    private static UnitClass Cls(string name, string asset, int hp, int dmg, int move, int attack,
        int minAtt = 1, bool pierces = false, string[]? traits = null, params UnitClass[] evolutions) =>
        new(name, asset, tier: 1, maxHp: hp, damage: dmg, moveRange: move, attackRange: attack,
            piercesAllies: pierces, minAttackRange: minAtt, traits: traits, evolutions: evolutions);

    /// <summary>Classe FEUILLE (tier 2, sans évolution).</summary>
    private static UnitClass Leaf(string name, string asset, int hp, int dmg, int move, int attack,
        int minAtt = 1, bool pierces = false, string[]? traits = null) =>
        new(name, asset, tier: 2, maxHp: hp, damage: dmg, moveRange: move, attackRange: attack,
            piercesAllies: pierces, minAttackRange: minAtt, traits: traits);
}
