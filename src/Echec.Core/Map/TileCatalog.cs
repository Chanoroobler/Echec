using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;

namespace Echec.Core.Map;

/// <summary>
/// Catalogue de tuiles chargé depuis <c>tiles.json</c> : associe chaque identifiant à sa
/// <see cref="TileDef"/> (règles de jeu). Source de vérité pour les maps dessinées à la main.
/// Domaine pur : prend du texte JSON, aucun accès disque.
/// </summary>
public sealed class TileCatalog
{
    private readonly Dictionary<string, TileDef> _byId;
    private readonly Dictionary<string, string> _legend;

    public TileCatalog(IEnumerable<TileDef> tiles, IReadOnlyDictionary<string, string>? legend = null)
    {
        _byId = new Dictionary<string, TileDef>(StringComparer.Ordinal);
        foreach (var tile in tiles)
        {
            if (string.IsNullOrWhiteSpace(tile.Id))
                throw new ArgumentException("Une tuile du catalogue a un id vide.");
            if (!_byId.TryAdd(tile.Id, tile))
                throw new ArgumentException($"Tuile en double dans le catalogue : '{tile.Id}'.");
        }

        _legend = legend is null ? new() : new Dictionary<string, string>(legend, StringComparer.Ordinal);
    }

    public int Count => _byId.Count;
    public IReadOnlyCollection<TileDef> All => _byId.Values;

    /// <summary>Légende globale (caractère → id de tuile) : valeur par défaut pour les grilles de map.</summary>
    public IReadOnlyDictionary<string, string> Legend => _legend;

    public bool Contains(string id) => _byId.ContainsKey(id);

    public bool TryGet(string id, [MaybeNullWhen(false)] out TileDef def) =>
        _byId.TryGetValue(id, out def);

    /// <summary>Tuile par id, ou lève si inconnue (référence orpheline dans une map / le catalogue).</summary>
    public TileDef Get(string id) =>
        _byId.TryGetValue(id, out var def)
            ? def
            : throw new KeyNotFoundException($"Tuile inconnue : '{id}'. Ajoute-la à tiles.json.");

    /// <summary>Parse le contenu JSON de <c>tiles.json</c>.</summary>
    public static TileCatalog FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<CatalogDto>(json, JsonOpts)
                  ?? throw new FormatException("tiles.json vide ou illisible.");
        if (dto.Tiles is null || dto.Tiles.Count == 0)
            throw new FormatException("tiles.json ne contient aucune tuile.");

        var tiles = dto.Tiles.Select(t => new TileDef(t.Id ?? "", t.BlocksMove, t.BlocksFire));

        // Légende globale : caractère 'key' → id (ignore les tuiles sans clé).
        var legend = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in dto.Tiles)
            if (!string.IsNullOrEmpty(t.Key) && !string.IsNullOrEmpty(t.Id))
                legend[t.Key] = t.Id!;

        return new TileCatalog(tiles, legend);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class CatalogDto
    {
        public List<TileDto>? Tiles { get; set; }
    }

    private sealed class TileDto
    {
        public string? Id { get; set; }
        public string? Key { get; set; }
        public bool BlocksMove { get; set; }
        public bool BlocksFire { get; set; }
    }
}
