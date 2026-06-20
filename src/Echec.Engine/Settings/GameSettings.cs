namespace Echec.Engine.Settings;

/// <summary>Agrège tous les réglages modifiables depuis le menu Options.</summary>
public sealed class GameSettings
{
    public DisplaySettings Display { get; } = new();
    public AudioSettings Audio { get; } = new();
}
