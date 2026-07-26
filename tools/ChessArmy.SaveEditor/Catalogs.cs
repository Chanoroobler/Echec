using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Battle.Config;
using ChessArmy.Core.Command;
using ChessArmy.Core.Command.Config;
using ChessArmy.Core.Equip;

namespace ChessArmy.SaveEditor;

/// <summary>
/// Remplit les registres statiques d'<c>ChessArmy.Core</c> depuis les JSON du jeu, exactement comme
/// <c>ChessArmyGame.LoadUnitConfig</c> &amp; co. Sans ça, l'éditeur ne proposerait que les valeurs codées
/// de repli — les listes déroulantes doivent montrer le contenu RÉEL du jeu.
/// Tolérant : un fichier manquant ou invalide laisse le repli en place et remonte le motif.
/// </summary>
internal static class Catalogs
{
    /// <summary>Recharge tous les catalogues. Renvoie les avertissements rencontrés (liste vide = tout va bien).</summary>
    public static IReadOnlyList<string> LoadAll()
    {
        var warnings = new List<string>();

        Try(warnings, "units.json", GamePaths.UnitsJson, json =>
        {
            Domaines.Load(DomaineCatalog.FromJson(json));
            Commandes.Load(DomaineCatalog.CommandesFromJson(json));
            Bosses.Load(DomaineCatalog.BossesFromJson(json));
        });

        Try(warnings, "equipment.json", GamePaths.EquipmentJson,
            json => Equipments.Load(EquipmentCatalog.FromJson(json)));

        Try(warnings, "commander_trees.json", GamePaths.CommandTreesJson,
            json => CommandTrees.Load(CommandTreeCatalog.FromJson(json)));

        return warnings;
    }

    private static void Try(List<string> warnings, string label, string path, Action<string> load)
    {
        if (!File.Exists(path))
        {
            warnings.Add($"{label} introuvable ({path}) — valeurs codées utilisées.");
            return;
        }
        try
        {
            load(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            warnings.Add($"{label} illisible : {ex.Message}");
        }
    }

    /// <summary>Toutes les classes d'un domaine, du tier 1 aux feuilles, dans l'ordre de l'arbre.</summary>
    public static IEnumerable<UnitClass> ClassesOf(Domaine domaine) => Flatten(Domaines.Of(domaine).BaseClass);

    private static IEnumerable<UnitClass> Flatten(UnitClass root)
    {
        yield return root;
        foreach (var evolution in root.Evolutions)
            foreach (var c in Flatten(evolution))
                yield return c;
    }

    /// <summary>Équipements du catalogue, groupés par rareté croissante puis par nom.</summary>
    public static IReadOnlyList<Equipment> EquipmentsSorted() =>
        Equipments.All.OrderBy(e => e.Rarity).ThenBy(e => e.Name, StringComparer.CurrentCulture).ToList();
}
