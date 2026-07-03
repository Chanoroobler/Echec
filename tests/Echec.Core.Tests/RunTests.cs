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
        Assert.Equal(2, run.Roster.Count(u => u.UnitClass == Domaines.Dame.BaseClass));
        Assert.Equal(1, run.CombatNumber);
        Assert.Equal(RunPhase.Placement, run.Phase);
    }

    // ─── Structure en 3 phases ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Constants_DescribeThreePhasesOfSixMissions()
    {
        Assert.Equal(3, Run.PhaseCount);
        Assert.Equal(6, Run.MissionsPerPhase);
        Assert.Equal(18, Run.TotalCombats);
    }

    [Fact]
    public void PhaseLayout_RepeatsFixedRhythmAcrossThreePhases()
    {
        // Rythme fixe d'une phase, répété 3 fois : Escarmouche ×2, Speciale, Escarmouche ×2, Boss.
        var slot = new[]
        {
            CombatType.Escarmouche, CombatType.Escarmouche, CombatType.Speciale,
            CombatType.Escarmouche, CombatType.Escarmouche, CombatType.Boss,
        };

        for (var combat = 1; combat <= Run.TotalCombats; combat++)
        {
            var run = RunAt(combat);
            Assert.Equal((combat - 1) / 6 + 1, run.PhaseIndex);
            Assert.Equal((combat - 1) % 6 + 1, run.MissionInPhase);
            Assert.Equal(slot[(combat - 1) % 6], run.CurrentMission);
        }
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(6, 1, 6)]
    [InlineData(7, 2, 1)]
    [InlineData(12, 2, 6)]
    [InlineData(13, 3, 1)]
    [InlineData(18, 3, 6)]
    public void CombatNumber_MapsTo_PhaseAndMission(int combat, int phase, int mission)
    {
        var run = RunAt(combat);
        Assert.Equal(phase, run.PhaseIndex);
        Assert.Equal(mission, run.MissionInPhase);
    }

    // ─── Composition des vagues (effectif + tiers) — source de vérité de la difficulté ───────────

    [Theory]
    // Phase 1 : apprentissage (T1, le T2 arrive à l'escarmouche 4). Démarrage adouci : 2 puis 3 pions.
    [InlineData(1, 2, 0, 0, 0)]
    [InlineData(2, 3, 0, 0, 0)]
    [InlineData(3, 4, 0, 0, 0)]   // spéciale phase 1 : 4 ennemis (temporaire)
    [InlineData(4, 5, 1, 0, 0)]
    [InlineData(5, 5, 2, 0, 0)]
    [InlineData(6, 7, 2, 0, 1)]  // boss + 7T1 + 2T2
    // Phase 2 : montée en puissance (T2 dominant).
    [InlineData(7, 4, 3, 0, 0)]
    [InlineData(8, 4, 4, 0, 0)]
    [InlineData(9, 3, 6, 0, 0)]
    [InlineData(10, 3, 6, 0, 0)]
    [InlineData(11, 2, 8, 0, 0)]
    [InlineData(12, 3, 7, 0, 1)]  // boss + 3T1 + 7T2
    // Phase 3 : fin de run (T2 → T3).
    [InlineData(13, 0, 5, 3, 0)]
    [InlineData(14, 0, 5, 4, 0)]
    [InlineData(15, 0, 4, 6, 0)]
    [InlineData(16, 0, 4, 6, 0)]
    [InlineData(17, 0, 3, 8, 0)]
    [InlineData(18, 0, 4, 8, 1)]  // boss final + 4T2 + 8T3
    public void BuildEnemyWave_HasExactHeadcountAndTierCounts(int combat, int t1, int t2, int t3, int bosses)
    {
        var wave = RunAt(combat).BuildEnemyWave();

        Assert.Equal(t1 + t2 + t3 + bosses, wave.Count);             // effectif total (escortes + boss)
        Assert.Equal(bosses, wave.Count(u => u.Essential));         // 0 ou 1 boss selon la mission

        var escorts = wave.Where(u => !u.Essential).ToList();       // le boss (essentiel) ne compte pas par tier
        Assert.Equal(t1, escorts.Count(u => u.UnitClass.Tier == 1));
        Assert.Equal(t2, escorts.Count(u => u.UnitClass.Tier == 2));
        Assert.Equal(t3, escorts.Count(u => u.UnitClass.Tier == 3));
    }

    [Fact]
    public void Boss_AppearsOnlyOnBossMissions_FinalOnlyInPhase3()
    {
        for (var combat = 1; combat <= Run.TotalCombats; combat++)
        {
            var run = RunAt(combat);
            var wave = run.BuildEnemyWave();
            var bosses = wave.Count(u => u.Essential);

            if (run.IsBossCombat)
            {
                Assert.Equal(1, bosses);
                Assert.Equal(Commandes.Boss.BaseClass, wave.Single(u => u.Essential).UnitClass);
            }
            else
            {
                Assert.Equal(0, bosses);
            }

            // Le boss FINAL n'existe qu'au tout dernier combat (phase 3, slot 6).
            Assert.Equal(combat == Run.TotalCombats, run.IsFinalBoss);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1000)]
    [InlineData(123456)]
    public void SameSeed_ProducesIdenticalWaves_PerMission(int seed)
    {
        for (var combat = 1; combat <= Run.TotalCombats; combat++)
        {
            var a = RunAt(combat, seed).BuildEnemyWave();
            var b = RunAt(combat, seed).BuildEnemyWave();

            Assert.Equal(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Domaine, b[i].Domaine);
                Assert.Same(a[i].UnitClass, b[i].UnitClass);   // même instance de classe (registre partagé)
            }
        }
    }

    [Fact]
    public void FirstRun_FirstCombat_HasOnlySoldiers()
    {
        // 1re campagne : pool le plus doux au combat 1 (soldat seul), comme avant la refonte.
        var wave = RunAt(1, seed: 1, firstRun: true).BuildEnemyWave();
        Assert.All(wave, u => Assert.Equal(Domaine.Dame, u.Domaine));
    }

    [Fact]
    public void BuildEnemyWave_PrioritizesDiscoveredUnits_AtTier2And3()
    {
        // Phase 3, mission 1 : 5× T2 + 3× T3. On ne "découvre" qu'une classe T2 et une classe T3 (Fou) ;
        // la vague ne doit alors contenir QUE ces classes aux tiers 2 et 3 (priorité maximale au découvert).
        var run = RunAt(13);
        var seenT2 = Run.ClassesAtTier(Domaine.Fou, 2)[0];   // Clerc
        var seenT3 = Run.ClassesAtTier(Domaine.Fou, 3)[0];   // Archevêque
        bool IsSeen(string asset) => asset == seenT2.Asset || asset == seenT3.Asset;

        var wave = run.BuildEnemyWave(IsSeen);

        Assert.Equal(5, wave.Count(u => u.UnitClass.Tier == 2));   // effectif/tiers toujours exacts
        Assert.Equal(3, wave.Count(u => u.UnitClass.Tier == 3));
        Assert.All(wave.Where(u => u.UnitClass.Tier == 2), u => Assert.Equal(seenT2.Asset, u.UnitClass.Asset));
        Assert.All(wave.Where(u => u.UnitClass.Tier == 3), u => Assert.Equal(seenT3.Asset, u.UnitClass.Asset));
    }

    [Fact]
    public void BuildEnemyWave_FallsBackToAll_WhenNothingDiscovered()
    {
        // Rien de découvert : on ne bloque pas la génération, l'effectif/les tiers restent exacts.
        var wave = RunAt(13).BuildEnemyWave(_ => false);
        Assert.Equal(8, wave.Count);   // phase 3 m1 : 5T2 + 3T3
        Assert.Equal(5, wave.Count(u => u.UnitClass.Tier == 2));
        Assert.Equal(3, wave.Count(u => u.UnitClass.Tier == 3));
    }

    [Fact]
    public void ClassesAtTier_ReturnsNonEmptySet_AllOfRequestedTier()
    {
        foreach (var domaine in new[] { Domaine.Dame, Domaine.Tour, Domaine.Cavalier, Domaine.Fou })
            for (var tier = 1; tier <= 3; tier++)
            {
                var classes = Run.ClassesAtTier(domaine, tier);
                Assert.NotEmpty(classes);
                Assert.All(classes, c => Assert.Equal(tier, c.Tier));
            }
    }

    // ─── Progression : victoire de run uniquement sur le boss final ──────────────────────────────

    [Theory]
    [InlineData(6)]   // boss de la phase 1
    [InlineData(12)]  // boss de la phase 2
    public void BossOfPhase1And2_ChainsToRecruitment(int combat)
    {
        var run = RunAt(combat);
        Assert.True(run.IsBossCombat);
        Assert.False(run.IsFinalBoss);

        run.StartBattle();
        run.CompleteCombat(Enumerable.Empty<UnitSpec>(), DefeatedWave(2));

        Assert.Equal(RunPhase.Recruitment, run.Phase);   // pas de fin de run : on recrute et on enchaîne
    }

    [Fact]
    public void FinalBoss_EndsRunInVictory()
    {
        var run = RunAt(Run.TotalCombats);
        Assert.True(run.IsFinalBoss);

        run.StartBattle();
        run.CompleteCombat(Enumerable.Empty<UnitSpec>(), DefeatedWave(0));

        Assert.Equal(RunPhase.Victory, run.Phase);
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
            new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass),
            new UnitSpec(Domaine.Tour, Domaines.Tour.BaseClass),
            new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass),
            new UnitSpec(Domaine.Tour, Domaines.Tour.BaseClass),
            new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass),
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

    // ─── Persistance ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RunSave_PreservesFirstRunFlag()
    {
        var first = RunSave.From(new Run(seed: 1, firstRun: true)).ToRun();
        var later = RunSave.From(new Run(seed: 1, firstRun: false)).ToRun();

        Assert.True(first.FirstRun);
        Assert.False(later.FirstRun);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(6, true)]
    [InlineData(18, true)]
    [InlineData(0, false)]
    [InlineData(19, false)]   // au-delà de l'échelle actuelle : sauvegarde ignorée
    public void RunSave_IsUsable_WithinCurrentScale(int combatNumber, bool usable)
    {
        var save = new RunSave { CombatNumber = combatNumber };
        Assert.Equal(usable, save.IsUsable);
    }

    // Fait sauter la run directement au combat voulu (la vague ne dépend que de seed + CombatNumber).
    private static Run RunAt(int combatNumber, int seed = 1, bool firstRun = false)
    {
        var commander = new UnitSpec(Commandes.Commander.Movement, Commandes.Commander.BaseClass, essential: true);
        return Run.Restore(new[] { commander }, combatNumber, seed, firstRun);
    }

    // Faux groupe d'ennemis vaincus (n soldats) pour alimenter le recrutement dans les tests.
    private static UnitSpec[] DefeatedWave(int n)
    {
        var wave = new UnitSpec[n];
        for (var i = 0; i < n; i++)
            wave[i] = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
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
        match.Place(new Cell(0, 7), Units.Soldat(Faction.Player)); // un autre allié bien vivant

        var enemyCell = new Cell(4, 5);
        match.Place(enemyCell, Units.Soldat(Faction.Enemy)); // adjacent au commandant

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
        match.Place(playerCell, Units.Soldat(Faction.Player));

        var bossCell = new Cell(4, 4);
        var boss = new UnitSpec(Commandes.Boss.Movement, Commandes.Boss.BaseClass, essential: true)
            .Spawn(Faction.Enemy);
        boss.TakeDamage(boss.Hp - 1); // à 1 PV
        match.Place(bossCell, boss);
        match.Place(new Cell(0, 0), Units.Soldat(Faction.Enemy)); // sbire bien vivant

        match.TryAttack(playerCell, bossCell); // tue le boss

        Assert.True(match.IsOver);
        Assert.Equal(Faction.Player, match.Winner);
    }
}
