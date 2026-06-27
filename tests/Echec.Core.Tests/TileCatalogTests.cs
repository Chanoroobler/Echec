using System;
using System.Collections.Generic;
using Echec.Core.Map;
using Xunit;

namespace Echec.Core.Tests;

public class TileCatalogTests
{
    private const string SampleJson = """
    {
      "tileSize": 64,
      "thickness": 16,
      "_comment": "ce champ est ignoré",
      "tiles": [
        { "id": "damier_clair", "blocksMove": false, "blocksFire": false, "desc": "sol" },
        { "id": "mur_bas",      "blocksMove": true,  "blocksFire": true },
        { "id": "eau_coin_hg",  "blocksMove": true,  "blocksFire": false }
      ]
    }
    """;

    [Fact]
    public void FromJson_LoadsAllTiles_WithFlags()
    {
        var cat = TileCatalog.FromJson(SampleJson);

        Assert.Equal(3, cat.Count);

        var mur = cat.Get("mur_bas");
        Assert.True(mur.BlocksMove);
        Assert.True(mur.BlocksFire);

        var sol = cat.Get("damier_clair");
        Assert.False(sol.BlocksMove);
        Assert.False(sol.BlocksFire);

        var eau = cat.Get("eau_coin_hg");
        Assert.True(eau.BlocksMove);
        Assert.False(eau.BlocksFire);   // l'eau laisse passer le tir
    }

    [Fact]
    public void Get_UnknownId_Throws()
    {
        var cat = TileCatalog.FromJson(SampleJson);
        Assert.Throws<KeyNotFoundException>(() => cat.Get("inconnue"));
    }

    [Fact]
    public void TryGet_ReportsPresence()
    {
        var cat = TileCatalog.FromJson(SampleJson);

        Assert.True(cat.TryGet("mur_bas", out var def));
        Assert.Equal("mur_bas", def.Id);
        Assert.False(cat.TryGet("nope", out _));
    }

    [Fact]
    public void FromJson_BuildsGlobalLegend_FromKeys()
    {
        var json = """
        { "tiles": [
            { "id": "damier_clair", "key": "1", "blocksMove": false, "blocksFire": false },
            { "id": "mur_bas",      "key": "_", "blocksMove": true,  "blocksFire": true }
        ]}
        """;
        var cat = TileCatalog.FromJson(json);

        Assert.Equal(2, cat.Legend.Count);
        Assert.Equal("damier_clair", cat.Legend["1"]);
        Assert.Equal("mur_bas", cat.Legend["_"]);
    }

    [Fact]
    public void Ctor_DuplicateId_Throws()
    {
        var dup = new[]
        {
            new TileDef("x", false, false),
            new TileDef("x", true, true),
        };
        Assert.Throws<ArgumentException>(() => new TileCatalog(dup));
    }
}
