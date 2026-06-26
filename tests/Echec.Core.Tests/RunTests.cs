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
    public void EnemyWave_FollowsFixedCountSchedule()
    {
        // Effectifs par combat non-boss : 2, 3, 3, 4, 4.
        var expected = new[] { 2, 3, 3, 4, 4 };

        var run = new Run(seed: 1);
        for (var combat = 1; combat <= 5; combat++)
        {
            Assert.Equal(combat, run.CombatNumber);
            Assert.Equal(expected[combat - 1], run.BuildEnemyWave().Count);

            if (combat < 5)
            {
                run.StartBattle();
                run.CompleteCombat(Enumerable.Empty<UnitSpec>(), DefeatedWave(2));
                run.Recruit(run.Draft[0]);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1000)]
    [InlineData(123456)]
    public void EnemyWave_NeverHasThreeOfTheSameType(int seed)
    {
        // Sur tous les combats (boss inclus) et les deux rythmes de campagne, aucune vague ne contient
        // 3 exemplaires d'un même type (le boss, pièce unique essentielle, est exclu du décompte).
        foreach (var firstRun in new[] { true, false })
        {
            var run = new Run(seed: seed, firstRun: firstRun);
            for (var combat = 1; combat <= Run.TotalCombats; combat++)
            {
                var maxSame = run.BuildEnemyWave()
                    .Where(u => !u.Essential)
                    .GroupBy(u => u.Domaine)
                    .Select(g => g.Count())
                    .DefaultIfEmpty(0)
                    .Max();
                Assert.True(maxSame <= 2,
                    $"seed {seed}, firstRun {firstRun}, combat {combat} : {maxSame} fois le même type");

                if (combat < Run.TotalCombats)
                {
                    run.StartBattle();
                    run.CompleteCombat(Enumerable.Empty<UnitSpec>(), DefeatedWave(2));
                    run.Recruit(run.Draft[0]);
                }
            }
        }
    }

    // Ordre attendu des déblocages : soldat, lancier, cavalier, archer, mage.
    private static readonly Domaine[] IntroOrder =
        { Domaine.Pion, Domaine.Tour, Domaine.Cavalier, Domaine.Dame, Domaine.Fou };

    [Fact]
    public void FirstRun_Combat1_IsOnlySoldiers()
    {
        var run = new Run(seed: 1, firstRun: true);
        var wave = run.BuildEnemyWave();

        Assert.All(wave, u => Assert.Equal(Domaine.Pion, u.Domaine)); // 1re campagne : soldat seul
    }

    [Fact]
    public void FirstRun_UnlocksOneTypePerCombat_FromSoldier()
    {
        // 1re campagne : combat N débloque les N premiers types (tout débloqué au combat 5).
        AssertUnlockSchedule(firstRun: true, combat => combat);
    }

    [Fact]
    public void LaterRun_StartsWithSoldierAndLancier_AllUnlockedByCombat4()
    {
        // Campagnes suivantes : +1 type d'avance → combat N débloque N+1 types, tout au combat 4.
        AssertUnlockSchedule(firstRun: false, combat => combat + 1);

        // Combat 1 d'une campagne suivante : déjà soldat + lancier disponibles.
        var run = new Run(seed: 1, firstRun: false);
        var pool = run.BuildEnemyWave().Select(u => u.Domaine).ToHashSet();
        Assert.Subset(new[] { Domaine.Pion, Domaine.Tour }.ToHashSet(), pool);
    }

    // Vérifie sur les combats 1..5 que seuls les types attendus apparaissent et que le type
    // fraîchement débloqué CE combat est garanti — selon le rythme donné (1re campagne ou suivante).
    private static void AssertUnlockSchedule(bool firstRun, System.Func<int, int> reachOf)
    {
        var run = new Run(seed: 1, firstRun: firstRun);
        for (var combat = 1; combat <= 5; combat++)
        {
            var reach = System.Math.Min(reachOf(combat), IntroOrder.Length);
            var wave = run.BuildEnemyWave();
            var unlocked = IntroOrder.Take(reach).ToHashSet();

            // Aucun type encore verrouillé n'apparaît.
            Assert.All(wave, u => Assert.Contains(u.Domaine, unlocked));
            // Le type fraîchement débloqué ce combat est garanti (tant que tout n'est pas déjà ouvert).
            if (reachOf(combat) <= IntroOrder.Length)
                Assert.Contains(wave, u => u.Domaine == IntroOrder[reach - 1]);

            if (combat < 5)
            {
                run.StartBattle();
                run.CompleteCombat(Enumerable.Empty<UnitSpec>(), DefeatedWave(2));
                run.Recruit(run.Draft[0]);
            }
        }
    }

    [Fact]
    public void RunSave_PreservesFirstRunFlag()
    {
        var first = RunSave.From(new Run(seed: 1, firstRun: true)).ToRun();
        var later = RunSave.From(new Run(seed: 1, firstRun: false)).ToRun();

        Assert.True(first.FirstRun);
        Assert.False(later.FirstRun);
    }

    [Fact]
    public void BossWave_IsBossPlusFourEscorts()
    {
        var run = new Run(seed: 1);
        AdvanceTo(run, 6);

        var wave = run.BuildEnemyWave();
        Assert.Equal(5, wave.Count);                       // boss + 4 escortes
        Assert.Single(wave, u => u.Essential);
        Assert.Equal(4, wave.Count(u => !u.Essential));
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
