using System;
using System.Collections.Generic;
using System.Linq;
using Echec.Core.Map;
using Xunit;

namespace Echec.Core.Tests;

public class MapLoaderTests
{
    private static TileCatalog Catalog() => new(new[]
    {
        new TileDef("damier_clair", false, false),
        new TileDef("damier_sombre", false, false),
        new TileDef("mur_bas", true, true),
    });

    private const string Map2x2 = """
    {
      "name": "test",
      "type": "Escarmouche",
      "width": 2,
      "height": 2,
      "legend": { "c": "damier_clair", "s": "damier_sombre", "#": "mur_bas" },
      "tiles":  [ "cs", "#c" ],
      "spawns": [ "E.", ".P" ]
    }
    """;

    [Fact]
    public void Parse_ReadsDimensionsTypeAndTiles()
    {
        var map = MapLoader.Parse(Map2x2, Catalog());

        Assert.Equal("test", map.Name);
        Assert.Equal(CombatType.Escarmouche, map.Type);
        Assert.Equal(2, map.Width);
        Assert.Equal(2, map.Height);

        Assert.Equal("damier_clair", map.TileAt(new Cell(0, 0)).Id);
        Assert.Equal("damier_sombre", map.TileAt(new Cell(1, 0)).Id);
        Assert.Equal("mur_bas", map.TileAt(new Cell(0, 1)).Id);
        Assert.True(map.TileAt(new Cell(0, 1)).BlocksMove);
    }

    [Fact]
    public void Parse_ReadsSpawnCells()
    {
        var map = MapLoader.Parse(Map2x2, Catalog());

        Assert.Equal(new[] { new Cell(0, 0) }, map.EnemySpawns);
        Assert.Equal(new[] { new Cell(1, 1) }, map.PlayerSpawns);
        Assert.Empty(map.BossSpawns);
    }

    [Fact]
    public void Parse_UnknownLegendChar_Throws()
    {
        var json = Map2x2.Replace("\"cs\"", "\"cZ\"");
        Assert.Throws<FormatException>(() => MapLoader.Parse(json, Catalog()));
    }

    [Fact]
    public void Parse_TileIdNotInCatalog_Throws()
    {
        var json = Map2x2.Replace("\"damier_clair\"", "\"absente\"");
        Assert.Throws<KeyNotFoundException>(() => MapLoader.Parse(json, Catalog()));
    }

    [Fact]
    public void Parse_WrongRowWidth_Throws()
    {
        var json = Map2x2.Replace("\"cs\", \"#c\"", "\"c\", \"#c\"");
        Assert.Throws<FormatException>(() => MapLoader.Parse(json, Catalog()));
    }

    [Fact]
    public void Parse_UsesCatalogGlobalLegend_WhenMapHasNone()
    {
        var catalog = new TileCatalog(
            new[] { new TileDef("damier_clair", false, false), new TileDef("damier_sombre", false, false) },
            new Dictionary<string, string> { ["1"] = "damier_clair", ["5"] = "damier_sombre" });

        var json = """
        { "name": "g", "type": "Escarmouche", "width": 2, "height": 2, "tiles": [ "15", "51" ] }
        """;
        var map = MapLoader.Parse(json, catalog);

        Assert.Equal("damier_clair", map.TileAt(new Cell(0, 0)).Id);
        Assert.Equal("damier_sombre", map.TileAt(new Cell(1, 0)).Id);
        Assert.Equal("damier_sombre", map.TileAt(new Cell(0, 1)).Id);
    }

    [Fact]
    public void Parse_UnknownType_Throws()
    {
        var json = Map2x2.Replace("\"Escarmouche\"", "\"Picnic\"");
        Assert.Throws<FormatException>(() => MapLoader.Parse(json, Catalog()));
    }

