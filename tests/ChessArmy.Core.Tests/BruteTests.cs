using System;
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
/// COMMANDANT BRUTE : la perte comme ressource. Couvre ses quatre traits de moteur (« Bouclier humain »,
/// « Sacrifice », « Chair à canon », « Vengeance »), son revenu « sur perte », le pion EXCLUSIF (le Paysan,
/// son trait « Survivant » et ses évolutions par nœud à CHOIX), « Révolte » et la persistance de tout ça.
///
/// Ces tests touchent des registres STATIQUES partagés (<see cref="Commandes"/>, <see cref="CommandTrees"/>,
/// <see cref="ExclusiveClasses"/>) : ils sont remis dans leur état de repli en sortant de CHAQUE test (la
/// parallélisation est désactivée pour toute l'assembly, cf. AssemblyInfo).
/// </summary>
public class BruteTests : IDisposable
{
    public void Dispose()
    {
        Commandes.ResetToDefaults();
        CommandTrees.ResetToDefaults();
        ExclusiveClasses.ResetToDefaults();
    }

    // ── Fabriques ────────────────────────────────────────────────────────────────

    private static Unit Make(Faction faction, int hp, int damage, string[] traits,
        Domaine domaine = Domaine.Dame, int attackRange = 1, int moveRange = 1, bool essential = false) =>
        new(domaine, faction,
            new UnitClass("T", "t", tier: 1, maxHp: hp, damage: damage, moveRange: moveRange,
                attackRange: attackRange, traits: traits),
            equipment: null)
        {
            IsEssential = essential,
        };

    private static string[] None => Array.Empty<string>();

    private static Match Board(int size = 8) => new(size, size);

    /// <summary>Commandant BRUTE de test : points « sur perte » sans plafond, arbre <paramref name="treeId"/>.</summary>
    private static CommandeDef BruteDef(string treeId = "bruteTest") =>
        new(CommandeRole.Commander, Domaine.Dame,
            new UnitClass("Brute", "brute", tier: 1, maxHp: 38, damage: 18, moveRange: 2, attackRange: 1),
            deployments: 5, reserveSize: 8, treeId: treeId, id: "bruteTest",
            startingUnits: Array.Empty<Domaine>(), allyDeathPoints: 1);

    private static void UseBruteRegistry()
    {
        Commandes.Load(new[]
        {
            new CommandeDef(CommandeRole.Commander, Domaine.Dame,
                new UnitClass("Commandant", "commandant", tier: 1, maxHp: 26, damage: 6, moveRange: 2, attackRange: 1),
                id: "commandant"),
            BruteDef(),
        });
    }

    /// <summary>
    /// Arbre de test : les nœuds du Paysan, ceux des pertes, la masse et « Révolte ». Les NIVEAUX y sont
    /// volontairement plats (les prérequis de branche ont leurs propres tests, cf. CommandTreeTests) : ce qui
    /// est éprouvé ici, ce sont les EFFETS.
    /// </summary>
    private static void UseBruteTree()
    {
        CommandTrees.Load(CommandTreeCatalog.FromJson("""
        { "trees": [ { "id": "bruteTest", "nodes": [
            { "id": "n_pv",       "branch": 0, "level": 1, "effects": [ { "kind": "allyDeathMaxHp", "amount": 2 } ] },
            { "id": "n_puissance","branch": 0, "level": 2, "effects": [ { "kind": "allyDeathPower", "amount": 1 } ] },
            { "id": "n_paysan",   "branch": 1, "level": 1, "effects": [ { "kind": "recruitExclusive", "asset": "paysan" } ] },
            { "id": "n_armement", "branch": 1, "level": 2, "effects": [ { "kind": "evolveExclusive" } ] },
            { "id": "n_berserk",  "branch": 1, "level": 2, "effects": [ { "kind": "exclusiveTrait", "trait": "Berserk" } ] },
            { "id": "n_protect",  "branch": 1, "level": 3, "effects": [ { "kind": "evolveExclusive" } ] },
            { "id": "n_revolte",  "branch": 1, "level": 2, "effects": [
                { "kind": "revolte", "asset": "paysan" },
                { "kind": "unitStat", "stat": "hp", "amount": 12, "tier": 1 },
                { "kind": "unitStat", "stat": "damage", "amount": 8, "tier": 1 },
                { "kind": "unitStat", "stat": "moveRange", "amount": 1, "tier": 1, "domaine": "Dame" },
                { "kind": "unitStat", "stat": "moveRange", "amount": 1, "tier": 1, "domaine": "Tour" },
                { "kind": "unitStat", "stat": "moveRange", "amount": 1, "tier": 1, "domaine": "Fou" } ] },
            { "id": "n_masse",    "branch": 2, "level": 1, "effects": [ { "kind": "reserveThresholdRecruit", "amount": 2, "domaine": "Dame" } ] },
            { "id": "n_releve",   "branch": 2, "level": 1, "effects": [ { "kind": "eliteDeathRecruit", "amount": 2 } ] } ] } ] }
        """));
    }

