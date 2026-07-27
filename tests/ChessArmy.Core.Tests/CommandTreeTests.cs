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
        var otherBranch = new HashSet<string> { "cmd_Esquive" };   // branche 0
        Assert.False(Tree.PrerequisiteMet(Node("logi_reserve_1"), otherBranch));

        var sameBranch = new HashSet<string> { "logi_deploiement_1" };   // branche 2
        Assert.True(Tree.PrerequisiteMet(Node("logi_reserve_1"), sameBranch));
    }

    [Fact]
    public void Prerequisite_AnyNodeOfTheLevelBelow_OpensTheWholeLevelAbove()
    {
        // Un seul des deux nœuds de niveau 2 suffit à ouvrir LES DEUX nœuds de niveau 3 de la branche.
        var owned = new HashSet<string> { "cmd_Esquive", "cmd_vie" };
        Assert.True(Tree.PrerequisiteMet(Node("cmd_mouvement"), owned));
        Assert.True(Tree.PrerequisiteMet(Node("cmd_Orage"), owned));
        Assert.False(Tree.PrerequisiteMet(Node("cmd_portee"), owned));   // niveau 4 : rien au niveau 3
    }

    [Fact]
    public void Unlock_SpendsPoints_AndRefusesWhenTooExpensive()
    {
        var run = RunWithPoints(2);
        Assert.True(run.CanUnlock(Node("cmd_Esquive")));
        Assert.True(run.Unlock(Node("cmd_Esquive")));
        Assert.Equal(0, run.CommandPoints);
        Assert.True(run.IsUnlocked("cmd_Esquive"));

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
        Assert.False(run.CanUnlock(Node("cmd_Esquive")));
        Assert.False(run.Unlock(Node("cmd_Esquive")));
    }

    [Fact]
    public void Unlock_RefusesAlreadyOwnedNode()
    {
        var run = RunWithPoints(10);
        Assert.True(run.Unlock(Node("cmd_Esquive")));
        Assert.False(run.CanUnlock(Node("cmd_Esquive")));
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
        run.Unlock(Node("cmd_Esquive"));   // ce nœud octroie le trait Esquive au commandant

        var commander = run.Commander;
        Assert.True(commander.Spawn(Faction.Player, run.BuffsFor(commander)).HasTrait(Trait.Esquive));

        var troop = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
        Assert.False(troop.Spawn(Faction.Player, run.BuffsFor(troop)).HasTrait(Trait.Esquive));
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
        run.Unlock(Node("cmd_Esquive"));           // commandant
        run.Unlock(Node("troupe_vie"));            // troupes
        run.Unlock(Node("logi_deploiement_1"));    // logistique : n'agit sur AUCUNE unité

        Assert.Equal(new[] { "cmd_Esquive" }, run.ActiveNodesFor(commander: true).Select(n => n.Id));
        Assert.Equal(new[] { "troupe_vie" }, run.ActiveNodesFor(commander: false).Select(n => n.Id));
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
        run.Unlock(Node("cmd_Esquive"));
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
        var troop = new UnitSpec(Domaine.Dame, soldat)
        {
            Equipment = Equipment.OfStat("test_pv", "Test", EquipStat.Hp, 5),
        };
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
            unlockedNodes: new[] { "cmd_Esquive", "noeud_supprime_du_json" });

        Assert.True(run.IsUnlocked("cmd_Esquive"));
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
        Assert.Equal("cmd_Esquive", Node("cmd_Esquive").Icon);
        Assert.Equal("logi_reserve_1", Node("logi_reserve_1").Icon);
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
}
