using System.Collections.Generic;

namespace Echec.Core.Equip;

/// <summary>Racine du fichier de configuration des équipements (equipment.json).</summary>
public sealed class EquipmentConfig
{
    public List<EquipmentEntry> Equipments { get; set; } = new();
}

/// <summary>
/// Une entrée d'équipement. <c>kind</c> = "Stat" (renseigne <c>stat</c> + <c>amount</c>) ou
/// "Trait" (renseigne <c>trait</c>). <c>rarity</c> = "Common"/"Rare".
/// </summary>
public sealed class EquipmentEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Rarity { get; set; } = "Common";
    public string Kind { get; set; } = "Stat";

    /// <summary>Nom du PNG d'icône 32×32 (Assets/Equipment/&lt;icon&gt;.png). Absent → défaut = <see cref="Id"/>.</summary>
    public string? Icon { get; set; }

    /// <summary>Stat visée (Hp/Damage/MoveRange/AttackRange) pour <c>kind = Stat</c>.</summary>
    public string? Stat { get; set; }

    /// <summary>Bonus plat pour <c>kind = Stat</c>.</summary>
    public int Amount { get; set; }

    /// <summary>Nom canonique du trait (cf. Trait.cs) pour <c>kind = Trait</c>.</summary>
    public string? Trait { get; set; }
}