    /// <summary>Run de BRUTE prête à acheter : registres chargés et points en poche.</summary>
    private static Run BruteRun(int points = 40)
    {
        UseBruteRegistry();
        UseBruteTree();
        var run = new Run(seed: 1, commander: BruteDef());
        run.GrantCommandPoints(points);
        return run;
    }

    private static bool Buy(Run run, string nodeId, string? choice = null) =>
        run.Unlock(run.Tree.ById(nodeId)!, choice);

    // ── « Bouclier humain » ──────────────────────────────────────────────────────

    [Fact]
    public void BouclierHumain_RedirectsTheWholeHit_ToTheNearestAllyInRange()
    {
        var match = Board();
        var brute = Make(Faction.Player, 40, 10, new[] { Trait.BouclierHumain }, attackRange: 1, essential: true);
        var ally = Make(Faction.Player, 30, 5, None);
        var attacker = Make(Faction.Enemy, 20, 12, None);
        match.Place(new Cell(3, 3), brute);
        match.Place(new Cell(4, 3), ally);     // au contact : dans la portée d'attaque (1) de la Brute
        match.Place(new Cell(2, 3), attacker);

        match.PassTurn();
        match.TryAttack(new Cell(2, 3), new Cell(3, 3));

        Assert.Equal(40, brute.Hp);            // le porteur n'a rien pris…
        Assert.Equal(18, ally.Hp);             // …c'est l'allié poussé devant lui qui encaisse les 12
        Assert.Single(match.LastShieldHits);
        Assert.Equal(new Cell(4, 3), match.LastShieldHits[0].Cell);
    }

    [Fact]
    public void BouclierHumain_WithNoAllyInRange_TheBearerTakesTheHit()
    {
        var match = Board();
        var brute = Make(Faction.Player, 40, 10, new[] { Trait.BouclierHumain }, attackRange: 1);
        var attacker = Make(Faction.Enemy, 20, 12, None);
        match.Place(new Cell(3, 3), brute);
        match.Place(new Cell(2, 3), attacker);

        match.PassTurn();
        match.TryAttack(new Cell(2, 3), new Cell(3, 3));

        Assert.Equal(28, brute.Hp);
        Assert.Empty(match.LastShieldHits);
    }

    [Fact]
    public void BouclierHumain_IsNeverRelayed_TheSecondBearerTakesItHimself()
    {
        var match = Board();
        var brute = Make(Faction.Player, 40, 10, new[] { Trait.BouclierHumain }, attackRange: 1);
        var ally = Make(Faction.Player, 30, 5, new[] { Trait.BouclierHumain }, attackRange: 1);
        var attacker = Make(Faction.Enemy, 20, 12, None);
        match.Place(new Cell(3, 3), brute);
        match.Place(new Cell(4, 3), ally);
        match.Place(new Cell(2, 3), attacker);

        match.PassTurn();
        match.TryAttack(new Cell(2, 3), new Cell(3, 3));

        Assert.Equal(40, brute.Hp);
        Assert.Equal(18, ally.Hp);   // l'allié encaisse et ne renvoie pas le coup au porteur d'origine
    }

    [Fact]
    public void ShieldTargetFor_PicksTheNearestAlly()
    {
        var match = Board();
        var brute = Make(Faction.Player, 40, 10, new[] { Trait.BouclierHumain }, attackRange: 3);
        match.Place(new Cell(3, 3), brute);
        match.Place(new Cell(6, 3), Make(Faction.Player, 30, 5, None));
        match.Place(new Cell(4, 4), Make(Faction.Player, 30, 5, None));

        Assert.Equal(new Cell(4, 4), match.ShieldTargetFor(brute));
    }

    // ── « Sacrifice » ────────────────────────────────────────────────────────────

    [Fact]
    public void Sacrifice_LetsTheBearerStrikeItsOwn_AndHealsIt()
    {
        var match = Board();
        var brute = Make(Faction.Player, 40, 10, new[] { Trait.Sacrifice }, attackRange: 1);
        var ally = Make(Faction.Player, 30, 5, None);
        match.Place(new Cell(3, 3), brute);
        match.Place(new Cell(4, 3), ally);
        brute.TakeDamage(20);

        Assert.Contains(new Cell(4, 3), match.AttackTargets(new Cell(3, 3)));
        Assert.Equal(MoveKind.Attacked, match.TryAttack(new Cell(3, 3), new Cell(4, 3)));

        Assert.Equal(20, ally.Hp);                       // l'allié encaisse la puissance normale
        Assert.Equal(20 + Match.SacrificeHeal, brute.Hp);
    }

    [Fact]
    public void Sacrifice_HealIsCappedAtMaxHp_AndKillsAreNotCredited()
    {
        var match = Board();
        var brute = Make(Faction.Player, 40, 30, new[] { Trait.Sacrifice }, attackRange: 1);
        var ally = Make(Faction.Player, 10, 5, None);
        match.Place(new Cell(3, 3), brute);
        match.Place(new Cell(4, 3), ally);

        match.TryAttack(new Cell(3, 3), new Cell(4, 3));

        Assert.Equal(40, brute.Hp);    // déjà au maximum : le soin ne déborde pas
        Assert.Equal(0, brute.Kills);  // tuer un des siens n'est pas une mise à mort
        Assert.Single(match.DeathLog);
    }

