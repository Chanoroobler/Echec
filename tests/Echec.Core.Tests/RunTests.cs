using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Campaign;
using Echec.Core.Map;
using Xunit;

namespace Echec.Core.Tests;

public class RunTests
{
    [Fact]
    public void NewRun_StartsWithCommanderAndTwoSoldiers_InPlacement()
    {
        var run = new Run(seed: 1);

        Assert.Equal(3, run.Roster.Count);
        Assert.Single(run.Roster, u => u.Essential);
        Assert.Equal(2, run.Roster.Count(u => u.UnitClass == Domaines.Pion.BaseClass));
        Assert.Equal(1, run.CombatNumber);
        Assert.Equal(RunPhase.Placement, run.Phase);
    }

    [Fact]
    public void EnemyWave_GrowsByOnePerCombat()
    {
        var run = new Run(seed: 1);
        Assert.Equal(2, run.BuildEnemyWave().Count); // combat 1

        // Avance jusqu'au combat 2 sans pertes.
        run.StartBattle();
        run.CompleteCombat(Enumerable.Empty<UnitSpec>(), DefeatedWave(2));
        run.Recruit(run.Draft[0]);

        Assert.Equal(2, run.CombatNumber);
        Assert.Equal(3, run.BuildEnemyWave().Count); // combat 2 = 3 ennemis
    }

    [Fact]
    public void BossCombat_IsTheSixth_AndContainsAnEssentialBoss()
    {
        var run = new Run(seed: 1);
        AdvanceTo(run, 6);

        Assert.True(run.IsBossCombat);
        var wave = run.BuildEnemyWave();
        Assert.Single(wave, u => u.Essential);
        Assert.Equal(Commandes.Boss.BaseClass, wave.First(u => u.Essential).UnitClass);
    }

    [Fact]
    public void CompleteCombat_RemovesCasualties_KeepsCommanderAndSurvivors()
    {
        var run = new Run(seed: 1);
        var soldier = run.Roster.First(u => !u.Essential);

        run.StartBattle();
        run.CompleteCombat(new[] { soldier }, DefeatedWave(2)); // un soldat tombe

        Assert.DoesNotContain(soldier, run.Roster);
        Assert.Equal(2, run.Roster.Count);            // commandant + 1 soldat
        Assert.Single(run.Roster, u => u.Essential);  // commandant intact
    }

    [Fact]
    public void Recruit_AddsUnit_AndAdvancesToNextPlacement()
    {
        var run = new Run(seed: 1);
        run.StartBattle();
        run.CompleteCombat(Enumerable.Empty<UnitSpec>(), DefeatedWave(2));
        Assert.Equal(RunPhase.Recruitment, run.Phase);

        var choice = run.Draft[1];
        run.Recruit(choice);

        Assert.Equal(4, run.Roster.Count);
        Assert.Equal(choice.UnitClass, run.Roster[^1].UnitClass);
        Assert.Equal(2, run.CombatNumber);
        Assert.Equal(RunPhase.Placement, run.Phase);
    }

    [Fact]
    public void Draft_OffersLastThreeDefeated_InKillOrder()
    {
        var run = new Run(seed: 1);
        run.StartBattle();

        // 5 ennemis vaincus (instances distinctes) dans l'ordre de leur mort.
        var defeated = new[]
        {
            new UnitSpec(Domaine.Pion, Domaines.Pion.BaseClass),
            new UnitSpec(Domaine.Tour, Domaines.Tour.BaseClass),
            new UnitSpec(Domaine.Pion, Domaines.Pion.BaseClass),
            new UnitSpec(Domaine.Tour, Domaines.Tour.BaseClass),
            new UnitSpec(Domaine.Pion, Domaines.Pion.BaseClass),
        };
        run.CompleteCombat(Enumerable.Empty<UnitSpec>(), defeated);

        Assert.Equal(3, run.Draft.Count);
        Assert.Same(defeated[2], run.Draft[0]); // les 3 DERNIERS, dans l'ordre
        Assert.Same(defeated[3], run.Draft[1]);
        Assert.Same(defeated[4], run.Draft[2]);
    }

    [Fact]
    public void Draft_HasFewerCards_WhenFewerEnemiesDefeated()
    {
        var run = new Run(seed: 1);
        run.StartBattle();
        run.CompleteCombat(Enumerable.Empty<UnitSpec>(), DefeatedWave(2));

        Assert.Equal(2, run.Draft.Count); // moins d'ennemis → moins de cartes
    }

    [Fact]
    public void WinningBossCombat_EndsInVictory()
    {
        var run = new Run(seed: 1);
        AdvanceTo(run, 6);

        run.StartBattle();
        run.CompleteCombat(Enumerable.Empty<UnitSpec>(), DefeatedWave(0));

        Assert.Equal(RunPhase.Victory, run.Phase);
    }

    // Fait avancer la campagne jusqu'au combat voulu (victoires sans perte + recrutement).
    private static void AdvanceTo(Run run, int combat)
    {
        while (run.CombatNumber < combat)
        {
            run.StartBattle();
            run.CompleteCombat(Enumerable.Empty<UnitSpec>(), DefeatedWave(2));
            run.Recruit(run.Draft[0]);
        }
    }

    // Faux groupe d'ennemis vaincus (n soldats) pour alimenter le recrutement dans les tests.
    private static UnitSpec[] DefeatedWave(int n)
    {
        var wave = new UnitSpec[n];
        for (var i = 0; i < n; i++)
            wave[i] = new UnitSpec(Domaine.Pion, Domaines.Pion.BaseClass);
        return wave;
    }
}

public class EssentialUnitTests
{
    [Fact]
    public void CommanderDeath_LosesEvenWithOtherUnitsAlive()
    {
        var match = new Match(8, 8);
        var commanderCell = new Cell(4, 6);
        var commander = new UnitSpec(Commandes.Commander.Movement, Commandes.Commander.BaseClass, essential: true)
            .Spawn(Faction.Player);
        commander.TakeDamage(commander.Hp - 1); // à 1 PV
        match.Place(commanderCell, commander);
        match.Place(new Cell(0, 7), Units.Pion(Faction.Player)); // un autre allié bien vivant

        var enemyCell = new Cell(4, 5);
        match.Place(enemyCell, Units.Pion(Faction.Enemy)); // adjacent au commandant

        // Tour ennemi : passe la main via un déplacement du soldat.
        match.TryMove(new Cell(0, 7), new Cell(1, 6));
        Assert.Equal(Faction.Enemy, match.CurrentTurn);

        match.TryAttack(enemyCell, commanderCell); // tue le commandant

        Assert.True(match.IsOver);
        Assert.Equal(Faction.Enemy, match.Winner);
    }

    [Fact]
    public void BossDeath_WinsEvenWithOtherEnemiesAlive()
    {
        var match = new Match(8, 8);
        var playerCell = new Cell(4, 5);
        match.Place(playerCell, Units.Pion(Faction.Player));

        var bossCell = new Cell(4, 4);
        var boss = new UnitSpec(Commandes.Boss.Movement, Commandes.Boss.BaseClass, essential: true)
            .Spawn(Faction.Enemy);
        boss.TakeDamage(boss.Hp - 1); // à 1 PV
        match.Place(bossCell, boss);
        match.Place(new Cell(0, 0), Units.Pion(Faction.Enemy)); // sbire bien vivant

        match.TryAttack(playerCell, bossCell); // tue le boss

        Assert.True(match.IsOver);
        Assert.Equal(Faction.Player, match.Winner);
    }
}
