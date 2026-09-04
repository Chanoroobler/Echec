using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Campaign;
using ChessArmy.Core.Command;
using ChessArmy.Core.Command.Config;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Map;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// Arbre de commandement : règles d'achat (coût par niveau, prérequis de branche), gains de points,
/// application des bonus aux unités, plafonds de réserve/déploiement et persistance.
/// </summary>
public class CommandTreeTests
{
    private static CommandTree Tree => CommandTrees.ById(CommandTrees.DefaultTreeId);
    private static CommandNode Node(string id) => Tree.ById(id)!;

    /// <summary>Run neuve en placement, dotée de <paramref name="points"/> points de commandement.</summary>
    private static Run RunWithPoints(int points)
    {
        var roster = new List<UnitSpec> { new(Domaine.Dame, Commandes.Commander.BaseClass, essential: true) };
        return Run.Restore(roster, combatNumber: 1, seed: 1, firstRun: false, commandPoints: points);
    }

    // ─── Règles d'achat ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CostOf_IsTwicetheLevel()
    {
        Assert.Equal(2, CommandTree.CostOf(1));
        Assert.Equal(4, CommandTree.CostOf(2));
        Assert.Equal(6, CommandTree.CostOf(3));
        Assert.Equal(8, CommandTree.CostOf(4));
    }

    [Fact]
    public void Level1Nodes_HaveNoPrerequisite()
    {
        var owned = new HashSet<string>();
        foreach (var node in Tree.Nodes.Where(n => n.Level == 1))
            Assert.True(Tree.PrerequisiteMet(node, owned));
    }

    [Fact]
    public void Prerequisite_NeedsALowerNodeOfTheSameBranch()
    {
        // « logi_reserve_1 » (branche 2, niveau 2) n'est PAS ouvert par un niveau 1 d'une AUTRE branche.
        var otherBranch = new HashSet<string> { "cmd_Duelliste" };   // branche 0
        Assert.False(Tree.PrerequisiteMet(Node("logi_reserve_1"), otherBranch));

        var sameBranch = new HashSet<string> { "logi_deploiement_1" };   // branche 2
        Assert.True(Tree.PrerequisiteMet(Node("logi_reserve_1"), sameBranch));
    }

    [Fact]
    public void Prerequisite_AnyNodeOfTheLevelBelow_OpensTheWholeLevelAbove()
    {
        // Un seul des deux nœuds de niveau 2 suffit à ouvrir LES DEUX nœuds de niveau 3 de la branche.
        var owned = new HashSet<string> { "cmd_Duelliste", "cmd_vie" };
        Assert.True(Tree.PrerequisiteMet(Node("cmd_mouvement"), owned));
        Assert.True(Tree.PrerequisiteMet(Node("cmd_Orage"), owned));
        Assert.False(Tree.PrerequisiteMet(Node("cmd_portee"), owned));   // niveau 4 : rien au niveau 3
    }

    [Fact]
    public void Unlock_SpendsPoints_AndRefusesWhenTooExpensive()
    {
        var run = RunWithPoints(2);
        Assert.True(run.CanUnlock(Node("cmd_Duelliste")));
        Assert.True(run.Unlock(Node("cmd_Duelliste")));
        Assert.Equal(0, run.CommandPoints);
        Assert.True(run.IsUnlocked("cmd_Duelliste"));

        // Plus assez pour le niveau 2 (4 points) : refusé, et rien ne bouge.
        Assert.False(run.CanUnlock(Node("cmd_vie")));
        Assert.False(run.Unlock(Node("cmd_vie")));
        Assert.Equal(0, run.CommandPoints);
        Assert.False(run.IsUnlocked("cmd_vie"));
    }

    [Fact]
    public void Unlock_RefusesLockedNode_EvenWithEnoughPoints()
    {
        var run = RunWithPoints(100);
        Assert.False(run.CanUnlock(Node("cmd_portee")));   // niveau 4, aucun prérequis possédé
        Assert.False(run.Unlock(Node("cmd_portee")));
        Assert.Equal(100, run.CommandPoints);
    }

    [Fact]
    public void Unlock_RefusedOutsidePlacement()
    {
        var run = RunWithPoints(10);
        run.StartBattle();
        Assert.False(run.CanUnlock(Node("cmd_Duelliste")));
        Assert.False(run.Unlock(Node("cmd_Duelliste")));
    }

    [Fact]
    public void Unlock_RefusesAlreadyOwnedNode()
    {
        var run = RunWithPoints(10);
        Assert.True(run.Unlock(Node("cmd_Duelliste")));
        Assert.False(run.CanUnlock(Node("cmd_Duelliste")));
        Assert.Equal(8, run.CommandPoints);   // débité une seule fois
    }

    // ─── Gain de points ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CompleteCombat_GrantsPointsPerMission()
    {
        var run = new Run(seed: 1);
        Assert.Equal(0, run.CommandPoints);
        run.StartBattle();
        run.CompleteCombat(casualties: new List<UnitSpec>(), defeatedEnemies: new List<UnitSpec>());
        Assert.Equal(Run.PointsPerMission, run.CommandPoints);
    }