    [Fact]
    public void Sacrifice_IsNeverOfferedToTheAi()
    {
        var match = Board();
        var enemy = Make(Faction.Enemy, 40, 10, new[] { Trait.Sacrifice }, attackRange: 1);
        match.Place(new Cell(3, 3), enemy);
        match.Place(new Cell(4, 3), Make(Faction.Enemy, 30, 5, None));

        match.PassTurn();

        Assert.Empty(match.AttackTargets(new Cell(3, 3)));
    }

    // ── « Chair à canon » ────────────────────────────────────────────────────────

    [Fact]
    public void ChairACanon_ThrowsAnAdjacentAlly_AtAnEnemyInRange()
    {
        var match = Board();
        var brute = Make(Faction.Player, 40, 14, new[] { Trait.ChairACanon }, attackRange: 1);
        var ally = Make(Faction.Player, 30, 5, None);
        var enemy = Make(Faction.Enemy, 40, 5, None);
        match.Place(new Cell(1, 1), brute);
        match.Place(new Cell(2, 1), ally);
        match.Place(new Cell(4, 1), enemy);   // à 3 cases du lanceur

        Assert.Contains(new Cell(2, 1), match.ThrowableAllies(new Cell(1, 1)));
        Assert.Contains(new Cell(4, 1), match.ThrowTargets(new Cell(1, 1)));

        Assert.Equal(MoveKind.Attacked, match.TryThrow(new Cell(1, 1), new Cell(2, 1), new Cell(4, 1)));

        Assert.Equal(26, enemy.Hp);                                   // la cible encaisse la puissance du lanceur
        Assert.Equal(30, ally.Hp);                                    // le projectile est indemne
        Assert.Equal(1, Chebyshev(match.CellOf(ally)!.Value, new Cell(4, 1)));   // et atterrit au contact
        Assert.Equal(Faction.Enemy, match.CurrentTurn);               // le lancer a consommé le tour
    }

    [Fact]
    public void ChairACanon_OutOfRangeOrWithoutTrait_IsRefused()
    {
        var match = Board();
        var plain = Make(Faction.Player, 40, 14, None, attackRange: 1);
        match.Place(new Cell(1, 1), plain);
        match.Place(new Cell(2, 1), Make(Faction.Player, 30, 5, None));
        match.Place(new Cell(5, 1), Make(Faction.Enemy, 40, 5, None));

        Assert.Empty(match.ThrowableAllies(new Cell(1, 1)));   // pas le trait : rien à empoigner
        Assert.Equal(MoveKind.Invalid, match.TryThrow(new Cell(1, 1), new Cell(2, 1), new Cell(5, 1)));

        var thrower = Board();
        var brute = Make(Faction.Player, 40, 14, new[] { Trait.ChairACanon }, attackRange: 1);
        thrower.Place(new Cell(1, 1), brute);
        thrower.Place(new Cell(2, 1), Make(Faction.Player, 30, 5, None));
        thrower.Place(new Cell(5, 1), Make(Faction.Enemy, 40, 5, None));   // 4 cases : hors de portée du jet

        Assert.Empty(thrower.ThrowTargets(new Cell(1, 1)));
    }

    [Fact]
    public void ChairACanon_KillingTheTarget_LandsTheThrownUnitOnTheFreedCell()
    {
        var match = Board();
        var brute = Make(Faction.Player, 40, 50, new[] { Trait.ChairACanon }, attackRange: 1);
        var ally = Make(Faction.Player, 30, 5, None);
        var enemy = Make(Faction.Enemy, 10, 5, None);
        match.Place(new Cell(1, 1), brute);
        match.Place(new Cell(2, 1), ally);
        match.Place(new Cell(3, 1), enemy);

        Assert.Equal(MoveKind.Killed, match.TryThrow(new Cell(1, 1), new Cell(2, 1), new Cell(3, 1)));

        Assert.Equal(1, brute.Kills);
        Assert.NotNull(match.CellOf(ally));
        Assert.Equal(new Cell(3, 1), match.CellOf(ally));   // la case libérée est la plus proche de la cible
    }

    private static int Chebyshev(Cell a, Cell b) =>
        Math.Max(Math.Abs(a.Column - b.Column), Math.Abs(a.Row - b.Row));

    // ── « Vengeance » ────────────────────────────────────────────────────────────

    [Fact]
    public void Vengeance_GrantsPowerForOneTurn_ThenFadesAway()
    {
        var match = Board();
        var avenger = Make(Faction.Player, 30, 10, new[] { Trait.Vengeance }, attackRange: 1);
        var doomed = Make(Faction.Player, 4, 5, None);
        var enemy = Make(Faction.Enemy, 60, 4, None);
        match.Place(new Cell(3, 3), avenger);
        match.Place(new Cell(1, 1), doomed);
        match.Place(new Cell(1, 2), enemy);

        match.PassTurn();
        match.TryAttack(new Cell(1, 2), new Cell(1, 1));   // l'ennemi abat le camarade

        Assert.Equal(Match.VengeancePowerBonus, avenger.VengeancePower);
        Assert.Equal(Faction.Player, match.CurrentTurn);

        match.TryMove(new Cell(3, 3), new Cell(3, 2));   // le joueur joue son tour : la colère est retombée

        Assert.Equal(0, avenger.VengeancePower);
        Assert.NotNull(enemy);
    }

