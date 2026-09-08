using System;
using ChessArmy.Core.Campaign;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// Plan de campagne (<see cref="CampaignPlan"/>) : parsing de campaign.json (taille de map + effectif/tiers
/// par mission), défauts codés, bornage. Les tests qui appellent <see cref="CampaignPlan.Load"/> RESTAURENT
/// les défauts (état statique partagé), pour ne pas fausser les autres tests.
/// </summary>
public class CampaignPlanTests
{
    // Table complète minimale (phases de 6 / 6 / 5 missions, cf. Run.MissionsIn — la phase 3 n'a pas
    // d'escarmouche avant le boss). Phase 1 m1 est unique ("enemies": [1, 2]) pour la retrouver.
    private const string FullJson = """
    {
      "phases": [
        { "missions": [
          { "mapSize": 6, "enemies": [1, 2] },
          { "mapSize": 6, "enemies": [1] }, { "mapSize": 6, "enemies": [1] },
          { "mapSize": 6, "enemies": [1] }, { "mapSize": 6, "enemies": [1] }, { "mapSize": 6, "enemies": [1] }
        ] },
        { "missions": [
          { "mapSize": 7, "enemies": [2] }, { "mapSize": 7, "enemies": [2] }, { "mapSize": 7, "enemies": [2] },
          { "mapSize": 7, "enemies": [2] }, { "mapSize": 7, "enemies": [2] }, { "mapSize": 7, "enemies": [2] }
        ] },
        { "missions": [
          { "mapSize": 8, "enemies": [3] }, { "mapSize": 8, "enemies": [3] }, { "mapSize": 8, "enemies": [3] },
          { "mapSize": 8, "enemies": [3] }, { "mapSize": 8, "enemies": [3, 3, 3] }
        ] }
      ]
    }
    """;

    [Fact]
    public void FromJson_ParsesMapSizeAndTiers()
    {
        var table = CampaignPlan.FromJson(FullJson);

        Assert.Equal(3, table.Count);
        Assert.Equal(6, table[0].Count);
        Assert.Equal(6, table[1].Count);
        Assert.Equal(5, table[2].Count);   // phase 3 : une mission de moins

        Assert.Equal(6, table[0][0].MapSize);
        Assert.Equal(new[] { 1, 2 }, table[0][0].Tiers);   // 2 ennemis : T1 + T2
        Assert.Equal(8, table[2][4].MapSize);
        Assert.Equal(3, table[2][4].Tiers.Count);          // phase 3 m5 (boss final) : 3 ennemis
    }

    [Fact]
    public void FromJson_Throws_OnIncompleteTable()
    {
        Assert.Throws<FormatException>(() => CampaignPlan.FromJson("""{ "phases": [ { "missions": [] } ] }"""));
        Assert.Throws<FormatException>(() => CampaignPlan.FromJson("""{ "phases": [] }"""));
    }

    [Fact]
    public void FromJson_Throws_OnBadValues()
    {
        Assert.Throws<FormatException>(() =>
            CampaignPlan.FromJson(FullJson.Replace("\"mapSize\": 6, \"enemies\": [1, 2]", "\"mapSize\": 0, \"enemies\": [1, 2]")));
        Assert.Throws<FormatException>(() => CampaignPlan.FromJson(FullJson.Replace("[1, 2]", "[]")));   // liste vide
        Assert.Throws<FormatException>(() => CampaignPlan.FromJson(FullJson.Replace("[1, 2]", "[4]")));  // tier hors 1..3
    }

    [Fact]
    public void Defaults_MatchLegacyValues()
    {
        // For() lit les défauts codés tant que Load n'a pas été appelé (== ancienne table WaveTiers / MapSizeFor).
        Assert.Equal(6, CampaignPlan.For(1, 1).MapSize);
        Assert.Equal(2, CampaignPlan.For(1, 1).Tiers.Count);
        Assert.Equal(7, CampaignPlan.For(1, 4).MapSize);        // spéciale ph.1 : 7x7
        Assert.Equal(7, CampaignPlan.For(1, 5).MapSize);        // escarmouche ph.1 m5 : 7x7
        Assert.Equal(6, CampaignPlan.For(1, 6).MapSize);        // boss ph.1 : 6x6
        Assert.Equal(8, CampaignPlan.For(3, 5).MapSize);
        Assert.Equal(12, CampaignPlan.For(3, 5).Tiers.Count);   // boss final (ph.3 m5) : 12 (gabarit d'escortes)
    }

    [Fact]
    public void For_ClampsOutOfRange_WithoutThrowing()
    {
        Assert.Equal(CampaignPlan.For(1, 1), CampaignPlan.For(0, 0));
        Assert.Equal(CampaignPlan.For(3, 5), CampaignPlan.For(99, 99));   // dernière mission de la dernière phase
    }

    [Fact]
    public void Load_ReplacesTable_ThenRestoresDefaults()
    {
        try
        {
            CampaignPlan.Load(CampaignPlan.FromJson(
                FullJson.Replace("\"mapSize\": 6, \"enemies\": [1, 2]", "\"mapSize\": 9, \"enemies\": [1, 2]")));
            Assert.Equal(9, CampaignPlan.For(1, 1).MapSize);
        }
        finally
        {
            CampaignPlan.Load(CampaignPlan.Defaults());   // NE PAS laisser fuir l'état statique aux autres tests
        }
        Assert.Equal(6, CampaignPlan.For(1, 1).MapSize);
    }
}
