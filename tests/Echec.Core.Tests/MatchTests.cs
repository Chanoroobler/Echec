using Echec.Core.Battle;
using Echec.Core.Map;
using Xunit;

namespace Echec.Core.Tests;

public class MatchTests
{
    private static Match TwoUnitMatch(out Cell playerCell, out Cell enemyCell)
    {
        var match = new Match(8, 8);
        playerCell = new Cell(4, 4);
        enemyCell = new Cell(4, 3); // adjacente
        match.Place(playerCell, Units.Pion(Faction.Player));
        match.Place(enemyCell, Units.Pion(Faction.Enemy));
        return match;
    }

    [Fact]
    public void LegalMoves_OnlyEmptyCells_OccupiedCellsBlock()
    {
        var match = TwoUnitMatch(out var playerCell, out var enemyCell);

        var moves = match.LegalMoves(playerCell);
        Assert.DoesNotContain(enemyCell, moves);          // une case occupée n'est pas un déplacement
        Assert.Equal(7, moves.Count);                     // 8 voisins - 1 occupé
    }

    [Fact]
    public void AttackTargets_IncludesAdjacentEnemy_ForMeleePion()
    {
        var match = TwoUnitMatch(out var playerCell, out var enemyCell);

        Assert.Equal(new[] { enemyCell }, match.AttackTargets(playerCell));
    }

    [Fact]
    public void MoveToEmpty_MovesUnit_AndPassesTurn()
    {
        var match = TwoUnitMatch(out var playerCell, out _);
        var destination = new Cell(3, 5);

        var kind = match.TryMove(playerCell, destination);

        Assert.Equal(MoveKind.Moved, kind);
        Assert.Null(match.UnitAt(playerCell));
        Assert.NotNull(match.UnitAt(destination));
        Assert.Equal(Faction.Enemy, match.CurrentTurn);
    }

    [Fact]
    public void MeleeAttack_NonLethal_DealsDamage_AttackerStays_TurnPasses()
    {
        var match = TwoUnitMatch(out var playerCell, out var enemyCell);
        var enemy = match.UnitAt(enemyCell)!;

        var kind = match.TryAttack(playerCell, enemyCell);

        Assert.Equal(MoveKind.Attacked, kind);
        Assert.Equal(6, enemy.Hp);                    // 10 - 4
        Assert.NotNull(match.UnitAt(playerCell));     // resté sur place (avant le kill)
        Assert.Equal(Faction.Enemy, match.CurrentTurn);
    }

    [Fact]
    public void MeleeKill_AttackerTakesPlace_AndWins()
    {
        var match = TwoUnitMatch(out var playerCell, out var enemyCell);
        var enemy = match.UnitAt(enemyCell)!;
        enemy.TakeDamage(enemy.Hp - 1);

        var kind = match.TryAttack(playerCell, enemyCell);

        Assert.Equal(MoveKind.Killed, kind);
        Assert.Null(match.UnitAt(playerCell));                          // mêlée : prend la place
        Assert.Equal(Faction.Player, match.UnitAt(enemyCell)!.Faction);
        Assert.True(match.IsOver);
        Assert.Equal(Faction.Player, match.Winner);
    }

    [Fact]
    public void RangedAttack_HitsAtDistance_AndStaysInPlaceOnKill()
    {
        var match = new Match(8, 8);
        var tourCell = new Cell(3, 7);
        var enemyCell = new Cell(3, 4); // 3 cases en ligne, à portée de tir (3)
        match.Place(tourCell, Units.Of(Domaine.Tour, Faction.Player));
        var enemy = Units.Pion(Faction.Enemy);
        enemy.TakeDamage(enemy.Hp - 1);
        match.Place(enemyCell, enemy);

        Assert.Contains(enemyCell, match.AttackTargets(tourCell));

        var kind = match.TryAttack(tourCell, enemyCell);

        Assert.Equal(MoveKind.Killed, kind);
        Assert.NotNull(match.UnitAt(tourCell));   // tir à distance : reste sur place
        Assert.Null(match.UnitAt(enemyCell));     // la case libérée reste vide
    }

    [Fact]
    public void RangedAttack_BlockedByUnitInLine()
    {
        var match = new Match(8, 8);
        var tourCell = new Cell(3, 7);
        match.Place(tourCell, Units.Of(Domaine.Tour, Faction.Player));
        match.Place(new Cell(3, 6), Units.Pion(Faction.Player));  // allié qui bloque la ligne
        match.Place(new Cell(3, 4), Units.Pion(Faction.Enemy));

        Assert.DoesNotContain(new Cell(3, 4), match.AttackTargets(tourCell));
    }

    [Fact]
    public void AttackRange_LimitsReach()
    {
        var match = new Match(8, 8);
        var tourCell = new Cell(3, 7);
        match.Place(tourCell, Units.Of(Domaine.Tour, Faction.Player)); // portée tir 3
        match.Place(new Cell(3, 3), Units.Pion(Faction.Enemy));        // distance 4

        Assert.Empty(match.AttackTargets(tourCell));
    }

    [Fact]
    public void EnemyUnit_HasNoActions_DuringPlayerTurn()
    {
        var match = TwoUnitMatch(out _, out var enemyCell);

        Assert.Empty(match.LegalMoves(enemyCell));
        Assert.Empty(match.AttackTargets(enemyCell));
    }

    [Fact]
    public void EnemyAi_PrefersLethalAttack()
    {
        var match = new Match(8, 8);
        var weak = new Cell(4, 4);
        var enemy = new Cell(4, 3);
        var throwaway = new Cell(0, 0);
        match.Place(weak, Units.Pion(Faction.Player));
        match.UnitAt(weak)!.TakeDamage(match.UnitAt(weak)!.Hp - 1);
        match.Place(enemy, Units.Pion(Faction.Enemy));
        match.Place(throwaway, Units.Pion(Faction.Player));

        match.TryMove(throwaway, new Cell(1, 1)); // passe la main à l'IA
        Assert.Equal(Faction.Enemy, match.CurrentTurn);

        var action = EnemyAi.ChooseAction(match);

        Assert.NotNull(action);
        Assert.True(action!.Value.IsAttack);
        Assert.Equal(weak, action.Value.To);
    }
}
