using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Engine.UI;

/// <summary>État visuel d'un bouton selon l'interaction souris.</summary>
public enum ButtonState { Normal, Hover, Pressed }

/// <summary>
/// Style d'UI partagé : panneaux et boutons « pixel-art » — fond TRAMÉ (dither) + RELIEF par
/// biseau (liseré clair en haut/gauche, ombre épaisse en bas/droite). Les boutons donnent un
/// retour d'ENFONCEMENT : léger au survol, plus marqué au clic (biseau inversé + contenu décalé
/// vers le bas). Toutes les couleurs viennent de la <see cref="Palette"/>. (Portée de CosyFarmer.)
/// </summary>
public sealed class UiStyle
{
    private static readonly Color Highlight = Palette.Black5; // arête éclairée
    private static readonly Color Shadow = Palette.Black1;    // arête dans l'ombre
    private static readonly Color HoverTint = Palette.White;  // × alpha (éclaircit au survol)
    private static readonly Color PressTint = Palette.Black1; // × alpha (assombrit au clic)

    private readonly Texture2D _pixel;
    private readonly Texture2D _tile;

    public UiStyle(Texture2D pixel, Texture2D tile)
    {
        _pixel = pixel;
        _tile = tile;
    }

    /// <summary>État d'un bouton à partir du survol et de l'enfoncement du bouton souris.</summary>
    public static ButtonState StateOf(bool hover, bool pointerDown)
        => !hover ? ButtonState.Normal : (pointerDown ? ButtonState.Pressed : ButtonState.Hover);

    /// <summary>Remplit <paramref name="r"/> avec la tuile tramée (répétée, rognée aux bords).</summary>
    public void FillDither(SpriteBatch sb, Rectangle r)
    {
        for (int y = r.Y; y < r.Bottom; y += _tile.Height)
            for (int x = r.X; x < r.Right; x += _tile.Width)
            {
                int w = Math.Min(_tile.Width, r.Right - x);
                int h = Math.Min(_tile.Height, r.Bottom - y);
                sb.Draw(_tile, new Rectangle(x, y, w, h), new Rectangle(0, 0, w, h), Color.White);
            }
    }

    /// <summary>Panneau en relief : cadre sombre + fond tramé + biseau surélevé.</summary>
    public void DrawPanel(SpriteBatch sb, Rectangle r)
    {
        Frame(sb, r);
        FillDither(sb, r);
        Bevel(sb, r, raised: true, thickness: 3);
    }

    /// <summary>Zone enfoncée (ex. valeur d'un stepper) : fond tramé + biseau inversé.</summary>
    public void DrawRecessed(SpriteBatch sb, Rectangle r)
    {
        FillDither(sb, r);
        Bevel(sb, r, raised: false, thickness: 2);
    }

    /// <summary>
    /// Bouton avec retour d'état. Renvoie le décalage VERTICAL à appliquer au contenu
    /// (texte/icône) pour accentuer la sensation d'enfoncement.
    /// </summary>
    public int DrawButton(SpriteBatch sb, Rectangle r, ButtonState state)
    {
        Frame(sb, r);
        FillDither(sb, r);
        switch (state)
        {
            case ButtonState.Hover:
                sb.Draw(_pixel, r, HoverTint * 0.12f);
                Bevel(sb, r, raised: false, thickness: 1);
                return 1;
            case ButtonState.Pressed:
                sb.Draw(_pixel, r, PressTint * 0.30f);
                Bevel(sb, r, raised: false, thickness: 3);
                return 2;
            default:
                Bevel(sb, r, raised: true, thickness: 3);
                return 0;
        }
    }

    private void Frame(SpriteBatch sb, Rectangle r)
        => sb.Draw(_pixel, new Rectangle(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), Shadow);

    private void Bevel(SpriteBatch sb, Rectangle r, bool raised, int thickness)
    {
        Color top = raised ? Highlight : Shadow;
        Color bottom = raised ? Shadow : Highlight;
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 1), top);                             // haut
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, 1, r.Height), top);                            // gauche
        sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - thickness, r.Width, thickness), bottom); // bas
        sb.Draw(_pixel, new Rectangle(r.Right - thickness, r.Y, thickness, r.Height), bottom); // droite
    }
}
