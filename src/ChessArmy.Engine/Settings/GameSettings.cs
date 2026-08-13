using ChessArmy.Engine.Localization;

namespace ChessArmy.Engine.Settings;

/// <summary>Agrège tous les réglages modifiables depuis le menu Options.</summary>
public sealed class GameSettings
{
    public DisplaySettings Display { get; } = new();
    public AudioSettings Audio { get; } = new();

    /// <summary>Langue de l'interface. Pilote <see cref="Loc.Current"/>.</summary>
    public Language Language { get; set; } = Language.Francais;

    /// <summary>
    /// Mode DÉMO (version de démonstration). Restreint la partie : la run s'arrête plus tôt (cf.
    /// <c>Run.EndAtPhase</c>), seuls les commandants ouverts d'office sont jouables (les autres restent
    /// verrouillés en vitrine) et les unités sont plafonnées au tier 2 (cf. <c>Run.MaxUnitTier</c>). Activé
    /// au démarrage par options.json, l'argument -demo, ou un fichier marqueur demo.flag (cf. ChessArmyGame).
    /// </summary>
    public bool IsDemo { get; set; }
}
