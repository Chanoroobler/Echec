using System;
using System.Globalization;
using ChessArmy.Core.Equip;
using ChessArmy.Engine.Localization;

namespace ChessArmy.Game.UI;

/// <summary>
/// Nom LOCALISÉ d'un équipement, partagé par tous les écrans (combat, Codex…). Clé <c>equip.&lt;id&gt;</c> ;
/// les variantes de rareté (« Rare » / « Legendaire ») partagent la clé de base (id sans suffixe). Repli sur
/// le nom brut de <c>equipment.json</c> si aucune traduction. Voir <c>Assets/Config/strings.csv</c> (equip.*).
/// </summary>
public static class EquipmentNames
{
    public static string Localized(Equipment equip) =>
        Loc.TOr("equip." + equip.Id, null!) ?? Loc.TOr("equip." + BaseId(equip.Id), equip.Name);

    /// <summary>
    /// Montant d'un bonus de stat AVEC son signe (« +10 », « -10 ») : la clé <c>equip.stat_bonus</c> ne porte
    /// plus le « + », sinon un malus s'afficherait « +-10 ».
    /// </summary>
    public static string Signed(int amount) => amount.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    /// <summary>Id sans suffixe de rareté : clé de nom partagée par les variantes commune/rare/légendaire.</summary>
    public static string BaseId(string id)
    {
        if (id.EndsWith("Legendaire", StringComparison.Ordinal)) return id[..^"Legendaire".Length];
        if (id.EndsWith("Rare", StringComparison.Ordinal)) return id[..^"Rare".Length];
        return id;
    }
}
