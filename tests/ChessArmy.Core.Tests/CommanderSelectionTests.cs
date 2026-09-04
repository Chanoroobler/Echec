using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Battle.Config;
using ChessArmy.Core.Campaign;
using ChessArmy.Core.Equip;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// CHOIX DU COMMANDANT (écran de création de partie) : chaque commandant apporte ses PIONS DE DÉPART, et le
/// couple commandant + difficulté est figé à la création puis persisté avec la run.
///
/// Ces tests remplacent temporairement le registre statique <see cref="Commandes"/> ; la parallélisation est
/// désactivée pour l'assembly (AssemblyInfo.cs), et chaque test restaure le registre dans un <c>finally</c>.
/// </summary>
public class CommanderSelectionTests
{
    private static CommandeDef Def(string id, string asset, params Domaine[] starting) =>
        new(CommandeRole.Commander, Domaine.Dame,
            new UnitClass(id, asset, tier: 1, maxHp: 26, damage: 6, moveRange: 2, attackRange: 1),
            deployments: 5, reserveSize: 8, treeId: "commandant", fusionPoints: 2,
            id: id, startingUnits: starting);

    /// <summary>Installe un registre de commandants le temps de l'action, puis restaure l'ancien.</summary>
    private static void WithCommanders(IReadOnlyList<CommandeDef> defs, Action body)
    {
        var previous = Commandes.All;
        try
        {
            Commandes.Load(defs);
            body();
        }
        finally
        {
            Commandes.Load(previous);
        }
    }

    [Fact]
    public void StartingUnits_DefineTheRoster()
    {
        var solo = Def("solo", "commandant");                                  // commence SEUL
        var trio = Def("trio", "brute", Domaine.Tour, Domaine.Fou, Domaine.Cavalier);

        WithCommanders(new[] { solo, trio }, () =>
        {
            var runSolo = new Run(seed: 1, commander: solo);
            Assert.Single(runSolo.Roster);                                     // le commandant, rien d'autre
            Assert.True(runSolo.Roster[0].Essential);

            var runTrio = new Run(seed: 1, commander: trio);
            Assert.Equal(4, runTrio.Roster.Count);                             // commandant + 3 pions
            Assert.Equal(new[] { Domaine.Tour, Domaine.Fou, Domaine.Cavalier },
                runTrio.Roster.Where(u => !u.Essential).Select(u => u.Domaine));
        });
    }

    [Fact]
    public void DefaultCommander_KeepsHistoricStart_CommanderAndTwoSoldats()
    {
        // Sans pions de départ déclarés, on garde le départ historique : commandant + 2 Soldats (domaine Dame).
        var run = new Run(seed: 1);

        Assert.Equal(3, run.Roster.Count);
        Assert.Equal(new[] { Domaine.Dame, Domaine.Dame },
            run.Roster.Where(u => !u.Essential).Select(u => u.Domaine));
    }

    [Fact]
    public void Save_RoundTrips_CommanderAndDifficulty()
    {
        var brute = Def("brute", "Commandant_brute", Domaine.Tour);

        WithCommanders(new[] { Def("commandant", "commandant", Domaine.Dame, Domaine.Dame), brute }, () =>
        {
            var run = new Run(seed: 7, commander: brute, difficulty: Difficulty.Difficile);

            var restored = RunSave.From(run).ToRun();

            Assert.Equal("brute", restored.CommanderDef.Id);
            Assert.Equal(Difficulty.Difficile, restored.Difficulty);
            Assert.Equal(new[] { Domaine.Tour },
                restored.Roster.Where(u => !u.Essential).Select(u => u.Domaine));
        });
    }

    [Fact]
    public void LegacySave_WithoutCommanderId_ResolvesByAsset()
    {
        var brute = Def("brute", "Commandant_brute", Domaine.Tour);

        WithCommanders(new[] { Def("commandant", "commandant", Domaine.Dame, Domaine.Dame), brute }, () =>
        {
            // Sauvegarde v2 : pas d'id de commandant, et pas de difficulté.
            var save = RunSave.From(new Run(seed: 7, commander: brute, difficulty: Difficulty.Difficile));
            save.CommanderId = null;
            save.Difficulty = Difficulty.Normal;

            var restored = save.ToRun();

            Assert.Equal("brute", restored.CommanderDef.Id);          // retrouvé par l'asset de sa classe
            Assert.Equal(Difficulty.Normal, restored.Difficulty);     // absente → Normal
        });
    }