    [Fact]
    public void CompleteSpecialNoDraft_GrantsPointsPerMission()
    {
        var run = new Run(seed: 1);
        run.StartBattle();
        run.CompleteSpecialNoDraft(casualties: new List<UnitSpec>());
        Assert.Equal(Run.PointsPerMission, run.CommandPoints);
    }

    [Fact]
    public void Fusion_GrantsTheCommanderFusionPoints()
    {
        var run = new Run(seed: 1);
        var soldat = Domaines.Dame.BaseClass;
        run.AddUnit(new UnitSpec(Domaine.Dame, soldat));   // 3 soldats au total avec les 2 de départ

        var fused = run.Fuse(run.Roster.First(u => !u.Essential), soldat.Evolutions[0]);
        Assert.NotNull(fused);
        Assert.Equal(run.CommanderDef.FusionPoints, run.CommandPoints);
        Assert.True(run.CommanderDef.FusionPoints > 0, "le commandant de départ gagne des points par fusion");
    }

    // ─── Plafonds : réserve et déploiement ───────────────────────────────────────────────────────

    [Fact]
    public void ReserveAndDeployLimits_StartAtTheCommanderBaseValues()
    {
        var run = new Run(seed: 1);
        Assert.Equal(run.CommanderDef.ReserveSize, run.ReserveLimit);
        Assert.Equal(run.CommanderDef.Deployments, run.DeployLimit);
    }

    [Fact]
    public void LogisticsNodes_GrowReserveAndDeployLimits()
    {
        var run = RunWithPoints(100);
        var baseReserve = run.ReserveLimit;
        var baseDeploy = run.DeployLimit;

        run.Unlock(Node("logi_deploiement_1"));   // +1 déploiement
        run.Unlock(Node("logi_reserve_1"));       // +2 réserve
        Assert.Equal(baseDeploy + 1, run.DeployLimit);
        Assert.Equal(baseReserve + 2, run.ReserveLimit);

        run.Unlock(Node("logi_reserve_2"));       // +2 réserve (niveau 3, ouvert par le niveau 2)
        run.Unlock(Node("logi_reserve_3"));       // +4 réserve (niveau 4)
        Assert.Equal(baseReserve + 8, run.ReserveLimit);
    }

    [Fact]
    public void EliteDeathRecruits_ZeroUntilTheReliefNodeIsBought()
    {
        var run = RunWithPoints(100);
        Assert.Equal(0, run.EliteDeathRecruits);

        run.Unlock(Node("troupe_vie"));      // niveau 1, ouvre la branche
        run.Unlock(Node("troupe_puissance"));// niveau 2
        run.Unlock(Node("troupe_releve"));   // niveau 3 : nœud « relève »
        Assert.Equal(1, run.EliteDeathRecruits);
    }

    [Fact]
    public void GrantEliteDeathReplacements_AddsOneSeenT1_PerFallenElite()
    {
        var run = RunWithPoints(100);
        run.Unlock(Node("troupe_vie"));
        run.Unlock(Node("troupe_puissance"));
        run.Unlock(Node("troupe_releve"));

        var before = run.ReserveCount;
        var archer = Domaines.Dame.BaseClass.Evolutions[0];   // tier 2
        var arbaletrier = archer.Evolutions[0];               // tier 3
        var casualties = new[]
        {
            new UnitSpec(Domaine.Dame, archer),                       // tier 2  → 1 relève
            new UnitSpec(Domaine.Dame, arbaletrier),                  // tier 3  → 1 relève
            new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass),      // tier 1  → aucune
        };

        var added = run.GrantEliteDeathReplacements(casualties, new System.Random(1), _ => true);

