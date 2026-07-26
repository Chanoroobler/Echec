using ChessArmy.Core.Campaign;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>Statistiques cumulées d'une run (récap de fin) : agrégation des dégâts par classe et compteurs.</summary>
public class RunStatsTests
{
    [Fact]
    public void TopDamage_OrdersDescending_AndTotalsAcrossClasses()
    {
        var s = new RunStats();
        s.AddDamage("Lancier", 100);
        s.AddDamage("Paladin", 250);
        s.AddDamage("Lancier", 50);   // cumule → 150
        s.AddDamage("Archer", 0);     // 0 ignoré

        Assert.Equal(400, s.TotalDamage);
        var top = s.TopDamage(2);
        Assert.Equal(("Paladin", 250), top[0]);
        Assert.Equal(("Lancier", 150), top[1]);
        Assert.Equal(2, top.Count);   // Archer (0) absent
    }

    [Fact]
    public void Counters_AccumulateAndClampNegatives()
    {
        var s = new RunStats();
        s.AddKills(3);
        s.AddKills(2);
        s.AddKills(-5);   // négatif ignoré
        s.AddUnitsLost(2);
        s.AddFusion();
        s.AddFusion();
        s.AddPaysansSaved(4);
        s.AddEquipmentFound();
        s.AddEquipmentFound();

        Assert.Equal(5, s.TotalKills);
        Assert.Equal(2, s.UnitsLost);
        Assert.Equal(2, s.Fusions);
        Assert.Equal(4, s.PaysansSaved);
        Assert.Equal(2, s.EquipmentFound);
    }

    [Fact]
    public void PlayTime_AccumulatesAndIgnoresNonPositive()
    {
        var s = new RunStats();
        Assert.Equal(0, s.PlayTimeSeconds);

        s.AddPlayTime(1.5);
        s.AddPlayTime(0.5);
        s.AddPlayTime(0);            // ignoré
        s.AddPlayTime(-10);          // ignoré
        s.AddPlayTime(double.NaN);   // ignoré (une frame aberrante ne doit pas empoisonner le compteur)

        Assert.Equal(2.0, s.PlayTimeSeconds, 6);
    }

    /// <summary>Le chronomètre survit à la sauvegarde du slot : une reprise repart du temps déjà écoulé.</summary>
    [Fact]
    public void PlayTime_SurvivesSaveRoundTrip()
    {
        var s = new RunStats();
        s.AddPlayTime(3725);   // 1 h 02 min 05 s
        s.AddKills(4);

        var restored = RunStatsSave.From(s).ToStats();

        Assert.Equal(3725, restored.PlayTimeSeconds, 6);
        Assert.Equal(4, restored.TotalKills);
    }

    /// <summary>Sauvegarde antérieure au chronomètre (récap absent) : l'écran de slots lit 0, pas une erreur.</summary>
    [Fact]
    public void PlayTime_MissingStats_ReadsAsZero()
    {
        Assert.Equal(0, new RunSave().PlayTimeSeconds);
        Assert.Equal(0, new RunSave { Stats = new RunStatsSave() }.PlayTimeSeconds);
    }

    [Fact]
    public void Unlocks_TrackedAndFlagged()
    {
        var s = new RunStats();
        Assert.False(s.HasUnlocks);

        s.AddUnlockedCommander("LANCIER");
        s.AddDiscoveredClass("Paladin");
        s.AddDiscoveredEquipment("Epee");

        Assert.True(s.HasUnlocks);
        Assert.Equal(new[] { "LANCIER" }, s.UnlockedCommanders);
        Assert.Equal(new[] { "Paladin" }, s.DiscoveredClasses);
        Assert.Equal(new[] { "Epee" }, s.DiscoveredEquipment);
    }
}
