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
    public const char EmptyTier = '.';

    public string Name { get; set; } = "nouvelle_map";
    public string Type { get; set; } = "Escarmouche";

    /// <summary>Sous-type d'objectif pour une map <c>Speciale</c> (sinon <c>Aucun</c>). Cf. Core SpecialObjective.</summary>
    public string Objective { get; set; } = "Aucun";

    /// <summary>Phase réservée (0 = toutes, 1..3). Sert au tirage des maps spéciales par phase.</summary>
    public int Phase { get; set; }

    /// <summary>Limite de tours d'une mission spéciale (0 = valeur par défaut du jeu).</summary>
    public int TurnLimit { get; set; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    public char[,] Tiles { get; private set; }
    public char[,] Spawns { get; private set; }
    public char[,] Objects { get; private set; }

    /// <summary>Tier (1..3) par case de spawn ENNEMI (E/D/O) ; <see cref="EmptyTier"/> ailleurs. Calque « tiers »
    /// de la map — fixe la composition des vagues spéciale/boss (cf. Core MapData.EnemyTiers).</summary>
    public char[,] Tiers { get; private set; }

    public string? FilePath { get; set; }

    private MapDocument(int width, int height)
    {
        Width = width;
        Height = height;
        Tiles = new char[height, width];
        Spawns = new char[height, width];
        Objects = new char[height, width];
        Tiers = new char[height, width];
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
                doc.Tiers[r, c] = EmptyTier;
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
            Objective = string.IsNullOrWhiteSpace(dto.Objective) ? "Aucun" : dto.Objective!,
            Phase = dto.Phase ?? 0,
            TurnLimit = dto.TurnLimit ?? 0,
            FilePath = path,
        };

        Fill(doc.Tiles, dto.Tiles, dto.Width, dto.Height, ' ');
        Fill(doc.Spawns, dto.Spawns, dto.Width, dto.Height, EmptySpawn);
        Fill(doc.Objects, dto.Objects, dto.Width, dto.Height, EmptyObject);
        Fill(doc.Tiers, dto.Tiers, dto.Width, dto.Height, EmptyTier);
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
        var ti = new char[newHeight, newWidth];
        for (var r = 0; r < newHeight; r++)
            for (var c = 0; c < newWidth; c++)
            {
                var inside = r < Height && c < Width;
                t[r, c] = inside ? Tiles[r, c] : defaultTileKey;
                s[r, c] = inside ? Spawns[r, c] : EmptySpawn;
                o[r, c] = inside ? Objects[r, c] : EmptyObject;
                ti[r, c] = inside ? Tiers[r, c] : EmptyTier;
            }
        Tiles = t; Spawns = s; Objects = o; Tiers = ti;
        Width = newWidth; Height = newHeight;
    }

    public string ToJson()
    {
        var dto = new MapDto
        {
            Name = Name,
            Type = Type,
            // On n'écrit le sous-type que s'il est réellement défini (évite un "objective":"Aucun" partout).
            Objective = string.IsNullOrWhiteSpace(Objective) || Objective == "Aucun" ? null : Objective,
            Phase = Phase == 0 ? null : Phase,   // 0 = toutes phases → champ omis
            TurnLimit = TurnLimit == 0 ? null : TurnLimit,   // 0 = défaut du jeu → champ omis
            Width = Width,
            Height = Height,
            Tiles = ToRows(Tiles),
            Spawns = ToRows(Spawns),
            Tiers = ToRows(Tiers),
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,   // n'écrit pas les champs null (ex. objective)
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class MapDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("objective")] public string? Objective { get; set; }
        [JsonPropertyName("phase")] public int? Phase { get; set; }
        [JsonPropertyName("turnLimit")] public int? TurnLimit { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("tiles")] public List<string>? Tiles { get; set; }
        [JsonPropertyName("spawns")] public List<string>? Spawns { get; set; }
        [JsonPropertyName("tiers")] public List<string>? Tiers { get; set; }
        [JsonPropertyName("objects")] public List<string>? Objects { get; set; }
    }
}