        Assert.Equal(2, added.Count);
        Assert.All(added, u => Assert.Equal(1, u.UnitClass.Tier));   // ce sont des pions de base (T1)
        Assert.Equal(before + 2, run.ReserveCount);
    }

    [Fact]
    public void GrantEliteDeathReplacements_DoesNothingWithoutTheNode()
    {
        var run = RunWithPoints(100);   // nœud « relève » NON acheté
        var archer = Domaines.Dame.BaseClass.Evolutions[0];   // tier 2
        var added = run.GrantEliteDeathReplacements(
            new[] { new UnitSpec(Domaine.Dame, archer) }, new System.Random(1), _ => true);

        Assert.Empty(added);
    }

    // ─── Application des bonus aux unités ────────────────────────────────────────────────────────

    [Fact]
    public void CommanderTraitNode_AppliesToTheCommanderOnly()
    {
        var run = RunWithPoints(2);
        run.Unlock(Node("cmd_Duelliste"));   // ce nœud octroie le trait Duelliste au commandant

        var commander = run.Commander;
        Assert.True(commander.Spawn(Faction.Player, run.BuffsFor(commander)).HasTrait(Trait.Duelliste));

        var troop = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
        Assert.False(troop.Spawn(Faction.Player, run.BuffsFor(troop)).HasTrait(Trait.Duelliste));
    }

    [Fact]
    public void TroopStatNode_AppliesToTroopsOnly()
    {
        var run = RunWithPoints(2);
        run.Unlock(Node("troupe_vie"));   // +2 PV aux unités (hors commandant)

        var soldat = Domaines.Dame.BaseClass;
        var troop = new UnitSpec(Domaine.Dame, soldat);
        Assert.Equal(soldat.MaxHp + 2, troop.Spawn(Faction.Player, run.BuffsFor(troop)).MaxHp);

        var commander = run.Commander;
        Assert.Equal(commander.UnitClass.MaxHp, commander.Spawn(Faction.Player, run.BuffsFor(commander)).MaxHp);
    }

    [Fact]
    public void ActiveNodesFor_ListsOnlyTheNodesActingOnThatTarget()
    {
        var run = RunWithPoints(100);
        run.Unlock(Node("cmd_Duelliste"));           // commandant
        run.Unlock(Node("troupe_vie"));            // troupes
        run.Unlock(Node("logi_deploiement_1"));    // logistique : n'agit sur AUCUNE unité

        Assert.Equal(new[] { "cmd_Duelliste" }, run.ActiveNodesFor(commander: true).Select(n => n.Id));
        Assert.Equal(new[] { "troupe_vie" }, run.ActiveNodesFor(commander: false).Select(n => n.Id));
    }

    /// <summary>
    /// La carte d'un pion affiche une icône par nœud d'arbre qui le CONCERNE : un nœud restreint à un autre
    /// domaine ne doit pas y figurer (une icône « domaine du Cavalier » sur un Soldat annonce un bonus qu'il
    /// ne reçoit pas). Le filtre doit donc coller à celui de <see cref="CommandBuffs.From"/>.
    /// </summary>
    [Fact]
    public void ActiveNodesFor_HidesNodesRestrictedToAnotherDomaine()
    {
        try
        {
            CommandTrees.Load(new[]
            {
                new CommandTree(CommandTrees.DefaultTreeId, new[]
                {
                    new CommandNode("tous", 0, 1, "tous", new[] { CommandEffect.UnitStat(EquipStat.Hp, 2) }),
                    new CommandNode("cavaliers", 1, 1, "cavaliers",
                        new[] { CommandEffect.UnitStat(EquipStat.Hp, 4, domaine: Domaine.Cavalier) }),
                }),
            });

            var run = RunWithPoints(100);
            run.Unlock(CommandTrees.ById(CommandTrees.DefaultTreeId).ById("tous")!);
            run.Unlock(CommandTrees.ById(CommandTrees.DefaultTreeId).ById("cavaliers")!);

            // Le pion Cavalier voit les deux ; le Soldat (domaine Dame) ne voit que le nœud non restreint.
            Assert.Equal(new[] { "tous", "cavaliers" },
                run.ActiveNodesFor(commander: false, Domaine.Cavalier).Select(n => n.Id));
            Assert.Equal(new[] { "tous" },
                run.ActiveNodesFor(commander: false, Domaine.Dame).Select(n => n.Id));
            // Sans domaine (appelant qui ne le connaît pas) : aucun filtre, comportement historique.
            Assert.Equal(new[] { "tous", "cavaliers" },
                run.ActiveNodesFor(commander: false).Select(n => n.Id));
        }
        finally
        {
            CommandTrees.ResetToDefaults();
        }
    }

    [Fact]
    public void EnemiesNeverGetBuffs()
    {
        var soldat = Domaines.Dame.BaseClass;
        var enemy = new UnitSpec(Domaine.Dame, soldat).Spawn(Faction.Enemy);
        Assert.Equal(soldat.MaxHp, enemy.MaxHp);
        Assert.Same(CommandBuffs.None, enemy.Buffs);
    }

    // ─── Bonus « par paire de classes distinctes » ───────────────────────────────────────────────

    [Fact]
    public void DistinctPairs_CountsDistinctClassesHalved_ExcludingCommander()
    {
        var run = new Run(seed: 1);                       // commandant + 2 Soldats → 1 classe distincte
        Assert.Equal(0, run.DistinctPairs);

        var dame = Domaines.Dame.BaseClass;
        run.AddUnit(new UnitSpec(Domaine.Fou, Domaines.Fou.BaseClass));    // 2 classes → 1 paire
        Assert.Equal(1, run.DistinctPairs);

        run.AddUnit(new UnitSpec(Domaine.Tour, Domaines.Tour.BaseClass));  // 3 classes → toujours 1 paire
        Assert.Equal(1, run.DistinctPairs);

        run.AddUnit(new UnitSpec(Domaine.Cavalier, Domaines.Cavalier.BaseClass));  // 4 classes → 2 paires
        Assert.Equal(2, run.DistinctPairs);

        run.AddUnit(new UnitSpec(Domaine.Dame, dame));    // doublon de classe : aucune paire de plus
        Assert.Equal(2, run.DistinctPairs);
    }

    [Fact]
    public void PerDistinctPairNode_ScalesWithTheRosterVariety()
    {
        var run = RunWithPoints(100);
        run.Unlock(Node("cmd_Duelliste"));
        run.Unlock(Node("cmd_vie"));     // +2 PV au commandant PAR PAIRE de classes distinctes

        var commander = run.Commander;
        var baseHp = commander.UnitClass.MaxHp;
        Assert.Equal(baseHp, commander.Spawn(Faction.Player, run.BuffsFor(commander)).MaxHp);   // roster vide : 0 paire

        run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
        run.AddUnit(new UnitSpec(Domaine.Fou, Domaines.Fou.BaseClass));                          // 2 classes → 1 paire
        Assert.Equal(baseHp + 2, commander.Spawn(Faction.Player, run.BuffsFor(commander)).MaxHp);

        run.AddUnit(new UnitSpec(Domaine.Tour, Domaines.Tour.BaseClass));
        run.AddUnit(new UnitSpec(Domaine.Cavalier, Domaines.Cavalier.BaseClass));                // 4 classes → 2 paires
        Assert.Equal(baseHp + 4, commander.Spawn(Faction.Player, run.BuffsFor(commander)).MaxHp);
    }

    [Fact]
    public void PerDistinctPairDamage_ReachesTheAttackResolution()
    {
        // Le bonus « puissance par paire » doit s'appliquer aux DÉGÂTS RÉELLEMENT INFLIGÉS (Match), pas
        // seulement à la stat affichée : c'est Unit.Damage que lit EffectiveDamage.
        var run = RunWithPoints(100);
        run.Unlock(Node("troupe_vie"));
        run.Unlock(Node("troupe_puissance"));   // +1 puissance par paire de classes distinctes

        run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
        run.AddUnit(new UnitSpec(Domaine.Fou, Domaines.Fou.BaseClass));
        run.AddUnit(new UnitSpec(Domaine.Tour, Domaines.Tour.BaseClass));
        run.AddUnit(new UnitSpec(Domaine.Cavalier, Domaines.Cavalier.BaseClass));
        Assert.Equal(2, run.DistinctPairs);   // 4 classes distinctes → 2 paires → +2 puissance

        var attackerSpec = run.Roster.First(u => !u.Essential && u.Domaine == Domaine.Dame);
        var attacker = attackerSpec.Spawn(Faction.Player, run.BuffsFor(attackerSpec));
        Assert.Equal(attackerSpec.UnitClass.Damage + 2, attacker.Damage);

        // Un gros sac de PV encaisse le coup : on lit la différence de PV, sans autre trait en jeu.
        var target = new UnitClass("Cible", "cible", tier: 1, maxHp: 100, damage: 0, moveRange: 1, attackRange: 1);
        var match = new Match(8, 8);
        var from = new Cell(4, 4);
        var to = new Cell(4, 3);
        match.Place(from, attacker);
        match.Place(to, new Unit(Domaine.Dame, Faction.Enemy, target));

        match.TryAttack(from, to);
        Assert.Equal(100 - (attackerSpec.UnitClass.Damage + 2), match.UnitAt(to)!.Hp);
    }

    [Fact]
    public void Buffs_StackWithEquipment()
    {
        var run = RunWithPoints(2);
        run.Unlock(Node("troupe_vie"));   // +2 PV

        var soldat = Domaines.Dame.BaseClass;
        var troop = new UnitSpec(Domaine.Dame, soldat);
        troop.AddEquipment(Equipment.OfStat("test_pv", "Test", EquipStat.Hp, 5));
        Assert.Equal(soldat.MaxHp + 2 + 5, troop.Spawn(Faction.Player, run.BuffsFor(troop)).MaxHp);
    }

    // ─── Persistance ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RunSave_RoundTripsPointsAndUnlockedNodes()
    {
        var run = RunWithPoints(10);
        run.Unlock(Node("logi_deploiement_1"));   // -2 → 8 points

        var restored = RunSave.From(run).ToRun();
        Assert.Equal(8, restored.CommandPoints);
        Assert.True(restored.IsUnlocked("logi_deploiement_1"));
        Assert.Equal(run.DeployLimit, restored.DeployLimit);
    }

    [Fact]
    public void Restore_IgnoresNodesUnknownToTheCurrentTree()
    {
        var roster = new List<UnitSpec> { new(Domaine.Dame, Commandes.Commander.BaseClass, essential: true) };
        var run = Run.Restore(roster, 1, 1, false, commandPoints: 4,
            unlockedNodes: new[] { "cmd_Duelliste", "noeud_supprime_du_json" });

        Assert.True(run.IsUnlocked("cmd_Duelliste"));
        Assert.Single(run.UnlockedNodes);
    }

    // ─── Chargement JSON ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_ParsesNodesEffectsAndScales()
    {
        const string json = """
        {
          "trees": [{
            "id": "test",
            "nodes": [
              { "id": "a", "branch": 0, "level": 1, "icon": "icone_a",
                "effects": [ { "kind": "commanderTrait", "trait": "Rempart" } ] },
              { "id": "b", "branch": 0, "level": 2,
                "effects": [ { "kind": "unitStat", "stat": "damage", "amount": 3, "scale": "perDistinctPair" },
                             { "kind": "reserveSlots", "amount": 2 } ] }
            ]
          }]
        }
        """;

        var tree = CommandTreeCatalog.FromJson(json).Single();
        Assert.Equal("test", tree.Id);
        Assert.Equal(1, tree.BranchCount);

        var a = tree.ById("a")!;
        Assert.Equal("icone_a", a.Icon);
        Assert.Equal(2, a.Cost);
        Assert.Equal(Trait.Rempart, a.Effects.Single().Trait);

        var b = tree.ById("b")!;
        Assert.Equal("b", b.Icon);   // icône absente → l'id du nœud
        Assert.Equal(2, b.Effects.Count);
        Assert.Equal(9, b.Effects[0].AmountFor(distinctPairs: 3));
        Assert.Equal(CommandEffectKind.ReserveSlots, b.Effects[1].Kind);
    }

    [Theory]
    [InlineData("""{"trees":[{"id":"t","nodes":[{"id":"a","branch":0,"level":9,"effects":[{"kind":"deploySlots"}]}]}]}""")]
    [InlineData("""{"trees":[{"id":"t","nodes":[{"id":"a","branch":0,"level":1,"effects":[]}]}]}""")]
    [InlineData("""{"trees":[{"id":"t","nodes":[{"id":"a","branch":0,"level":1,"effects":[{"kind":"inconnu"}]}]}]}""")]
    [InlineData("""{"trees":[{"id":"t","nodes":[{"id":"a","branch":0,"level":1,"effects":[{"kind":"unitTrait","trait":"PasUnTrait"}]}]}]}""")]
    [InlineData("""{"trees":[{"id":"t","nodes":[{"id":"a","branch":0,"level":1,"effects":[{"kind":"unitStat","stat":"pv"}]}]}]}""")]
    public void Catalog_RejectsInvalidNodes(string json) =>
        Assert.ThrowsAny<System.Exception>(() => CommandTreeCatalog.FromJson(json));

    [Fact]
    public void NodeIcon_DefaultsToTheNodeId()
    {
        // Sans champ « icon » explicite dans le JSON, l'icône d'un nœud est son id — quel que soit l'effet.
        Assert.Equal("cmd_Duelliste", Node("cmd_Duelliste").Icon);
        Assert.Equal("logi_reserve_1", Node("logi_reserve_1").Icon);
    }

    [Fact]
    public void NodeAsset_TakesPriorityOverIconAndId()
    {
        // « asset » (aligné sur units.json) l'emporte sur « icon » (alias hérité), qui l'emporte sur l'id.
        const string json = """
        {
          "trees": [{
            "id": "t",
            "nodes": [
              { "id": "a", "branch": 0, "level": 1, "asset": "png_a",
                "effects": [ { "kind": "deploySlots", "amount": 1 } ] },
              { "id": "b", "branch": 0, "level": 1, "asset": "png_b", "icon": "ignore_moi",
                "effects": [ { "kind": "deploySlots", "amount": 1 } ] },
              { "id": "c", "branch": 0, "level": 1, "icon": "png_c",
                "effects": [ { "kind": "deploySlots", "amount": 1 } ] },
              { "id": "d", "branch": 0, "level": 1,
                "effects": [ { "kind": "deploySlots", "amount": 1 } ] }
            ]
          }]
        }
        """;

        var tree = CommandTreeCatalog.FromJson(json).Single();
        Assert.Equal("png_a", tree.ById("a")!.Icon);      // asset seul
        Assert.Equal("png_b", tree.ById("b")!.Icon);      // asset prioritaire sur icon
        Assert.Equal("png_c", tree.ById("c")!.Icon);      // icon seul (repli hérité)
        Assert.Equal("d", tree.ById("d")!.Icon);          // rien → l'id du nœud
    }

    [Fact]
    public void DefaultTree_MatchesTheDocumentedShape()
    {
        Assert.Equal(3, Tree.BranchCount);
        Assert.Equal(17, Tree.Nodes.Count);
        // Chaque branche a au moins un nœud à chaque niveau : sinon un palier serait infranchissable.
        for (var branch = 0; branch < Tree.BranchCount; branch++)
            for (var level = 1; level <= CommandTree.MaxLevel; level++)
                Assert.NotEmpty(Tree.At(branch, level));
    }

    // ─── Config LIVRÉE ───────────────────────────────────────────────────────────────────────────
    // Le jeu ignore SILENCIEUSEMENT un commander_trees.json invalide (repli sur le codé) : sans ce test,
    // une faute — trait inconnu, id renommé sans sa traduction — ne se verrait qu'en jouant.

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string AssetPath(params string[] parts) =>
        System.IO.Path.Combine(new[] { RepoRoot(), "src", "ChessArmy.Game", "Assets" }.Concat(parts).ToArray());

    [Fact]
    public void ShippedTrees_Parse_AndEveryNodeHasItsLocalizedLabelAndDescription()
    {
        var trees = CommandTreeCatalog.FromJson(
            System.IO.File.ReadAllText(AssetPath("Config", "commander_trees.json")));
        Assert.NotEmpty(trees);

        // strings.csv : chaque nœud DOIT avoir « tree.<id> » et « tree.<id>.desc », sinon l'infobulle affiche
        // la clé brute (le bug d'un id renommé sans sa traduction). Idem pour les libellés de branche et la
        // ligne de gain de chaque arbre.
        var keys = new HashSet<string>();
        foreach (var line in System.IO.File.ReadAllLines(AssetPath("Config", "strings.csv")))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;
            var comma = line.IndexOf(',');
            if (comma > 0)
                keys.Add(line[..comma].Trim());
        }

        foreach (var tree in trees)
        {
            foreach (var node in tree.Nodes)
            {
                Assert.True(keys.Contains(node.NameKey), $"libellé manquant : {node.NameKey}");
                Assert.True(keys.Contains(node.DescKey), $"description manquante : {node.DescKey}");
            }

            // Libellés de branche (clé tree.<id>.branch<N>) et ligne de gain (tree.<id>.income).
            for (var branch = 0; branch < tree.BranchCount; branch++)
                Assert.True(keys.Contains($"tree.{tree.Id}.branch{branch}"),
                    $"libellé de branche manquant : tree.{tree.Id}.branch{branch}");
            Assert.True(keys.Contains($"tree.{tree.Id}.income"),
                $"ligne de gain manquante : tree.{tree.Id}.income");
        }
    }

    // ─── Effets CIBLÉS PAR DOMAINE / échelle par domaine (arbre Lancier) ──────────────────────────

    // ─── Source de points « sur coup reçu » (commandant Lancier) ─────────────────────────────────

    [Fact]
    public void GrantCommanderHitPoints_CreditsPerHit_UpToCap()
    {
        var onHit = new CommandeDef(CommandeRole.Commander, Domaine.Dame,
            new UnitClass("L", "l", tier: 1, maxHp: 40, damage: 10, moveRange: 1, attackRange: 1),
            fusionPoints: 0, onHitPoints: 1, onHitCap: 2);
        var run = new Run(seed: 1, commander: onHit);   // CommandPoints = 0 au départ

        run.GrantCommanderHitPoints(1);
        Assert.Equal(1, run.CommandPoints);              // 1 coup → 1 point

        run.GrantCommanderHitPoints(5);
        Assert.Equal(3, run.CommandPoints);              // plafonné à 2 coups → +2 (total 3)

        run.GrantCommanderHitPoints(0);
        Assert.Equal(3, run.CommandPoints);              // 0 coup → rien
    }

    [Fact]
    public void GrantCommanderHitPoints_NoOp_WhenCommanderHasNoOnHitSource()
    {
        var fusionCmd = new CommandeDef(CommandeRole.Commander, Domaine.Dame,
            new UnitClass("C", "c", tier: 1, maxHp: 28, damage: 12, moveRange: 1, attackRange: 1),
            fusionPoints: 2);   // OnHitPoints = 0 (défaut)
        var run = new Run(seed: 1, commander: fusionCmd);
        run.GrantCommanderHitPoints(5);
        Assert.Equal(0, run.CommandPoints);
    }

    [Fact]
    public void GrantCommanderRangedHitPoints_CreditsPerHit_UpToCap()
    {
        // Commandant du Fou : +1 point par coup à distance, 2 max par combat.
        var rangedCmd = new CommandeDef(CommandeRole.Commander, Domaine.Fou,
            new UnitClass("F", "f", tier: 1, maxHp: 20, damage: 15, moveRange: 2, attackRange: 3),
            rangedHitPoints: 1, rangedHitCap: 2);
        var run = new Run(seed: 1, commander: rangedCmd);   // CommandPoints = 0 au départ

        run.GrantCommanderRangedHitPoints(1);
        Assert.Equal(1, run.CommandPoints);              // 1 coup à distance → 1 point

        run.GrantCommanderRangedHitPoints(5);
        Assert.Equal(3, run.CommandPoints);              // plafonné à 2 coups → +2 (total 3)

        run.GrantCommanderRangedHitPoints(0);
        Assert.Equal(3, run.CommandPoints);              // 0 coup → rien
    }

    [Fact]
    public void GrantCommanderRangedHitPoints_NoOp_WhenCommanderHasNoRangedSource()
    {
        var fusionCmd = new CommandeDef(CommandeRole.Commander, Domaine.Dame,
            new UnitClass("C", "c", tier: 1, maxHp: 28, damage: 12, moveRange: 1, attackRange: 1),
            fusionPoints: 2);   // RangedHitPoints = 0 (défaut)
        var run = new Run(seed: 1, commander: fusionCmd);
        run.GrantCommanderRangedHitPoints(5);
        Assert.Equal(0, run.CommandPoints);
    }

    // ─── Effets CIBLÉS PAR DOMAINE / échelle par domaine (arbre Lancier) ──────────────────────────

    [Fact]
    public void UnitStat_DomaineFilter_OnlyBuffsThatDomaine()
    {
        var effects = new[] { CommandEffect.UnitStat(EquipStat.Hp, 4, domaine: Domaine.Tour) };
        var tour = CommandBuffs.From(effects, commander: false, distinctPairs: 0, targetDomaine: Domaine.Tour);
        var dame = CommandBuffs.From(effects, commander: false, distinctPairs: 0, targetDomaine: Domaine.Dame);
        Assert.Equal(4, tour.BonusFor(EquipStat.Hp));   // unité du bon domaine : bonus appliqué
        Assert.Equal(0, dame.BonusFor(EquipStat.Hp));   // autre domaine : pas touchée
    }

    [Fact]
    public void PerDomaineUnit_ScalesByDomaineUnitCount()
    {
        var effects = new[] { CommandEffect.CommanderStat(EquipStat.Hp, 3, CommandScale.PerDomaineUnit, Domaine.Tour) };
        var buffs = CommandBuffs.From(effects, commander: true, distinctPairs: 0,
            targetDomaine: null, domaineCount: d => d == Domaine.Tour ? 2 : 0);
        Assert.Equal(6, buffs.BonusFor(EquipStat.Hp));   // 3 × 2 unités du domaine Tour
    }

    [Fact]
    public void UnitTrait_DomaineFilter_OnlyGrantsToThatDomaine()
    {
        var effects = new[] { CommandEffect.UnitTrait(Trait.AuraDePuissance, Domaine.Tour) };
        Assert.True(CommandBuffs.From(effects, false, 0, Domaine.Tour).GrantsTrait(Trait.AuraDePuissance));
        Assert.False(CommandBuffs.From(effects, false, 0, Domaine.Fou).GrantsTrait(Trait.AuraDePuissance));
    }

    [Fact]
    public void LancierTree_RecruitAndTargetedNodes_CarryTourDomaine()
    {
        var trees = CommandTreeCatalog.FromJson(
            System.IO.File.ReadAllText(AssetPath("Config", "commander_trees.json")));
        var lancier = trees.Single(t => t.Id == "commandantLancier");

        CommandEffect Effect(string nodeId) => lancier.ById(nodeId)!.Effects.Single();

        // Recrutement d'un LANCIER (domaine Tour) plutôt qu'un tier 1 générique.
        var fusion = Effect("lan_logi_fusion");
        Assert.Equal(CommandEffectKind.FusionRecruit, fusion.Kind);
        Assert.Equal(Domaine.Tour, fusion.Domaine);

        var releve = Effect("lan_tour_releve");
        Assert.Equal(CommandEffectKind.EliteDeathRecruit, releve.Kind);
        Assert.Equal(Domaine.Tour, releve.Domaine);

        // Bonus d'unité restreint au domaine de la tour + échelle par unité de ce domaine (côté commandant).
        Assert.Equal(Domaine.Tour, Effect("lan_tour_pv").Domaine);
        Assert.Equal(Domaine.Tour, Effect("lan_cmd_pv_tour").Domaine);
        Assert.Equal(CommandScale.PerDomaineUnit, Effect("lan_cmd_pv_tour").Scale);

        // « Amalgame » du Bastion : la réduction de taille de fusion est restreinte au domaine de la tour.
        var amalgame = Effect("lan_logi_reserve_2");
        Assert.Equal(CommandEffectKind.FusionSizeReduction, amalgame.Kind);
        Assert.Equal(Domaine.Tour, amalgame.Domaine);
    }

    /// <summary>
    /// Le CHIFFRE d'un nœud ne vit que dans commander_trees.json : la description l'injecte via <c>{0}</c>
    /// (cf. <see cref="CommandNode.DescArgs"/>). Sans ce test, rééquilibrer un nœud (« Meneur de cavalerie »
    /// passé de +3 à +1) laisse une infobulle qui MENT — le bug ne se voit qu'en survolant le nœud en jeu.
    /// Ne concerne PAS les nombres qui viennent d'une constante du moteur (orage, réduction de base du rempart,
    /// taille de fusion) : ils ne sont pas dans le JSON, donc pas dans <c>DescArgs</c>.
    /// </summary>
    [Fact]
    public void ShippedTreeDescriptions_TakeTheirAmountsFromTheJson_NotFromTheText()
    {
        var trees = CommandTreeCatalog.FromJson(
            System.IO.File.ReadAllText(AssetPath("Config", "commander_trees.json")));

        var french = new Dictionary<string, string>();
        foreach (var line in System.IO.File.ReadAllLines(AssetPath("Config", "strings.csv")))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;
            var parts = line.Split(',');   // les valeurs de strings.csv ne contiennent jamais de virgule
            if (parts.Length > 1)
                french[parts[0].Trim()] = parts[1];
        }

        foreach (var node in trees.SelectMany(t => t.Nodes))
        {
            if (!french.TryGetValue(node.DescKey, out var desc))
                continue;   // la PRÉSENCE de la clé est gardée par ShippedTrees_...

            // Le gabarit doit se formater avec les montants du nœud (pas de {1} sur un nœud à un seul effet).
            var error = Record.Exception(() => string.Format(desc, node.DescArgs));
            Assert.True(error is null, $"{node.DescKey} : gabarit incompatible avec ses effets ({error?.Message})");

            // Un bonus chiffré en tête de description doit venir du JSON.
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(desc, @"^\+\d"),
                $"{node.DescKey} : montant écrit en dur (\"{desc}\") — écrire « +{{0}} ».");
        }
    }

    // ─── Arbre du CAVALIER : charge (tour bonus), Impact renforcé, échelle « par pion déployé » ───

    [Fact]
    public void CavalierTree_ChargeAndReinforcedImpact_AreWiredToTheKnightDomaine()
    {
        var trees = CommandTreeCatalog.FromJson(
            System.IO.File.ReadAllText(AssetPath("Config", "commander_trees.json")));
        var cavalier = trees.Single(t => t.Id == "commandantCavalier");

        CommandEffect Effect(string nodeId) => cavalier.ById(nodeId)!.Effects.Single();

        // « Charge » : le tour bonus est restreint au domaine du cavalier.
        var charge = Effect("cav_unit_charge");
        Assert.Equal(CommandEffectKind.ExtraTurnOnKill, charge.Kind);
        Assert.Equal(Domaine.Cavalier, charge.Domaine);

        // « Choc renforcé » : +3 aux dégâts d'Impact (5 → 8), sans domaine (c'est un réglage moteur).
        var impact = Effect("cav_unit_impact_renforce");
        Assert.Equal(CommandEffectKind.ImpactBonus, impact.Kind);
        Assert.Equal(3, impact.Amount);
        Assert.Equal(8, Match.BaseImpactDamage + impact.Amount);

        // « Ruée » : puissance à l'échelle des seuls pions DÉPLOYÉS du domaine.
        var ruee = Effect("cav_unit_puissance_cavalier");
        Assert.Equal(CommandScale.PerDeployedDomaineUnit, ruee.Scale);
        Assert.Equal(Domaine.Cavalier, ruee.Domaine);

        // Recrues et amalgame ciblent bien le cavalier.
        Assert.Equal(Domaine.Cavalier, Effect("cav_logi_fusion").Domaine);
        Assert.Equal(Domaine.Cavalier, Effect("cav_unit_releve").Domaine);
        Assert.Equal(Domaine.Cavalier, Effect("cav_logi_amalgame").Domaine);
    }

    /// <summary>
    /// units.json est chargé de la même façon que les arbres (repli SILENCIEUX sur le codé s'il ne parse pas) :
    /// on vérifie ici que la config LIVRÉE se lit, que chaque commandant pointe sur un arbre existant, et que
    /// tout trait écrit à la main (commandant ou profil de boss) est un trait CONNU du moteur — une faute de
    /// frappe ne se verrait sinon qu'en jouant, sous la forme d'un trait qui ne fait rien.
    /// </summary>
    [Fact]
    public void ShippedCommandes_Parse_AndDeclareKnownTreesAndTraits()
    {
        var json = System.IO.File.ReadAllText(AssetPath("Config", "units.json"));
        var trees = CommandTreeCatalog.FromJson(
            System.IO.File.ReadAllText(AssetPath("Config", "commander_trees.json")));
        var treeIds = trees.Select(t => t.Id).ToHashSet();

        var commanders = Battle.Config.DomaineCatalog.CommandesFromJson(json);
        Assert.NotEmpty(commanders);
        foreach (var c in commanders)
        {
            Assert.True(treeIds.Contains(c.TreeId), $"arbre inconnu pour le commandant '{c.Id}' : {c.TreeId}");
            foreach (var t in c.BaseClass.Traits)
                Assert.True(Trait.All.Contains(t), $"trait inconnu sur le commandant '{c.Id}' : {t}");
        }

        var bosses = Battle.Config.DomaineCatalog.BossesFromJson(json);
        Assert.NotEmpty(bosses);

        // Le pool d'équipement d'un boss est écrit à la main : une faute de frappe le désarmerait en silence
        // (l'id inconnu est sauté au tirage). On le confronte donc au catalogue RÉELLEMENT LIVRÉ.
        var equipmentIds = Equip.EquipmentCatalog
            .FromJson(System.IO.File.ReadAllText(AssetPath("Config", "equipment.json")))
            .Select(e => e.Id).ToHashSet();

        foreach (var b in bosses)
        {
            foreach (var profile in b.Profiles.Values)
                foreach (var t in profile.Traits)
                    Assert.True(Trait.All.Contains(t), $"trait inconnu sur le boss '{b.Name}' : {t}");

            foreach (var id in b.EquipmentPool)
                Assert.True(equipmentIds.Contains(id), $"équipement inconnu dans le pool du boss '{b.Name}' : {id}");

            // Un boss ne peut pas porter deux fois le même objet : le pool doit couvrir la phase la plus gourmande.
            for (var phase = 1; phase <= 3; phase++)
                Assert.True(b.EquipmentCountFor(phase) <= b.EquipmentPool.Count,
                    $"pool trop petit pour le boss '{b.Name}' en phase {phase}");
        }
    }

    [Fact]
    public void PerDeployedDomaineUnit_ScalesOnTheDeployedCounter_NotTheRoster()
    {
        var effects = new[]
        {
            CommandEffect.UnitStat(EquipStat.Damage, 2, CommandScale.PerDeployedDomaineUnit, Domaine.Cavalier),
        };

        // 3 cavaliers posés → +6 ; le compteur de ROSTER (domaineCount) ne doit pas être utilisé.
        var buffs = CommandBuffs.From(effects, commander: false, distinctPairs: 0, Domaine.Cavalier,
            domaineCount: _ => 99, deployedCount: _ => 3);
        Assert.Equal(6, buffs.BonusFor(EquipStat.Damage));

        // Sans compteur de déploiement (aperçu hors plateau) : le bonus vaut 0.
        var none = CommandBuffs.From(effects, commander: false, distinctPairs: 0, Domaine.Cavalier,
            domaineCount: _ => 99);
        Assert.Equal(0, none.BonusFor(EquipStat.Damage));
    }
}
