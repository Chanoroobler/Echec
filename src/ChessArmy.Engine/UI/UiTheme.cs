using Microsoft.Xna.Framework;

namespace ChessArmy.Engine.UI;

/// <summary>Thème de couleur de l'UI, choisi dans le menu Options (Doré par défaut).</summary>
public enum UiThemeId { Dark, Olive, Gold, Crimson }

/// <summary>
/// Couleurs d'un thème d'UI. Ne touche QUE le tramage des panneaux/pop-ups/boutons, leur biseau, et le fond +
/// liseré gauche du panneau de droite : ni textes, ni plateau, ni voile, ni surbrillances.
/// </summary>
/// <param name="DitherA">Tramage pop-ups / boutons, ton A.</param>
/// <param name="DitherB">Tramage pop-ups / boutons, ton B.</param>
/// <param name="Highlight">Arête claire du biseau.</param>
/// <param name="Shadow">Arête sombre du biseau.</param>
/// <param name="PanelA">Tramage du panneau de droite, ton A.</param>
/// <param name="PanelB">Tramage du panneau de droite, ton B.</param>
/// <param name="PanelEdge">Liseré gauche (2 px) du panneau de droite.</param>
/// <param name="SelectA">Tramage « sélection / survol » des boutons (fond plus clair), ton A.</param>
/// <param name="SelectB">Tramage « sélection / survol » des boutons, ton B.</param>
public sealed record UiTheme(Color DitherA, Color DitherB, Color Highlight, Color Shadow,
    Color PanelA, Color PanelB, Color PanelEdge, Color SelectA, Color SelectB)
{
    /// <summary>Ordre de défilement dans le menu Options : DORE → OLIVE → CRAMOISI → SOMBRE (en boucle).</summary>
    public static readonly UiThemeId[] MenuOrder = { UiThemeId.Gold, UiThemeId.Olive, UiThemeId.Crimson, UiThemeId.Dark };

    public static UiTheme For(UiThemeId id) => id switch
    {
        UiThemeId.Dark => new(Palette.Black3, Palette.Black2, Palette.Black5, Palette.Black1,
            Palette.Black3, Palette.Black2, Palette.Navy1, Palette.Black4, Palette.Black5),
        UiThemeId.Olive => new(Palette.Green2, Palette.Green1, Palette.Grey, Palette.Black1,
            Palette.Green2, Palette.Green1, Palette.Grey, Palette.Green1, Palette.Grey),
        UiThemeId.Crimson => new(Palette.Purple1, Palette.Purple2, Palette.Purple3, Palette.Black1,
            Palette.Black2, Palette.Purple1, Palette.Purple2, Palette.Purple2, Palette.Purple3),
        _ => new(Palette.Black4, Palette.Black5, Palette.Yellow1, Palette.Brown1,
            Palette.Black4, Palette.Black5, Palette.Yellow1, Palette.Black5, Palette.Blue3),
    };
}
