using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Echec.Core.Map;

/// <summary>
/// Charge une map depuis son JSON (deux grilles ASCII alignées : <c>tiles</c> + <c>spawns</c>) en
/// s'appuyant sur un <see cref="TileCatalog"/> pour résoudre chaque caractère de légende en
/// <see cref="TileDef"/>. Domaine pur : prend du texte, rend une <see cref="MapData"/> ; aucun
/// accès disque. Lève <see cref="FormatException"/> sur une map mal formée.
/// </summary>
public static class MapLoader
{
    public static MapData Parse(string json, TileCatalog catalog)
    {
        var dto = JsonSerializer.Deserialize<MapDto>(json, JsonOpts)
                  ?? throw new FormatException("Map vide ou illisible.");

        var type = ParseType(dto.Type);
        var width = dto.Width;
        var height = dto.Height;
        if (width <= 0 || height <= 0)
            throw new FormatException($"Dimensions de map invalides : {width}x{height}.");
        // Légende : celle de la map si elle en déclare une, sinon la légende globale de tiles.json.
        IReadOnlyDictionary<string, string> legend =
            dto.Legend is { Count: > 0 } ? dto.Legend : catalog.Legend;
        if (legend.Count == 0)
            throw new FormatException("Map sans légende (ni dans la map, ni de clés globales dans tiles.json).");
        if (dto.Tiles is null)
            throw new FormatException("Map sans grille 'tiles'.");

        RequireGrid(dto.Tiles, width, height, "tiles");

        var tiles = new TileDef[width, height];
        for (var row = 0; row < height; row++)
        {
            var line = dto.Tiles[row];
            for (var col = 0; col < width; col++)
            {
                var ch = line[col].ToString();
                if (!legend.TryGetValue(ch, out var id))
                    throw new FormatException($"Caractère '{ch}' (tuiles, ligne {row}) absent de la légende.");
                tiles[col, row] = catalog.Get(id);   // lève si l'id n'est pas au catalogue
            }
        }

        var player = new List<Cell>();
        var enemy = new List<Cell>();
        var boss = new List<Cell>();
        if (dto.Spawns is not null)
        {
            RequireGrid(dto.Spawns, width, height, "spawns");
            for (var row = 0; row < height; row++)
            {
                var line = dto.Spawns[row];
                for (var col = 0; col < width; col++)
                {
                    var cell = new Cell(col, row);
                    switch (line[col])
                    {
                        case 'P': player.Add(cell); break;
                        case 'E': enemy.Add(cell); break;
                        case 'B': boss.Add(cell); break;
                        case '.':
                        case ' ': break;
                        default:
                            throw new FormatException(
                                $"Caractère de spawn inconnu '{line[col]}' (ligne {row}). Attendu P, E, B ou '.'.");
                    }
                }
            }
        }

        var objects = new List<MapObject>();
        if (dto.Objects is not null)
        {
            RequireGrid(dto.Objects, width, height, "objects");
            for (var row = 0; row < height; row++)
            {
                var line = dto.Objects[row];
                for (var col = 0; col < width; col++)
                {
                    var cell = new Cell(col, row);
                    switch (line[col])
                    {
                        case 'C': objects.Add(new MapObject(cell, MapObjectKind.ChestCommon)); break;
                        case 'K': objects.Add(new MapObject(cell, MapObjectKind.ChestRare)); break;
                        case 'k': objects.Add(new MapObject(cell, MapObjectKind.Key)); break;
                        case 'R': objects.Add(new MapObject(cell, MapObjectKind.Recruit)); break;
                        case 'B': objects.Add(new MapObject(cell, MapObjectKind.Bush)); break;
                        case '.':
                        case ' ': break;
                        default:
                            throw new FormatException(
                                $"Caractère d'objet inconnu '{line[col]}' (ligne {row}). Attendu C, K, k, R, B ou '.'.");
                    }
                }
            }
        }

        return new MapData(dto.Name ?? "", type, width, height, tiles, player, enemy, boss, objects);
    }

    private static void RequireGrid(IReadOnlyList<string> rows, int width, int height, string label)
    {
        if (rows.Count != height)
            throw new FormatException($"La grille '{label}' a {rows.Count} lignes, attendu {height}.");
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Length != width)
                throw new FormatException(
                    $"La grille '{label}', ligne {i}, fait {rows[i].Length} colonnes, attendu {width}.");
    }

    private static CombatType ParseType(string? type) =>
        Enum.TryParse<CombatType>(type, ignoreCase: true, out var t)
            ? t
            : throw new FormatException($"Type de combat inconnu : '{type}'. Attendu Escarmouche ou Boss.");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class MapDto
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public Dictionary<string, string>? Legend { get; set; }
        public List<string>? Tiles { get; set; }
        public List<string>? Spawns { get; set; }
        public List<string>? Objects { get; set; }
    }
}
