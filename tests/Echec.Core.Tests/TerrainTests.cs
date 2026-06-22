using System;
using Echec.Core.Battle;
using Echec.Core.Map;
using Xunit;

namespace Echec.Core.Tests;

public class TerrainTests
{
    private static Battlefield FlatWith(Cell cell, TerrainType terrain)
    {
        var field = Battlefield.CreateFlat(8, 8);
        field[cell] = new Tile(terrain);
        return field;
    }

    // ── Déplacement : eau ET montagne bornent (ni s'arrêter, ni passer) ──────────────
    [Theory]
    [InlineData(TerrainType.Water)]
    [InlineData(TerrainType.Mountain)]
    public void Obstacle_BlocksMovement_NotALegalMove_AndBlocksPassage(TerrainType obstacle)
    {
        var field = FlatWith(new Cell(3, 5), obstacle);          // 2 cases au nord du lancier
        var match = new Match(8, 8, field);
        var from = new Cell(3, 7);
        match.Place(from, Units.Of(Domaine.Tour, Faction.Player)); // déplacement 3, glisse en croix

        var moves = match.LegalMoves(from);

        Assert.Contains(new Cell(3, 6), moves);       // avant l'obstacle : atteignable
        Assert.DoesNotContain(new Cell(3, 5), moves); // l'obstacle : pas un déplacement
        Assert.DoesNotContain(new Cell(3, 4), moves); // au-delà : pas de passage à travers
    }

    // ── Tir : la montagne arrête la ligne, l'eau la laisse passer ────────────────────
    [Fact]
    public void Water_DoesNotBlock_LineOfFire()
    {
        var field = FlatWith(new Cell(3, 6), TerrainType.Water);  // entre le tireur et la cible
        var match = new Match(8, 8, field);
        var tour = new Cell(3, 7);
        var enemy = new Cell(3, 5);                               // distance 2 = portée de tir
        match.Place(tour, Units.Of(Domaine.Tour, Faction.Player));
        match.Place(enemy, Units.Pion(Faction.Enemy));

        Assert.Contains(enemy, match.AttackTargets(tour));        // le tir passe au-dessus de l'eau
    }

    [Fact]
    public void Mountain_Blocks_LineOfFire_AndThreat()
    {
        var field = FlatWith(new Cell(3, 5), TerrainType.Mountain); // entre le tireur et la cible
        var match = new Match(8, 8, field);
        var tour = new Cell(3, 7);
        var enemy = new Cell(3, 4);
        match.Place(tour, Units.Of(Domaine.Tour, Faction.Player));
        match.Place(enemy, Units.Pion(Faction.Enemy));

        Assert.DoesNotContain(enemy, match.AttackTargets(tour));   // la montagne arrête le tir

        var threat = match.ThreatenedCells(tour);
        Assert.Contains(new Cell(3, 6), threat);       // avant la montagne : menacé
        Assert.DoesNotContain(new Cell(3, 5), threat); // la montagne borne la menace
        Assert.DoesNotContain(new Cell(3, 4), threat); // au-delà : hors d'atteinte
    }

    // ── Génération ───────────────────────────────────────────────────────────────────
    [Fact]
    public void Generate_KeepsDeployRowsClear_AndIsVerticallySymmetric()
    {
        var field = TerrainGenerator.Generate(8, 8, new Random(123), deployRows: 2, obstaclePairs: 4);

        foreach (var cell in field.Cells())
        {
            // Les 2 rangées de déploiement de chaque camp restent en herbe.
            if (cell.Row < 2 || cell.Row > 5)
                Assert.Equal(TerrainType.Grass, field[cell].Terrain);

            // Symétrie haut/bas (équité) : chaque case = son miroir.
            var mirror = new Cell(cell.Column, 8 - 1 - cell.Row);
            Assert.Equal(field[cell].Terrain, field[mirror].Terrain);
        }
    }

    [Fact]
    public void Generate_IsDeterministic_ForSameSeed()
    {
        var a = TerrainGenerator.Generate(8, 8, new Random(7));
        var b = TerrainGenerator.Generate(8, 8, new Random(7));

        foreach (var cell in a.Cells())
            Assert.Equal(a[cell].Terrain, b[cell].Terrain);
    }

    [Fact]
    public void Generate_PlacesSomeObstacles_InNeutralZone()
    {
        var field = TerrainGenerator.Generate(8, 8, new Random(42), deployRows: 2, obstaclePairs: 4);

        var obstacles = 0;
        foreach (var cell in field.Cells())
            if (field[cell].Terrain != TerrainType.Grass)
                obstacles++;

        Assert.True(obstacles > 0, "la génération devrait poser au moins un obstacle");
    }
}
