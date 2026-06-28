using Echec.Engine.Localization;
using Echec.Engine.UI.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Engine.UI;

/// <summary>
/// Rendu sobre du menu pause et du panneau Options : overlay assombrissant, panneau
/// central, boutons et contrôles (steppers / bascule). Le layout vient de
/// <see cref="PauseMenu.Layout"/> (même source que le hit-test), donc ce qui est
/// dessiné est exactement ce qui est cliquable. (Porté de CosyFarmer.)
/// </summary>
public sealed class PauseMenuRenderer
{
    private static readonly Color Overlay = Palette.Navy2 * 0.85f;
    private static readonly Color Text = Palette.White;
    private static readonly Color TextDim = Palette.Blue1;
    private static readonly Color TitleColor = Palette.Yellow2;

    private readonly Texture2D _pixel;
    private readonly PixelFont _font;
    private readonly UiStyle _style;

    public PauseMenuRenderer(Texture2D pixel, PixelFont font, UiStyle style)
    {
        _pixel = pixel;
        _font = font;
        _style = style;
    }

    /// <summary>
    /// <paramref name="focus"/> : ligne sous le focus manette. Les boutons de cette ligne (‹ ›,
    /// bascule, RETOUR…) s'allument comme au survol (null en souris : le survol suffit).
    /// </summary>
    public void Draw(SpriteBatch sb, PauseMenu menu, int vpW, int vpH, Vector2 pointer, bool pointerDown,
        Rectangle? focus = null)
    {
        var p = pointer.ToPoint();
        sb.Draw(_pixel, new Rectangle(0, 0, vpW, vpH), Overlay);

        var l = menu.Layout(vpW, vpH);
        _style.DrawPanel(sb, l.Panel);

        if (menu.Panel == MenuPanel.Root)
        {
            _font.DrawCentered(sb, Loc.T("menu.pause"), l.Title, 2, TitleColor);
            Button(sb, l.Resume, Loc.T("menu.resume"), p, pointerDown, focus);
            Button(sb, l.Options, Loc.T("menu.options"), p, pointerDown, focus);
            Button(sb, l.MainMenu, Loc.T("menu.main_menu"), p, pointerDown, focus);
            Button(sb, l.Quit, Loc.T("menu.quit"), p, pointerDown, focus);
        }
        else
        {
            _font.DrawCentered(sb, Loc.T("options.title"), l.Title, 2, TitleColor);

            Label(sb, l.ResRow, Loc.T("options.resolution"));
            Stepper(sb, l.ResLeft, l.ResValue, l.ResRight, menu.ResolutionText, p, pointerDown, focus);

            Label(sb, l.ModeRow, Loc.T("options.display_mode"));
            Stepper(sb, l.ModeLeft, l.ModeValue, l.ModeRight, menu.ModeText, p, pointerDown, focus);

            Label(sb, l.MasterRow, Loc.T("options.volume_master"));
            Stepper(sb, l.MasterLeft, l.MasterValue, l.MasterRight, menu.MasterVolumeText, p, pointerDown, focus);

            Label(sb, l.MusicRow, Loc.T("options.volume_music"));
            Stepper(sb, l.MusicLeft, l.MusicValue, l.MusicRight, menu.MusicVolumeText, p, pointerDown, focus);

            Label(sb, l.SfxRow, Loc.T("options.volume_sfx"));
            Stepper(sb, l.SfxLeft, l.SfxValue, l.SfxRight, menu.SfxVolumeText, p, pointerDown, focus);

            Label(sb, l.LangRow, Loc.T("options.language"));
            Stepper(sb, l.LangLeft, l.LangValue, l.LangRight, menu.LanguageText, p, pointerDown, focus);

            Button(sb, l.Back, Loc.T("options.back"), p, pointerDown, focus);
        }
    }

    private void Button(SpriteBatch sb, Rectangle r, string label, Point pointer, bool pointerDown,
        Rectangle? focus = null)
    {
        // Survol souris OU bouton appartenant à la ligne sous le focus manette.
        bool hover = r.Contains(pointer) || (focus?.Contains(r.Center) ?? false);
        int dy = _style.DrawButton(sb, r, UiStyle.StateOf(hover, pointerDown));
        var area = r; area.Offset(0, dy);
        _font.DrawCentered(sb, label, area, 1, Text);
    }

    private void Stepper(SpriteBatch sb, Rectangle left, Rectangle value, Rectangle right,
        string text, Point pointer, bool pointerDown, Rectangle? focus = null)
    {
        Button(sb, left, "<", pointer, pointerDown, focus);
        _style.DrawRecessed(sb, value);
        _font.DrawCentered(sb, text, value, 1, Text);
        Button(sb, right, ">", pointer, pointerDown, focus);
    }

    /// <summary>Libellé d'une ligne d'option, aligné à gauche et centré verticalement.</summary>
    private void Label(SpriteBatch sb, Rectangle row, string text)
    {
        int y = row.Y + (row.Height - _font.LineHeight()) / 2;
        _font.Draw(sb, text, new Vector2(row.X + 18, y), 1, TextDim);
    }
}
