using ChessArmy.Engine.Localization;

namespace ChessArmy.Engine.UI.Text;

/// <summary>
/// Sélecteur de la police de texte ACTIVE selon la langue (même motif statique que <see cref="Loc"/>).
/// Renseigné une fois au démarrage (<see cref="Pixel"/> = PixelFont latine, <see cref="Cjk"/> = BdfFont chinoise).
/// <see cref="Active"/> renvoie la CJK uniquement quand la langue active est le chinois ET que sa police a bien
/// chargé ; sinon la PixelFont. Les renderers passent par <c>GameContext.Font</c> (qui délègue ici) à chaque
/// frame, donc changer de langue à chaud change aussi la police sans recréer quoi que ce soit.
/// </summary>
public static class Fonts
{
    public static ITextFont Pixel { get; set; } = null!;
    public static ITextFont? Cjk { get; set; }

    public static ITextFont Active =>
        Loc.Current == Language.ChineseSimplified && Cjk != null ? Cjk : Pixel;
}