    [Fact]
    public void Vengeance_BornOnItsOwnTurn_SurvivesUntilTheNextOne()
    {
        var match = Board();
        var brute = Make(Faction.Player, 40, 30, new[] { Trait.Sacrifice }, attackRange: 1);
        var avenger = Make(Faction.Player, 30, 10, new[] { Trait.Vengeance }, attackRange: 1);
        var doomed = Make(Faction.Player, 10, 5, None);
        match.Place(new Cell(3, 3), brute);
        match.Place(new Cell(4, 3), doomed);
        match.Place(new Cell(6, 6), avenger);
        match.Place(new Cell(0, 0), Make(Faction.Enemy, 40, 5, None));

        match.TryAttack(new Cell(3, 3), new Cell(4, 3));   // la Brute sacrifie un des siens PENDANT son tour

        Assert.Equal(Match.VengeancePowerBonus, avenger.VengeancePower);
        Assert.Equal(Faction.Enemy, match.CurrentTurn);

        match.TryMove(new Cell(0, 0), new Cell(0, 1));     // tour ennemi : la colère tient toujours
        Assert.Equal(Match.VengeancePowerBonus, avenger.VengeancePower);
    }

    // ── Revenu « sur perte » ─────────────────────────────────────────────────────

    [Fact]
    public void AllyDeaths_PayEveryTime_WithoutCap()
    {
        var run = BruteRun();
        run.StartBattle();

        for (var i = 0; i < 5; i++)
            Assert.Equal(1, run.RegisterAllyDeath());

        Assert.Equal(5, run.AllyDeaths);
        Assert.Equal(45, run.CommandPoints);   // 40 de départ + 5
    }

    [Fact]
    public void AllyDeaths_FeedTheCommanderMaxHpAndPower()
    {
        var run = BruteRun();
        Assert.True(Buy(run, "n_pv"));
        Assert.True(Buy(run, "n_puissance"));
        for (var i = 0; i < 7; i++)
            run.RegisterAllyDeath();

        var buffs = run.BuffsFor(run.Commander);
        Assert.Equal(14, buffs.BonusFor(EquipStat.Hp));      // 7 pertes × 2 PV max
        Assert.Equal(2, buffs.BonusFor(EquipStat.Damage));   // 7 pertes / 3 = 2 paliers
    }

    [Fact]
    public void AllyDeathPoints_AreNotGivenToOtherCommanders()
    {
        var run = new Run(seed: 1);   // commandant de base : ce n'est pas sa source
        run.StartBattle();

        Assert.Equal(0, run.RegisterAllyDeath());
        Assert.Equal(1, run.AllyDeaths);   // le compteur tourne quand même (il ne sert qu'à la Brute)
    }

    // ── Le PAYSAN : recrue, trait « Survivant », évolutions ──────────────────────

    [Fact]
    public void RecruitExclusive_BringsThePeasantOnce()
    {
        var run = BruteRun();
        Assert.Null(run.ExclusiveSpec);

        Assert.True(Buy(run, "n_paysan"));

        Assert.NotNull(run.ExclusiveSpec);
        Assert.Equal(ExclusiveClasses.PaysanAsset, run.ExclusiveSpec!.UnitClass.Asset);
        Assert.Single(run.Roster, u => ExclusiveClasses.IsExclusive(u.UnitClass));
    }

    /// <summary>
    /// La paysanne vit HORS du plafond de réserve : elle vient d'un nœud payé, pas d'un recrutement. Elle ne
    /// prend donc la place de personne, et une réserve pleine ne l'empêche ni d'arriver ni de rester.
    /// </summary>
    [Fact]
    public void ThePeasant_DoesNotCountAgainstTheReserveCap()
    {
        var run = BruteRun();
        while (!run.IsReserveFull)
            run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
        var full = run.ReserveCount;

        Assert.True(Buy(run, "n_paysan"));   // réserve pleine : elle arrive quand même

        Assert.NotNull(run.ExclusiveSpec);
        Assert.Equal(full, run.ReserveCount);   // …et ne consomme aucune place
        Assert.True(run.IsReserveFull);
        Assert.Equal(full + 2, run.Roster.Count);   // commandant + réserve pleine + la paysanne
    }

    [Fact]
    public void ThePeasant_NeverFusesNorRerolls()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        var paysan = run.ExclusiveSpec!;

