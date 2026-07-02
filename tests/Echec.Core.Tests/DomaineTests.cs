using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Map;
using Xunit;

namespace Echec.Core.Tests;

public class DomaineTests
{
    [Fact]
    public void FourDomaines_NamedAfterChessPieces()
    {
        Assert.Equal(
            new[] { "Dame", "Fou", "Cavalier", "Tour" },
            Domaines.All.Select(d => d.Name));
    }

    [Theory]
    [InlineData(Domaine.Dame, 8, MovementKind.Slide)]   // 8 directions (base des unités de troupe)
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
        var soldat = Domaines.Dame.BaseClass;

        Assert.Equal("Soldat", soldat.Name);
        Assert.Equal("soldat", soldat.Asset);
        Assert.Equal(12, soldat.MaxHp);
        Assert.Equal(10, soldat.Damage);
        Assert.Equal(1, soldat.MoveRange);
    }

    [Fact]
    public void Dame_KingStep_Range1_HasEightMovesFromCenter()
    {
        var match = new Match(8, 8);
        var from = new Cell(4, 4);
        match.Place(from, Units.Of(Domaine.Dame, Faction.Player)); // Soldat : 1 pas, 8 directions

        Assert.Equal(8, match.LegalMoves(from).Count);
    }

    [Fact]
    public void Fou_SlidesDiagonally_UpToClassRange()
    {
        var match = new Match(8, 8);
        var from = new Cell(3, 3);
        match.Place(from, Units.Of(Domaine.Fou, Faction.Player)); // Mage : déplacement 1

        var moves = match.LegalMoves(from);
        Assert.Contains(new Cell(2, 2), moves);  // 1 case en diagonale
        Assert.Contains(new Cell(4, 4), moves);
        Assert.DoesNotContain(new Cell(1, 1), moves); // 2 cases = hors portée (déplacement 1)
        Assert.DoesNotContain(new Cell(2, 3), moves); // pas de déplacement orthogonal
        Assert.Equal(4, moves.Count);            // 4 diagonales × 1 case
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
        foreach (var d in Movement.Vectors(Domaine.Dame))
            match.Place(new Cell(3 + d.Column, 3 + d.Row), Units.Of(Domaine.Dame, Faction.Player));

        var moves = match.LegalMoves(from);
        Assert.Equal(8, moves.Count);              // tous les sauts en L restent possibles
        Assert.Contains(new Cell(5, 4), moves);
        Assert.Contains(new Cell(1, 2), moves);
    }
}
