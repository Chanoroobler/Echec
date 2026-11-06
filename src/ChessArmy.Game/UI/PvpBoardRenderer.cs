using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;
using ChessArmy.Engine;
using ChessArmy.Engine.UI;
using ChessArmy.Engine.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.UI;

/// <summary>
/// Le plateau multi 8×8 de l'écran Armée, rendu comme en combat : tuiles de la map Multi par défaut
/// (<c>multi_8_01</c>, à défaut la première map Multi jouable), cellules 64×80 des feuilles de <c>tiles.json</c>
/// (64 de dessus + 16 d'épaisseur), dessinées de l'arrière vers l'avant pour que l'épaisseur se recouvre.
/// Les rectangles source sont résolus une fois au chargement : le dessin n'alloue rien.
/// </summary>
public sealed class PvpBoardRenderer : IDisposable
{
    public const int Tile = 64;
    public const int Thickness = 16;
    public const int Size = PvpSetup.Size;
    public const string DefaultMap = "multi_8_01";

    private readonly List<Texture2D> _owned = new();
    private readonly Texture2D?[] _tex = new Texture2D?[Size * Size];
    private readonly Rectangle[] _src = new Rectangle[Size * Size];
    private readonly bool[] _bush = new bool[Size * Size];
    private readonly Texture2D? _bushSprite;

    public PvpBoardRenderer(GameContext ctx)
    {
        var root = AppContext.BaseDirectory;
        var device = ctx.GraphicsDevice;
        try
        {
            var tilesJson = File.ReadAllText(Path.Combine(root, "Assets/Tiles/tiles.json"));
            var catalog = TileCatalog.FromJson(tilesJson);
            var maps = LoadMultiMaps(catalog);
            var map = maps.FirstOrDefault(m => m.Name == DefaultMap)
                      ?? maps.Where(m => !m.IsDraft && PvpSetup.Problem(m) is null)
                          .OrderBy(m => m.Name, StringComparer.Ordinal).FirstOrDefault();
            if (map == null)
                return;
            var field = PvpSetup.BuildBattlefield(map, isHost: true, out var bushes);
            foreach (var b in bushes)
                if (b.Column is >= 0 and < Size && b.Row is >= 0 and < Size)
                    _bush[b.Row * Size + b.Column] = true;

            var sheets = ReadSheets(tilesJson, device, root, out var cells);
            var single = new Dictionary<string, Texture2D>();
            foreach (var cell in field.Cells())
            {
                var id = field[cell].Id;
                var i = cell.Row * Size + cell.Column;
                if (cells.TryGetValue(id, out var where) && sheets.TryGetValue(where.Sheet, out var sheet))
                {
                    _tex[i] = sheet;
                    _src[i] = where.Variants[where.Variants.Count == 1 ? 0 : VariantIndex(id, cell, where.Variants.Count)];
                }
                else
                {
                    if (!single.TryGetValue(id, out var png))
                    {
                        png = Textures.LoadTileOrPlaceholder(device, Path.Combine(root, $"Assets/Tiles/{id}.png"));
                        single[id] = png;
                        _owned.Add(png);
                    }
                    _tex[i] = png;
                    _src[i] = new Rectangle(0, 0, png.Width, png.Height);
                }
            }
            if (_bush.Any(b => b))
            {
                _bushSprite = Textures.LoadPngOrNull(device, Path.Combine(root, "Assets/Objects/buisson.png"));
                if (_bushSprite != null)
                    _owned.Add(_bushSprite);
            }
        }
        catch (Exception e) when (e is IOException or FormatException or JsonException or KeyNotFoundException)
        {
            // Pas de map lisible : plateau d'aplats (cf. Draw).
        }
    }

    public void Dispose()
    {
        foreach (var t in _owned)
            t.Dispose();
        _owned.Clear();
    }