    [Fact]
    public void UnknownCommanderId_FallsBackInsteadOfCrashing()
    {
        // Commandant supprimé du JSON depuis la sauvegarde : on ne plante pas, on retombe sur un commandant.
        WithCommanders(new[] { Def("commandant", "commandant", Domaine.Dame) }, () =>
        {
            var save = RunSave.From(new Run(seed: 7));
            save.CommanderId = "disparu";

            Assert.Equal("commandant", save.ToRun().CommanderDef.Id);
        });
    }

    [Fact]
    public void Config_ParsesIdAndStartingUnits()
    {
        const string json = """
        {
          "domaines": [],
          "commandes": [
            { "role": "Commander", "id": "brute", "domaine": "Dame", "name": "Brute", "asset": "Commandant_brute",
              "hp": 36, "damage": 14, "moveRange": 1, "attackRange": 1,
              "startingUnits": [ "Dame", "Tour", "Fou" ] }
          ]
        }
        """;

        var def = Assert.Single(DomaineCatalog.CommandesFromJson(json));

        Assert.Equal("brute", def.Id);
        Assert.Equal(new[] { Domaine.Dame, Domaine.Tour, Domaine.Fou }, def.StartingUnits);
    }

    [Fact]
    public void Config_ParsesUnlockedFlag()
    {
        const string json = """
        {
          "domaines": [],
          "commandes": [
            { "role": "Commander", "id": "libre", "domaine": "Dame", "name": "Libre", "asset": "commandant",
              "hp": 28, "damage": 12, "moveRange": 1, "attackRange": 1 },
            { "role": "Commander", "id": "verrouille", "domaine": "Dame", "name": "Verrouille", "asset": "brute",
              "hp": 36, "damage": 14, "moveRange": 1, "attackRange": 1, "unlocked": false }
          ]
        }
        """;

        var defs = DomaineCatalog.CommandesFromJson(json);

        Assert.True(defs[0].StartsUnlocked);    // champ absent → débloqué
        Assert.False(defs[1].StartsUnlocked);
    }

    [Fact]
    public void Config_MissingId_FallsBackToAsset()
    {
        const string json = """
        {
          "domaines": [],
          "commandes": [
            { "role": "Commander", "domaine": "Dame", "name": "Commandant", "asset": "commandant",
              "hp": 28, "damage": 12, "moveRange": 1, "attackRange": 1 }
          ]
        }
        """;

        var def = Assert.Single(DomaineCatalog.CommandesFromJson(json));

        Assert.Equal("commandant", def.Id);
        // Pions de départ absents → départ historique (2 Soldats).
        Assert.Equal(new[] { Domaine.Dame, Domaine.Dame }, def.StartingUnits);
    }

    // ── Difficulté : composition en tiers des vagues ─────────────────────────────
    // La table de campagne est calée sur Normal. Facile rétrograde UN pion du tier le plus haut,
    // Difficile promeut UN pion du tier le plus bas ; l'effectif ne bouge jamais.

    [Fact]
    public void Tiers_Normal_LeavesTheWaveUntouched()
    {
        var tiers = new[] { 1, 1, 1, 1, 2, 2, 2 };

        Assert.Equal(tiers, Run.AdjustTiers(tiers, Difficulty.Normal));
    }

    [Fact]
    public void Tiers_Facile_DemotesOneOfTheHighest()
    {
        // 4× T1 + 3× T2 → 5× T1 + 2× T2 : un seul T2 de moins, toujours 7 ennemis.
        var adjusted = Run.AdjustTiers(new[] { 1, 1, 1, 1, 2, 2, 2 }, Difficulty.Facile);

        Assert.Equal(7, adjusted.Count);
        Assert.Equal(5, adjusted.Count(t => t == 1));
        Assert.Equal(2, adjusted.Count(t => t == 2));
    }

    [Fact]
    public void Tiers_Difficile_PromotesOneOfTheLowest()
    {
        // 4× T1 + 3× T2 → 3× T1 + 4× T2. Aucun T3 ne doit apparaître : on promeut le plus FAIBLE.
        var adjusted = Run.AdjustTiers(new[] { 1, 1, 1, 1, 2, 2, 2 }, Difficulty.Difficile);

        Assert.Equal(7, adjusted.Count);
        Assert.Equal(3, adjusted.Count(t => t == 1));
        Assert.Equal(4, adjusted.Count(t => t == 2));
        Assert.DoesNotContain(3, adjusted);
    }

