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

    public void Draw(SpriteBatch sb, PauseMenu menu, int vpW, int vpH, Vector2 pointer, bool pointerDown)
    {
        var p = pointer.ToPoint();
        sb.Draw(_pixel, new Rectangle(0, 0, vpW, vpH), Overlay);

        var l = menu.Layout(vpW, vpH);
        _style.DrawPanel(sb, l.Panel);

        if (menu.Panel == MenuPanel.Root)
        {
            _font.DrawCentered(sb, "PAUSE", l.Title, 2, TitleColor);
            Button(sb, l.Resume, "REPRENDRE", p, pointerDown);
            Button(sb, l.Options, "OPTIONS", p, pointerDown);
            Button(sb, l.Quit, "QUITTER", p, pointerDown);
        }
        else
        {
            _font.DrawCentered(sb, "OPTIONS", l.Title, 2, TitleColor);

            Label(sb, l.ResRow, "RESOLUTION");
            Stepper(sb, l.ResLeft, l.ResValue, l.ResRight, menu.ResolutionText, p, pointerDown);

            Label(sb, l.FsRow, "PLEIN ECRAN");
            Button(sb, l.FsToggle, menu.FullscreenText, p, pointerDown);

            Label(sb, l.BdRow, "SANS BORDURE");
            Button(sb, l.BdToggle, menu.BorderlessText, p, pointerDown);

            Label(sb, l.MasterRow, "VOLUME GLOBAL");
            Stepper(sb, l.MasterLeft, l.MasterValue, l.MasterRight, menu.MasterVolumeText, p, pointerDown);

            Label(sb, l.MusicRow, "VOLUME MUSIQUE");
            Stepper(sb, l.MusicLeft, l.MusicValue, l.MusicRight, menu.MusicVolumeText, p, pointerDown);

            Label(sb, l.SfxRow, "VOLUME EFFETS");
            Stepper(sb, l.SfxLeft, l.SfxValue, l.SfxRight, menu.SfxVolumeText, p, pointerDown);

            Button(sb, l.Back, "RETOUR", p, pointerDown);
        }
    }

    private void Button(SpriteBatch sb, Rectangle r, string label, Point pointer, bool pointerDown)
    {
        bool hover = r.Contains(pointer);
        int dy = _style.DrawButton(sb, r, UiStyle.StateOf(hover, pointerDown));
        var area = r; area.Offset(0, dy);
        _font.DrawCentered(sb, label, area, 1, Text);
    }

    private void Stepper(SpriteBatch sb, Rectangle left, Rectangle value, Rectangle right,
        string text, Point pointer, bool pointerDown)
    {
        Button(sb, left, "<", pointer, pointerDown);
        _style.DrawRecessed(sb, value);
        _font.DrawCentered(sb, text, value, 1, Text);
        Button(sb, right, ">", pointer, pointerDown);
    }

    /// <summary>Libellé d'une ligne d'option, aligné à gauche et centré verticalement.</summary>
    private void Label(SpriteBatch sb, Rectangle row, string text)
    {
        int y = row.Y + (row.Height - _font.LineHeight()) / 2;
        _font.Draw(sb, text, new Vector2(row.X + 18, y), 1, TextDim);
    }
}
