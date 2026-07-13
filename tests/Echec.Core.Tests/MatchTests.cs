using Echec.Core.Battle;
using Echec.Core.Campaign;
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
        match.Place(playerCell, Units.Soldat(Faction.Player));
        match.Place(enemyCell, Units.Soldat(Faction.Enemy));
        return match;
    }

    [Fact]
    public void MountedArcher_MovesInL_ButAttacksInLines()
    {
        // Cavalier monté (archer) : DÉPLACEMENT en L (domaine Cavalier), mais TIR en lignes (attaque = Dame).
        var cls = new UnitClass("MA", "ma", tier: 1, maxHp: 12, damage: 10,
            moveRange: 2, attackRange: 3, attackDomaine: Domaine.Dame);
        var match = new Match(8, 8);
        var from = new Cell(4, 4);
        match.Place(from, new Unit(Domaine.Cavalier, Faction.Player, cls));

        var moves = match.LegalMoves(from);
        Assert.Contains(new Cell(6, 5), moves);         // saut en L (décalage 2,1)
        Assert.DoesNotContain(new Cell(4, 6), moves);   // une ligne droite n'est PAS un déplacement

        match.Place(new Cell(4, 6), Units.Soldat(Faction.Enemy));   // ennemi en ligne droite (distance 2)
        match.Place(new Cell(6, 5), Units.Soldat(Faction.Enemy));   // ennemi en position de L
        var targets = match.AttackTargets(from);
        Assert.Contains(new Cell(4, 6), targets);         // tire en LIGNE
        Assert.DoesNotContain(new Cell(6, 5), targets);   // ne tire PAS en L
    }

    [Fact]
    public void LegalMoves_Player_CannotLandOnPlayerBlockedCell()
    {
        // Mission « protéger » : le joueur ne peut pas s'arrêter sur une case paysan.
        var blocked = new Cell(4, 3);
        var match = new Match(8, 8, playerBlockedCells: new[] { blocked });
        var pCell = new Cell(4, 4);
        match.Place(pCell, Units.Soldat(Faction.Player));

        Assert.DoesNotContain(blocked, match.LegalMoves(pCell));
    }

    [Fact]
    public void LegalMoves_Enemy_CanLandOnPlayerBlockedCell()
    {
        // La même case reste accessible aux ENNEMIS (ils vont capturer le paysan).
        var blocked = new Cell(4, 3);
        var match = new Match(8, 8, playerBlockedCells: new[] { blocked });
        var eCell = new Cell(4, 4);
        match.Place(eCell, Units.Soldat(Faction.Enemy));
        match.PassTurn();   // → tour ennemi

        Assert.Contains(blocked, match.LegalMoves(eCell));
    }

    [Fact]
    public void UnblockPlayerCell_MakesPaysanCellLandableAgain()
    {
        // Paysan CAPTURÉ (mission « protéger ») : sa case redevient accessible au joueur.
        var paysan = new Cell(4, 3);
        var match = new Match(8, 8, playerBlockedCells: new[] { paysan });
        var pCell = new Cell(4, 4);
        match.Place(pCell, Units.Soldat(Faction.Player));
        Assert.DoesNotContain(paysan, match.LegalMoves(pCell));   // avant : interdit

        match.UnblockPlayerCell(paysan);
        Assert.Contains(paysan, match.LegalMoves(pCell));         // après capture : accessible
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
        Assert.Equal(2, enemy.Hp);                    // 12 - 10
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
    public void MeleeKill_IncrementsAttackerKillCount()
    {
        var match = TwoUnitMatch(out var playerCell, out var enemyCell);
        var attacker = match.UnitAt(playerCell)!;   // référence conservée : après la mise à mort il prend la place
        var enemy = match.UnitAt(enemyCell)!;
        enemy.TakeDamage(enemy.Hp - 1);
        Assert.Equal(0, attacker.Kills);

        match.TryAttack(playerCell, enemyCell);

        Assert.Equal(1, attacker.Kills);
    }

    [Fact]
    public void NonLethalAttack_DoesNotCountAsKill()
    {
        // Un soldat ne tue pas un soldat à PV pleins d'un coup : la cible survit, aucun kill crédité.
        var match = TwoUnitMatch(out var playerCell, out var enemyCell);
        var attacker = match.UnitAt(playerCell)!;

        match.TryAttack(playerCell, enemyCell);

        Assert.True(match.UnitAt(enemyCell)!.IsAlive);
        Assert.Equal(0, attacker.Kills);
    }

    [Fact]
    public void Spawn_SeedsUnitKillsFromSpec()
    {
        // Le total à vie du gabarit est repris sur l'unité spawnée (affiché tel quel dès le combat suivant).
        var spec = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass) { Kills = 5 };
        Assert.Equal(5, spec.Spawn(Faction.Player).Kills);
    }

    [Fact]
    public void LancierKill_TakesPlace_WhenPathClearAndInMoveRange()
    {
        var match = new Match(8, 8);
        var tourCell = new Cell(3, 7);
        var enemyCell = new Cell(3, 5); // 2 cases en ligne : à portée de tir ET de déplacement (2)
        match.Place(tourCell, Units.Of(Domaine.Tour, Faction.Player));
        var enemy = Units.Soldat(Faction.Enemy);
        enemy.TakeDamage(enemy.Hp - 1);
        match.Place(enemyCell, enemy);

        var kind = match.TryAttack(tourCell, enemyCell);

        Assert.Equal(MoveKind.Killed, kind);
        Assert.Null(match.UnitAt(tourCell));                            // ligne dégagée : prend la place
        Assert.Equal(Faction.Player, match.UnitAt(enemyCell)!.Faction);
    }

    [Fact]
    public void RangedKill_StaysInPlace_WhenTargetBeyondMoveRange()
    {
        var match = new Match(8, 8);
        var mageCell = new Cell(4, 7);
        var enemyCell = new Cell(1, 4); // 3 en diagonale : à portée de TIR (3) mais hors DÉPLACEMENT (1)
        match.Place(mageCell, Units.Of(Domaine.Fou, Faction.Player)); // Mage : tir 3, déplacement 1, diagonales
        var enemy = Units.Soldat(Faction.Enemy);
        enemy.TakeDamage(enemy.Hp - 1);
        match.Place(enemyCell, enemy);

        var kind = match.TryAttack(mageCell, enemyCell);

        Assert.Equal(MoveKind.Killed, kind);
        Assert.NotNull(match.UnitAt(mageCell));    // hors de portée de déplacement : reste sur place
        Assert.Null(match.UnitAt(enemyCell));      // la case libérée reste vide
    }

    [Fact]
    public void LancierKill_StaysInPlace_WhenAllyBlocksMovePath()
    {
        var match = new Match(8, 8);
        var tourCell = new Cell(3, 7);
        match.Place(tourCell, Units.Of(Domaine.Tour, Faction.Player)); // tir 3, traverse ses alliés
        match.Place(new Cell(3, 6), Units.Soldat(Faction.Player));       // allié sur la ligne
        var enemy = Units.Soldat(Faction.Enemy);
        enemy.TakeDamage(enemy.Hp - 1);
        match.Place(new Cell(3, 5), enemy);                            // ennemi DERRIÈRE l'allié

        var kind = match.TryAttack(tourCell, new Cell(3, 5));          // tire à travers l'allié, tue

        Assert.Equal(MoveKind.Killed, kind);
        Assert.NotNull(match.UnitAt(tourCell));   // le déplacement, lui, est bloqué par l'allié → reste
        Assert.Null(match.UnitAt(new Cell(3, 5))); // case libérée, inoccupée
    }

    [Fact]
    public void RangedAttack_BlockedByAlly_ForNonLancier()
    {
        var match = new Match(8, 8);
        var archerCell = new Cell(3, 7);
        // Archer (évolution du Soldat) : tir 2, ne traverse PAS ses alliés.
        var archer = new Unit(Domaine.Dame, Faction.Player, Domaines.Dame.BaseClass.Evolutions[0]);
        match.Place(archerCell, archer);
        match.Place(new Cell(3, 6), Units.Soldat(Faction.Player));  // allié qui bloque la ligne
        match.Place(new Cell(3, 5), Units.Soldat(Faction.Enemy));   // ennemi (distance 2) DERRIÈRE l'allié

        Assert.DoesNotContain(new Cell(3, 5), match.AttackTargets(archerCell));
    }

    [Fact]
    public void Lancier_PiercesAlly_TargetsEnemyBehind()
    {
        var match = new Match(8, 8);
        var lancier = new Cell(3, 7);
        match.Place(lancier, Units.Of(Domaine.Tour, Faction.Player)); // lancier : traverse ses alliés
        match.Place(new Cell(3, 6), Units.Soldat(Faction.Player));      // allié sur la ligne de tir
        match.Place(new Cell(3, 5), Units.Soldat(Faction.Enemy));       // ennemi DERRIÈRE l'allié

        var targets = match.AttackTargets(lancier);
        Assert.Contains(new Cell(3, 5), targets);       // traverse l'allié sans le toucher, vise l'ennemi
        Assert.DoesNotContain(new Cell(3, 6), targets); // l'allié n'est jamais une cible
    }

    [Fact]
    public void Lancier_EnemyStillBlocksLine_NoTargetBehindEnemy()
    {
        var match = new Match(8, 8);
        var lancier = new Cell(3, 7);
        match.Place(lancier, Units.Of(Domaine.Tour, Faction.Player)); // tir 3
        match.Place(new Cell(3, 6), Units.Soldat(Faction.Enemy));       // ennemi 1 (borne la ligne)
        match.Place(new Cell(3, 5), Units.Soldat(Faction.Enemy));       // ennemi 2 derrière

        var targets = match.AttackTargets(lancier);
        Assert.Contains(new Cell(3, 6), targets);       // premier ennemi : ciblé
        Assert.DoesNotContain(new Cell(3, 5), targets); // un ennemi borne la ligne (pas de tir au-delà)
    }

    [Fact]
    public void Lancier_Threat_PassesThroughAlly_ButNotEnemy()
    {
        var match = new Match(8, 8);
        var lancier = new Cell(3, 7);
        match.Place(lancier, Units.Of(Domaine.Tour, Faction.Enemy)); // lancier ennemi
        match.Place(new Cell(3, 6), Units.Soldat(Faction.Enemy));      // allié du lancier sur la ligne
        match.Place(new Cell(3, 5), Units.Soldat(Faction.Player));     // joueur derrière

        var threat = match.ThreatenedCells(lancier);
        Assert.DoesNotContain(new Cell(3, 6), threat); // traverse l'allié sans le menacer
        Assert.Contains(new Cell(3, 5), threat);       // menace bien le joueur derrière
    }

    [Fact]
    public void AttackRange_LimitsReach()
    {
        var match = new Match(8, 8);
        var tourCell = new Cell(3, 7);
        match.Place(tourCell, Units.Of(Domaine.Tour, Faction.Player)); // portée tir 3
        match.Place(new Cell(3, 3), Units.Soldat(Faction.Enemy));        // distance 4

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
    public void ThreatenedCells_ComputedRegardlessOfTurn_EvenForEnemy()
    {
        var match = TwoUnitMatch(out var playerCell, out var enemyCell);

        // C'est le tour du joueur : l'ennemi n'a aucune action jouable...
        Assert.Empty(match.AttackTargets(enemyCell));

        // ...mais sa zone de MENACE reste calculable (8 voisins du pion ennemi centré),
        // et elle inclut bien le joueur adjacent.
        var threat = match.ThreatenedCells(enemyCell);
        Assert.Equal(8, threat.Count);
        Assert.Contains(playerCell, threat);
    }

    [Fact]
    public void ThreatenedCells_StopAtFirstBlocker_WhichIsIncluded()
    {
        var match = new Match(8, 8);
        var tour = new Cell(3, 7);
        match.Place(tour, Units.Of(Domaine.Tour, Faction.Enemy)); // tir 3, glisse en croix
        var blocker = new Cell(3, 5);                             // 2 cases au nord
        match.Place(blocker, Units.Soldat(Faction.Player));

        var threat = match.ThreatenedCells(tour);
        Assert.Contains(new Cell(3, 6), threat);       // case vide avant le bloqueur : menacée
        Assert.Contains(blocker, threat);              // le bloqueur subirait l'attaque : menacé
        Assert.DoesNotContain(new Cell(3, 4), threat); // au-delà du bloqueur : hors de portée
    }

    [Fact]
    public void EnemyAi_PrefersLethalAttack()
    {
        var match = new Match(8, 8);
        var weak = new Cell(4, 4);
        var enemy = new Cell(4, 3);
        var throwaway = new Cell(0, 0);
        match.Place(weak, Units.Soldat(Faction.Player));
        match.UnitAt(weak)!.TakeDamage(match.UnitAt(weak)!.Hp - 1);
        match.Place(enemy, Units.Soldat(Faction.Enemy));
        match.Place(throwaway, Units.Soldat(Faction.Player));

        match.TryMove(throwaway, new Cell(1, 1)); // passe la main à l'IA
        Assert.Equal(Faction.Enemy, match.CurrentTurn);

        var action = EnemyAi.ChooseAction(match);

        Assert.NotNull(action);
        Assert.True(action!.Value.IsAttack);
        Assert.Equal(weak, action.Value.To);
    }
}