    [Fact]
    public void Parse_DefensiveSpawn_CountsAsEnemyAndIsMarkedDefensive()
    {
        // 'D' = garde défensif : c'est une case d'apparition ennemie ET une case défensive.
        var json = Map2x2.Replace("\"E.\", \".P\"", "\"ED\", \".P\"");
        var map = MapLoader.Parse(json, Catalog());

        Assert.Equal(new[] { new Cell(0, 0), new Cell(1, 0) }, map.EnemySpawns);
        Assert.Equal(new[] { new Cell(1, 0) }, map.DefensiveEnemySpawns);
    }

    [Fact]
    public void Parse_NoDefensiveSpawn_GivesEmpty()
    {
        var map = MapLoader.Parse(Map2x2, Catalog());
        Assert.Empty(map.DefensiveEnemySpawns);
    }

    [Fact]
    public void Parse_OffensiveSpawn_CountsAsEnemyAndIsMarkedOffensive()
    {
        // 'O' = assaillant offensif : case d'apparition ennemie ET case offensive.
        var json = Map2x2.Replace("\"E.\", \".P\"", "\"EO\", \".P\"");
        var map = MapLoader.Parse(json, Catalog());

        Assert.Equal(new[] { new Cell(0, 0), new Cell(1, 0) }, map.EnemySpawns);
        Assert.Equal(new[] { new Cell(1, 0) }, map.OffensiveEnemySpawns);
        Assert.Empty(map.DefensiveEnemySpawns);
    }

    [Fact]
    public void Parse_ReadsProtegerObjective()
    {
        var json = Map2x2.Replace("\"type\": \"Escarmouche\",",
            "\"type\": \"Speciale\", \"objective\": \"ProtegerPaysans\",");
        var map = MapLoader.Parse(json, Catalog());
        Assert.Equal(SpecialObjective.ProtegerPaysans, map.Objective);
    }

    [Fact]
    public void Parse_ReadsSpecialObjective()
    {
        var json = Map2x2.Replace("\"type\": \"Escarmouche\",",
            "\"type\": \"Speciale\", \"objective\": \"LibererPaysans\",");
        var map = MapLoader.Parse(json, Catalog());

        Assert.Equal(CombatType.Speciale, map.Type);
        Assert.Equal(SpecialObjective.LibererPaysans, map.Objective);
    }

    [Fact]
    public void Parse_NoObjective_DefaultsToAucun()
    {
        var map = MapLoader.Parse(Map2x2, Catalog());
        Assert.Equal(SpecialObjective.Aucun, map.Objective);
    }

    [Fact]
    public void Parse_UnknownObjective_Throws()
    {
        var json = Map2x2.Replace("\"type\": \"Escarmouche\",",
            "\"type\": \"Speciale\", \"objective\": \"SauverLeMonde\",");
        Assert.Throws<FormatException>(() => MapLoader.Parse(json, Catalog()));
    }

    [Fact]
    public void Parse_ReadsPhase()
    {
        var json = Map2x2.Replace("\"width\": 2,", "\"phase\": 2, \"width\": 2,");
        var map = MapLoader.Parse(json, Catalog());
        Assert.Equal(2, map.Phase);
    }

    [Fact]
    public void Parse_NoPhase_DefaultsToZero()
    {
        var map = MapLoader.Parse(Map2x2, Catalog());
        Assert.Equal(0, map.Phase);   // 0 = toutes phases
    }

    [Fact]
    public void Parse_PhaseOutOfRange_Throws()
    {
        var json = Map2x2.Replace("\"width\": 2,", "\"phase\": 5, \"width\": 2,");
        Assert.Throws<FormatException>(() => MapLoader.Parse(json, Catalog()));
    }

    [Fact]
    public void Parse_ReadsTurnLimit()
    {
        var json = Map2x2.Replace("\"width\": 2,", "\"turnLimit\": 20, \"width\": 2,");
        var map = MapLoader.Parse(json, Catalog());
        Assert.Equal(20, map.TurnLimit);
    }

