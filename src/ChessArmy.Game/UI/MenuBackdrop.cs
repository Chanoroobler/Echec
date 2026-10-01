using System;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.UI;

/// <summary>
/// Fond dégradé tramé du menu principal (et de l'écran du logo), du haut vers le bas : vert-nuit un peu
/// plus clair en haut, fondu vers le presque noir en bas. Couvre le canvas ET les bandes du letterbox,
/// pour remplir TOUT l'écran quel que soit son format (ultra-large : bandes gauche/droite ; 4:3 : haut/bas).
/// </summary>
public sealed class MenuBackdrop : IDisposable
{
    private static readonly Color Top = Palette.Black4;
    private static readonly Color Mid = Palette.Navy2;
    private static readonly Color Bottom = Palette.Black1;

    private const int DitherPeriod = 4;   // trame Bayer 4×4 : un décalage multiple de 4 garde le motif raccordé

    private readonly GraphicsDevice _device;
    private readonly Texture2D _pixel;
    private Texture2D? _canvas;   // taille du canvas
    private Texture2D? _wide;     // largeur de l'écran entier (en pixels canvas), même hauteur que le canvas

    public MenuBackdrop(GraphicsDevice device, Texture2D pixel)
    {
        _device = device;
        _pixel = pixel;
    }

    /// <summary>Dessine le dégradé sur le canvas, dans le batch ouvert par l'appelant.</summary>
    public void Draw(SpriteBatch sb, int w, int h)
    {
        _canvas = Ensure(_canvas, w, h);
        sb.Draw(_canvas, new Rectangle(0, 0, w, h), Color.White);
    }

    /// <summary>
    /// Prolonge le dégradé dans les bandes du letterbox (backbuffer réel, sous le blit du canvas), à la
    /// même échelle entière et sur la même trame que le canvas. Au-dessus / au-dessous du canvas (écran
    /// plus haut que 16:9) : tons d'extrémité unis. <paramref name="veils"/> = voiles d'overlay à reproduire
    /// dans les bandes, dans l'ordre où le canvas les empile.
    /// </summary>
    public void DrawBands(SpriteBatch sb, Point realScreen, Point canvasOffset, int canvasScale, Point canvas,
        params Color[] veils)
    {
        if (canvasScale <= 0)
            return;

        // Colonnes à gauche du canvas, arrondies à la période de la trame pour raccorder le motif.
        var left = (canvasOffset.X + canvasScale - 1) / canvasScale;
        left = (left + DitherPeriod - 1) / DitherPeriod * DitherPeriod;
        var right = (realScreen.X - canvasOffset.X + canvasScale - 1) / canvasScale;
        _wide = Ensure(_wide, Math.Max(1, left + right), canvas.Y);

        sb.Begin(samplerState: SamplerState.PointClamp,
            transformMatrix: Matrix.CreateScale(canvasScale, canvasScale, 1f)
                * Matrix.CreateTranslation(canvasOffset.X, canvasOffset.Y, 0f));
        sb.Draw(_wide, new Vector2(-left, 0), Color.White);
        sb.End();

        // Bandes haut/bas unies + voiles, directement en pixels écran.
        sb.Begin(samplerState: SamplerState.PointClamp);
        if (canvasOffset.Y > 0)
            sb.Draw(_pixel, new Rectangle(0, 0, realScreen.X, canvasOffset.Y), Top);
        var canvasBottom = canvasOffset.Y + canvas.Y * canvasScale;
        if (canvasBottom < realScreen.Y)
            sb.Draw(_pixel, new Rectangle(0, canvasBottom, realScreen.X, realScreen.Y - canvasBottom), Bottom);
        var full = new Rectangle(0, 0, realScreen.X, realScreen.Y);
        foreach (var v in veils)
            sb.Draw(_pixel, full, v);
        sb.End();
    }

    private Texture2D Ensure(Texture2D? tex, int w, int h)
    {
        if (tex != null && tex.Width == w && tex.Height == h)
            return tex;
        tex?.Dispose();
        return Textures.CreateVerticalDitherGradient(_device, w, h, Top, Mid, Bottom);
    }

    public void Dispose()
    {
        _canvas?.Dispose();
        _wide?.Dispose();
        _canvas = _wide = null;
    }
}
