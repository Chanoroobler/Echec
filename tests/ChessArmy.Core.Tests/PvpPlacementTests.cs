using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Battle.Config;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Pvp;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// Gestes de l'écran Armée du multi (<see cref="PvpPlacement"/>) : poser, remplacer, déplacer (l'équipement
/// suit), échanger, retirer ; équiper, déplacer et échanger un équipement ; budget ; case du commandant.
/// Barème fixé ici (pas celui de pvp.json, qui bouge à chaque mesure de l'outil d'équilibrage).
/// </summary>
public class PvpPlacementTests : IDisposable
{
    private readonly IReadOnlyList<DomaineDef> _domaines = Domaines.All;
    private readonly IReadOnlyList<CommandeDef> _commandes = Commandes.All;
    private readonly IReadOnlyList<Equipment> _equipment = Equipments.All;
    private readonly PvpConfig _config = PvpConfig.Current;

    private readonly string _soldat, _mage, _cavalier;
    private readonly string _eqA, _eqB;

    private const int Back = PvpBuild.BackLine, Front = PvpBuild.FrontLine;

    public PvpPlacementTests()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        var config = System.IO.Path.Combine(dir!.FullName, "src", "ChessArmy.Game", "Assets", "Config");
        var units = System.IO.File.ReadAllText(System.IO.Path.Combine(config, "units.json"));
        Domaines.Load(DomaineCatalog.FromJson(units));
        Commandes.Load(DomaineCatalog.CommandesFromJson(units));
        Equipments.Load(EquipmentCatalog.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(config, "equipment.json"))));

        _soldat = Domaines.Dame.BaseClass.Asset;
        _mage = Domaines.Fou.BaseClass.Asset;
        _cavalier = Domaines.Cavalier.BaseClass.Asset;
        var eq = PvpCatalog.AllEquipment().Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        _eqA = eq[0];
        _eqB = eq[1];
        PvpConfig.Load(new PvpConfig
        {
            Budget = 10,
            EquipmentBudget = 2,
            UnitCost = new Dictionary<string, int> { [_soldat] = 2, [_mage] = 3, [_cavalier] = 4 },
            EquipmentCost = new Dictionary<string, int> { [_eqA] = 1, [_eqB] = 2 },
        });
    }

    public void Dispose()
    {
        Domaines.Load(_domaines);
        Commandes.Load(_commandes);
        Equipments.Load(_equipment);
        PvpConfig.Load(_config);
    }

    [Fact]
    public void PlaceUnit_OnFreeCell_CostsItsPrice_AndIsRefusedOverBudget()
    {
        var build = new PvpBuild();
        Assert.Equal(PlacementResult.Ok, PvpPlacement.PlaceUnit(build, 0, Front, _cavalier));   // 4
        Assert.Equal(PlacementResult.Ok, PvpPlacement.PlaceUnit(build, 1, Front, _cavalier));   // 8
        Assert.Equal(PlacementResult.OverBudget, PvpPlacement.PlaceUnit(build, 2, Front, _mage));   // 11 > 10
        Assert.Null(build.SlotAt(2, Front));
        Assert.Equal(PlacementResult.Ok, PvpPlacement.PlaceUnit(build, 2, Front, _soldat));     // 10
        Assert.Equal(0, PvpPlacement.Remaining(build));
    }

    [Fact]
    public void PlaceUnit_OnOccupiedCell_Replaces_AndDropsTheOldEquipment()
    {
        var build = new PvpBuild();
        build.Set(3, Back, _cavalier, _eqB);   // armée 4, équipement 2
        Assert.Equal(PlacementResult.Ok, PvpPlacement.PlaceUnit(build, 3, Back, _mage));
        Assert.Equal(new PvpBuildSlot(3, Back, _mage, null), build.SlotAt(3, Back));
        Assert.Equal(3, PvpCatalog.Cost(build));
    }

    [Fact]
    public void PlaceUnit_Replacing_CountsTheFreedPoints()
    {
        var build = new PvpBuild();
        build.Set(0, Front, _cavalier, _eqB);   // armée 4 (+ équipement 2, budget à part)
        build.Set(1, Front, _cavalier);         // 10 : budget plein
        // Remplacer le cavalier équipé par un cavalier nu ne coûte rien de plus : accepté, l'équipement est rendu.
        Assert.Equal(PlacementResult.Ok, PvpPlacement.PlaceUnit(build, 0, Front, _cavalier));
        Assert.Equal(8, PvpCatalog.Cost(build));
    }

    [Fact]
    public void MoveUnit_EquipmentFollows_AndOccupiedCellSwapsBothUnitsAndEquipment()
    {
        var build = new PvpBuild();
        build.Set(0, Front, _soldat, _eqA);
        Assert.Equal(PlacementResult.Ok, PvpPlacement.MoveUnit(build, 0, Front, 5, Back));
        Assert.Null(build.SlotAt(0, Front));
        Assert.Equal(new PvpBuildSlot(5, Back, _soldat, _eqA), build.SlotAt(5, Back));

        build.Set(2, Front, _mage, _eqB);
        Assert.Equal(PlacementResult.Ok, PvpPlacement.MoveUnit(build, 5, Back, 2, Front));
        Assert.Equal(new PvpBuildSlot(2, Front, _soldat, _eqA), build.SlotAt(2, Front));
        Assert.Equal(new PvpBuildSlot(5, Back, _mage, _eqB), build.SlotAt(5, Back));
        Assert.Equal(2, build.Slots.Count);
    }

    [Fact]
    public void MoveUnit_NeverChangesTheCost_EvenWhenTheBudgetIsFull()
    {
        var build = new PvpBuild();
        build.Set(0, Front, _cavalier, _eqB);
        build.Set(1, Front, _cavalier);
        var cost = PvpCatalog.Cost(build);
        Assert.Equal(PlacementResult.Ok, PvpPlacement.MoveUnit(build, 0, Front, 1, Front));
        Assert.Equal(cost, PvpCatalog.Cost(build));
    }

    [Fact]
    public void KingCell_RefusesEverything()
    {
        var build = new PvpBuild();
        build.Set(0, Front, _soldat, _eqA);
        var k = PvpBuild.KingColumn;
        Assert.Equal(PlacementResult.KingCell, PvpPlacement.PlaceUnit(build, k, Back, _soldat));
        Assert.Equal(PlacementResult.KingCell, PvpPlacement.MoveUnit(build, 0, Front, k, Back));
        Assert.Equal(PlacementResult.KingCell, PvpPlacement.MoveUnit(build, k, Back, 0, Front));
        Assert.Equal(PlacementResult.KingCell, PvpPlacement.RemoveUnit(build, k, Back));
        Assert.Equal(PlacementResult.CommanderNoEquipment, PvpPlacement.Equip(build, k, Back, _eqB));
        Assert.Equal(PlacementResult.CommanderNoEquipment, PvpPlacement.MoveEquipment(build, 0, Front, k, Back));
        Assert.Equal(new PvpBuildSlot(0, Front, _soldat, _eqA), Assert.Single(build.Slots));
    }

    [Fact]
    public void OutsideThePlayerLines_IsRefused()
    {
        var build = new PvpBuild();
        Assert.Equal(PlacementResult.OutOfZone, PvpPlacement.PlaceUnit(build, 0, 2, _soldat));
        Assert.Equal(PlacementResult.OutOfZone, PvpPlacement.PlaceUnit(build, 8, Front, _soldat));
        Assert.Empty(build.Slots);
    }

    [Fact]
    public void RemoveUnit_TakesItsEquipmentAlong()
    {
        var build = new PvpBuild();
        build.Set(6, Back, _mage, _eqB);
        Assert.Equal(PlacementResult.Ok, PvpPlacement.RemoveUnit(build, 6, Back));
        Assert.Empty(build.Slots);
        Assert.Equal(0, PvpCatalog.Cost(build));
    }

    [Fact]
    public void Equip_ReplacesTheWornItem_RefusesEmptyCells_AndRespectsTheBudget()
    {
        var build = new PvpBuild();
        build.Set(0, Front, _cavalier);
        build.Set(1, Front, _cavalier);   // armée 8 / 10
        Assert.Equal(PlacementResult.NoUnit, PvpPlacement.Equip(build, 2, Front, _eqA));
        Assert.Equal(PlacementResult.Ok, PvpPlacement.Equip(build, 0, Front, _eqA));     // équipement 1 / 2
        Assert.Equal(PlacementResult.Ok, PvpPlacement.Equip(build, 0, Front, _eqB));     // 2 / 2 : remplace A
        Assert.Equal(_eqB, build.SlotAt(0, Front)!.EquipmentId);
        Assert.Equal(PlacementResult.OverBudget, PvpPlacement.Equip(build, 1, Front, _eqA));   // 3 > 2
        Assert.Null(build.SlotAt(1, Front)!.EquipmentId);
        Assert.Equal(8, PvpCatalog.Cost(build));   // les équipements ne mangent pas les points d'armée
        Assert.Equal(2, PvpCatalog.EquipmentCost(build));
        Assert.Equal(2, PvpPlacement.Remaining(build));
        Assert.Equal(0, PvpPlacement.RemainingEquipment(build));
    }

    [Fact]
    public void ArmyAndEquipmentBudgets_AreSeparate()
    {
        var build = new PvpBuild();
        build.Set(0, Front, _cavalier, _eqB);
        build.Set(1, Front, _cavalier);   // armée 8 / 10, équipement 2 / 2
        // Des points d'équipement pleins n'empêchent pas de poser un pion, et inversement.
        Assert.Equal(PlacementResult.Ok, PvpPlacement.PlaceUnit(build, 2, Front, _soldat));   // armée 10 / 10
        Assert.Equal(PlacementResult.Ok, PvpPlacement.MoveEquipment(build, 0, Front, 2, Front));
        Assert.Null(PvpCatalog.Problem(build));
        build.Set(3, Front, _soldat, _eqA);   // armée 12, équipement 3 : les deux budgets débordent
        Assert.Equal("budget d'armée dépassé", PvpCatalog.Problem(build));
        build.Remove(3, Front);
        build.Set(1, Front, _cavalier, _eqA);   // armée 10 / 10, équipement 3 / 2
        Assert.Equal("budget d'équipement dépassé", PvpCatalog.Problem(build));
    }

    [Fact]
    public void SameEquipment_AtMostTwoCopiesPerArmy()
    {
        PvpConfig.Load(new PvpConfig
        {
            Budget = 10,
            EquipmentBudget = 10,
            UnitCost = new Dictionary<string, int> { [_soldat] = 2 },
            EquipmentCost = new Dictionary<string, int> { [_eqA] = 1, [_eqB] = 1 },
        });
        var build = new PvpBuild();
        for (var c = 0; c < 4; c++)
            build.Set(c, Front, _soldat);
        Assert.Equal(PlacementResult.Ok, PvpPlacement.Equip(build, 0, Front, _eqA));
        Assert.Equal(PlacementResult.Ok, PvpPlacement.Equip(build, 1, Front, _eqA));
        Assert.Equal(PlacementResult.TooManyCopies, PvpPlacement.Equip(build, 2, Front, _eqA));   // troisième : refusé
        Assert.Equal(PlacementResult.Ok, PvpPlacement.Equip(build, 2, Front, _eqB));             // un autre : permis
        Assert.Equal(PlacementResult.Ok, PvpPlacement.MoveEquipment(build, 0, Front, 3, Front));  // déplacer ne compte pas
        Assert.Null(PvpCatalog.Problem(build));
        build.Set(3, Front, _soldat, _eqA);
        build.Set(0, Front, _soldat, _eqA);   // trois exemplaires forcés (vieux build, triche)
        Assert.NotNull(PvpCatalog.Problem(build));
    }

    [Fact]
    public void MoveEquipment_ToBareUnitMoves_ToEquippedUnitSwaps_ToEmptyCellIsRefused()
    {
        var build = new PvpBuild();
        build.Set(0, Front, _soldat, _eqA);
        build.Set(1, Front, _soldat);
        build.Set(2, Front, _mage, _eqB);

        Assert.Equal(PlacementResult.Ok, PvpPlacement.MoveEquipment(build, 0, Front, 1, Front));
        Assert.Null(build.SlotAt(0, Front)!.EquipmentId);
        Assert.Equal(_eqA, build.SlotAt(1, Front)!.EquipmentId);

        Assert.Equal(PlacementResult.Ok, PvpPlacement.MoveEquipment(build, 1, Front, 2, Front));
        Assert.Equal(_eqB, build.SlotAt(1, Front)!.EquipmentId);
        Assert.Equal(_eqA, build.SlotAt(2, Front)!.EquipmentId);

        Assert.Equal(PlacementResult.NoUnit, PvpPlacement.MoveEquipment(build, 1, Front, 7, Back));
        Assert.Equal(_eqB, build.SlotAt(1, Front)!.EquipmentId);
    }

    [Fact]
    public void RemoveEquipment_KeepsTheUnit_AndFreesThePoints()
    {
        var build = new PvpBuild();
        build.Set(3, Front, _mage, _eqB);
        Assert.Equal(PlacementResult.Ok, PvpPlacement.RemoveEquipment(build, 3, Front));
        Assert.Equal(new PvpBuildSlot(3, Front, _mage, null), build.SlotAt(3, Front));
        Assert.Equal(3, PvpCatalog.Cost(build));
    }
}