    /// <summary>
    /// Tuiles des rangées <paramref name="firstRow"/>..7, la rangée <paramref name="firstRow"/> en haut à
    /// <paramref name="origin"/>. L'épaisseur de la dernière rangée dépasse de <see cref="Thickness"/> px dessous.
    /// </summary>
    public void Draw(SpriteBatch sb, Texture2D pixel, Point origin, int firstRow)
    {
        for (var row = firstRow; row < Size; row++)
            for (var col = 0; col < Size; col++)
            {
                var i = row * Size + col;
                var at = new Rectangle(origin.X + col * Tile, origin.Y + (row - firstRow) * Tile, Tile, Tile + Thickness);
                if (_tex[i] is { } tex)
                    sb.Draw(tex, new Rectangle(at.X, at.Y, _src[i].Width, _src[i].Height), _src[i], Color.White);
                else
                {
                    sb.Draw(pixel, new Rectangle(at.X, at.Y, Tile, Tile), (col + row) % 2 == 0 ? Palette.Yellow3 : Palette.Yellow4);
                    sb.Draw(pixel, new Rectangle(at.X, at.Y + Tile, Tile, Thickness), Palette.Green2);
                }
            }
    }

    /// <summary>Buissons de la map (couvert), par-dessus les pions de leur rangée : à appeler après eux.</summary>
    public void DrawBushes(SpriteBatch sb, Point origin, int firstRow)
    {
        if (_bushSprite == null)
            return;
        for (var row = firstRow; row < Size; row++)
            for (var col = 0; col < Size; col++)
                if (_bush[row * Size + col])
                    sb.Draw(_bushSprite, new Vector2(origin.X + col * Tile + (Tile - _bushSprite.Width) / 2,
                        origin.Y + (row - firstRow) * Tile + Tile - _bushSprite.Height), Color.White);
    }

    /// <summary>Maps Multi livrées (brouillons exclus), lues comme le combat les lit.</summary>
    public static List<MapData> LoadMultiMaps(TileCatalog catalog)
    {
        var maps = new List<MapData>();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Assets/Maps"), "*.json"))
        {
            try
            {
                var map = MapLoader.Parse(File.ReadAllText(file), catalog);
                if (map.Type == CombatType.Multi && !map.IsDraft)
                    maps.Add(map);
            }
            catch (FormatException) { }
            catch (KeyNotFoundException) { }
        }
        return maps;
    }

    // ── tiles.json : feuilles et cellules ────────────────────────────────────────

    private sealed record CellRef(string Sheet, List<Rectangle> Variants);

    private Dictionary<string, Texture2D> ReadSheets(string tilesJson, GraphicsDevice device, string root,
        out Dictionary<string, CellRef> cells)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var doc = JsonSerializer.Deserialize<TilesDto>(tilesJson, opts);
        var sheets = new Dictionary<string, Texture2D>();
        cells = new Dictionary<string, CellRef>();
        if (doc?.Tilesets is null || doc.Tiles is null)
            return sheets;
        var used = new HashSet<string>();
        foreach (var t in doc.Tiles)
        {
            if (t.Id is null || t.Sheet is null || !doc.Tilesets.TryGetValue(t.Sheet, out var sheet))
                continue;
            var refs = t.Variants is { Count: > 0 } ? t.Variants : new List<CellDto> { new() { Col = t.Col, Row = t.Row } };
            cells[t.Id] = new CellRef(t.Sheet, refs.Select(c => new Rectangle(c.Col * sheet.CellW, c.Row * sheet.CellH, sheet.CellW, sheet.CellH)).ToList());
            used.Add(t.Sheet);
        }
        foreach (var name in used)
            if (doc.Tilesets[name].File is { } file
                && Textures.LoadPngOrNull(device, Path.Combine(root, $"Assets/Tilesets/{file}")) is { } tex)
            {
                sheets[name] = tex;
                _owned.Add(tex);
            }
        return sheets;
    }

    /// <summary>Même tirage stable de variante que le combat (FNV-1a id + colonne + rangée).</summary>
    private static int VariantIndex(string id, Cell cell, int count)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (var c in id) { h ^= c; h *= 16777619u; }
            h ^= (uint)cell.Column; h *= 16777619u;
            h ^= (uint)cell.Row; h *= 16777619u;
            return (int)(h % (uint)count);
        }
    }

    private sealed class TilesDto
    {
        public Dictionary<string, SheetDto>? Tilesets { get; set; }
        public List<TileDto>? Tiles { get; set; }
    }

    private sealed class SheetDto
    {
        public string? File { get; set; }
        public int CellW { get; set; }
        public int CellH { get; set; }
    }

    private sealed class TileDto
    {
        public string? Id { get; set; }
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
