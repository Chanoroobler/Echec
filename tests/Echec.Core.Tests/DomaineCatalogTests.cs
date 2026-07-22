using System.Collections.Generic;
using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Battle.Config;
using Xunit;

namespace Echec.Core.Tests;

public class DomaineCatalogTests
{
    private const string Json = """
    {
      // commentaire autorisé
      "domaines": [
        { "domaine": "Dame", "baseClass": { "name": "Recrue", "asset": "recrue", "hp": 7, "damage": 2, "moveRange": 1, "attackRange": 1 } },
        { "domaine": "Tour", "baseClass": { "name": "Veilleur", "asset": "veilleur", "hp": 20, "damage": 3, "moveRange": 5, "attackRange": 4, "piercesAllies": true } },
      ]
    }
    """;

    [Fact]
    public void FromJson_BuildsDomaineDefs_WithClassStats()
    {
        var defs = DomaineCatalog.FromJson(Json);

        var dame = defs.Single(d => d.Id == Domaine.Dame);
        Assert.Equal("Recrue", dame.BaseClass.Name);
        Assert.Equal("recrue", dame.BaseClass.Asset);
        Assert.Equal(7, dame.BaseClass.MaxHp);
        Assert.Equal(2, dame.BaseClass.Damage);
        Assert.Equal(1, dame.BaseClass.MoveRange);
        Assert.Equal(1, dame.BaseClass.AttackRange);
        Assert.False(dame.BaseClass.PiercesAllies);   // champ absent → false par défaut

        var tour = defs.Single(d => d.Id == Domaine.Tour);
        Assert.Equal(4, tour.BaseClass.AttackRange);
        Assert.Equal(5, tour.BaseClass.MoveRange);
        Assert.True(tour.BaseClass.PiercesAllies);     // "piercesAllies": true lu depuis le JSON
    }

    [Fact]
    public void CommandesFromJson_BuildsCommanders_FilteringOutBosses()
    {
        const string json = """
        {
          "domaines": [],
          "commandes": [
            { "role": "Commander", "domaine": "Dame", "name": "Generale", "asset": "generale", "hp": 30, "damage": 7, "moveRange": 2, "attackRange": 1 },
            { "role": "Boss",      "domaine": "Tour", "name": "Colosse",  "asset": "colosse",  "phases": { "1": { "hp": 40, "damage": 9, "moveRange": 1, "attackRange": 2 } } }
          ]
        }
        """;

        var commander = Assert.Single(DomaineCatalog.CommandesFromJson(json));   // le boss est filtré (cf. BossesFromJson)
        Assert.Equal(CommandeRole.Commander, commander.Role);
        Assert.Equal("Generale", commander.Name);
        Assert.Equal(Domaine.Dame, commander.Movement); // emprunte le déplacement de la Dame
        Assert.Equal(30, commander.BaseClass.MaxHp);
    }

    [Fact]
    public void BossesFromJson_ReadsPerPhaseProfiles_WithTraits()
    {
        const string json = """
        {
          "domaines": [],
          "commandes": [
            { "role": "Boss", "name": "Necromancien", "asset": "necro", "domaine": "Dame",
              "phases": {
                "1": { "hp": 30, "damage": 9,  "moveRange": 1, "attackRange": 1 },
                "3": { "hp": 60, "damage": 15, "moveRange": 1, "attackRange": 2, "traits": ["Rage", "Drain de vie"] }
              }
            }
          ]
        }
        """;

        var boss = Assert.Single(DomaineCatalog.BossesFromJson(json));
        Assert.Equal("Necromancien", boss.Name);
        Assert.Equal(Domaine.Dame, boss.Movement);

        Assert.True(boss.SupportsPhase(1));
        Assert.False(boss.SupportsPhase(2));                        // phase non déclarée
        Assert.Equal(30, boss.ProfileFor(1).MaxHp);
        Assert.Equal(60, boss.ProfileFor(3).MaxHp);
        Assert.Contains("Drain de vie", boss.ProfileFor(3).Traits);
        Assert.Empty(boss.ProfileFor(1).Traits);
        Assert.Same(boss.ProfileFor(1), boss.ProfileFor(2));        // phase 2 non déclarée → repli sur la plus proche ≤ 2 (phase 1)
    }

    [Fact]
    public void BossesFromJson_ReadsUnlockedCommander()
    {
        const string json = """
        {
          "domaines": [],
          "commandes": [
            { "role": "Boss", "name": "Commandant_lancier", "asset": "Commandant_lancier", "domaine": "Tour",
              "unlocksCommander": "Commandant_lancier",
              "phases": { "3": { "hp": 64, "damage": 20, "moveRange": 2, "attackRange": 3, "piercesAllies": true } }
            }
          ]
        }
        """;

        var boss = Assert.Single(DomaineCatalog.BossesFromJson(json));
        Assert.Equal("Commandant_lancier", boss.UnlocksCommander);
    }

