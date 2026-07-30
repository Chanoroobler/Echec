using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Map;
using Xunit;

namespace ChessArmy.Core.Tests;

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

    // Map boss 3x3 : E/D = ennemis (tiers), B = boss (sans tier). Grille `tiers` alignée aux spawns.
    private const string MapWithTiers = """
    {
      "name": "t",
      "type": "Boss",
      "width": 3,
      "height": 3,
      "legend": { "c": "damier_clair" },
      "tiles":  [ "ccc", "ccc", "ccc" ],
      "spawns": [ "EBE", "..D", "PPP" ],
      "tiers":  [ "3.2", "..1", "..." ]
    }
    """;

    [Fact]
    public void Parse_ReadsEnemyTiers_FromTiersGrid()
    {
        var map = MapLoader.Parse(MapWithTiers, Catalog());
        // Tiers lus aux cases de spawn ENNEMI, dans l'ordre de lecture : (0,0)=3, (2,0)=2, (2,1)=1. Boss ignoré.
        Assert.Equal(new[] { 3, 2, 1 }, map.EnemyTiers);
    }

    [Fact]
    public void Parse_NoTiersGrid_EnemyTiersEmpty()
    {
        Assert.Empty(MapLoader.Parse(Map2x2, Catalog()).EnemyTiers);   // pas de calque tiers → repli campaign.json
    }

    [Fact]
    public void Parse_InvalidTierChar_Throws()
    {
        var json = MapWithTiers.Replace("\"3.2\"", "\"5.2\"");   // 5 hors 1..3, sur une case ennemie
        Assert.Throws<FormatException>(() => MapLoader.Parse(json, Catalog()));
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
    public void Parse_TwoCharKeys_ResolveViaPrefix()
    {
        // Clé '_x' de 2 caractères : le parseur, voyant le préfixe '_', lit 2 caractères pour la case.
        // Mélange libre avec des clés 1-caractère sur la même ligne (les maps 1-car restent inchangées).
        var json = """
        {
          "name": "t", "type": "Escarmouche", "width": 2, "height": 2,
          "legend": { "c": "damier_clair", "_x": "mur_bas" },
          "tiles": [ "c_x", "_xc" ]
        }
        """;
        var map = MapLoader.Parse(json, Catalog());
        Assert.Equal("damier_clair", map.TileAt(new Cell(0, 0)).Id);
        Assert.Equal("mur_bas",      map.TileAt(new Cell(1, 0)).Id);
        Assert.Equal("mur_bas",      map.TileAt(new Cell(0, 1)).Id);
        Assert.Equal("damier_clair", map.TileAt(new Cell(1, 1)).Id);
    }

    [Fact]
    public void Parse_TwoCharKey_TruncatedAtRowEnd_Throws()
    {
        // 'c' puis un préfixe '_' seul en fin de ligne : la clé 2-car est tronquée → ligne invalide.
        var json = """
        {
          "name": "t", "type": "Escarmouche", "width": 2, "height": 1,
          "legend": { "c": "damier_clair", "_x": "mur_bas" },
          "tiles": [ "c_" ]
        }
        """;
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
    public void Parse_ReadsEnemyFacingLayer()
    {
        // 'v' = regarde vers le bas (true), '^' = vers le haut (false) ; PAR CASE de spawn ennemi.
        var json = Map2x2.Replace("\"spawns\": [ \"E.\", \".P\" ]",
            "\"spawns\": [ \"EE\", \".P\" ], \"facing\": [ \"v^\", \"..\" ]");
        var map = MapLoader.Parse(json, Catalog());

        Assert.True(map.EnemyFacing[new Cell(0, 0)]);    // 'v' → bas
        Assert.False(map.EnemyFacing[new Cell(1, 0)]);   // '^' → haut
        Assert.Equal(2, map.EnemyFacing.Count);
    }

    [Fact]
    public void Parse_NoFacingLayer_EmptyFacing()
    {
        var map = MapLoader.Parse(Map2x2, Catalog());
        Assert.Empty(map.EnemyFacing);
    }

    [Fact]
    public void Parse_UnknownFacingChar_Throws()
    {
        var json = Map2x2.Replace("\"spawns\": [ \"E.\", \".P\" ]",
            "\"spawns\": [ \"E.\", \".P\" ], \"facing\": [ \"x.\", \"..\" ]");
        Assert.Throws<FormatException>(() => MapLoader.Parse(json, Catalog()));
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
    public void Parse_ReadsSauverObjective()
    {
        var json = Map2x2.Replace("\"type\": \"Escarmouche\",",
            "\"type\": \"Speciale\", \"objective\": \"SauverPaysans\",");
        var map = MapLoader.Parse(json, Catalog());
        Assert.Equal(SpecialObjective.SauverPaysans, map.Objective);
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
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string AssetPath(params string[] parts) =>
        System.IO.Path.Combine(new[] { RepoRoot(), "src", "ChessArmy.Game", "Assets" }.Concat(parts).ToArray());

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
