using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ChessArmy.MapEditor;

/// <summary>Une tuile telle que l'éditeur en a besoin : règles de jeu + image découpée du tileset.</summary>
internal sealed class TileInfo
{
    public required string Id { get; init; }
    /// <summary>Clé de légende : 1 OU 2 caractères (une clé de 2 commence par un caractère préfixe, cf. <see cref="TileRenderCatalog.KeyLeads"/>).</summary>
    public required string Key { get; init; }
    // Modifiables via l'inspecteur (persistés dans tiles.json par TileRenderCatalog.SetTileRules).
    public bool BlocksMove { get; set; }
    public bool BlocksFire { get; set; }
    /// <summary>Tuile GLISSANTE (glace) : un pion qui s'y arrête glisse d'une case (cf. jeu).</summary>
    public bool Slides { get; set; }
    /// <summary>ONGLET de palette de la tuile (Terrain). Dérivé du tileset via <see cref="TileRenderCatalog.TabFor"/>
    /// (nom du tileset sans son chiffre final) : ainsi les tilesets d'une même famille — <c>eaux</c>, <c>eaux2</c>,
    /// <c>eaux3</c> — sont REGROUPÉS sous un seul onglet <c>eaux</c>. N'affecte QUE le regroupement de la palette :
    /// l'image, elle, est déjà découpée depuis le vrai tileset au chargement (cf. <see cref="TileRenderCatalog.CropTile"/>).
    /// Null si la tuile n'a pas de tileset.</summary>
    public string? Sheet { get; init; }
    /// <summary>Cellule découpée du tileset (cellW × cellH), ou null si la tuile n'a pas d'art.</summary>
    public Bitmap? Image { get; init; }
}

/// <summary>
/// Lit <c>tiles.json</c> ENTIÈREMENT (règles + infos de rendu : tilesets, col/row, variants) et
/// découpe l'image de chaque tuile depuis sa feuille. Sert la palette et le rendu de la grille.
/// Conserve le JSON brut pour re-valider les maps via <c>ChessArmy.Core</c> à la sauvegarde.
/// </summary>
internal sealed class TileRenderCatalog
{
    public IReadOnlyList<TileInfo> Tiles { get; private set; } = Array.Empty<TileInfo>();
    public required IReadOnlyDictionary<string, TileInfo> ByKey { get; init; }
    /// <summary>Caractères « préfixes » : premier caractère de chaque clé de longueur ≥ 2. En voyant un tel
    /// caractère dans une grille de tuiles, le parseur lit 2 caractères. Vide si aucune clé 2-car.</summary>
    public required IReadOnlySet<char> KeyLeads { get; init; }
    public int TileSize { get; init; } = 64;
    public int Thickness { get; init; } = 16;
    public string RawJson { get; private set; } = "";
    /// <summary>Chemin du tiles.json chargé, pour réécrire au même endroit (cf. <see cref="SetBlocking"/>).</summary>
    public string SourcePath { get; private set; } = "";

    public TileInfo? TileForKey(string key) => ByKey.TryGetValue(key, out var t) ? t : null;

