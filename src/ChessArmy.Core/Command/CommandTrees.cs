using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Command;

/// <summary>
/// Registre des arbres de commandement, chargé depuis <c>commander_trees.json</c> via <see cref="Load"/> ;
/// à défaut, l'arbre du commandant de départ (codé dans <see cref="Defaults"/>) sert de repli (tests,
/// fichier manquant). Pendant de <see cref="Domaines"/> / <see cref="Equipments"/> pour les arbres.
/// Un commandant référence son arbre par <see cref="CommandeDef.TreeId"/> : chaque futur commandant
/// apporte le sien, sans toucher au code.
/// </summary>
public static class CommandTrees
{
    /// <summary>Identifiant de l'arbre du commandant de départ.</summary>
    public const string DefaultTreeId = "commandant";

    private static IReadOnlyList<CommandTree> _all = Defaults();
    private static Dictionary<string, CommandTree> _byId = Index(_all);

    /// <summary>Remplace les arbres (depuis le JSON). Ignoré si la liste est vide.</summary>
    public static void Load(IReadOnlyList<CommandTree> trees)
    {
        if (trees.Count == 0)
            return;
        _all = trees;
        _byId = Index(trees);
    }

    /// <summary>Restaure le repli codé (réinitialise l'état statique partagé entre tests).</summary>
    public static void ResetToDefaults()
    {
        _all = Defaults();
        _byId = Index(_all);
    }

    public static IReadOnlyList<CommandTree> All => _all;

    /// <summary>Arbre par identifiant, avec repli sur le premier arbre défini si l'id est inconnu.</summary>
    public static CommandTree ById(string id) => _byId.GetValueOrDefault(id) ?? _all[0];

    /// <summary>Arbre du commandant donné (cf. <see cref="CommandeDef.TreeId"/>).</summary>
    public static CommandTree For(CommandeDef commander) => ById(commander.TreeId);

    private static Dictionary<string, CommandTree> Index(IReadOnlyList<CommandTree> trees) =>
        trees.ToDictionary(t => t.Id);

    // Repli codé (DOIT rester aligné avec Assets/Config/commander_trees.json) : l'arbre du commandant de
    // départ, 3 branches × 4 niveaux. Branche 0 = commandant, 1 = troupes, 2 = logistique.
    private static IReadOnlyList<CommandTree> Defaults() => new[]
    {
        new CommandTree(DefaultTreeId, new[]
        {
            // ── Branche 0 : le COMMANDANT lui-même ──────────────────────────────────────────────
            Node("cmd_Esquive",    0, 1, CommandEffect.CommanderTrait(Trait.Esquive)),
            Node("cmd_puissance",  0, 2, CommandEffect.CommanderStat(EquipStat.Damage, 1, CommandScale.PerDistinctPair)),
            Node("cmd_vie",        0, 2, CommandEffect.CommanderStat(EquipStat.Hp, 2, CommandScale.PerDistinctPair)),
            Node("cmd_mouvement",  0, 3, CommandEffect.CommanderStat(EquipStat.MoveRange, 1)),
            Node("cmd_Orage",      0, 3, CommandEffect.CommanderTrait(Trait.Orage)),
            Node("cmd_portee",     0, 4, CommandEffect.CommanderStat(EquipStat.AttackRange, 1)),

            // ── Branche 1 : les TROUPES (tout le roster hors commandant) ────────────────────────
            Node("troupe_vie",       1, 1, CommandEffect.UnitStat(EquipStat.Hp, 2)),
            Node("troupe_puissance", 1, 2, CommandEffect.UnitStat(EquipStat.Damage, 1, CommandScale.PerDistinctPair)),
            Node("troupe_vie_paire", 1, 2, CommandEffect.UnitStat(EquipStat.Hp, 1, CommandScale.PerDistinctPair)),
            Node("troupe_releve",    1, 3, CommandEffect.EliteDeathRecruit()),
            Node("troupe_mouvement", 1, 4, CommandEffect.UnitStat(EquipStat.MoveRange, 1)),

            // ── Branche 2 : la LOGISTIQUE (déploiement + réserve) ───────────────────────────────
            Node("logi_deploiement_1", 2, 1, CommandEffect.DeploySlots(1)),
            Node("logi_reserve_1",     2, 2, CommandEffect.ReserveSlots(2)),
            Node("logi_deploiement_2", 2, 2, CommandEffect.DeploySlots(1)),
            Node("logi_reserve_2",     2, 3, CommandEffect.ReserveSlots(2)),
            Node("logi_deploiement_3", 2, 3, CommandEffect.DeploySlots(1)),
            Node("logi_reserve_3",     2, 4, CommandEffect.ReserveSlots(4)),
        }),
    };

    /// <summary>Nœud du repli codé : l'icône porte l'id du nœud (même règle que le catalogue JSON).</summary>
    private static CommandNode Node(string id, int branch, int level, params CommandEffect[] effects) =>
        new(id, branch, level, id, effects);
}
