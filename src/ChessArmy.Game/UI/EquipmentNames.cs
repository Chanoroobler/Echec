using System;
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

    /// <summary>Id sans suffixe de rareté : clé de nom partagée par les variantes commune/rare/légendaire.</summary>
    public static string BaseId(string id)
    {
        if (id.EndsWith("Legendaire", StringComparison.Ordinal)) return id[..^"Legendaire".Length];
        if (id.EndsWith("Rare", StringComparison.Ordinal)) return id[..^"Rare".Length];
        return id;
    }
}
