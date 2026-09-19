using System.Collections.Generic;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// TOUR DE GUET (<see cref="TileDef.RangeBonus"/>) : la tuile allonge la portée d'attaque de l'unité postée
/// dessus, mais SEULEMENT si elle tire déjà (portée native &gt;= <see cref="Match.RangedAttackRange"/>).
/// Une tour rallonge un tir, pas un bras.
/// </summary>
public class WatchtowerTests
{
    private static readonly TileDef Tower = new("tour", BlocksMove: false, BlocksFire: false, RangeBonus: 1,
        Trait: ChessArmy.Core.Battle.Trait.Balistique);

    /// <summary>Montagne : bloque le déplacement ET la ligne de tir — sauf pour un tir balistique.</summary>
    private static readonly TileDef Mountain = new("montagne", BlocksMove: true, BlocksFire: true);

    private static Battlefield Field(params Cell[] towers)
    {
        var field = Battlefield.CreateFlat(8, 8);
        foreach (var cell in towers)
            field[cell] = new Tile(Tower);
        return field;
    }

    private static Unit Shooter(Faction f, int attackRange) =>
        new(Domaine.Tour, f, new UnitClass("S", "s", tier: 1, maxHp: 20, damage: 5,
            moveRange: 1, attackRange: attackRange));

    [Fact]
    public void ARangedUnitOnATower_ReachesOneTileFurther()
    {
        var from = new Cell(0, 0);
        var target = new Cell(3, 0);   // à 3 cases : hors de portée 2, à portée une fois sur la tour

        var ground = new Match(8, 8, Field());
        ground.Place(from, Shooter(Faction.Player, attackRange: 2));
        ground.Place(target, Shooter(Faction.Enemy, attackRange: 1));
        Assert.DoesNotContain(target, ground.AttackTargets(from));

        var tower = new Match(8, 8, Field(from));
        tower.Place(from, Shooter(Faction.Player, attackRange: 2));
        tower.Place(target, Shooter(Faction.Enemy, attackRange: 1));
        Assert.Contains(target, tower.AttackTargets(from));
        Assert.Equal(1, tower.AttackRangeBonus(from));
    }

    /// <summary>Un pion de CONTACT (portée 1) ne gagne rien à monter : le seuil est la portée NATIVE.</summary>
    [Fact]
    public void AMeleeUnitOnATower_GainsNothing()
    {
        var from = new Cell(0, 0);
        var match = new Match(8, 8, Field(from));
        match.Place(from, Shooter(Faction.Player, attackRange: 1));
        match.Place(new Cell(2, 0), Shooter(Faction.Enemy, attackRange: 1));

        Assert.Equal(0, match.AttackRangeBonus(from));
        Assert.DoesNotContain(new Cell(2, 0), match.AttackTargets(from));
    }

    /// <summary>
    /// La MENACE affichée suit la portée allongée : sans ça l'aperçu promettrait au joueur une zone de tir
    /// plus courte que celle que le moteur joue.
    /// </summary>
    [Fact]
    public void TheThreatPreviewFollowsTheExtendedRange()
    {
        var from = new Cell(0, 0);
        var match = new Match(8, 8, Field(from));
        match.Place(from, Shooter(Faction.Enemy, attackRange: 2));

        var reach = new List<Cell>();
        match.ThreatenedCells(from, reach);

        Assert.Contains(new Cell(3, 0), reach);   // 3 cases : la tour porte jusque-là
        Assert.DoesNotContain(new Cell(4, 0), reach);
    }

    /// <summary>Une case vide ne donne rien : le bonus se lit sur l'OCCUPANT, pas sur la tuile seule.</summary>
    [Fact]
    public void AnEmptyTower_GivesNoBonus()
    {
        var cell = new Cell(0, 0);
        Assert.Equal(0, new Match(8, 8, Field(cell)).AttackRangeBonus(cell));
    }

    // ── Trait prêté par la tuile : le mirador donne « Balistique » ───────────────

    /// <summary>
    /// Posté sur le mirador, un tireur ORDINAIRE tire par-dessus la montagne : la tuile lui prête
    /// « Balistique ». Le même tireur au sol se fait couper la ligne.
    /// </summary>
    [Fact]
    public void AUnitOnATower_ShootsOverAMountain()
    {
        var from = new Cell(0, 0);
        var behind = new Cell(2, 0);   // derrière la montagne de (1 0)

        var ground = Battlefield.CreateFlat(8, 8);
        ground[new Cell(1, 0)] = new Tile(Mountain);
        var flat = new Match(8, 8, ground);
        flat.Place(from, Shooter(Faction.Player, attackRange: 3));
        flat.Place(behind, Shooter(Faction.Enemy, attackRange: 1));
        Assert.DoesNotContain(behind, flat.AttackTargets(from));   // la montagne coupe la ligne

        var raised = Battlefield.CreateFlat(8, 8);
        raised[new Cell(1, 0)] = new Tile(Mountain);
        raised[from] = new Tile(Tower);
        var tower = new Match(8, 8, raised);
        tower.Place(from, Shooter(Faction.Player, attackRange: 3));
        tower.Place(behind, Shooter(Faction.Enemy, attackRange: 1));
        Assert.Contains(behind, tower.AttackTargets(from));        // …plus depuis le mirador
        Assert.Equal(ChessArmy.Core.Battle.Trait.Balistique, tower.TileTraitAt(from));
    }

    /// <summary>Le trait ne suit PAS le pion : il descend de la tour, il retire son tir indirect.</summary>
    [Fact]
    public void TheTowerTrait_IsLostOnLeavingTheTile()
    {
        var field = Battlefield.CreateFlat(8, 8);
        field[new Cell(0, 0)] = new Tile(Tower);
        var match = new Match(8, 8, field);

        Assert.Equal(ChessArmy.Core.Battle.Trait.Balistique, match.TileTraitAt(new Cell(0, 0)));
        Assert.Null(match.TileTraitAt(new Cell(1, 0)));   // case voisine ordinaire : rien de prêté
    }

    /// <summary>Le catalogue lit bien <c>trait</c> ; absent ou vide, la tuile n'en prête aucun.</summary>
    [Fact]
    public void TheCatalogReadsTheGrantedTrait()
    {
        var catalog = TileCatalog.FromJson("""
        { "tiles": [
            { "id": "tour",  "key": "^a", "blocksMove": false, "blocksFire": false, "trait": "Balistique" },
            { "id": "herbe", "key": "h",  "blocksMove": false, "blocksFire": false },
            { "id": "vide",  "key": "v",  "blocksMove": false, "blocksFire": false, "trait": "  " } ] }
        """);

        Assert.Equal("Balistique", catalog.Get("tour").Trait);
        Assert.Null(catalog.Get("herbe").Trait);
        Assert.Null(catalog.Get("vide").Trait);   // chaîne vide/blanche traitée comme « aucun »
    }

    /// <summary>Le catalogue lit bien <c>rangeBonus</c> ; absent, il vaut 0 (toutes les tuiles existantes).</summary>
    [Fact]
    public void TheCatalogReadsTheRangeBonus()
    {
        var catalog = TileCatalog.FromJson("""
        { "tiles": [
            { "id": "tour",  "key": "^a", "blocksMove": false, "blocksFire": false, "rangeBonus": 2 },
            { "id": "herbe", "key": "h",  "blocksMove": false, "blocksFire": false } ] }
        """);

        Assert.Equal(2, catalog.Get("tour").RangeBonus);
        Assert.Equal(0, catalog.Get("herbe").RangeBonus);
    }
}
