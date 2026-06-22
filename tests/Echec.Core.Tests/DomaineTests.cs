using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Map;
using Xunit;

namespace Echec.Core.Tests;

public class DomaineTests
{
    [Fact]
    public void FiveDomaines_NamedAfterChessPieces()
    {
        Assert.Equal(
            new[] { "Pion", "Fou", "Cavalier", "Tour", "Dame" },
            Domaines.All.Select(d => d.Name));
    }

    [Theory]
    [InlineData(Domaine.Pion, 8, MovementKind.Slide)]
    [InlineData(Domaine.Dame, 8, MovementKind.Slide)]   // mêmes directions que le Pion
    [InlineData(Domaine.Fou, 4, MovementKind.Slide)]
    [InlineData(Domaine.Tour, 4, MovementKind.Slide)]
    [InlineData(Domaine.Cavalier, 8, MovementKind.Jump)]
    public void Movement_HasExpectedVectorsAndKind(Domaine domaine, int vectorCount, MovementKind kind)
    {
        Assert.Equal(vectorCount, Movement.Vectors(domaine).Count);
        Assert.Equal(kind, Movement.Kind(domaine));
    }

    [Fact]
    public void BaseClass_CarriesAssetAndStats()
    {
        var soldat = Domaines.Pion.BaseClass;

        Assert.Equal("Soldat", soldat.Name);
        Assert.Equal("soldat", soldat.Asset);
        Assert.Equal(12, soldat.MaxHp);
        Assert.Equal(10, soldat.Damage);
        Assert.Equal(1, soldat.MoveRange);
    }

    [Fact]
    public void Pion_KingStep_Range1_HasEightMovesFromCenter()
    {
        var match = new Match(8, 8);
        var from = new Cell(4, 4);
        match.Place(from, Units.Of(Domaine.Pion, Faction.Player));

        Assert.Equal(8, match.LegalMoves(from).Count);
    }

    [Fact]
    public void Fou_SlidesDiagonally_UpToClassRange()
    {
        var match = new Match(8, 8);
        var from = new Cell(3, 3);
        match.Place(from, Units.Of(Domaine.Fou, Faction.Player)); // Mage : déplacement 2

        var moves = match.LegalMoves(from);
        Assert.Contains(new Cell(1, 1), moves);  // 2 cases en diagonale
        Assert.Contains(new Cell(5, 5), moves);
        Assert.DoesNotContain(new Cell(0, 0), moves); // 3 cases = hors portée
        Assert.DoesNotContain(new Cell(2, 3), moves); // pas de déplacement orthogonal
        Assert.Equal(8, moves.Count);            // 4 diagonales × 2 cases
    }

    [Fact]
    public void Tour_SlidesOrthogonally_Only()
    {
        var match = new Match(8, 8);
        var from = new Cell(3, 3);
        match.Place(from, Units.Of(Domaine.Tour, Faction.Player)); // Lancier : déplacement 2

        var moves = match.LegalMoves(from);
        Assert.Contains(new Cell(3, 1), moves);  // 2 vers le haut
        Assert.DoesNotContain(new Cell(5, 5), moves); // pas de diagonale
    }

    [Fact]
    public void Cavalier_JumpsOverUnits()
    {
        var match = new Match(8, 8);
        var from = new Cell(3, 3);
        match.Place(from, Units.Of(Domaine.Cavalier, Faction.Player));
        // Entoure le cavalier d'alliés : il saute par-dessus.
        foreach (var d in Movement.Vectors(Domaine.Pion))
            match.Place(new Cell(3 + d.Column, 3 + d.Row), Units.Of(Domaine.Pion, Faction.Player));

        var moves = match.LegalMoves(from);
        Assert.Equal(8, moves.Count);              // tous les sauts en L restent possibles
        Assert.Contains(new Cell(5, 4), moves);
        Assert.Contains(new Cell(1, 2), moves);
    }
}