        Assert.False(run.CanFuse(paysan));
        Assert.Null(run.RerollUnit(paysan, new Random(1), _ => true));
    }

    [Fact]
    public void Survivant_AddsOnePowerAndTwoMaxHpPerMissionSurvived()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        var paysan = run.ExclusiveSpec!;

        for (var mission = 0; mission < 3; mission++)   // trois missions passées debout
            Assert.Equal(1, run.GrantSurvivantStacks(new[] { paysan }));

        var buffs = run.BuffsFor(paysan);
        Assert.Equal(3, buffs.BonusFor(EquipStat.Damage));
        Assert.Equal(3 * Run.SurvivantHpPerStack, buffs.BonusFor(EquipStat.Hp));
    }

    [Fact]
    public void Survivant_OnlyCountsForBearers()
    {
        var run = BruteRun();
        var soldier = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
        run.AddUnit(soldier);

        Assert.Equal(0, run.GrantSurvivantStacks(new[] { soldier }));
        Assert.Equal(0, run.BuffsFor(soldier).BonusFor(EquipStat.Damage));
    }

    [Fact]
    public void FallenPeasant_DiesForGood_AndTakesItsBranchWithIt()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        Buy(run, "n_armement", "paysan_archer");
        var paysan = run.ExclusiveSpec!;
        run.GrantSurvivantStacks(new[] { paysan });
        run.StartBattle();

        run.CompleteCombat(new[] { paysan }, Array.Empty<UnitSpec>());

        Assert.Null(run.ExclusiveSpec);                 // permadeath : elle tombe comme n'importe quel pion
        Assert.DoesNotContain(paysan, run.Roster);
        Assert.Empty(run.EvolutionChoices(run.Tree.ById("n_protect")!));   // plus personne à faire évoluer
        Assert.True(run.IsUnlocked("n_armement"));      // les nœuds d'évolution restent acquis
    }

    [Fact]
    public void PeasantNode_BecomesBuyableAgain_WhenThePeasantIsDead()
    {
        var run = BruteRun();
        var node = run.Tree.ById("n_paysan")!;
        Buy(run, "n_paysan");
        Assert.False(run.CanRebuy(node));    // tant qu'elle est vivante le nœud reste acquis
        Assert.False(run.CanUnlock(node));

        var paysan = run.ExclusiveSpec!;
        run.StartBattle();
        run.CompleteCombat(new[] { paysan }, Array.Empty<UnitSpec>());
        run.SkipRecruitment();   // retour en PLACEMENT : la seule phase où l'arbre s'achète

        Assert.True(run.CanRebuy(node));
        Assert.True(run.CanUnlock(node));
    }

    [Fact]
    public void RebuyingThePeasant_CostsAgain_AndBringsHerBackAlreadyEvolved()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        Buy(run, "n_armement", "paysan_archer");
        Buy(run, "n_protect", "paysan_archer_rempart");
        var fallen = run.ExclusiveSpec!;
        fallen.SurvivantStacks = 5;
        run.StartBattle();
        run.CompleteCombat(new[] { fallen }, Array.Empty<UnitSpec>());
        run.SkipRecruitment();   // retour en PLACEMENT
        var points = run.CommandPoints;

        Assert.True(Buy(run, "n_paysan"));

        var fresh = run.ExclusiveSpec!;
        Assert.NotSame(fallen, fresh);
        Assert.Equal(points - run.Tree.ById("n_paysan")!.Cost, run.CommandPoints);   // elle se repaie
        Assert.Equal("paysan_archer_rempart", fresh.UnitClass.Asset);   // les évolutions déjà payées tiennent
        Assert.Equal(0, fresh.SurvivantStacks);                         // mais tout est à refaire côté vécu
    }

    [Fact]
    public void RebuyingThePeasant_WithoutEvolutions_BringsBackThePlainOne()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        var fallen = run.ExclusiveSpec!;
        run.StartBattle();
        run.CompleteCombat(new[] { fallen }, Array.Empty<UnitSpec>());
        run.SkipRecruitment();   // retour en PLACEMENT

        Assert.True(Buy(run, "n_paysan"));

        Assert.Equal(ExclusiveClasses.PaysanAsset, run.ExclusiveSpec!.UnitClass.Asset);
    }

    [Fact]
    public void OtherNodes_AreNeverRebuyable()
    {
        var run = BruteRun();
        Buy(run, "n_pv");

        Assert.False(run.CanRebuy(run.Tree.ById("n_pv")!));
        Assert.False(run.CanUnlock(run.Tree.ById("n_pv")!));
    }

    /// <summary>
    /// Le Paysan PROMU par « Révolte » échappe au retrait, comme tout meneur : sa chute ne se solde pas par
    /// une ligne de roster en moins mais par la fin de la partie (la scène perd la mission avant d'en arriver
    /// à la clôture). Sans cette garde, un meneur tombé disparaîtrait du roster sauvegardé.
    /// </summary>
    [Fact]
    public void PromotedPeasant_IsNotRemovedAsACasualty()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        Buy(run, "n_revolte");
        var leader = run.ExclusiveSpec!;
        run.StartBattle();

        run.CompleteCombat(new[] { leader }, Array.Empty<UnitSpec>());

        Assert.Contains(leader, run.Roster);
        Assert.True(leader.Essential);
    }

    [Fact]
    public void OrdinaryPawns_StillDieForGood()
    {
        var run = BruteRun();
        var soldier = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
        run.AddUnit(soldier);
        run.StartBattle();

        run.CompleteCombat(new[] { soldier }, Array.Empty<UnitSpec>());

        Assert.DoesNotContain(soldier, run.Roster);
    }

    // ── Nœuds à CHOIX ────────────────────────────────────────────────────────────

    [Fact]
    public void EvolveNode_RefusesToBuyWithoutAChoice()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        var points = run.CommandPoints;

        Assert.False(Buy(run, "n_armement"));                 // aucun choix fourni
        Assert.False(Buy(run, "n_armement", "soldat"));       // choix étranger à l'arbre du pion
        Assert.Equal(points, run.CommandPoints);              // rien n'a été dépensé
        Assert.False(run.IsUnlocked("n_armement"));
    }

    [Fact]
    public void EvolveNode_TransformsThePeasantInPlace_KeepingItsIdentity()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        var paysan = run.ExclusiveSpec!;
        paysan.Kills = 4;
        paysan.SurvivantStacks = 2;

        Assert.True(Buy(run, "n_armement", "paysan_epeiste"));

        Assert.Same(paysan, run.ExclusiveSpec);                  // le MÊME pion, pas un remplaçant
        Assert.Equal("paysan_epeiste", paysan.UnitClass.Asset);
        Assert.Equal(4, paysan.Kills);                           // une fusion aurait tout remis à zéro
        Assert.Equal(2, paysan.SurvivantStacks);
        Assert.Equal("paysan_epeiste", run.ChoiceOf("n_armement"));
    }

    [Fact]
    public void EvolveNodes_ChainOnTheBranchAlreadyChosen()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        Buy(run, "n_armement", "paysan_archer");

        // Le second nœud ne propose plus que les feuilles de la branche ARCHER.
        var options = run.EvolutionChoices(run.Tree.ById("n_protect")!).Select(c => c.Asset).ToList();
        Assert.Equal(new[] { "paysan_archer_duelliste", "paysan_archer_rempart" }, options);
        Assert.False(Buy(run, "n_protect", "paysan_epeiste_rempart"));   // l'autre branche est fermée

        Assert.True(Buy(run, "n_protect", "paysan_archer_rempart"));
        Assert.Equal("paysan_archer_rempart", run.ExclusiveSpec!.UnitClass.Asset);
    }

    [Fact]
    public void ExclusiveTrait_ReachesThePeasantOnly()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        Buy(run, "n_berserk");
        var soldier = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
        run.AddUnit(soldier);

        Assert.True(run.BuffsFor(run.ExclusiveSpec!).GrantsTrait(Trait.Berserk));
        Assert.False(run.BuffsFor(soldier).GrantsTrait(Trait.Berserk));
        Assert.False(run.BuffsFor(run.Commander).GrantsTrait(Trait.Berserk));
    }

    // ── « Révolte » ──────────────────────────────────────────────────────────────

    [Fact]
    public void Revolte_HandsCommandToThePeasant_AndLeavesTheBruteAsAnOrdinaryPawn()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        var brute = run.Commander;
        var paysan = run.ExclusiveSpec!;

        Assert.True(Buy(run, "n_revolte"));

        Assert.True(paysan.Essential);          // sa mort perd désormais la run
        Assert.False(paysan.CommanderBody);
        Assert.False(brute.Essential);          // la Brute est mortelle comme les autres…
        Assert.True(brute.CommanderBody);       // …mais garde les bonus de sa branche
        Assert.Same(paysan, run.Commander);
        Assert.True(run.RevolteDone);
    }

    [Fact]
    public void Revolte_KeepsCommanderBonusesOnTheBrute_AndLiftsEveryTier1()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        Buy(run, "n_pv");
        var brute = run.Commander;
        run.RegisterAllyDeath();
        run.RegisterAllyDeath();
        var soldier = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
        run.AddUnit(soldier);

        Buy(run, "n_revolte");

        Assert.Equal(4, run.BuffsFor(brute).BonusFor(EquipStat.Hp));   // 2 pertes × 2 PV max, toujours à elle
        var troop = run.BuffsFor(soldier);
        Assert.Equal(12, troop.BonusFor(EquipStat.Hp));                // la piétaille tier 1 est relevée
        Assert.Equal(8, troop.BonusFor(EquipStat.Damage));
    }

    [Fact]
    public void Revolte_GivesOneStepToTier1_ExceptTheCavalier()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        Buy(run, "n_revolte");

        UnitSpec Of(Domaine d) => new(d, Domaines.Of(d).BaseClass);
        Assert.Equal(1, run.BuffsFor(Of(Domaine.Dame)).BonusFor(EquipStat.MoveRange));
        Assert.Equal(1, run.BuffsFor(Of(Domaine.Tour)).BonusFor(EquipStat.MoveRange));
        Assert.Equal(1, run.BuffsFor(Of(Domaine.Fou)).BonusFor(EquipStat.MoveRange));
        Assert.Equal(0, run.BuffsFor(Of(Domaine.Cavalier)).BonusFor(EquipStat.MoveRange));   // déjà rapide
        var archer = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass.Evolutions[0]);
        Assert.Equal(0, run.BuffsFor(archer).BonusFor(EquipStat.MoveRange));                 // tier 2 : rien
        Assert.Equal(12, run.BuffsFor(Of(Domaine.Cavalier)).BonusFor(EquipStat.Hp));         // le reste, si
    }

    [Fact]
    public void Revolte_ThePromotedPeasantKeepsHerEquipmentSlots_AndTheBruteGainsNone()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        var paysanne = run.ExclusiveSpec!;
        var brute = run.Commander;
        var before = run.SlotsFor(paysanne);
        Assert.True(before > 0);                    // pion ordinaire : elle s'équipe
        Assert.Equal(0, run.SlotsFor(brute));        // le commandant, lui, non

        Buy(run, "n_revolte");

        Assert.True(paysanne.Essential);
        Assert.Equal(before, run.SlotsFor(paysanne));   // meneuse, elle s'équipe toujours
        Assert.Equal(0, run.SlotsFor(brute));            // déchue, la Brute ne gagne pas de slot
    }

    [Fact]
    public void Revolte_TheDethronedBruteDoesNotEatAReserveSlot()
    {
        var run = BruteRun();
        run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
        Buy(run, "n_paysan");
        var before = run.ReserveCount;   // le soldat seul : la paysanne vit hors du plafond

        Buy(run, "n_revolte");

        // La Brute déchue n'entre pas dans la réserve : elle reste un meneur déchu, pas un pion de plus à
        // loger. Le compte ne bouge donc pas d'un cran malgré la passation de commandement.
        Assert.Equal(before, run.ReserveCount);
        Assert.Equal(1, run.ReserveCount);
    }

    // ── Recrue de masse ──────────────────────────────────────────────────────────

    [Fact]
    public void ReserveThresholdRecruit_OnlyFiresAboveTheThreshold()
    {
        var run = BruteRun();
        Buy(run, "n_masse");   // seuil 2
        run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
        run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
        run.StartBattle();
        run.CompleteCombat(Array.Empty<UnitSpec>(), Array.Empty<UnitSpec>());
        run.SkipRecruitment();

        Assert.Equal(2, run.ReserveCount);   // réserve à 2 : le seuil n'est pas DÉPASSÉ

        run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
        run.StartBattle();
        run.CompleteCombat(Array.Empty<UnitSpec>(), Array.Empty<UnitSpec>());
        run.SkipRecruitment();

        Assert.Equal(4, run.ReserveCount);   // 3 > 2 : un Soldat de plus arrive
    }

    // ── Relève des soldats ───────────────────────────────────────────────────────

    [Fact]
    public void GrandeReleve_EachFallenElite_BringsTwoRandomDiscoveredTier1()
    {
        var run = BruteRun();
        Buy(run, "n_releve");
        var archer = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass.Evolutions[0]);   // tier 2
        var recruit = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);                // tier 1
        run.AddUnit(archer);
        run.AddUnit(recruit);
        run.StartBattle();
        run.CompleteCombat(new[] { archer, recruit }, Array.Empty<UnitSpec>());

        // Seul le Lancier est « déjà découvert » : le tirage ne peut sortir que lui.
        var lancier = Domaines.Tour.BaseClass;
        var added = run.GrantEliteDeathReplacements(new[] { archer, recruit }, new Random(1),
            asset => asset == lancier.Asset);

        Assert.Equal(2, added.Count);   // seul l'archer (tier 2) compte, et il en vaut deux
        Assert.All(added, s => Assert.Equal(lancier, s.UnitClass));   // tier 1 tiré parmi les découverts
    }

    // ── Persistance ──────────────────────────────────────────────────────────────

    [Fact]
    public void Save_RoundTrips_Deaths_Choices_Stacks_AndRoles()
    {
        var run = BruteRun();
        Buy(run, "n_paysan");
        Buy(run, "n_armement", "paysan_archer");
        run.ExclusiveSpec!.SurvivantStacks = 3;
        run.RegisterAllyDeath();
        run.RegisterAllyDeath();
        Buy(run, "n_revolte");

        var restored = RunSave.From(run).ToRun();

        Assert.Equal(2, restored.AllyDeaths);
        Assert.Equal("paysan_archer", restored.ChoiceOf("n_armement"));
        var paysan = restored.ExclusiveSpec!;
        Assert.Equal("paysan_archer", paysan.UnitClass.Asset);   // l'évolution survit au rechargement
        Assert.Equal(3, paysan.SurvivantStacks);
        Assert.True(paysan.Essential);                           // …ainsi que la passation de commandement
        Assert.True(restored.Roster.Single(u => u.CommanderBody).CommanderBody);
        Assert.False(restored.Roster.Single(u => u.CommanderBody).Essential);
        Assert.Equal("bruteTest", restored.CommanderDef.Id);     // le commandant est retrouvé malgré la révolte
    }

    // ── Configuration LIVRÉE ─────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string AssetPath(params string[] parts) =>
        System.IO.Path.Combine(new[] { RepoRoot(), "src", "ChessArmy.Game", "Assets" }.Concat(parts).ToArray());

    /// <summary>
    /// La BRUTE livrée : verrouillée derrière son boss, payée sur les pertes, et dotée d'un arbre dont les
    /// nœuds de Paysan pointent tous sur des classes exclusives RÉELLEMENT déclarées. Une faute de frappe dans
    /// un asset ne se verrait sinon qu'en jouant, sous la forme d'un nœud qui ne fait rien.
    /// </summary>
    [Fact]
    public void ShippedBrute_EarnsOnLosses_AndItsPeasantNodesPointAtRealClasses()
    {
        var json = System.IO.File.ReadAllText(AssetPath("Config", "units.json"));
        ExclusiveClasses.Load(Battle.Config.DomaineCatalog.ExclusivesFromJson(json));

        var brute = Battle.Config.DomaineCatalog.CommandesFromJson(json).Single(c => c.Id == "Commandant_brute");
        Assert.False(brute.StartsUnlocked);                 // elle s'ouvre en battant son boss
        Assert.Equal(1, brute.AllyDeathPoints);
        Assert.Equal(int.MaxValue, brute.AllyDeathCap);     // aucun plafond : c'est l'esprit du personnage
        Assert.Equal(Run.PointsPerMission, brute.MissionPoints);
        Assert.Equal("commandantBrute", brute.TreeId);

        // Le boss qui la débloque existe et la désigne bien.
        Assert.Contains(Battle.Config.DomaineCatalog.BossesFromJson(json),
            b => b.UnlocksCommander == "Commandant_brute");

        var tree = CommandTreeCatalog.FromJson(
            System.IO.File.ReadAllText(AssetPath("Config", "commander_trees.json")))
            .Single(t => t.Id == "commandantBrute");
        foreach (var effect in tree.Nodes.SelectMany(n => n.Effects))
        {
            if (effect.Asset is { } asset)
                Assert.True(ExclusiveClasses.Find(asset) != null, $"classe exclusive inconnue : {asset}");
            foreach (var choice in effect.Choices)
                Assert.True(ExclusiveClasses.Find(choice) != null, $"choix inconnu : {choice}");
        }

        // Le Paysan livré porte « Survivant » et ouvre sur DEUX évolutions qui en ouvrent DEUX chacune.
        var paysan = ExclusiveClasses.Paysan!.BaseClass;
        Assert.Contains(Trait.Survivant, paysan.Traits);
        Assert.Equal(2, paysan.Evolutions.Count);
        Assert.All(paysan.Evolutions, e => Assert.Equal(2, e.Evolutions.Count));
        AssertProtectionBonuses(paysan);
    }

    /// <summary>PV max qu'apporte l'armure REMPART par rapport à la forme d'armement dont elle sort.</summary>
    private const int RempartMaxHpBonus = 10;

    /// <summary>
    /// « Protection » : chaque armure apporte son trait ET un avantage propre, par rapport à la forme
    /// d'armement dont elle sort. DUELLISTE = un pas de plus (PV inchangés) ; REMPART = +10 PV max
    /// (déplacement inchangé). Vérifié sur la config livrée ET sur le repli codé, qui doivent rester alignés.
    /// </summary>
    private static void AssertProtectionBonuses(UnitClass paysan)
    {
        foreach (var armed in paysan.Evolutions)
            foreach (var shielded in armed.Evolutions)
            {
                if (shielded.Traits.Contains(Trait.Duelliste))
                {
                    Assert.True(shielded.MoveRange == armed.MoveRange + 1,
                        $"{shielded.Asset} devrait avoir {armed.MoveRange + 1} de déplacement, a {shielded.MoveRange}");
                    Assert.Equal(armed.MaxHp, shielded.MaxHp);
                }
                else
                {
                    Assert.Contains(Trait.Rempart, shielded.Traits);
                    Assert.True(shielded.MaxHp == armed.MaxHp + RempartMaxHpBonus,
                        $"{shielded.Asset} devrait avoir {armed.MaxHp + RempartMaxHpBonus} PV max, a {shielded.MaxHp}");
                    Assert.Equal(armed.MoveRange, shielded.MoveRange);
                }
            }
    }

    [Fact]
    public void FallbackPeasant_ProtectionBonusesMatchTheDesign() =>
        AssertProtectionBonuses(ExclusiveClasses.Paysan!.BaseClass);   // repli codé (registre remis à zéro)

    [Fact]
    public void ExclusiveClasses_AreOutOfEveryDomainTree()
    {
        Assert.NotNull(ExclusiveClasses.Paysan);
        Assert.True(ExclusiveClasses.IsExclusive(ExclusiveClasses.Find("paysan_epeiste_rempart")));
        Assert.False(ExclusiveClasses.IsExclusive(Domaines.Dame.BaseClass));

        // Toute forme du Paysan remonte à la MÊME racine : c'est elle qui nomme le sprite de meneuse
        // commun aux sept formes (cf. « Révolte »).
        Assert.Equal(ExclusiveClasses.PaysanAsset, ExclusiveClasses.RootAssetOf("paysan_epeiste_rempart"));
        Assert.Equal(ExclusiveClasses.PaysanAsset, ExclusiveClasses.RootAssetOf(ExclusiveClasses.PaysanAsset));
        Assert.Null(ExclusiveClasses.RootAssetOf("soldat"));
        Assert.All(Domaines.All, d => Assert.NotEqual(ExclusiveClasses.PaysanAsset, d.BaseClass.Asset));
    }
}
