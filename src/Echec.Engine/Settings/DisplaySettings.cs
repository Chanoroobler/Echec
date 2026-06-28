namespace Echec.Engine.Settings;

/// <summary>Mode d'affichage de la fenêtre (un seul réglage : remplace les anciens booléens
/// « plein écran » + « sans bordure » qui se contredisaient).</summary>
public enum WindowMode
{
    /// <summary>Fenêtre classique, avec bordure et barre de titre, à la résolution choisie.</summary>
    Windowed,

    /// <summary>Fenêtre SANS bordure ni titre, à la résolution choisie.</summary>
    Borderless,

    /// <summary>Plein écran « fenêtré » : fenêtre sans bordure à la résolution NATIVE de l'écran.</summary>
    Fullscreen,
}

/// <summary>Réglages d'affichage : résolution et mode de fenêtre.</summary>
public sealed class DisplaySettings
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;

    /// <summary>Mode de fenêtre. La résolution (Width/Height) est ignorée en <see cref="WindowMode.Fullscreen"/>.</summary>
    public WindowMode Mode { get; set; } = WindowMode.Windowed;
}
