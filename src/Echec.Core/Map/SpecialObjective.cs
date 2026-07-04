namespace Echec.Core.Map;

/// <summary>
/// Sous-type d'une mission <see cref="CombatType.Speciale"/> : l'objectif précis de la map. Le
/// <see cref="CombatType"/> dit « c'est une mission spéciale » ; ce sous-type dit LAQUELLE (le jeu y
/// branche ses règles d'objectif). Vaut <see cref="Aucun"/> pour une map non spéciale.
/// </summary>
public enum SpecialObjective
{
    /// <summary>Pas d'objectif spécial (maps Escarmouche/Boss, ou Speciale non renseignée).</summary>
    Aucun,

    /// <summary>Libérer le maximum de paysans (tuiles recrue) avant la limite de tours (le joueur les récupère).</summary>
    LibererPaysans,

    /// <summary>Protéger les paysans : les ennemis (IA offensive) tentent de les capturer ; garder le maximum vivants.</summary>
    ProtegerPaysans,
}