    [Fact]
    public void Tiers_Facile_AllTier1_StaysAsIs()
    {
        // Plancher atteint : rien à rétrograder, et on ne compense pas en retirant un pion.
        var tiers = new[] { 1, 1, 1 };

        Assert.Equal(tiers, Run.AdjustTiers(tiers, Difficulty.Facile));
    }

    [Fact]
    public void Tiers_Difficile_AllTier3_StaysAsIs()
    {
        var tiers = new[] { 3, 3, 3 };

        Assert.Equal(tiers, Run.AdjustTiers(tiers, Difficulty.Difficile));
    }

    [Theory]
    [InlineData(Difficulty.Facile)]
    [InlineData(Difficulty.Normal)]
    [InlineData(Difficulty.Difficile)]
    public void Tiers_EnemyCount_IsNeverChanged(Difficulty difficulty)
    {
        var tiers = new[] { 1, 1, 2, 2, 3 };

        Assert.Equal(tiers.Length, Run.AdjustTiers(tiers, difficulty).Count);
    }

    [Fact]
    public void Tiers_ShiftAppliesOncePerWave_NotOncePerCycle()
    {
        // Les vagues « pilotées par la map » CYCLENT le gabarit pour atteindre le nombre de spawns. Le
        // décalage doit porter sur la vague déroulée, sinon il serait appliqué une fois par cycle.
        var wave = new[] { 1, 2, 1, 2, 1, 2 };   // gabarit {1,2} cyclé 3 fois

        var adjusted = Run.AdjustTiers(wave, Difficulty.Facile);

        Assert.Equal(4, adjusted.Count(t => t == 1));   // un seul T2 rétrogradé, pas trois
        Assert.Equal(2, adjusted.Count(t => t == 2));
    }

    /// <summary>Composition FIXÉE dans l'éditeur de map : la référence, calée sur Normal.</summary>
    private static readonly int[] DrawnTiers = { 1, 1, 2, 2, 2, 2 };

    private static Run RunAt(int combat, Difficulty difficulty, int seed = 1) =>
        Run.Restore(new Run(seed: seed).Roster.ToList(), combatNumber: combat, seed: seed, firstRun: false,
            difficulty: difficulty);

    private static (int T1, int T2) Count(IEnumerable<UnitSpec> units)
    {
        var list = units.ToList();
        return (list.Count(u => u.UnitClass.Tier == 1), list.Count(u => u.UnitClass.Tier == 2));
    }

    [Theory]
    [InlineData(Difficulty.Facile, 3, 3)]
    [InlineData(Difficulty.Normal, 2, 4)]     // exactement la composition dessinée
    [InlineData(Difficulty.Difficile, 1, 5)]
    public void SpecialWave_DrawnComposition_IsTheNormalBaseline(Difficulty difficulty, int t1, int t2)
    {
        var wave = RunAt(9, difficulty).BuildSpecialEnemyWave(DrawnTiers.Length, fixedTiers: DrawnTiers);

        Assert.Equal(DrawnTiers.Length, wave.Count);   // l'effectif suit les spawns de la map, jamais la difficulté
        Assert.Equal((t1, t2), Count(wave));
    }

    [Theory]
    [InlineData(Difficulty.Facile, 3, 3)]
    [InlineData(Difficulty.Normal, 2, 4)]
    [InlineData(Difficulty.Difficile, 1, 5)]
    public void BossWave_EscortsFollowDifficulty(Difficulty difficulty, int t1, int t2)
    {
        var wave = RunAt(6, difficulty).BuildBossEnemyWave(DrawnTiers.Length, fixedTiers: DrawnTiers);

        Assert.Equal(DrawnTiers.Length + 1, wave.Count);       // le boss s'ajoute AUX escortes
        Assert.Equal((t1, t2), Count(wave.Skip(1)));
    }

