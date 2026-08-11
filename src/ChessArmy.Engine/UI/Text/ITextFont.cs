using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Engine.UI.Text;

/// <summary>
/// Abstraction d'une police de texte de l'UI. Deux implémentations : <see cref="PixelFont"/> (5×7 procédurale,
/// langues latines) et <see cref="BdfFont"/> (bitmap CJK Fusion Pixel, chinois). La police ACTIVE est choisie
/// selon <see cref="ChessArmy.Engine.Localization.Loc.Current"/> via <see cref="Fonts.Active"/> ; les renderers
/// tapent <c>Context.Font</c> (qui délègue à la police active) à chaque frame, donc le changement de langue à
/// chaud bascule aussi la police. Les signatures reprennent celles de PixelFont pour un remplacement transparent.
/// </summary>
public interface ITextFont
{
    /// <summary>Hauteur d'un glyphe à l'échelle 1 (sert aux calculs de mise en page ; ex. centrage vertical).</summary>
    int GlyphHeight { get; }

    int LineHeight(int scale = 1);
    int Measure(string text, int scale = 1);
    void Draw(SpriteBatch sb, string text, Vector2 pos, int scale, Color color, bool preserveCase = false);
    void DrawCentered(SpriteBatch sb, string text, Rectangle area, int scale, Color color, bool preserveCase = false);
    void DrawGradient(SpriteBatch sb, string text, Vector2 pos, int scale, Color[] stops, bool preserveCase = false);
    void DrawCenteredGradient(SpriteBatch sb, string text, Rectangle area, int scale, Color[] stops, bool preserveCase = false);
}