    [Fact]
    public void AssignForRun_IsDeterministic_AndPrefersDistinctBosses()
    {
        var pool = new[] { Boss("A"), Boss("B"), Boss("C") };

        var a1 = Bosses.AssignForRun(pool, seed: 12345, phaseCount: 3);
        var a2 = Bosses.AssignForRun(pool, seed: 12345, phaseCount: 3);

        Assert.Equal(a1.Select(b => b.Name), a2.Select(b => b.Name));   // rejoué à l'identique
        Assert.Equal(3, a1.Select(b => b.Name).Distinct().Count());     // 3 boss distincts (pool suffisant)
    }

    [Fact]
    public void AssignForRun_FinalPhase_PrioritizesLockedUnlockableBoss()
    {
        var pool = new[] { Boss("A"), Boss("B"), UnlockBoss("Lancier", "cmd_lancier") };
        // Commandant encore verrouillé : le boss qui le débloque occupe la DERNIÈRE phase, quel que soit le seed.
        for (var seed = 0; seed < 40; seed++)
        {
            var assign = Bosses.AssignForRun(pool, seed, phaseCount: 3, unlockedCommanders: new HashSet<string>());
            Assert.Equal("cmd_lancier", assign[2].UnlocksCommander);
        }
    }

    [Fact]
    public void AssignForRun_FinalPhase_NoPriority_WhenCommanderAlreadyUnlocked()
    {
        var pool = new[] { Boss("A"), Boss("B"), UnlockBoss("Lancier", "cmd_lancier") };
        var already = new HashSet<string> { "cmd_lancier" };
        var finalUnlocks = new HashSet<string?>();
        for (var seed = 0; seed < 40; seed++)
            finalUnlocks.Add(Bosses.AssignForRun(pool, seed, phaseCount: 3, already)[2].UnlocksCommander);
        // Déjà débloqué → plus de priorité : la phase 3 n'est plus systématiquement le boss lié.
        Assert.Contains(null, finalUnlocks);
    }

    [Fact]
    public void AssignForRun_RepeatsWhenPoolTooSmall_AndUsesClosestProfile()
    {
        var only = new BossDef("Solo", "solo", Domaine.Dame, new Dictionary<int, UnitClass>
        {
            [1] = new("Solo", "solo", tier: 1, maxHp: 30, damage: 9,  moveRange: 1, attackRange: 1),
            [3] = new("Solo", "solo", tier: 1, maxHp: 50, damage: 13, moveRange: 1, attackRange: 1),
        });

        var assign = Bosses.AssignForRun(new[] { only }, seed: 1, phaseCount: 3);

        Assert.All(assign, b => Assert.Equal("Solo", b.Name));   // un seul boss → répété sur les 3 phases
        Assert.Equal(50, assign[2].ProfileFor(3).MaxHp);         // phase 3 → profil 3
        Assert.Equal(30, assign[1].ProfileFor(2).MaxHp);         // phase 2 non déclarée → repli profil 1
    }

    private static BossDef Boss(string name) => new(name, name, Domaine.Dame, new Dictionary<int, UnitClass>
    {
        [1] = new(name, name, tier: 1, maxHp: 30, damage: 9,  moveRange: 1, attackRange: 1),
        [2] = new(name, name, tier: 1, maxHp: 40, damage: 11, moveRange: 1, attackRange: 1),
        [3] = new(name, name, tier: 1, maxHp: 50, damage: 13, moveRange: 1, attackRange: 1),
    });

    /// <summary>Boss éligible aux 3 phases qui DÉBLOQUE le commandant <paramref name="unlocksCommander"/>.</summary>
    private static BossDef UnlockBoss(string name, string unlocksCommander) =>
        new(name, name, Domaine.Dame, new Dictionary<int, UnitClass>
        {
            [1] = new(name, name, tier: 1, maxHp: 30, damage: 9,  moveRange: 1, attackRange: 1),
            [2] = new(name, name, tier: 1, maxHp: 40, damage: 11, moveRange: 1, attackRange: 1),
            [3] = new(name, name, tier: 1, maxHp: 50, damage: 13, moveRange: 1, attackRange: 1),
        }, unlocksCommander: unlocksCommander);

    [Fact]
    public void Load_OverridesDefaults()
    {
        try
        {
            Domaines.Load(DomaineCatalog.FromJson(Json));

            Assert.Equal("Recrue", Domaines.Dame.BaseClass.Name);
            Assert.Equal(7, Units.Soldat(Faction.Player).MaxHp);
        }
        finally
        {
            // Restaure l'arbre par défaut COMPLET (évolutions incluses) pour ne pas polluer les autres
            // tests : un JSON réécrit à la main omettait les évolutions et cassait les tests de fusion.
            Domaines.ResetToDefaults();
        }
    }
}