    [Fact]
    public void BossItself_IsNeverTouchedByDifficulty()
    {
        // Le boss est inséré HORS de la liste de tiers : ses stats de phase doivent être identiques partout.
        var bosses = DifficultySettings.AllLevels
            .Select(d => RunAt(6, d).BuildBossEnemyWave(DrawnTiers.Length, fixedTiers: DrawnTiers)[0].UnitClass)
            .ToList();

        Assert.All(bosses, b =>
        {
            Assert.Equal(bosses[0].Name, b.Name);
            Assert.Equal(bosses[0].MaxHp, b.MaxHp);
            Assert.Equal(bosses[0].Damage, b.Damage);
        });
    }

    [Theory]
    [InlineData(Difficulty.Facile)]
    [InlineData(Difficulty.Normal)]
    [InlineData(Difficulty.Difficile)]
    public void FirstMission_IsAlwaysTwoTier1_WhateverTheDifficulty(Difficulty difficulty)
    {
        // Mise en jambes : le combat 1 reste la vague de la table (2× T1). En difficile, la promotion
        // d'un pion sortirait un T2 dès l'ouverture — c'est justement ce qu'on interdit ici.
        for (var seed = 1; seed <= 15; seed++)
        {
            var wave = RunAt(1, difficulty, seed).BuildEnemyWave();

            Assert.Equal(2, wave.Count);
            Assert.All(wave, u => Assert.Equal(1, u.UnitClass.Tier));
        }
    }

    [Fact]
    public void SecondMission_StillFollowsTheDifficulty()
    {
        // Le garde-fou ne vaut QUE pour la première mission : dès le combat 2 le décalage reprend.
        var wave = RunAt(2, Difficulty.Difficile).BuildEnemyWave();

        Assert.Equal(3, wave.Count);                   // table : 3× T1
        Assert.Equal((2, 1), Count(wave));             // difficile : un T1 promu en T2
    }

    // ── Difficulté : équipement des pions ennemis ────────────────────────────────

    private static int Equipped(int combat, Difficulty difficulty, int seed = 1) =>
        RunAt(combat, difficulty, seed).BuildEnemyWave().Count(u => u.HasEquipment);

