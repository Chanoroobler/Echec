namespace Echec.Engine.Settings;

/// <summary>Réglages d'affichage : résolution, plein écran, et bordure de fenêtre.</summary>
public sealed class DisplaySettings
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public bool Fullscreen { get; set; }

    /// <summary>Fenêtre sans bordure (titre/cadre) en mode FENÊTRÉ. Ignoré en plein écran (déjà borderless).</summary>
    public bool Borderless { get; set; }
}
