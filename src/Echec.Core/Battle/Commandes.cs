using System.Collections.Generic;
using System.Linq;

namespace Echec.Core.Battle;

/// <summary>
/// Registre des unités COMMANDE (commandant joueur, boss). Chargé depuis units.json
/// via <see cref="Load"/> ; à défaut, des valeurs codées servent de repli (tests,
/// fichier manquant). Pendant de <see cref="Domaines"/> pour le rôle COMMANDE.
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

    /// <summary>Premier boss défini.</summary>
    public static CommandeDef Boss => _all.First(c => c.Role == CommandeRole.Boss);

    // Repli codé (doit rester aligné avec Assets/Config/units.json).
    private static IReadOnlyList<CommandeDef> Defaults() => new[]
    {
        new CommandeDef(CommandeRole.Commander, Domaine.Dame,
            new UnitClass("Commandant", "commandant", tier: 1, maxHp: 26, damage: 6, moveRange: 2, attackRange: 1)),
        new CommandeDef(CommandeRole.Boss, Domaine.Pion,
            new UnitClass("Boss", "boss", tier: 1, maxHp: 30, damage: 8, moveRange: 1, attackRange: 1)),
    };
}
