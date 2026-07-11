using System.Collections.Generic;
using System.Linq;

namespace Echec.Core.Battle;

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

    /// <summary>Premier commandant défini (le choix par le joueur viendra plus tard).</summary>
    public static CommandeDef Commander => _all.First(c => c.Role == CommandeRole.Commander);

    // Repli codé (doit rester aligné avec Assets/Config/units.json). Les boss sont dans Bosses.Defaults.
    private static IReadOnlyList<CommandeDef> Defaults() => new[]
    {
        new CommandeDef(CommandeRole.Commander, Domaine.Dame,
            new UnitClass("Commandant", "commandant", tier: 1, maxHp: 26, damage: 6, moveRange: 2, attackRange: 1),
            deployments: 5, reserveSize: 8, treeId: "commandant", fusionPoints: 4),
    };
}
