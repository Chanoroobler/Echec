using System;
using System.Collections.Generic;
using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Campaign;
using Echec.Core.Equip;
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
    public void PhaseLayout_Phase1MovesSpecialToSlot4_Phases2And3Standard()
    {
        // Phase 1 : la spéciale est décalée au slot 4 (Escarmouche ×3, Speciale, Escarmouche, Boss).
        var phase1 = new[]
        {
            CombatType.Escarmouche, CombatType.Escarmouche, CombatType.Escarmouche,
            CombatType.Speciale, CombatType.Escarmouche, CombatType.Boss,
        };
        // Phases 2-3 : rythme standard (Escarmouche ×2, Speciale, Escarmouche ×2, Boss).
        var standard = new[]
        {
            CombatType.Escarmouche, CombatType.Escarmouche, CombatType.Speciale,
            CombatType.Escarmouche, CombatType.Escarmouche, CombatType.Boss,
        };

        for (var combat = 1; combat <= Run.TotalCombats; combat++)
        {
            var run = RunAt(combat);
            var phase = (combat - 1) / 6 + 1;
            var mission = (combat - 1) % 6 + 1;
            Assert.Equal(phase, run.PhaseIndex);
            Assert.Equal(mission, run.MissionInPhase);

            var expected = (phase == 1 ? phase1 : standard)[mission - 1];
            Assert.Equal(expected, run.CurrentMission);
            Assert.Equal(expected, Run.MissionKindAt(phase, mission));   // statique == instance
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
    // Phase 1 : apprentissage (T1, le T2 arrive au combat 4). Démarrage adouci : 2 puis 3 pions.
    [InlineData(1, 2, 0, 0, 0)]
    [InlineData(2, 3, 0, 0, 0)]
    [InlineData(3, 4, 0, 0, 0)]   // escarmouche phase 1 slot 3 : 4× T1
    [InlineData(4, 5, 1, 0, 0)]   // spéciale phase 1 slot 4 : effectif liste = 6 (l'effectif réel vient de la map)
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

    [Theory]
    [InlineData(4, 5)]    // phase 1 mission 4 (spéciale) : 5 spawns
    [InlineData(4, 12)]   // effectif > gabarit de la table : le gabarit de tiers est cyclé
    [InlineData(9, 6)]    // phase 2 mission 3 : 6 spawns
    public void BuildSpecialEnemyWave_HasExactlyRequestedCount_NoBoss(int combat, int count)
    {
        var wave = RunAt(combat).BuildSpecialEnemyWave(count);

        Assert.Equal(count, wave.Count);                 // EXACTEMENT le nombre demandé (= spawns de la map)
        Assert.DoesNotContain(wave, u => u.Essential);   // jamais de boss en mission spéciale
    }

    [Fact]
    public void BuildSpecialEnemyWave_ZeroOrNegative_IsEmpty()
    {
        Assert.Empty(RunAt(3).BuildSpecialEnemyWave(0));
        Assert.Empty(RunAt(3).BuildSpecialEnemyWave(-2));
    }

    [Fact]
    public void BuildSpecialEnemyWave_IsDeterministic()
    {
        var a = RunAt(3, seed: 7).BuildSpecialEnemyWave(6);
        var b = RunAt(3, seed: 7).BuildSpecialEnemyWave(6);
        Assert.Equal(a.Select(u => u.UnitClass), b.Select(u => u.UnitClass));
    }

    [Fact]
    public void BuildSpecialEnemyWave_FixedTiers_SetExactComposition_OverridingTemplate()
    {
        // Tiers FIXÉS par la map (éditeur) : la vague a EXACTEMENT cette composition, quel que soit campaign.json.
        var tiers = new[] { 3, 3, 2, 1 };
        var wave = RunAt(15).BuildSpecialEnemyWave(tiers.Length, isSeen: null, fixedTiers: tiers);

        Assert.Equal(4, wave.Count);
        Assert.Equal(2, wave.Count(u => u.UnitClass.Tier == 3));
        Assert.Equal(1, wave.Count(u => u.UnitClass.Tier == 2));
        Assert.Equal(1, wave.Count(u => u.UnitClass.Tier == 1));   // un T1 fixé même en phase 3
    }

    [Fact]
    public void BuildBossEnemyWave_FixedTiers_SetEscortComposition()
    {
        var tiers = new[] { 3, 2, 2 };
        var wave = RunAt(18).BuildBossEnemyWave(tiers.Length, isSeen: null, fixedTiers: tiers);

        Assert.True(wave[0].Essential);   // boss en tête (hors composition d'escortes)
        var escorts = wave.Where(u => !u.Essential).ToList();
        Assert.Equal(3, escorts.Count);
        Assert.Equal(1, escorts.Count(u => u.UnitClass.Tier == 3));
        Assert.Equal(2, escorts.Count(u => u.UnitClass.Tier == 2));
    }

    [Theory]
    [InlineData(6, 4)]     // phase 1 boss : 4 escortes
    [InlineData(12, 9)]    // phase 2 boss : 9 escortes
    [InlineData(18, 0)]    // boss SEUL (map sans case d'escorte)
    public void BuildBossEnemyWave_BossInFront_ExactEscortCount(int combat, int escorts)
    {
        var wave = RunAt(combat).BuildBossEnemyWave(escorts);

        Assert.Equal(escorts + 1, wave.Count);              // boss + escortes
        Assert.True(wave[0].Essential);                     // boss EN TÊTE (posé sur une case B)
        Assert.Single(wave, u => u.Essential);              // un seul essentiel : le boss
    }

    [Fact]
    public void BuildBossEnemyWave_IsDeterministic()
    {
        var a = RunAt(6, seed: 7).BuildBossEnemyWave(4);
        var b = RunAt(6, seed: 7).BuildBossEnemyWave(4);
        Assert.Equal(a.Select(u => u.UnitClass), b.Select(u => u.UnitClass));
    }

    // ─── Rareté de coffre (chances par phase + pitié) ────────────────────────────────────────────
    [Theory]
    // Phase 1 : légendaire 2 %, rare 15 % (combat 1).
    [InlineData(1, 1.0, EquipmentRarity.Legendary)]
    [InlineData(1, 2.0, EquipmentRarity.Rare)]     // pile au-dessus du seuil légendaire → rare
    [InlineData(1, 16.9, EquipmentRarity.Rare)]
    [InlineData(1, 17.0, EquipmentRarity.Common)]  // 2 + 15 → commun
    // Phase 3 : légendaire 10 %, rare 40 % (combat 13).
    [InlineData(13, 9.9, EquipmentRarity.Legendary)]
    [InlineData(13, 10.0, EquipmentRarity.Rare)]
    [InlineData(13, 49.9, EquipmentRarity.Rare)]
    [InlineData(13, 50.0, EquipmentRarity.Common)]
    public void ResolveChestRarity_ThresholdsByPhase(int combat, double roll, EquipmentRarity expected)
    {
        Assert.Equal(expected, RunAt(combat).ResolveChestRarity(roll));
    }

    [Fact]
    public void ChestPity_BuildsOnMiss_ResetsOnDrop_Independently()
    {
        var run = RunAt(1);   // phase 1 : légendaire 2 %, rare 15 %

        // Coffre commun : les deux pitiés montent (légendaire +1, rare +2).
        Assert.Equal(EquipmentRarity.Common, run.ResolveChestRarity(99.0));
        Assert.Equal(1, run.LegendaryPity);
        Assert.Equal(2, run.RarePity);

        // Deuxième coffre commun : elles continuent de monter.
        Assert.Equal(EquipmentRarity.Common, run.ResolveChestRarity(99.0));
        Assert.Equal(2, run.LegendaryPity);
        Assert.Equal(4, run.RarePity);

        // Coffre RARE (fenêtre élargie : légendaire 2+2=4, rare 15+4=19 → roll 10) : rare remis à zéro,
        // légendaire continue (+1).
        Assert.Equal(EquipmentRarity.Rare, run.ResolveChestRarity(10.0));
        Assert.Equal(0, run.RarePity);
        Assert.Equal(3, run.LegendaryPity);

        // Coffre LÉGENDAIRE (fenêtre légendaire 2+3=5 → roll 1) : légendaire remis à zéro, rare monte (+2).
        Assert.Equal(EquipmentRarity.Legendary, run.ResolveChestRarity(1.0));
        Assert.Equal(0, run.LegendaryPity);
        Assert.Equal(2, run.RarePity);
    }

    [Fact]
    public void ChestPity_SurvivesSaveRoundTrip()
    {
        var run = RunAt(5);
        run.ResolveChestRarity(99.0);   // commun → legPity 1, rarePity 2
        run.ResolveChestRarity(99.0);   // → legPity 2, rarePity 4

        var restored = RunSave.From(run).ToRun();

        Assert.Equal(2, restored.LegendaryPity);
        Assert.Equal(4, restored.RarePity);
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
                // Le pion essentiel = le boss ASSIGNÉ à cette phase, avec le profil (stats/traits) de la phase.
                Assert.Equal(run.BossOfPhase(run.PhaseIndex).ProfileFor(run.PhaseIndex),
                    wave.Single(u => u.Essential).UnitClass);
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
    public void BuildEnemyWave_AvoidsMoreThanTwoOfSameClass_WhenPoolAllowsIt()
    {
        // Phase 3 : pool riche (4 domaines × 2 classes/tier = 8 par tier) → aucune classe ne dépasse 2.
        for (var combat = 13; combat <= 17; combat++)
        {
            var wave = RunAt(combat).BuildEnemyWave();
            var maxSame = wave.Where(u => !u.Essential).GroupBy(u => u.UnitClass).Max(g => g.Count());
            Assert.True(maxSame <= 2, $"combat {combat} : une classe apparaît {maxSame} fois");
        }
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

        // Un boss AVANT la phase de fin enchaîne vers le recrutement ; le boss de la phase de FIN
        // (cf. Run.EndAtPhase) termine la run en victoire. Robuste au réglage de playtest.
        var expected = run.PhaseIndex >= Run.EndAtPhase ? RunPhase.Victory : RunPhase.Recruitment;
        Assert.Equal(expected, run.Phase);
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

    [Fact]
    public void RunSave_PreservesKillCount()
    {
        // Le total de kills à vie de chaque pion survit à un aller-retour de sauvegarde.
        var run = new Run(seed: 1, firstRun: true);
        run.Roster[0].Kills = 7;

        var restored = RunSave.From(run).ToRun();

        Assert.Equal(7, restored.Roster[0].Kills);
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
    [Fact]
    public void ReserveCount_ExcludesCommander_AndTracksLimit()
    {
        var run = new Run(seed: 1);                 // commandant + 2 soldats
        Assert.Equal(2, run.ReserveCount);
        Assert.False(run.IsReserveFull);

        while (run.ReserveCount < run.ReserveLimit)
            run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
        Assert.Equal(run.ReserveLimit, run.ReserveCount);
        Assert.True(run.IsReserveFull);
    }

    [Fact]
    public void DeleteUnit_RemovesReservePion_RefusesCommander()
    {
        var run = new Run(seed: 1);
        var pion = run.Roster.First(u => !u.Essential);

        Assert.True(run.DeleteUnit(pion));
        Assert.DoesNotContain(pion, run.Roster);
        Assert.False(run.DeleteUnit(run.Commander));   // jamais le commandant
    }

    [Fact]
    public void Fuse_WorksInRecruitmentPhase_ToMakeRoom()
    {
        var run = new Run(seed: 1);
        run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));   // 3 soldats de base au total
        run.CompleteCombat(System.Array.Empty<UnitSpec>(),
            new[] { new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass) });
        Assert.Equal(RunPhase.Recruitment, run.Phase);

        var group = run.Roster.Where(u => !u.Essential && u.UnitClass == Domaines.Dame.BaseClass).Take(3).ToList();
        var evo = Domaines.Dame.BaseClass.Evolutions[0];

        var fused = run.Fuse(group, evo);

        Assert.NotNull(fused);
        Assert.Equal(evo, fused!.UnitClass);
    }

    // ─── Recrue de tuile : chance croissante d'un T2 déjà découvert ──────────────────────────────

    [Theory]
    [InlineData(1, 0)]     // mission 1 : jamais de T2
    [InlineData(2, 5)]     // +5 % dès la mission 2
    [InlineData(3, 10)]
    [InlineData(18, 85)]   // dernière mission de la campagne
    public void Tier2RecruitChance_StartsAtZero_AndGrows5PerMissionFromMission2(int combat, int expected)
    {
        Assert.Equal(expected, RunAt(combat).Tier2RecruitChance);
    }

    [Fact]
    public void SeenTier2Recruits_ListsOnlyDiscoveredTier2()
    {
        // Un seul T2 « découvert » : l'archer (évolution de la Dame). Le pool ne contient que lui.
        var pool = RunAt(2).SeenTier2Recruits(asset => asset == "archer");

        Assert.Single(pool);
        Assert.Equal("archer", pool[0].UnitClass.Asset);
        Assert.Equal(2, pool[0].UnitClass.Tier);
    }

    [Fact]
    public void RollSeenRecruit_Mission1_NeverReturnsTier2_EvenWhenAllSeen()
    {
        var run = RunAt(1);   // chance 0 %
        var rng = new Random(1);
        for (var i = 0; i < 100; i++)
            Assert.Equal(1, run.RollSeenRecruit(rng, _ => true).UnitClass.Tier);
    }

    [Fact]
    public void RollSeenRecruit_NoTier2Discovered_AlwaysTier1_EvenLateGame()
    {
        // Seuls les tier 1 sont vus : malgré une forte chance (mission 18), aucun T2 ne peut sortir.
        var run = RunAt(18);
        var rng = new Random(1);
        var seenT1 = new HashSet<string>
        {
            Domaines.Dame.BaseClass.Asset, Domaines.Fou.BaseClass.Asset,
            Domaines.Cavalier.BaseClass.Asset, Domaines.Tour.BaseClass.Asset,
        };
        for (var i = 0; i < 100; i++)
            Assert.Equal(1, run.RollSeenRecruit(rng, seenT1.Contains).UnitClass.Tier);
    }

    [Fact]
    public void RollSeenRecruit_LateGame_MixesTier1AndDiscoveredTier2()
    {
        // Mission 18 (85 %), tout découvert : sur de nombreux tirages, des T2 ET des T1 sortent.
        var run = RunAt(18);
        var rng = new Random(1);
        var results = new List<UnitSpec>();
        for (var i = 0; i < 100; i++)
            results.Add(run.RollSeenRecruit(rng, _ => true));

        Assert.Contains(results, r => r.UnitClass.Tier == 2);
        Assert.Contains(results, r => r.UnitClass.Tier == 1);   // la chance n'est jamais 100 %
    }

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
        var bossDef = Bosses.All[0];
        var boss = new UnitSpec(bossDef.Movement, bossDef.ProfileFor(1), essential: true)
            .Spawn(Faction.Enemy);
        boss.TakeDamage(boss.Hp - 1); // à 1 PV
        match.Place(bossCell, boss);
        match.Place(new Cell(0, 0), Units.Soldat(Faction.Enemy)); // sbire bien vivant

        match.TryAttack(playerCell, bossCell); // tue le boss

        Assert.True(match.IsOver);
        Assert.Equal(Faction.Player, match.Winner);
    }
}
