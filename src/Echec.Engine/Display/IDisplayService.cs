using Echec.Engine.Settings;

namespace Echec.Engine.Display;

/// <summary>
/// Applique des réglages d'affichage au périphérique graphique.
/// Implémenté par la couche Game (qui possède le GraphicsDeviceManager).
/// </summary>
public interface IDisplayService
{
    void Apply(DisplaySettings settings);
}
