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
        { "domaine": "Pion", "baseClass": { "name": "Recrue", "asset": "recrue", "hp": 7, "damage": 2, "moveRange": 1, "attackRange": 1 } },
        { "domaine": "Tour", "baseClass": { "name": "Veilleur", "asset": "veilleur", "hp": 20, "damage": 3, "moveRange": 5, "attackRange": 4 } },
      ]
    }
    """;

    [Fact]
    public void FromJson_BuildsDomaineDefs_WithClassStats()
    {
        var defs = DomaineCatalog.FromJson(Json);

        var pion = defs.Single(d => d.Id == Domaine.Pion);
        Assert.Equal("Recrue", pion.BaseClass.Name);
        Assert.Equal("recrue", pion.BaseClass.Asset);
        Assert.Equal(7, pion.BaseClass.MaxHp);
        Assert.Equal(2, pion.BaseClass.Damage);
        Assert.Equal(1, pion.BaseClass.MoveRange);
        Assert.Equal(1, pion.BaseClass.AttackRange);

        var tour = defs.Single(d => d.Id == Domaine.Tour);
        Assert.Equal(4, tour.BaseClass.AttackRange);
        Assert.Equal(5, tour.BaseClass.MoveRange);
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
    }

    [Fact]
    public void Load_OverridesDefaults()
    {
        try
        {
            Domaines.Load(DomaineCatalog.FromJson(Json));

            Assert.Equal("Recrue", Domaines.Pion.BaseClass.Name);
            Assert.Equal(7, Units.Pion(Faction.Player).MaxHp);
        }
        finally
        {
            // Restaure les défauts pour ne pas polluer les autres tests.
            Domaines.Load(DomaineCatalog.FromJson(DefaultJson));
        }
    }

    // JSON équivalent aux valeurs par défaut, pour restaurer l'état après le test.
    private const string DefaultJson = """
    { "domaines": [
      { "domaine": "Pion",     "baseClass": { "name": "Soldat",     "asset": "soldat",     "hp": 10, "damage": 4, "moveRange": 1, "attackRange": 1 } },
      { "domaine": "Fou",      "baseClass": { "name": "Eclaireur",  "asset": "eclaireur",  "hp": 8,  "damage": 4, "moveRange": 3, "attackRange": 2 } },
      { "domaine": "Cavalier", "baseClass": { "name": "Cavalier",   "asset": "cavalier",   "hp": 10, "damage": 5, "moveRange": 1, "attackRange": 1 } },
      { "domaine": "Tour",     "baseClass": { "name": "Lancier",    "asset": "lancier",    "hp": 12, "damage": 4, "moveRange": 3, "attackRange": 3 } },
      { "domaine": "Dame",     "baseClass": { "name": "Archer",     "asset": "archer",     "hp": 8,  "damage": 5, "moveRange": 2, "attackRange": 3 } }
    ] }
    """;
}