    /// <summary>
    /// Met à jour <c>blocksMove</c>/<c>blocksFire</c>/<c>glisse</c> d'une tuile en mémoire ET dans tiles.json,
    /// en préservant le reste du fichier (tilesets, variants, commentaires, ordre des tuiles). Écrit
    /// immédiatement sur le disque : c'est le fichier lu par le jeu et par l'éditeur.
    /// </summary>
    public void SetTileRules(string id, bool blocksMove, bool blocksFire, bool slides)
    {
        foreach (var t in Tiles)
            if (t.Id == id) { t.BlocksMove = blocksMove; t.BlocksFire = blocksFire; t.Slides = slides; }

        var root = JsonNode.Parse(RawJson, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new FormatException("tiles.json illisible.");

        if (root["tiles"] is JsonArray arr)
            foreach (var node in arr)
                if (node is JsonObject obj && obj["id"]?.GetValue<string>() == id)
                {
                    obj["blocksMove"] = blocksMove;
                    obj["blocksFire"] = blocksFire;
                    obj["glisse"] = slides;
                }

        RawJson = root.ToJsonString(WriteOpts);
        File.WriteAllText(SourcePath, RawJson);
    }

    /// <summary>
    /// Déplace la tuile <paramref name="movedKey"/> juste AVANT la tuile <paramref name="targetKey"/> dans la
    /// palette : réordonne la liste en mémoire ET le tableau <c>tiles</c> de tiles.json (le reste du fichier —
    /// tilesets, règles, variants, feuilles — est préservé). L'ordre du tableau n'a aucun effet sur le jeu
    /// (qui indexe par id/clé) ; il ne sert qu'à ranger la palette de l'éditeur. Sans effet si une clé est
    /// inconnue ou si la cible est la tuile déplacée.
    /// </summary>
    public void MoveTile(string movedKey, string targetKey)
    {
        if (movedKey == targetKey)
            return;

        var list = Tiles.ToList();
        var from = list.FindIndex(t => t.Key == movedKey);
        var to = list.FindIndex(t => t.Key == targetKey);
        if (from < 0 || to < 0)
            return;

        var moved = list[from];
        list.RemoveAt(from);
        if (from < to) to--;   // la suppression a décalé les indices situés après la source
        list.Insert(to, moved);
        Tiles = list;

        PersistOrder(list);
    }

    /// <summary>Réécrit le tableau <c>tiles</c> de tiles.json dans l'ordre de <paramref name="ordered"/> (par id),
    /// en clonant les nœuds pour préserver chaque tuile telle quelle (commentaires et champs inclus).</summary>
    private void PersistOrder(IReadOnlyList<TileInfo> ordered)
    {
        var root = JsonNode.Parse(RawJson, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new FormatException("tiles.json illisible.");

        if (root["tiles"] is not JsonArray oldArr)
            return;

        // Indexe les nœuds par id (clonés : évite les conflits de parent en les replaçant dans un nouveau tableau).
        var byId = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var extras = new List<JsonNode>();   // nœud sans id (ne devrait pas exister) : préservé en fin
        foreach (var node in oldArr)
        {
            if (node is JsonObject obj && obj["id"]?.GetValue<string>() is { } id)
                byId[id] = node.DeepClone();
            else if (node is not null)
                extras.Add(node.DeepClone());
        }

        var newArr = new JsonArray();
        foreach (var t in ordered)
            if (byId.Remove(t.Id, out var node))
                newArr.Add(node);
        foreach (var leftover in byId.Values)   // id absent de la liste (catalogue désynchronisé) : conservé
            newArr.Add(leftover);
        foreach (var extra in extras)
            newArr.Add(extra);

        root["tiles"] = newArr;
        RawJson = root.ToJsonString(WriteOpts);
        File.WriteAllText(SourcePath, RawJson);
    }

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
        var byKey = new Dictionary<string, TileInfo>(StringComparer.Ordinal);
        var leads = new HashSet<char>();
        foreach (var t in dto.Tiles)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrEmpty(t.Key))
                continue;

            var image = CropTile(t, dto.Tilesets, sheets);
            var info = new TileInfo
            {
                Id = t.Id!,
                Key = t.Key!,
                BlocksMove = t.BlocksMove,
                BlocksFire = t.BlocksFire,
                Slides = t.Glisse,
                Sheet = TabFor(t.Sheet),   // onglet de palette : familles « eaux/eaux2/eaux3 » regroupées sous « eaux »
                Image = image,
            };
            tiles.Add(info);
            byKey[info.Key] = info;
            if (info.Key.Length >= 2)
                leads.Add(info.Key[0]);
        }

        foreach (var s in sheets.Values) s.Dispose();

        return new TileRenderCatalog
        {
            Tiles = tiles,
            ByKey = byKey,
            KeyLeads = leads,
            TileSize = dto.TileSize > 0 ? dto.TileSize : 64,
            Thickness = dto.Thickness,
            RawJson = raw,
            SourcePath = tilesJsonPath,
        };
    }

    /// <summary>
    /// ONGLET de palette d'un tileset : son nom SANS le chiffre final, pour regrouper une famille de feuilles
    /// sous un même onglet (<c>eaux</c>, <c>eaux2</c>, <c>eaux3</c> → <c>eaux</c>). Sans effet sur les noms sans
    /// chiffre final (<c>herb</c>, <c>murs</c>, <c>neige</c>…). Un nom entièrement numérique est laissé tel quel
    /// (jamais d'onglet vide). N'affecte que l'affichage : le découpage de l'image se fait via le vrai tileset.
    /// </summary>
    internal static string? TabFor(string? sheet)
    {
        if (string.IsNullOrEmpty(sheet))
            return sheet;
        var end = sheet.Length;
        while (end > 0 && char.IsDigit(sheet[end - 1]))
            end--;
        return end == 0 ? sheet : sheet[..end];   // nom uniquement numérique : conservé tel quel
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
        public bool Glisse { get; set; }
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
