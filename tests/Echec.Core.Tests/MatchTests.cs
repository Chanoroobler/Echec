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
        enemyCell = new Cell(4, 3); // adjacente, au-dessus
        match.Place(playerCell, Units.Soldier(Faction.Player));
        match.Place(enemyCell, Units.Soldier(Faction.Enemy));
        return match;
    }

    [Fact]
    public void Soldier_InCenter_HasEightLegalMoves()
    {
        var match = new Match(8, 8);
        var center = new Cell(4, 4);
        match.Place(center, Units.Soldier(Faction.Player));

        Assert.Equal(8, match.LegalMoves(center).Count);
    }

    [Fact]
    public void Soldier_InCorner_HasThreeLegalMoves()
    {
        var match = new Match(8, 8);
        var corner = new Cell(0, 0);
        match.Place(corner, Units.Soldier(Faction.Player));

        Assert.Equal(3, match.LegalMoves(corner).Count);
    }

    [Fact]
    public void LegalMoves_ExcludesFriendlyButIncludesEnemy()
    {
        var match = new Match(8, 8);
        var from = new Cell(4, 4);
        match.Place(from, Units.Soldier(Faction.Player));
        match.Place(new Cell(4, 3), Units.Soldier(Faction.Player)); // ami
        match.Place(new Cell(5, 4), Units.Soldier(Faction.Enemy));  // ennemi

        var moves = match.LegalMoves(from);
        Assert.DoesNotContain(new Cell(4, 3), moves); // ami exclu
        Assert.Contains(new Cell(5, 4), moves);       // ennemi = attaque possible
        Assert.Equal(7, moves.Count);
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
    public void AttackNonLethal_DealsDamage_AttackerStays_TurnPasses()
    {
        var match = TwoUnitMatch(out var playerCell, out var enemyCell);
        var enemy = match.UnitAt(enemyCell)!;

        var kind = match.TryMove(playerCell, enemyCell);

        Assert.Equal(MoveKind.Attacked, kind);
        Assert.Equal(6, enemy.Hp);                    // 10 - 4
        Assert.NotNull(match.UnitAt(playerCell));     // attaquant resté sur place
        Assert.Same(enemy, match.UnitAt(enemyCell));
        Assert.Equal(Faction.Enemy, match.CurrentTurn);
    }

    [Fact]
    public void LethalAttack_KillsTarget_AttackerTakesPlace_AndWins()
    {
        var match = TwoUnitMatch(out var playerCell, out var enemyCell);
        var enemy = match.UnitAt(enemyCell)!;
        enemy.TakeDamage(enemy.Hp - 1); // 1 PV : le prochain coup tue

        var kind = match.TryMove(playerCell, enemyCell);

        Assert.Equal(MoveKind.Killed, kind);
        Assert.Null(match.UnitAt(playerCell));        // l'attaquant a bougé
        Assert.Equal(Faction.Player, match.UnitAt(enemyCell)!.Faction); // il prend la place
        Assert.True(match.IsOver);
        Assert.Equal(Faction.Player, match.Winner);
    }

    [Fact]
    public void EnemyUnit_HasNoLegalMoves_DuringPlayerTurn()
    {
        var match = TwoUnitMatch(out _, out var enemyCell);

        Assert.Empty(match.LegalMoves(enemyCell)); // pas le tour de l'ennemi
    }

    [Fact]
    public void EnemyAi_PrefersLethalAttack()
    {
        var match = new Match(8, 8);
        var weak = new Cell(4, 4);
        var enemy = new Cell(4, 3);
        var throwaway = new Cell(0, 0);
        match.Place(weak, Units.Soldier(Faction.Player));
        match.UnitAt(weak)!.TakeDamage(match.UnitAt(weak)!.Hp - 1); // tuable en un coup
        match.Place(enemy, Units.Soldier(Faction.Enemy));
        match.Place(throwaway, Units.Soldier(Faction.Player));

        // Le joueur joue un coup neutre pour passer la main à l'IA.
        match.TryMove(throwaway, new Cell(1, 1));
        Assert.Equal(Faction.Enemy, match.CurrentTurn);

        var move = EnemyAi.ChooseMove(match);

        Assert.NotNull(move);
        Assert.Equal(weak, move!.Value.To); // l'IA vise la cible mortelle
    }
}
