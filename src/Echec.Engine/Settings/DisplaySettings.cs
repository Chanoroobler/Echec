namespace Echec.Engine.Settings;

/// <summary>Réglages d'affichage : résolution et plein écran.</summary>
public sealed class DisplaySettings
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public bool Fullscreen { get; set; }
}
