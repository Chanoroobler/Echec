using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;

namespace ChessArmy.Core.Map;

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

        var tiles = dto.Tiles.Select(t => new TileDef(t.Id ?? "", t.BlocksMove, t.BlocksFire, t.Glisse, t.RangeBonus,
            t.OccupantDx, t.OccupantDy, string.IsNullOrWhiteSpace(t.Trait) ? null : t.Trait));

        // Légende globale : caractère 'key' → id (ignore les tuiles sans clé).
        var legend = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in dto.Tiles)
            if (!string.IsNullOrEmpty(t.Key) && !string.IsNullOrEmpty(t.Id))
                legend[t.Key] = t.Id!;

        var catalog = new TileCatalog(tiles, legend);
        foreach (var t in dto.Tiles)
            if (!string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.Miroir))
                catalog._mirror[t.Id!] = t.Miroir!;
        return catalog;
    }

    private readonly Dictionary<string, string> _mirror = new(StringComparer.Ordinal);

    /// <summary>
    /// Tuile JUMELLE de <paramref name="def"/> sur un plateau retourné haut/bas (<c>miroir</c> dans tiles.json) :
    /// mêmes raccords une fois le nord et le sud échangés (coin haut-gauche ↔ coin bas-gauche, berge rocheuse du
    /// haut ↔ berge du bas…), dessinée dans la même perspective. Sert à l'invité d'une partie PvP, dont le plateau
    /// est retourné : sa map reste cohérente à l'œil. Une jumelle aux RÈGLES différentes est ignorée (la case
    /// doit garder les siennes) ; sans jumelle, la tuile elle-même (herbe, rocher, tuile symétrique).
    /// </summary>
    public TileDef MirrorOf(TileDef def) =>
        _mirror.TryGetValue(def.Id, out var id) && _byId.TryGetValue(id, out var twin)
        && twin.BlocksMove == def.BlocksMove && twin.BlocksFire == def.BlocksFire && twin.Slides == def.Slides
        && twin.RangeBonus == def.RangeBonus && twin.Trait == def.Trait
            ? twin
            : def;

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
        public bool Glisse { get; set; }

        /// <summary>Portée d'attaque gagnée par le TIREUR posté dessus (tour de guet) ; absent → 0.</summary>
        public int RangeBonus { get; set; }

        /// <summary>Décalage cosmétique du pion posté dessus (px de tuile native) ; absent → 0 (centré).</summary>
        public int OccupantDx { get; set; }
        public int OccupantDy { get; set; }

        /// <summary>Trait de combat prêté à l'unité postée dessus (cf. Battle.Trait) ; absent → aucun.</summary>
        public string? Trait { get; set; }

        /// <summary>Id de la tuile jumelle sur un plateau retourné haut/bas (cf. <see cref="MirrorOf"/>) ; absent → elle-même.</summary>
        public string? Miroir { get; set; }
    }
}