    [Theory]
    // Le nombre d'équipés est EXACT, pas probabiliste : phase 1 → 1, phases 2-3 → 3 en normal.
    // Bonus difficile PAR PHASE : phase 1 +1, phase 2 +1, phase 3 +3.
    [InlineData(3, Difficulty.Facile, 0)]      // facile : jamais d'équipement
    [InlineData(3, Difficulty.Normal, 1)]      // phase 1
    [InlineData(3, Difficulty.Difficile, 2)]
    [InlineData(7, Difficulty.Normal, 3)]      // phase 2
    [InlineData(7, Difficulty.Difficile, 4)]
    [InlineData(13, Difficulty.Normal, 3)]     // phase 3
    [InlineData(13, Difficulty.Difficile, 6)]
    public void EnemyEquipment_ExactCountPerPhaseAndDifficulty(int combat, Difficulty difficulty, int expected)
    {
        // Même nombre quelle que soit la graine : seul le PORTEUR et l'OBJET sont tirés au sort.
        for (var seed = 1; seed <= 15; seed++)
            Assert.Equal(expected, Equipped(combat, difficulty, seed));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EnemyEquipment_NoneOnTheFirstTwoCombats(int combat)
    {
        // Montée en douceur : le joueur découvre le jeu nu, même en difficile.
        foreach (var difficulty in DifficultySettings.AllLevels)
            for (var seed = 1; seed <= 10; seed++)
                Assert.Equal(0, Equipped(combat, difficulty, seed));
    }

    [Fact]
    public void EnemyEquipment_IsNeverLegendary()
    {
        // Un légendaire sur un ennemi de passage serait hors de proportion : commun ou rare seulement.
        for (var combat = 3; combat <= 18; combat++)
            for (var seed = 1; seed <= 10; seed++)
                Assert.All(RunAt(combat, Difficulty.Difficile, seed).BuildEnemyWave(),
                    u => Assert.NotEqual(EquipmentRarity.Legendary, u.Equipments.FirstOrDefault()?.Rarity ?? EquipmentRarity.Common));
    }

    [Fact]
    public void EnemyEquipment_BossIsNeverEquipped()
    {
        // Le boss est Essential, comme le commandant du joueur que Run.CanEquip refuse d'équiper.
        for (var seed = 1; seed <= 20; seed++)
        {
            var wave = RunAt(6, Difficulty.Difficile, seed)
                .BuildBossEnemyWave(DrawnTiers.Length, fixedTiers: DrawnTiers);

            Assert.False(wave[0].HasEquipment);   // le boss est inséré EN TÊTE
        }
    }

    [Fact]
    public void EnemyEquipment_SameSeed_GivesTheSameWave()
    {
        // La vague n'est pas sauvegardée : elle est REGÉNÉRÉE depuis la graine. Reprendre une partie doit
        // donc rendre exactement les mêmes ennemis avec les mêmes objets.
        var first = RunAt(7, Difficulty.Difficile).BuildEnemyWave().Select(u => u.Equipments.FirstOrDefault()?.Id).ToList();
        var second = RunAt(7, Difficulty.Difficile).BuildEnemyWave().Select(u => u.Equipments.FirstOrDefault()?.Id).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void EnemyEquipment_DoesNotDisturbWaveComposition()
    {
        // Le tirage d'équipement a son PROPRE sel de RNG : il ne doit décaler ni l'effectif ni les tiers.
        var wave = RunAt(7, Difficulty.Normal).BuildEnemyWave();

        Assert.Equal(7, wave.Count);
        Assert.Equal(4, wave.Count(u => u.UnitClass.Tier == 1));
        Assert.Equal(3, wave.Count(u => u.UnitClass.Tier == 2));
    }

    [Theory]
    [InlineData(Difficulty.Facile, 0)]      // aucune exigence : la mission spéciale ne peut pas être ratée
    [InlineData(Difficulty.Normal, 1)]
    [InlineData(Difficulty.Difficile, 2)]
    public void SpecialMissions_PaysanQuotaPerDifficulty(Difficulty difficulty, int required)
    {
        // Contrat lu par GameplayScene pour décider si la mission « libérer » est perdue (= fin de run).
        Assert.Equal(required, DifficultySettings.For(difficulty).PaysansRequired);
    }

    [Theory]
    [InlineData(Difficulty.Facile, 0)]      // aucune exigence : la mission spéciale ne peut pas être ratée
    [InlineData(Difficulty.Normal, 2)]
    [InlineData(Difficulty.Difficile, 3)]
    public void ProtectMissions_PaysanQuotaPerDifficulty(Difficulty difficulty, int required)
    {
        // Barème DISTINCT (plus exigeant) des missions « protéger », lu par GameplayScene.PaysansRequired.
        Assert.Equal(required, DifficultySettings.For(difficulty).PaysansRequiredProtect);
    }

    [Theory]
    [InlineData(Difficulty.Facile, 0)]      // aucune exigence : la course ne peut pas être perdue sur le quota
    [InlineData(Difficulty.Normal, 2)]
    [InlineData(Difficulty.Difficile, 3)]
    public void SaveMissions_PaysanQuotaPerDifficulty(Difficulty difficulty, int required)
    {
        // Barème des missions « sauver » (course contre l'IA), lu par GameplayScene.PaysansRequired.
        Assert.Equal(required, DifficultySettings.For(difficulty).PaysansRequiredSave);
    }

    [Fact]
    public void MissionSnapshot_SurvivesLosses_SoRestartBringsPawnsBack()
    {
        // « Recommencer » (menu pause) rejoue l'instantané pris à l'ENTRÉE de la mission. Il doit rester
        // intact quoi qu'il arrive ensuite à la run vivante — c'est ce qui ramène les pions tombés.
        var run = new Run(seed: 1);                       // commandant + 2 soldats
        var snapshot = RunSave.From(run);
        var victim = run.Roster.First(u => !u.Essential);

        run.CompleteCombat(new[] { victim }, System.Array.Empty<UnitSpec>());

        Assert.Equal(2, run.Roster.Count);                // la perte est bien appliquée à la run vivante
        Assert.Equal(3, snapshot.ToRun().Roster.Count);   // l'instantané, lui, garde l'effectif d'origine
    }

    [Fact]
    public void ById_IsCaseInsensitive_AndNullForUnknown()
    {
        WithCommanders(new[] { Def("brute", "Commandant_brute") }, () =>
        {
            Assert.Equal("brute", Commandes.ById("BRUTE")?.Id);
            Assert.Null(Commandes.ById("inconnu"));
            Assert.Null(Commandes.ById(null));
        });
    }
}
