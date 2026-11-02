using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.UI;

/// <summary>
/// Sprites posés à la résolution de l'ÉCRAN, après l'agrandissement du canvas (cf. ChessArmyGame.Draw), quelle que
/// soit la scène. Sert au pion des plateaux de carte (<see cref="MoveAttackGrid.DrawBoard"/>) : réduit de moitié
/// dans le canvas, il occupe 64 px d'écran à l'échelle ×2 (1080p, 1440p plein écran), donc chaque pixel du sprite
/// reste un vrai pixel, comme au dézoom, au lieu d'en perdre un sur deux. Passe AU-DESSUS de tout le canvas :
/// n'y mettre que ce qui est déjà au premier plan. File fixe, aucune allocation par frame.
/// </summary>
public static class NativeSprites
{
    private struct Entry { public Texture2D Tex; public Rectangle Canvas; public Color Tint; }
    private static readonly Entry[] Entries = new Entry[8];
    private static int _count;

    /// <summary>Échelle canvas → écran de la frame en cours (fixée par la couche Game avant le dessin des scènes).</summary>
    public static int Scale { get; set; } = 1;

    /// <summary>Vrai si un sprite peut passer par la couche écran : échelle ≥ 2 (sinon aucun pixel à gagner) et
    /// place dans la file. Sinon, l'appelant dessine son repli dans le canvas.</summary>
    public static bool CanQueue => Scale >= 2 && _count < Entries.Length;

    public static bool HasAny => _count > 0;

    /// <summary>Ajoute un sprite dont l'emplacement CANVAS est <paramref name="canvas"/> (à moitié de sa taille).</summary>
    public static void Queue(Texture2D tex, Rectangle canvas, Color tint)
    {
        if (_count < Entries.Length)
            Entries[_count++] = new Entry { Tex = tex, Canvas = canvas, Tint = tint };
    }

    public static void Clear() => _count = 0;

    /// <summary>
    /// Dessine la file sur le backbuffer (aucun batch actif) puis la vide. 1 pixel du sprite = scale/2 pixels
    /// d'écran (entier) : à ×2 il occupe pile son emplacement canvas, à ×3 il est un peu plus petit mais net.
    /// Ancré par le centre du socle (<see cref="MoveAttackGrid.SocleCenterFraction"/>).
    /// </summary>
    public static void Flush(SpriteBatch sb, Rectangle virtualDest, int scale)
    {
        if (_count == 0)
            return;
        var per = System.Math.Max(1, scale / 2);
        sb.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
        for (var i = 0; i < _count; i++)
        {
            ref readonly var e = ref Entries[i];
            var w = e.Tex.Width * per;
            var h = e.Tex.Height * per;
            var x = virtualDest.X + e.Canvas.X * scale + (e.Canvas.Width * scale - w) / 2;
            var y = virtualDest.Y + (int)((e.Canvas.Y + e.Canvas.Height * MoveAttackGrid.SocleCenterFraction) * scale)
                    - (int)(h * MoveAttackGrid.SocleCenterFraction);
            sb.Draw(e.Tex, new Rectangle(x, y, w, h), e.Tint);
        }
        sb.End();
        _count = 0;
    }
}
