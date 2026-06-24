using Echec.Engine.Localization;

namespace Echec.Engine.Settings;

/// <summary>Agrège tous les réglages modifiables depuis le menu Options.</summary>
public sealed class GameSettings
{
    public DisplaySettings Display { get; } = new();
    public AudioSettings Audio { get; } = new();

    /// <summary>Langue de l'interface. Pilote <see cref="Loc.Current"/>.</summary>
    public Language Language { get; set; } = Language.Francais;
}
