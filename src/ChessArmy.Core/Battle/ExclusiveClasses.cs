using System.Collections.Generic;
using System.Linq;

namespace ChessArmy.Core.Battle;

/// <summary>
/// Registre des classes EXCLUSIVES : des pions qui n'appartiennent à AUCUN des 4 domaines du jeu normal
/// et n'entrent donc dans AUCUN tirage (recrutement, draft, tuile recrue, relance, pool de l'IA, Codex) ni
/// dans les fusions. Ils n'existent que par l'arbre de commandement qui les amène — aujourd'hui le seul cas
/// est le PAYSAN du commandant BRUTE, qui monte par son trait « Survivant » et évolue par nœud d'arbre
/// (cf. <see cref="Command.CommandEffectKind.EvolveExclusive"/>) plutôt que par fusion.
///
/// Chaque entrée est un arbre de classes complet (base → 2 branches → 2 feuilles, exactement comme un
/// domaine) associé au <see cref="Domaine"/> dont le pion emprunte le motif de déplacement. C'est le
/// pendant de <see cref="Domaines"/> pour ces pions hors-système : même format dans units.json (section
/// <c>« exclusives »</c>), mais un registre séparé — c'est justement le fait de ne pas être dans
/// <see cref="Domaines.All"/> qui les tient hors des tirages, sans une seule garde à écrire ailleurs.
/// </summary>
public static class ExclusiveClasses
{
    /// <summary>Asset (donc identité stable) de la classe de base du PAYSAN.</summary>
    public const string PaysanAsset = "paysan";

    private static IReadOnlyList<DomaineDef> _all = Defaults();

    /// <summary>Remplace les définitions (depuis le JSON). Ignoré si la liste est vide.</summary>
    public static void Load(IReadOnlyList<DomaineDef> defs)
    {
        if (defs.Count == 0)
            return;
        _all = defs;
    }

    /// <summary>Restaure le repli codé (réinitialise l'état statique partagé après un test).</summary>
    public static void ResetToDefaults() => _all = Defaults();

    /// <summary>Tous les arbres exclusifs (racine + domaine de mouvement).</summary>
    public static IReadOnlyList<DomaineDef> All => _all;

    /// <summary>Arbre du PAYSAN, ou <c>null</c> s'il a été retiré de la configuration.</summary>
    public static DomaineDef? Paysan => _all.FirstOrDefault(d => d.BaseClass.Asset == PaysanAsset);

    /// <summary>
    /// Classe exclusive par son asset, à N'IMPORTE QUEL tier de l'arbre (la base comme une feuille
    /// d'évolution), ou <c>null</c> si l'asset n'est pas exclusif. C'est ce qui permet à la sauvegarde de
    /// retrouver un Paysan déjà évolué (cf. <c>Campaign.UnitSpecSave.ToSpec</c>).
    /// </summary>
    public static UnitClass? Find(string? asset) =>
        string.IsNullOrWhiteSpace(asset) ? null : _all.Select(d => Find(d.BaseClass, asset!)).FirstOrDefault(c => c != null);

    /// <summary>
    /// Asset de la classe de BASE de l'arbre exclusif qui contient <paramref name="asset"/> (« paysan » pour
    /// n'importe laquelle de ses évolutions), ou <c>null</c> si l'asset n'est pas exclusif. Sert au rendu :
    /// un sprite commun à tout l'arbre (la paysanne devenue meneuse) se nomme d'après cette racine.
    /// </summary>
    public static string? RootAssetOf(string? asset) =>
        string.IsNullOrWhiteSpace(asset)
            ? null
            : _all.FirstOrDefault(d => Find(d.BaseClass, asset!) != null)?.BaseClass.Asset;

    /// <summary>Domaine de mouvement de l'arbre exclusif qui contient <paramref name="asset"/>, ou <c>null</c>.</summary>
    public static Domaine? DomaineOf(string? asset) =>
        string.IsNullOrWhiteSpace(asset)
            ? null
            : _all.FirstOrDefault(d => Find(d.BaseClass, asset!) != null)?.Id;

    /// <summary>
    /// Vrai si cette classe est exclusive : elle ne fusionne pas, ne se relance pas et ne se découvre pas.
    /// Les appelants (fusion, relance, Codex) interrogent ce prédicat plutôt qu'une liste en dur.
    /// </summary>
    public static bool IsExclusive(UnitClass? unitClass) => unitClass != null && Find(unitClass.Asset) != null;

    private static UnitClass? Find(UnitClass root, string asset)
    {
        if (root.Asset == asset)
            return root;
        foreach (var evolution in root.Evolutions)
            if (Find(evolution, asset) is { } found)
                return found;
        return null;
    }

    // Repli codé (DOIT rester aligné avec la section « exclusives » d'Assets/Config/units.json).
    // Le Paysan est volontairement MISÉRABLE : toute sa valeur vient du trait « Survivant » (+1 puissance
    // et +2 PV max par mission jouée) et de ses deux évolutions d'arbre, qui n'ajoutent chacune qu'une
    // portée, un pas, ou un trait défensif.
    private static IReadOnlyList<DomaineDef> Defaults() => new[]
    {
        new DomaineDef(Domaine.Dame,
            new UnitClass("Paysanne", PaysanAsset, tier: 1, maxHp: 8, damage: 4, moveRange: 1, attackRange: 1,
                piercesAllies: false, minAttackRange: 1, traits: new[] { Trait.Survivant }, attackDomaine: null,
                new UnitClass("Paysanne archère", "paysan_archer", tier: 2, maxHp: 8, damage: 4, moveRange: 1,
                    attackRange: 2, piercesAllies: false, minAttackRange: 1, traits: new[] { Trait.Survivant },
                    attackDomaine: null,
                    new UnitClass("Paysanne archère duelliste", "paysan_archer_duelliste", tier: 3, maxHp: 8, damage: 4,
                        moveRange: 2, attackRange: 2, piercesAllies: false, minAttackRange: 1,
                        traits: new[] { Trait.Survivant, Trait.Duelliste }),
                    new UnitClass("Paysanne archère rempart", "paysan_archer_rempart", tier: 3, maxHp: 18, damage: 4,
                        moveRange: 1, attackRange: 2, piercesAllies: false, minAttackRange: 1,
                        traits: new[] { Trait.Survivant, Trait.Rempart })),
                new UnitClass("Paysanne épéiste", "paysan_epeiste", tier: 2, maxHp: 8, damage: 4, moveRange: 2,
                    attackRange: 1, piercesAllies: false, minAttackRange: 1, traits: new[] { Trait.Survivant },
                    attackDomaine: null,
                    new UnitClass("Paysanne épéiste duelliste", "paysan_epeiste_duelliste", tier: 3, maxHp: 8, damage: 4,
                        moveRange: 3, attackRange: 1, piercesAllies: false, minAttackRange: 1,
                        traits: new[] { Trait.Survivant, Trait.Duelliste }),
                    new UnitClass("Paysanne épéiste rempart", "paysan_epeiste_rempart", tier: 3, maxHp: 18, damage: 4,
                        moveRange: 2, attackRange: 1, piercesAllies: false, minAttackRange: 1,
                        traits: new[] { Trait.Survivant, Trait.Rempart })))),
    };
}
