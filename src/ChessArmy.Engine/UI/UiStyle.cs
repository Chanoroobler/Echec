using System;
using ChessArmy.Engine.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Engine.UI;

/// <summary>État visuel d'un bouton selon l'interaction souris.</summary>
public enum ButtonState { Normal, Hover, Pressed }

/// <summary>
/// Style d'UI partagé : panneaux et boutons « pixel-art » — fond TRAMÉ (dither) + RELIEF par
/// biseau (liseré clair en haut/gauche, ombre épaisse en bas/droite). Les boutons donnent un
/// retour d'ENFONCEMENT : léger au survol, plus marqué au clic (biseau inversé + contenu décalé
/// vers le bas). Toutes les couleurs viennent de la <see cref="Palette"/>. (Portée de CosyFarmer.)
/// Les couleurs du tramage et du biseau suivent le <see cref="UiTheme"/> courant, changeable à chaud
/// (<see cref="SetTheme"/>) : tout ce qui est dessiné ensuite prend les nouvelles couleurs, sans redémarrer.
/// </summary>
public sealed class UiStyle
{
    private const int TileSize = 8;

    private readonly GraphicsDevice _device;
    private readonly Texture2D _pixel;
    private Texture2D _tile = null!;        // pop-ups, panneaux, boutons
    private Texture2D _panelTile = null!;   // fond du panneau de droite
    private Texture2D _selectTile = null!;  // fond clair des boutons survolés / sélectionnés

    public UiStyle(GraphicsDevice device, Texture2D pixel, UiThemeId theme)
    {
        _device = device;
        _pixel = pixel;
        SetTheme(theme);
    }

    /// <summary>Thème courant (couleurs du tramage, du biseau et du panneau de droite).</summary>
    public UiTheme Theme { get; private set; } = null!;
    public UiThemeId ThemeId { get; private set; }

    private Color Highlight => Theme.Highlight;   // arête éclairée
    private Color Shadow => Theme.Shadow;         // arête dans l'ombre

    /// <summary>Applique un thème À CHAUD : recrée les trois tuiles tramées, le biseau suit (lu à chaque dessin).</summary>
    public void SetTheme(UiThemeId id)
    {
        var theme = UiTheme.For(id);
        var tile = Textures.CreateDitherTile(_device, TileSize, theme.DitherA, theme.DitherB);
        var panelTile = Textures.CreateDitherTile(_device, TileSize, theme.PanelA, theme.PanelB);
        var selectTile = Textures.CreateDitherTile(_device, TileSize, theme.SelectA, theme.SelectB);
        _tile?.Dispose();
        _panelTile?.Dispose();
        _selectTile?.Dispose();
        _tile = tile;
        _panelTile = panelTile;
        _selectTile = selectTile;
        Theme = theme;
        ThemeId = id;
    }

    /// <summary>État d'un bouton à partir du survol et de l'enfoncement du bouton souris.</summary>
    public static ButtonState StateOf(bool hover, bool pointerDown)
        => !hover ? ButtonState.Normal : (pointerDown ? ButtonState.Pressed : ButtonState.Hover);

    /// <summary>Remplit <paramref name="r"/> avec la tuile tramée (répétée, rognée aux bords).</summary>
    public void FillDither(SpriteBatch sb, Rectangle r) => Fill(sb, r, _tile);

    /// <summary>Remplit <paramref name="r"/> avec le tramage du PANNEAU DE DROITE (couleurs propres au thème).</summary>
    public void FillPanelDither(SpriteBatch sb, Rectangle r) => Fill(sb, r, _panelTile);

    /// <summary>Hauteur du motif du fond de panneau : pour caler un prolongement sur la même trame.</summary>
    public int PanelTileHeight => _panelTile.Height;

    private static void Fill(SpriteBatch sb, Rectangle r, Texture2D tile)
    {
        for (int y = r.Y; y < r.Bottom; y += tile.Height)
            for (int x = r.X; x < r.Right; x += tile.Width)
            {
                int w = Math.Min(tile.Width, r.Right - x);
                int h = Math.Min(tile.Height, r.Bottom - y);
                sb.Draw(tile, new Rectangle(x, y, w, h), new Rectangle(0, 0, w, h), Color.White);
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
    /// Survol et clic prennent la tuile CLAIRE « sélection » du thème (le bouton s'enfonce et s'éclaire) ;
    /// <paramref name="selected"/> donne ce même fond clair à un bouton au repos (choix retenu), biseau normal.
    /// </summary>
    public int DrawButton(SpriteBatch sb, Rectangle r, ButtonState state, bool selected = false)
    {
        Frame(sb, r);
        Fill(sb, r, state != ButtonState.Normal || selected ? _selectTile : _tile);
        switch (state)
        {
            case ButtonState.Hover:
                Bevel(sb, r, raised: false, thickness: 1);
                return 1;
            case ButtonState.Pressed:
                Bevel(sb, r, raised: false, thickness: 3);
                return 2;
            default:
                Bevel(sb, r, raised: true, thickness: 3);
                return 0;
        }
    }

    // Cadre extérieur d'1 px : TOUJOURS Black1, quel que soit le thème.
    private void Frame(SpriteBatch sb, Rectangle r)
        => sb.Draw(_pixel, new Rectangle(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), Palette.Black1);

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
