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
    public void CommandesFromJson_BuildsLeaders_WithRoleAndMovement()
    {
        const string json = """
        {
          "domaines": [],
          "commandes": [
            { "role": "Commander", "domaine": "Dame", "name": "Generale", "asset": "generale", "hp": 30, "damage": 7, "moveRange": 2, "attackRange": 1 },
            { "role": "Boss",      "domaine": "Tour", "name": "Colosse",  "asset": "colosse",  "hp": 40, "damage": 9, "moveRange": 1, "attackRange": 2 }
          ]
        }
        """;

        var defs = DomaineCatalog.CommandesFromJson(json);

        var commander = defs.Single(d => d.Role == CommandeRole.Commander);
        Assert.Equal("Generale", commander.Name);
        Assert.Equal(Domaine.Dame, commander.Movement); // emprunte le déplacement de la Dame
        Assert.Equal(30, commander.BaseClass.MaxHp);

        var boss = defs.Single(d => d.Role == CommandeRole.Boss);
        Assert.Equal(Domaine.Tour, boss.Movement);
        Assert.Equal(2, boss.BaseClass.AttackRange);
        Assert.Equal(0, boss.Phase);   // "phase" absente → 0 (toutes phases)
    }

    [Fact]
    public void CommandesFromJson_ReadsBossPhase()
    {
        const string json = """
        {
          "domaines": [],
          "commandes": [
            { "role": "Boss", "domaine": "Dame", "name": "Boss2", "asset": "boss", "hp": 30, "damage": 9, "moveRange": 1, "attackRange": 1, "phase": 2 }
          ]
        }
        """;

        var boss = DomaineCatalog.CommandesFromJson(json).Single();
        Assert.Equal(2, boss.Phase);
    }

    [Fact]
    public void BossFor_PicksPhaseBoss_ThenAllPhases_ThenFirst()
    {
        const string json = """
        {
          "domaines": [],
          "commandes": [
            { "role": "Boss", "domaine": "Dame", "name": "BossAny", "asset": "boss", "hp": 30, "damage": 9, "moveRange": 1, "attackRange": 1, "phase": 0 },
            { "role": "Boss", "domaine": "Dame", "name": "Boss2",   "asset": "boss", "hp": 30, "damage": 9, "moveRange": 1, "attackRange": 1, "phase": 2 }
          ]
        }
        """;
        var defs = DomaineCatalog.CommandesFromJson(json);

        Assert.Equal("Boss2", Commandes.BossFor(defs, 2).Name);    // boss réservé à la phase 2
        Assert.Equal("BossAny", Commandes.BossFor(defs, 1).Name);  // pas de boss phase 1 → repli « toutes phases »
        Assert.Equal("BossAny", Commandes.BossFor(defs, 3).Name);  // idem phase 3
    }

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
