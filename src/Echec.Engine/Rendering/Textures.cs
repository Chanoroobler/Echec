using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Engine.Rendering;

/// <summary>
/// Utilitaires de texture : chargement depuis un PNG sur disque avec repli
/// sur une tuile placeholder générée, afin que le jeu tourne même sans assets.
/// </summary>
public static class Textures
{
    private static readonly int[,] Bayer4 =
    {
        {  0,  8,  2, 10 },
        { 12,  4, 14,  6 },
        {  3, 11,  1,  9 },
        { 15,  7, 13,  5 },
    };

    /// <summary>Texture 1×1 blanche, base de tout rendu de rectangles / police pixel.</summary>
    public static Texture2D CreatePixel(GraphicsDevice graphicsDevice)
    {
        var pixel = new Texture2D(graphicsDevice, 1, 1);
        pixel.SetData([Color.White]);
        return pixel;
    }

    /// <summary>
    /// Petite tuile tramée 2 tons (dithering ordonné Bayer à 50 %), tuilable dans les
    /// deux sens. Sert de fond texturé aux panneaux et boutons d'UI. (Portée de CosyFarmer.)
    /// </summary>
    public static Texture2D CreateDitherTile(GraphicsDevice graphicsDevice, int size, Color a, Color b)
    {
        var data = new Color[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                data[y * size + x] = Bayer4[y & 3, x & 3] < 8 ? a : b;

        var texture = new Texture2D(graphicsDevice, size, size);
        texture.SetData(data);
        return texture;
    }

    /// <summary>Charge un PNG via <c>Texture2D.FromFile</c>, ou renvoie un placeholder si absent/illisible.</summary>
    public static Texture2D LoadTileOrPlaceholder(GraphicsDevice graphicsDevice, string path,
        int surfaceSize = GridLayout.DefaultTileSize, int thickness = 10)
    {
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                return Texture2D.FromStream(graphicsDevice, stream);
            }
            catch
            {
                // PNG illisible : on retombe sur le placeholder.
            }
        }

        return CreateTilePlaceholder(graphicsDevice, surfaceSize, thickness);
    }

    /// <summary>
    /// Génère une tuile 64×(64+épaisseur) : surface verte + bande d'épaisseur plus
    /// sombre + liseré, pour visualiser la grille avant d'avoir les vrais sprites.
    /// </summary>
    public static Texture2D CreateTilePlaceholder(GraphicsDevice graphicsDevice,
        int surfaceSize = GridLayout.DefaultTileSize, int thickness = 10)
    {
        var width = surfaceSize;
        var height = surfaceSize + thickness;

        var surface = new Color(106, 170, 100);   // vert herbe
        var surfaceEdge = new Color(86, 145, 82);
        var side = new Color(74, 108, 58);         // épaisseur plus sombre
        var border = new Color(60, 90, 50);

        var data = new Color[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Color color;
                if (y < surfaceSize)
                {
                    var onBorder = x == 0 || x == width - 1 || y == 0;
                    color = onBorder ? surfaceEdge : surface;
                }
                else
                {
                    var onBorder = x == 0 || x == width - 1 || y == height - 1;
                    color = onBorder ? border : side;
                }

                data[y * width + x] = color;
            }
        }

        var texture = new Texture2D(graphicsDevice, width, height);
        texture.SetData(data);
        return texture;
    }
}
