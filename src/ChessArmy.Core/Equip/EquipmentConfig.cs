using System.Collections.Generic;

namespace ChessArmy.Core.Equip;

/// <summary>Racine du fichier de configuration des équipements (equipment.json).</summary>
public sealed class EquipmentConfig
{
    public List<EquipmentEntry> Equipments { get; set; } = new();
}

/// <summary>
/// Une entrée d'équipement. Ses effets se déclarent soit via le tableau <c>effects</c> (un ou plusieurs
/// effets : chacun est un bonus de stat <c>{ "stat", "amount" }</c> ou un trait <c>{ "trait" }</c>), soit
/// via les champs HÉRITÉS de premier niveau (<c>stat</c>+<c>amount</c> ou <c>trait</c>) traités comme un
/// effet unique. <c>rarity</c> = "Common"/"Rare".
/// </summary>
public sealed class EquipmentEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Rarity { get; set; } = "Common";

    /// <summary>Nom du PNG d'icône 32×32 (Assets/Equipment/&lt;icon&gt;.png). Absent → défaut = <see cref="Id"/>.</summary>
    public string? Icon { get; set; }

    /// <summary>Effets multiples (prioritaire s'il est présent). Chaque effet = stat+amount OU trait.</summary>
    public List<EquipEffectEntry>? Effects { get; set; }

    // ── Forme HÉRITÉE (effet unique) : utilisée si "effects" est absent ─────────────────────────────

    /// <summary>Stat visée (Hp/Damage/MoveRange/AttackRange) pour un effet de stat unique.</summary>
    public string? Stat { get; set; }

    /// <summary>Bonus plat pour un effet de stat unique.</summary>
    public int Amount { get; set; }

    /// <summary>Nom canonique du trait (cf. Trait.cs) pour un effet de trait unique.</summary>
    public string? Trait { get; set; }
}

/// <summary>
/// Un effet dans le tableau <c>effects</c> : un bonus de STAT (<c>stat</c> + <c>amount</c>) OU un TRAIT
/// (<c>trait</c> renseigné). La présence de <c>trait</c> décide de la nature de l'effet.
/// </summary>
public sealed class EquipEffectEntry
{
    public string? Stat { get; set; }
    public int Amount { get; set; }
    public string? Trait { get; set; }
}
