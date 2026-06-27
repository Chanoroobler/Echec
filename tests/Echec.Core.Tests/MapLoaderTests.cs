using System;
using System.Collections.Generic;
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
}
