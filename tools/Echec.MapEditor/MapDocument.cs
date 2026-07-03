using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echec.MapEditor;

/// <summary>
/// Map éditable en mémoire : trois calques de caractères (terrain / spawns / objets) + métadonnées.
/// Charge et sérialise EXACTEMENT le format des <c>escarmouche_*.json</c> du jeu
/// (name/type/width/height + grilles ASCII). Indexation <c>[row, col]</c>.
/// </summary>
internal sealed class MapDocument
{
    public const char EmptySpawn = '.';
    public const char EmptyObject = '.';

    public string Name { get; set; } = "nouvelle_map";
    public string Type { get; set; } = "Escarmouche";
    public int Width { get; private set; }
    public int Height { get; private set; }

    public char[,] Tiles { get; private set; }
    public char[,] Spawns { get; private set; }
    public char[,] Objects { get; private set; }

    public string? FilePath { get; set; }

    private MapDocument(int width, int height)
    {
        Width = width;
        Height = height;
        Tiles = new char[height, width];
        Spawns = new char[height, width];
        Objects = new char[height, width];
    }

    public static MapDocument NewMap(int width, int height, char defaultTileKey)
    {
        var doc = new MapDocument(width, height);
        for (var r = 0; r < height; r++)
            for (var c = 0; c < width; c++)
            {
                doc.Tiles[r, c] = defaultTileKey;
                doc.Spawns[r, c] = EmptySpawn;
                doc.Objects[r, c] = EmptyObject;
            }
        return doc;
    }

    public static MapDocument Load(string path)
    {
        var dto = JsonSerializer.Deserialize<MapDto>(File.ReadAllText(path), JsonOpts)
                  ?? throw new FormatException("Map vide ou illisible.");
        if (dto.Width <= 0 || dto.Height <= 0)
            throw new FormatException($"Dimensions invalides : {dto.Width}x{dto.Height}.");
        if (dto.Tiles is null || dto.Tiles.Count != dto.Height)
            throw new FormatException("Grille 'tiles' absente ou de mauvaise hauteur.");

        var doc = new MapDocument(dto.Width, dto.Height)
        {
            Name = dto.Name ?? Path.GetFileNameWithoutExtension(path),
            Type = string.IsNullOrWhiteSpace(dto.Type) ? "Escarmouche" : dto.Type!,
            FilePath = path,
        };

        Fill(doc.Tiles, dto.Tiles, dto.Width, dto.Height, ' ');
        Fill(doc.Spawns, dto.Spawns, dto.Width, dto.Height, EmptySpawn);
        Fill(doc.Objects, dto.Objects, dto.Width, dto.Height, EmptyObject);
        return doc;
    }

    private static void Fill(char[,] grid, List<string>? rows, int width, int height, char pad)
    {
        for (var r = 0; r < height; r++)
        {
            var line = rows is not null && r < rows.Count ? rows[r] : "";
            for (var c = 0; c < width; c++)
                grid[r, c] = c < line.Length ? line[c] : pad;
        }
    }

    /// <summary>Redimensionne en conservant le coin haut-gauche ; remplit le neuf avec les valeurs par défaut.</summary>
    public void Resize(int newWidth, int newHeight, char defaultTileKey)
    {
        var t = new char[newHeight, newWidth];
        var s = new char[newHeight, newWidth];
        var o = new char[newHeight, newWidth];
        for (var r = 0; r < newHeight; r++)
            for (var c = 0; c < newWidth; c++)
            {
                var inside = r < Height && c < Width;
                t[r, c] = inside ? Tiles[r, c] : defaultTileKey;
                s[r, c] = inside ? Spawns[r, c] : EmptySpawn;
                o[r, c] = inside ? Objects[r, c] : EmptyObject;
            }
        Tiles = t; Spawns = s; Objects = o;
        Width = newWidth; Height = newHeight;
    }

    public string ToJson()
    {
        var dto = new MapDto
        {
            Name = Name,
            Type = Type,
            Width = Width,
            Height = Height,
            Tiles = ToRows(Tiles),
            Spawns = ToRows(Spawns),
            Objects = ToRows(Objects),
        };
        return JsonSerializer.Serialize(dto, WriteOpts);
    }

    private List<string> ToRows(char[,] grid)
    {
        var rows = new List<string>(Height);
        var buf = new char[Width];
        for (var r = 0; r < Height; r++)
        {
            for (var c = 0; c < Width; c++)
                buf[c] = grid[r, c];
            rows.Add(new string(buf));
        }
        return rows;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class MapDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("tiles")] public List<string>? Tiles { get; set; }
        [JsonPropertyName("spawns")] public List<string>? Spawns { get; set; }
        [JsonPropertyName("objects")] public List<string>? Objects { get; set; }
    }
}
