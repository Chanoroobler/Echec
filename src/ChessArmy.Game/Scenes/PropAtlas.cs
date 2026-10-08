using System;
using System.Collections.Generic;
using System.IO;
using ChessArmy.Engine.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Planche de props de décor (tous les PNG d'un dossier), rangée par BANDES horizontales (une colonne par sorte) :
/// les sprites, puis leurs silhouettes en blanc (ombres, teintées au rendu), et un pixel blanc en (0, 0). Tous les
/// props d'un plateau se dessinent ainsi d'un seul lot GPU (aucun changement de texture).
/// </summary>
internal sealed class PropAtlas : IDisposable
{
    public Texture2D? Texture { get; private set; }
    public Rectangle WhitePx { get; } = new(0, 0, 1, 1);
    public readonly List<Rectangle> Src = new();         // sprite de chaque sorte
    public readonly List<Rectangle> ShadowSrc = new();   // sa silhouette (même taille que le sprite)
    public readonly List<string> Names = new();          // nom de fichier (sans extension) : sert à classer les sortes

    public int Count => Src.Count;

    /// <summary>Charge tous les PNG de <paramref name="dir"/> (un nouveau prop = un PNG de plus) ; ignore ceux plus
    /// grands que <paramref name="maxSize"/>.</summary>
    public static PropAtlas Load(GraphicsDevice gd, string dir, int maxSize)
    {
        var result = new PropAtlas();
        if (!Directory.Exists(dir))
            return result;
        var sprites = new List<(Color[] Data, int W, int H, string Name)>();
        foreach (var file in Directory.GetFiles(dir, "*.png"))
        {
            if (Textures.LoadPngOrNull(gd, file) is not { } tex)
                continue;
            if (tex.Width <= maxSize && tex.Height <= maxSize)
            {
                var data = new Color[tex.Width * tex.Height];
                tex.GetData(data);
                sprites.Add((data, tex.Width, tex.Height, Path.GetFileNameWithoutExtension(file)));
            }
            tex.Dispose();
        }
        return FromSprites(gd, sprites);
    }

    /// <summary>Planche à partir de sprites déjà en mémoire (ex. générés par le code : flaques).</summary>
    public static PropAtlas FromSprites(GraphicsDevice gd, List<(Color[] Data, int W, int H, string Name)> sprites)
    {
        var result = new PropAtlas();
        if (sprites.Count == 0)
            return result;

        int width = 1, rowH = 0;   // colonne 0 : le pixel blanc
        foreach (var s in sprites)
        {
            width += s.W + 1;
            rowH = Math.Max(rowH, s.H);
        }
        // Bandes : sprites en haut, silhouettes juste en dessous (1 pixel d'écart).
        var shadowY = rowH + 1;
        var height = 2 * rowH + 1;
        var atlas = new Color[width * height];
        atlas[0] = Color.White;   // colonne 0 : n'appartient à aucune sorte
        var x = 1;
        foreach (var s in sprites)
        {
            for (var y = 0; y < s.H; y++)
                for (var i = 0; i < s.W; i++)
                {
                    var c = s.Data[y * s.W + i];
                    atlas[y * width + x + i] = c;
                    if (c.A > 0)
                        atlas[(shadowY + y) * width + x + i] = Color.White;
                }
            result.Src.Add(new Rectangle(x, 0, s.W, s.H));
            result.ShadowSrc.Add(new Rectangle(x, shadowY, s.W, s.H));
            result.Names.Add(s.Name);
            x += s.W + 1;
        }
        result.Texture = new Texture2D(gd, width, height);
        result.Texture.SetData(atlas);
        return result;
    }

    public bool NameStarts(int kind, string prefix) => Names[kind].StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Texture?.Dispose();
        Texture = null;
        Src.Clear();
        ShadowSrc.Clear();
        Names.Clear();
    }
}
