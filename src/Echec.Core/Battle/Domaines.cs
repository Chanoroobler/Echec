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

    // Repli codé (DOIT rester aligné avec Assets/Config/units.json) : 4 arbres à 3 tiers
    // (base → 2 branches → 2 feuilles chacune). « Traverse allié » = pierces:true (pas dans les traits).
    // « Zone morte » pilote la portée min (2) via le trait ; les sprites partagés sont donnés via sprite:.
    private static IReadOnlyList<DomaineDef> Defaults() => new[]
    {
        new DomaineDef(Domaine.Dame,
            N(1, "Soldat", "soldat", 12, 10, 1, 1, evo: new[]
            {
                N(2, "Archer", "archer", 12, 12, 2, 2, traits: new[] { Trait.ZoneMorte }, evo: new[]
                {
                    N(3, "Arbalétrier", "arbaletrier", 18, 14, 2, 2, traits: new[] { Trait.ZoneMorte, Trait.Balistique }),
                    N(3, "Rôdeur", "rodeur", 16, 14, 2, 3, traits: new[] { Trait.ZoneMorte }),
                }),
                N(2, "Spadassin", "spadassin", 14, 14, 2, 1, evo: new[]
                {
                    N(3, "Maître d'armes", "maitre_armes", 20, 16, 2, 1, traits: new[] { Trait.Duelliste }),
                    N(3, "Barbare", "barbare", 18, 18, 2, 1, traits: new[] { Trait.Rage }),
                }),
            })),
        new DomaineDef(Domaine.Fou,
            N(1, "Mage", "mage", 6, 14, 1, 3, evo: new[]
            {
                N(2, "Clerc", "clerc", 8, 12, 2, 4, traits: new[] { Trait.Soin }, evo: new[]
                {
                    N(3, "Archevêque", "archeveque", 12, 14, 2, 4, traits: new[] { Trait.Soin, Trait.BouclierDivin }),
                    N(3, "Oracle", "oracle", 14, 12, 2, 5, traits: new[] { Trait.Soin, Trait.Benediction }),
                }),
                N(2, "Sorcier", "sorcier", 6, 18, 2, 4, evo: new[]
                {
                    N(3, "Archimage", "archimage", 12, 22, 3, 4),
                    N(3, "Démoniste", "demoniste", 16, 20, 3, 4, traits: new[] { Trait.DrainDeVie }),
                }),
            })),
        new DomaineDef(Domaine.Cavalier,
            N(1, "Cavalier", "cavalier", 12, 10, 3, 3, traits: new[] { Trait.Franchissement }, evo: new[]
            {
                N(2, "Cavalier lourd", "cavalier_lourd", 18, 14, 3, 3, traits: new[] { Trait.Franchissement }, sprite: "chevalier", evo: new[]
                {
                    N(3, "Paladin", "paladin", 22, 18, 3, 3, traits: new[] { Trait.Franchissement, Trait.AuraDeRempart }),
                    N(3, "Cavalier griffon", "cavalier_griffon", 20, 16, 3, 3, traits: new[] { Trait.Vol }),
                }),
                N(2, "Archer monté", "archer_monte", 14, 10, 3, 3, pierces: true, traits: new[] { Trait.ZoneMorte, Trait.Franchissement }, evo: new[]
                {
                    N(3, "Archer griffon", "archer_griffon", 16, 14, 3, 3, pierces: true, traits: new[] { Trait.ZoneMorte, Trait.Vol }),
                    N(3, "Arbalétrier monté", "arbaletrier_monte", 18, 14, 3, 3, traits: new[] { Trait.ZoneMorte, Trait.Vol, Trait.Balistique }),
                }),
            })),
        new DomaineDef(Domaine.Tour,
            N(1, "Lancier", "lancier", 10, 12, 2, 2, pierces: true, evo: new[]
            {
                N(2, "Garde", "garde", 20, 12, 2, 2, pierces: true, traits: new[] { Trait.Rempart }, sprite: "empaleur", evo: new[]
                {
                    N(3, "Phalange", "phalange", 24, 14, 2, 2, pierces: true, traits: new[] { Trait.Rempart, Trait.Formation }),
                    N(3, "Forteresse", "forteresse", 28, 12, 1, 2, pierces: true, traits: new[] { Trait.Rempart }),
                }),
                N(2, "Tirailleur", "tirailleur", 14, 14, 2, 2, pierces: true, traits: new[] { Trait.Esquive }, evo: new[]
                {
                    N(3, "Javelinier d'élite", "javelinier_elite", 18, 16, 2, 3, pierces: true, traits: new[] { Trait.Embrochage }, sprite: "tirailleur"),
                    N(3, "Voltigeur", "voltigeur", 14, 14, 2, 3, pierces: true, traits: new[] { Trait.Esquive, Trait.Riposte }, sprite: "tirailleur"),
                }),
            })),
    };

    /// <summary>Nœud de l'arbre de classes (tier explicite, sprite/traits/évolutions optionnels).</summary>
    private static UnitClass N(int tier, string name, string asset, int hp, int dmg, int move, int attack,
        bool pierces = false, string[]? traits = null, string? sprite = null, UnitClass[]? evo = null) =>
        new(name, asset, tier, maxHp: hp, damage: dmg, moveRange: move, attackRange: attack,
            piercesAllies: pierces, minAttackRange: 1, traits: traits, sprite: sprite,
            evolutions: evo ?? System.Array.Empty<UnitClass>());
}