    [Fact]
    public void Parse_NoTurnLimit_DefaultsToZero()
    {
        var map = MapLoader.Parse(Map2x2, Catalog());
        Assert.Equal(0, map.TurnLimit);   // 0 = valeur par défaut du jeu
    }

    [Fact]
    public void Parse_NegativeTurnLimit_Throws()
    {
        var json = Map2x2.Replace("\"width\": 2,", "\"turnLimit\": -3, \"width\": 2,");
        Assert.Throws<FormatException>(() => MapLoader.Parse(json, Catalog()));
    }

    [Fact]
    public void Parse_ReadsObjectsLayer_Chest()
    {
        var json = Map2x2.TrimEnd().TrimEnd('}') + """
        , "objects": [ ".C", ".." ] }
        """;
        var map = MapLoader.Parse(json, Catalog());

        var chest = Assert.Single(map.Objects);
        Assert.Equal(MapObjectKind.ChestCommon, chest.Kind);
        Assert.Equal(new Cell(1, 0), chest.Cell);
    }

    [Fact]
    public void Parse_NoObjectsLayer_GivesEmpty()
    {
        var map = MapLoader.Parse(Map2x2, Catalog());
        Assert.Empty(map.Objects);
    }

    [Fact]
    public void Parse_UnknownObjectChar_Throws()
    {
        var json = Map2x2.TrimEnd().TrimEnd('}') + """
        , "objects": [ ".Z", ".." ] }
        """;
        Assert.Throws<FormatException>(() => MapLoader.Parse(json, Catalog()));
    }

    // ─── Maps LIVRÉES ────────────────────────────────────────────────────────────────────────────
    // Le jeu ignore SILENCIEUSEMENT une map mal formée (cf. GameplayScene.LoadMaps) : sans ces tests, une
    // faute de frappe dans un fichier ne se verrait qu'en jouant la mission concernée.

    /// <summary>Racine du dépôt, trouvée en remontant depuis le binaire de test.</summary>
    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "Echec.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string AssetPath(params string[] parts) =>
        System.IO.Path.Combine(new[] { RepoRoot(), "src", "Echec.Game", "Assets" }.Concat(parts).ToArray());

    private static TileCatalog ShippedCatalog() =>
        TileCatalog.FromJson(System.IO.File.ReadAllText(AssetPath("Tiles", "tiles.json")));

    private static List<MapData> ShippedMaps() =>
        System.IO.Directory.GetFiles(AssetPath("Maps"), "*.json")
            .Select(f => MapLoader.Parse(System.IO.File.ReadAllText(f), ShippedCatalog()))
            .ToList();

    [Fact]
    public void ShippedMaps_AllParse_WithTheShippedTileCatalog()
    {
        var catalog = ShippedCatalog();
        var files = System.IO.Directory.GetFiles(AssetPath("Maps"), "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var ex = Record.Exception(() => MapLoader.Parse(System.IO.File.ReadAllText(file), catalog));
            Assert.True(ex is null, $"{System.IO.Path.GetFileName(file)} : {ex?.Message}");
        }
    }

    [Fact]
    public void ShippedMaps_ExactlyOneTutorialMap_SixBySixWithASingleChest()
    {
        // Le tuto charge SA map par ce type ; la campagne, elle, ne tire jamais dedans.
        var tuto = Assert.Single(ShippedMaps(), m => m.Type == CombatType.Tutoriel);
        Assert.Equal(6, tuto.Width);
        Assert.Equal(6, tuto.Height);
        Assert.NotEmpty(tuto.PlayerSpawns);

        // Un seul objet : le coffre de la leçon « équipement ». Rien d'autre ne doit distraire. Sa CASE,
        // elle, est sans importance : le tuto le repose contre le soldat une fois celui-ci déployé.
        var chest = Assert.Single(tuto.Objects);
        Assert.Equal(MapObjectKind.ChestCommon, chest.Kind);
    }
}
