using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echec.MapEditor;

/// <summary>Une tuile telle que l'éditeur en a besoin : règles de jeu + image découpée du tileset.</summary>
internal sealed class TileInfo
{
    public required string Id { get; init; }
    public char Key { get; init; }
    public bool BlocksMove { get; init; }
    public bool BlocksFire { get; init; }
    /// <summary>Cellule découpée du tileset (cellW × cellH), ou null si la tuile n'a pas d'art.</summary>
    public Bitmap? Image { get; init; }
}

/// <summary>
/// Lit <c>tiles.json</c> ENTIÈREMENT (règles + infos de rendu : tilesets, col/row, variants) et
/// découpe l'image de chaque tuile depuis sa feuille. Sert la palette et le rendu de la grille.
/// Conserve le JSON brut pour re-valider les maps via <c>Echec.Core</c> à la sauvegarde.
/// </summary>
internal sealed class TileRenderCatalog
{
    public required IReadOnlyList<TileInfo> Tiles { get; init; }
    public required IReadOnlyDictionary<char, TileInfo> ByKey { get; init; }
    public int TileSize { get; init; } = 64;
    public int Thickness { get; init; } = 16;
    public required string RawJson { get; init; }

    public TileInfo? TileForKey(char key) => ByKey.TryGetValue(key, out var t) ? t : null;

    public static TileRenderCatalog Load(string tilesJsonPath, string tilesetsDir)
    {
        var raw = File.ReadAllText(tilesJsonPath);
        var dto = JsonSerializer.Deserialize<CatalogDto>(raw, JsonOpts)
                  ?? throw new FormatException("tiles.json vide ou illisible.");
        if (dto.Tiles is null || dto.Tiles.Count == 0)
            throw new FormatException("tiles.json ne contient aucune tuile.");

        // Charge chaque feuille de tileset une seule fois (copie en mémoire : pas de verrou fichier).
        var sheets = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        foreach (var (name, def) in dto.Tilesets ?? new())
        {
            var path = Path.Combine(tilesetsDir, def.File ?? "");
            if (File.Exists(path))
                sheets[name] = LoadUnlocked(path);
        }

        var tiles = new List<TileInfo>();
        var byKey = new Dictionary<char, TileInfo>();
        foreach (var t in dto.Tiles)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrEmpty(t.Key))
                continue;

            var image = CropTile(t, dto.Tilesets, sheets);
            var info = new TileInfo
            {
                Id = t.Id!,
                Key = t.Key![0],
                BlocksMove = t.BlocksMove,
                BlocksFire = t.BlocksFire,
                Image = image,
            };
            tiles.Add(info);
            byKey[info.Key] = info;
        }

        foreach (var s in sheets.Values) s.Dispose();

        return new TileRenderCatalog
        {
            Tiles = tiles,
            ByKey = byKey,
            TileSize = dto.TileSize > 0 ? dto.TileSize : 64,
            Thickness = dto.Thickness,
            RawJson = raw,
        };
    }

    private static Bitmap? CropTile(TileDto t, Dictionary<string, TilesetDto>? tilesets,
        Dictionary<string, Bitmap> sheets)
    {
        if (t.Sheet is null || tilesets is null
            || !tilesets.TryGetValue(t.Sheet, out var set)
            || !sheets.TryGetValue(t.Sheet, out var sheet))
            return null;

        // col/row directs, sinon première variante.
        int col = t.Col, row = t.Row;
        if (t.Variants is { Count: > 0 })
        {
            col = t.Variants[0].Col;
            row = t.Variants[0].Row;
        }

        int cw = set.CellW > 0 ? set.CellW : 64;
        int ch = set.CellH > 0 ? set.CellH : 80;
        var src = new Rectangle(col * cw, row * ch, cw, ch);
        if (src.Right > sheet.Width || src.Bottom > sheet.Height || src.X < 0 || src.Y < 0)
            return null; // cellule hors feuille -> placeholder

        var dst = new Bitmap(cw, ch);
        using (var g = Graphics.FromImage(dst))
        {
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(sheet, new Rectangle(0, 0, cw, ch), src, GraphicsUnit.Pixel);
        }
        return dst;
    }

    private static Bitmap LoadUnlocked(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes);
        using var tmp = new Bitmap(ms);
        return new Bitmap(tmp); // copie détachée du flux
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ---- DTO calqués sur tiles.json ----
    private sealed class CatalogDto
    {
        public int TileSize { get; set; }
        public int Thickness { get; set; }
        public Dictionary<string, TilesetDto>? Tilesets { get; set; }
        public List<TileDto>? Tiles { get; set; }
    }

    private sealed class TilesetDto
    {
        public string? File { get; set; }
        public int CellW { get; set; }
        public int CellH { get; set; }
    }

    private sealed class TileDto
    {
        public string? Id { get; set; }
        public string? Key { get; set; }
        public bool BlocksMove { get; set; }
        public bool BlocksFire { get; set; }
        public string? Sheet { get; set; }
        public int Col { get; set; }
        public int Row { get; set; }
        public List<CellDto>? Variants { get; set; }
    }

    private sealed class CellDto
    {
        public int Col { get; set; }
        public int Row { get; set; }
    }
}
