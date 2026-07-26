using System.Collections.Generic;
using System.Linq;

namespace ChessArmy.Core.Battle;

/// <summary>
/// Registre des COMMANDANTS (rôle Commander). Chargé depuis units.json via <see cref="Load"/> ; à défaut,
/// des valeurs codées servent de repli (tests, fichier manquant). Pendant de <see cref="Domaines"/> pour le
/// rôle COMMANDE. Les BOSS ont leur propre registre <see cref="Bosses"/> (format à profils par phase).
/// </summary>
public static class Commandes
{
    private static IReadOnlyList<CommandeDef> _all = Defaults();

    /// <summary>Remplace les définitions (depuis le JSON). Ignoré si la liste est vide.</summary>
    public static void Load(IReadOnlyList<CommandeDef> defs)
    {
        if (defs.Count == 0)
            return;
        _all = defs;
    }

    public static IReadOnlyList<CommandeDef> All => _all;

    /// <summary>Commandants JOUABLES, dans l'ordre du JSON : c'est la source du carrousel de sélection.</summary>
    public static IReadOnlyList<CommandeDef> Playable =>
        _all.Where(c => c.Role == CommandeRole.Commander).ToList();

    /// <summary>Commandant par défaut (premier défini) : sert quand aucun choix n'a été fait.</summary>
    public static CommandeDef Commander => _all.First(c => c.Role == CommandeRole.Commander);

    /// <summary>
    /// Commandant par son <see cref="CommandeDef.Id"/> (insensible à la casse), ou <c>null</c>. Utilisé à la
    /// reprise d'une sauvegarde : un id inconnu (commandant supprimé du JSON) doit être traité par l'appelant.
    /// </summary>
    public static CommandeDef? ById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : _all.FirstOrDefault(c => c.Role == CommandeRole.Commander
                && string.Equals(c.Id, id, System.StringComparison.OrdinalIgnoreCase));

    // Repli codé (doit rester aligné avec Assets/Config/units.json). Les boss sont dans Bosses.Defaults.
    private static IReadOnlyList<CommandeDef> Defaults() => new[]
    {
        new CommandeDef(CommandeRole.Commander, Domaine.Dame,
            new UnitClass("Commandant", "commandant", tier: 1, maxHp: 26, damage: 6, moveRange: 2, attackRange: 1),
            deployments: 5, reserveSize: 8, treeId: "commandant", fusionPoints: 4, id: "commandant"),
    };
}
