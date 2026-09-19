using System;
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
/// COMMANDANT DUO (l'artisan meurtrier + Basile) : deux meneurs essentiels, run sans armée, traits de
/// l'arbre (lien d'amitié, position stratégique, roque, réaction en chaîne, tir en ligne) et OBJETS À
/// LANCER de la sacoche (grenade, balle rebondissante, javelot, flèche de Cupidon).
///
/// Ces tests touchent des registres STATIQUES partagés (<see cref="Commandes"/>, <see cref="Equipments"/>,
/// <see cref="CommandTrees"/>) : ils sont remis dans leur état de repli en sortant de CHAQUE test (la
/// parallélisation est désactivée pour toute l'assembly, cf. AssemblyInfo).
/// </summary>
public class DuoTests : IDisposable
{
    public void Dispose()
    {
        Commandes.ResetToDefaults();
        Equipments.ResetToDefaults();
        CommandTrees.ResetToDefaults();
        Bosses.ResetToDefaults();   // le test du boss ARTISAN charge son propre pool
    }

    // ── Fabriques ────────────────────────────────────────────────────────────────

    private static Unit Make(Faction faction, int hp, int damage, string[] traits,
        Domaine domaine = Domaine.Tour, int attackRange = 3, int moveRange = 1,
        bool essential = false, bool companion = false, int kills = 0, Equipment[]? equipment = null)
    {
        var cls = new UnitClass("T", "t", tier: 1, maxHp: hp, damage: damage,
            moveRange: moveRange, attackRange: attackRange, traits: traits);
        return new Unit(domaine, faction, cls, equipment, kills: kills)
        {
            IsEssential = essential,
            IsCompanion = companion,
        };
    }

    private static string[] None => Array.Empty<string>();

    private static Match Board(int size = 8) => new(size, size);

    /// <summary>Commandant DUO de test : sans armée, second meneur « basileTest », points sur soin.</summary>
    private static CommandeDef DuoDef(string treeId = "commandant") =>
        new(CommandeRole.Commander, Domaine.Dame,
            new UnitClass("Artisan", "artisan", tier: 1, maxHp: 30, damage: 14, moveRange: 2, attackRange: 1),
            deployments: 2, reserveSize: 0, treeId: treeId, startingUnits: Array.Empty<Domaine>(),
            healPoints: 1, healCap: 2, companionId: "basileTest", noArmy: true, id: "duoTest");

    private static CommandeDef CompanionDef() =>
        new(CommandeRole.Companion, Domaine.Tour,
            new UnitClass("Basile", "basile", tier: 1, maxHp: 22, damage: 10, moveRange: 2, attackRange: 3),
            id: "basileTest");

    /// <summary>Installe le registre des commandes avec le duo de test (les tests statiques se partagent l'état).</summary>
    private static void UseDuoRegistry()
    {
        Commandes.Load(new[]
        {
            new CommandeDef(CommandeRole.Commander, Domaine.Dame,
                new UnitClass("Commandant", "commandant", tier: 1, maxHp: 26, damage: 6, moveRange: 2, attackRange: 1),
                id: "commandant"),
            DuoDef(),
            CompanionDef(),
        });
    }

    // ── Run : deux meneurs, pas d'armée ──────────────────────────────────────────

    [Fact]
    public void DuoRun_StartsWithTwoEssentialLeaders_AndNoArmy()
    {
        UseDuoRegistry();
        var run = new Run(seed: 1, commander: DuoDef());

        Assert.Equal(2, run.Commanders.Count);
        Assert.Equal(0, run.ReserveCount);               // aucun pion de troupe
        Assert.True(run.NoArmy);
        Assert.NotNull(run.CompanionSpec);
        Assert.True(run.CompanionSpec!.Essential);       // le second meneur est essentiel lui aussi
        Assert.Equal("basile", run.CompanionSpec.UnitClass.Asset);
        Assert.Equal(0, run.ReserveLimit);               // et il n'y a pas de place pour en recruter
    }

    [Fact]
    public void DuoRun_WinningACombat_NeverOpensADraft()
    {
        UseDuoRegistry();
        var run = new Run(seed: 1, commander: DuoDef());
        run.StartBattle();

        var slain = new[] { new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass) };
        run.CompleteCombat(Array.Empty<UnitSpec>(), slain);

        Assert.Equal(RunPhase.Recruitment, run.Phase);   // l'écran post-combat s'ouvre…
        Assert.Empty(run.Draft);                         // …mais il n'y a rien à drafter (la scène enchaîne)
    }

    [Fact]
    public void DuoRun_SpecialMissions_BecomeSkirmishes()
    {
        UseDuoRegistry();
        var duo = new Run(seed: 1, commander: DuoDef());
        var solo = new Run(seed: 1);

        // La phase 1 place une mission spéciale au rang 4 : elle reste spéciale pour un commandant normal,
        // et devient une escarmouche pour celui qui n'a pas d'armée (personne à libérer ni à protéger).
        Assert.Equal(CombatType.Speciale, solo.MissionKindFor(1, 4));
        Assert.Equal(CombatType.Escarmouche, duo.MissionKindFor(1, 4));
    }

    [Fact]
    public void HealPoints_CreditOncePerHeal_UpToTheCap()
    {
        UseDuoRegistry();
        var run = new Run(seed: 1, commander: DuoDef());
        run.StartBattle();

        Assert.Equal(1, run.GrantHealPoint());
        Assert.Equal(1, run.GrantHealPoint());
        Assert.Equal(0, run.GrantHealPoint());   // plafond healCap = 2 atteint
        Assert.Equal(2, run.CommandPoints);

        run.ReturnToPlacement();
        run.StartBattle();                        // nouveau combat : le plafond est rendu
        Assert.Equal(1, run.GrantHealPoint());
    }

    [Fact]
    public void LeaderBonusHp_AccumulatesOnPickups_AndReachesBothLeaders()
    {
        UseDuoRegistry();
        var tree = CommandTreeCatalog.FromJson("""
        { "trees": [ { "id": "duoTest", "nodes": [
            { "id": "n_trousse", "branch": 0, "level": 1, "effects": [ { "kind": "healKitMaxHp", "amount": 1 } ] },
            { "id": "n_sacoche", "branch": 1, "level": 1, "effects": [ { "kind": "satchelMaxHp", "amount": 2 } ] } ] } ] }
        """);
        CommandTrees.Load(tree);
        var run = new Run(seed: 1, commander: DuoDef("duoTest"));
        run.GrantCommandPoints(20);
        Assert.True(run.Unlock(run.Tree.ById("n_trousse")!));
        Assert.True(run.Unlock(run.Tree.ById("n_sacoche")!));

        Assert.Equal(1, run.GrantLeaderBonusHp(healKit: true));
        Assert.Equal(2, run.GrantLeaderBonusHp(healKit: false));
        Assert.Equal(3, run.LeaderBonusHp);

        // Les deux meneurs en profitent, la troupe (s'il y en avait) non.
        foreach (var leader in run.Commanders)
            Assert.Equal(3, run.BuffsFor(leader).BonusFor(EquipStat.Hp));

        CommandTrees.ResetToDefaults();
    }

    /// <summary>
    /// « Barda » est plafonné à <see cref="Run.SatchelMaxHpPerCombat"/> PV max PAR COMBAT : la sacoche ne se
    /// consommant pas, on pourrait sinon y revenir en boucle et empiler des PV max à l'infini. Le plafond est
    /// rendu au combat suivant. La trousse de soin (« Remède durable »), elle, n'est pas concernée.
    /// </summary>
    /// <summary>
    /// Les PV max ramassés EN COMBAT entrent tout de suite dans les stats du pion (et remplissent la jauge
    /// d'autant) : les buffs d'arbre sont figés au spawn, donc sans ce cumul le gain n'aurait servi qu'au
    /// combat suivant. Cf. <see cref="Unit.GainMaxHp"/>, appelé par la scène au ramassage.
    /// </summary>
    [Fact]
    public void GainMaxHp_AppliesImmediately_ToMaxAndCurrentHp()
    {
        var leader = Make(Faction.Player, 30, 10, None, essential: true);
        leader.TakeDamage(12);
        Assert.Equal(18, leader.Hp);

        leader.GainMaxHp(2);

        Assert.Equal(32, leader.MaxHp);
        Assert.Equal(20, leader.Hp);   // le gain remplit aussi la jauge : sinon il ne se verrait pas

        leader.GainMaxHp(0);
        Assert.Equal(32, leader.MaxHp);
    }

    [Fact]
    public void SatchelBonusHp_IsCappedPerCombat()
    {
        UseDuoRegistry();
        var tree = CommandTreeCatalog.FromJson("""
        { "trees": [ { "id": "duoTest", "nodes": [
            { "id": "n_sacoche", "branch": 0, "level": 1, "effects": [ { "kind": "satchelMaxHp", "amount": 2 } ] },
            { "id": "n_trousse", "branch": 1, "level": 1, "effects": [ { "kind": "healKitMaxHp", "amount": 2 } ] } ] } ] }
        """);
        CommandTrees.Load(tree);
        var run = new Run(seed: 1, commander: DuoDef("duoTest"));
        run.GrantCommandPoints(20);
        Assert.True(run.Unlock(run.Tree.ById("n_sacoche")!));
        Assert.True(run.Unlock(run.Tree.ById("n_trousse")!));
        run.StartBattle();

        Assert.Equal(2, run.GrantLeaderBonusHp(healKit: false));   // plafond atteint d'un coup
        Assert.Equal(0, run.GrantLeaderBonusHp(healKit: false));   // les sacoches suivantes ne donnent plus rien
        Assert.Equal(2, run.GrantLeaderBonusHp(healKit: true));    // …mais la trousse n'est pas plafonnée
        Assert.Equal(4, run.LeaderBonusHp);

        run.ReturnToPlacement();
        run.StartBattle();                                         // combat suivant : le plafond est rendu
        Assert.Equal(2, run.GrantLeaderBonusHp(healKit: false));

        CommandTrees.ResetToDefaults();
    }

    [Fact]
    public void CompanionEffects_TargetBasileOnly_NotTheCommander()
    {
        UseDuoRegistry();
        var tree = CommandTreeCatalog.FromJson("""
        { "trees": [ { "id": "duoTest", "nodes": [
            { "id": "n_portee", "branch": 0, "level": 1, "effects": [ { "kind": "companionStat", "stat": "attackRange", "amount": 1 } ] },
            { "id": "n_riposte", "branch": 1, "level": 1, "effects": [ { "kind": "commanderTrait", "trait": "Riposte" } ] } ] } ] }
        """);
        CommandTrees.Load(tree);
        var run = new Run(seed: 1, commander: DuoDef("duoTest"));
        run.GrantCommandPoints(20);
        Assert.True(run.Unlock(run.Tree.ById("n_portee")!));
        Assert.True(run.Unlock(run.Tree.ById("n_riposte")!));

        var artisan = run.Commanders.First(c => !c.Companion);
        var basile = run.CompanionSpec!;

        Assert.Equal(1, run.BuffsFor(basile).BonusFor(EquipStat.AttackRange));
        Assert.Equal(0, run.BuffsFor(artisan).BonusFor(EquipStat.AttackRange));
        Assert.True(run.BuffsFor(artisan).GrantsTrait(Trait.Riposte));
        Assert.False(run.BuffsFor(basile).GrantsTrait(Trait.Riposte));

        CommandTrees.ResetToDefaults();
    }

    // ── Traits du duo ────────────────────────────────────────────────────────────

    // Les porteurs du lien sont ici du camp ENNEMI : c'est le camp du joueur qui a le trait dans le jeu,
    // mais le moteur ne joue que le tour courant (le joueur) — on inverse donc les rôles pour pouvoir frapper.
    [Fact]
    public void LienDAmitie_SplitsDamageBetweenTheBondedLeaders()
    {
        var m = Board();
        var artisan = Make(Faction.Enemy, 30, 0, new[] { Trait.LienDAmitie });
        var basile = Make(Faction.Enemy, 30, 0, new[] { Trait.LienDAmitie });
        m.Place(new Cell(0, 0), artisan);
        m.Place(new Cell(0, 1), basile);              // à portée de déplacement de l'autre (motif Tour)
        m.Place(new Cell(3, 0), Make(Faction.Player, 20, 10, None));

        m.TryAttack(new Cell(3, 0), new Cell(0, 0));

        Assert.Equal(26, artisan.Hp);   // 10 divisés en deux : 5 pour lui, moins 1 d'absorption…
        Assert.Equal(26, basile.Hp);    // …et autant pour son camarade
    }

    /// <summary>
    /// Le trait est porté par la VICTIME seule : n'importe quel allié à portée encaisse une part, qu'il ait
    /// le lien ou non. C'est un trait générique — il vaut pour une armée entière, pas seulement pour le duo.
    /// </summary>
    [Fact]
    public void LienDAmitie_SplitsWithEveryAllyInReach_EvenThoseWithoutTheTrait()
    {
        var m = Board();
        var bearer = Make(Faction.Enemy, 30, 0, new[] { Trait.LienDAmitie });
        var plain1 = Make(Faction.Enemy, 30, 0, None);   // simple allié : pas de trait
        var plain2 = Make(Faction.Enemy, 30, 0, None);
        m.Place(new Cell(0, 0), bearer);
        m.Place(new Cell(0, 1), plain1);                 // sur la colonne : dans les rayons du motif Tour
        m.Place(new Cell(0, 2), plain2);
        m.Place(new Cell(5, 5), Make(Faction.Enemy, 30, 0, None));   // hors des rayons : épargné
        // L'attaquant tire le long de la RANGÉE : elle doit rester dégagée, sinon c'est le premier pion
        // rencontré qui serait la cible (cf. AttackTargets) et le coup ne partirait jamais sur le porteur.
        m.Place(new Cell(3, 0), Make(Faction.Player, 20, 12, None));

        m.TryAttack(new Cell(3, 0), new Cell(0, 0));

        // 12 dégâts pour 3 unités liées : 4 chacun (aucun reste), moins 1 d'absorption chacune.
        Assert.Equal(27, bearer.Hp);
        Assert.Equal(27, plain1.Hp);
        Assert.Equal(27, plain2.Hp);
        Assert.Equal(30, m.UnitAt(new Cell(5, 5))!.Hp);
    }

    [Fact]
    public void LienDAmitie_LeavesTheRemainderToTheVictim()
    {
        var m = Board();
        var bearer = Make(Faction.Enemy, 30, 0, new[] { Trait.LienDAmitie });
        var ally = Make(Faction.Enemy, 30, 0, None);
        m.Place(new Cell(0, 0), bearer);
        m.Place(new Cell(0, 1), ally);
        m.Place(new Cell(3, 0), Make(Faction.Player, 20, 11, None));

        m.TryAttack(new Cell(3, 0), new Cell(0, 0));

        Assert.Equal(25, bearer.Hp);   // 11 pour deux : 6 (part + reste) pour la victime moins 1 = 5
        Assert.Equal(26, ally.Hp);     // …et 5 pour le camarade moins 1 = 4
    }

    /// <summary>
    /// L'absorption est PAR UNITÉ TOUCHÉE : chaque part perd 1, la victime comme chacun de ses camarades.
    /// Le lien est donc d'autant plus solide qu'il y a de monde autour — trois camarades effacent 4 dégâts
    /// du coup total, pas 1.
    /// </summary>
    [Fact]
    public void LienDAmitie_TakesOneOffEveryShare_NotJustTheVictims()
    {
        var m = Board();
        var bearer = Make(Faction.Enemy, 30, 0, new[] { Trait.LienDAmitie });
        var a1 = Make(Faction.Enemy, 30, 0, None);
        var a2 = Make(Faction.Enemy, 30, 0, None);
        var a3 = Make(Faction.Enemy, 30, 0, None);
        m.Place(new Cell(0, 0), bearer);
        m.Place(new Cell(0, 1), a1);
        m.Place(new Cell(0, 2), a2);
        m.Place(new Cell(0, 3), a3);
        m.Place(new Cell(3, 0), Make(Faction.Player, 20, 20, None));

        m.TryAttack(new Cell(3, 0), new Cell(0, 0));

        // 20 pour 4 unités liées : 5 chacune, moins 1 = 4 encaissés par tête (16 sur 20 seulement).
        Assert.Equal(26, bearer.Hp);
        Assert.Equal(26, a1.Hp);
        Assert.Equal(26, a2.Hp);
        Assert.Equal(26, a3.Hp);
    }

    /// <summary>
    /// Conséquence assumée de l'absorption : un coup dont chaque part vaut 1 est ENTIÈREMENT encaissé par le
    /// lien — personne ne perd de PV. Le trait n'a donc pas de plancher à 1 dégât.
    /// </summary>
    [Fact]
    public void LienDAmitie_AbsorbsASmallHitEntirely()
    {
        var m = Board();
        var bearer = Make(Faction.Enemy, 30, 0, new[] { Trait.LienDAmitie });
        var ally = Make(Faction.Enemy, 30, 0, None);
        m.Place(new Cell(0, 0), bearer);
        m.Place(new Cell(0, 1), ally);
        m.Place(new Cell(3, 0), Make(Faction.Player, 20, 2, None));

        Assert.Equal(0, m.PreviewDamage(new Cell(3, 0), new Cell(0, 0)));

        m.TryAttack(new Cell(3, 0), new Cell(0, 0));

        Assert.Equal(30, bearer.Hp);   // 2 pour deux : 1 chacun, moins 1 = rien
        Assert.Equal(30, ally.Hp);
    }

    [Fact]
    public void LienDAmitie_WithoutABondedAllyInReach_ChangesNothing()
    {
        var m = Board();
        var artisan = Make(Faction.Enemy, 30, 0, new[] { Trait.LienDAmitie });
        m.Place(new Cell(0, 0), artisan);
        m.Place(new Cell(3, 0), Make(Faction.Player, 20, 10, None));

        m.TryAttack(new Cell(3, 0), new Cell(0, 0));

        Assert.Equal(20, artisan.Hp);   // seul : il encaisse tout
    }

    [Fact]
    public void PositionStrategique_AddsPowerOnlyWhileAnAllyIsInReach()
    {
        var withAlly = Board();
        withAlly.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.PositionStrategique }));
        withAlly.Place(new Cell(0, 1), Make(Faction.Player, 20, 0, None));
        withAlly.Place(new Cell(3, 0), Make(Faction.Enemy, 40, 0, None));
        withAlly.TryAttack(new Cell(0, 0), new Cell(3, 0));
        Assert.Equal(25, withAlly.UnitAt(new Cell(3, 0))!.Hp);   // 40 - (10 + 5)

        var alone = Board();
        alone.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.PositionStrategique }));
        alone.Place(new Cell(3, 0), Make(Faction.Enemy, 40, 0, None));
        alone.TryAttack(new Cell(0, 0), new Cell(3, 0));
        Assert.Equal(30, alone.UnitAt(new Cell(3, 0))!.Hp);      // 40 - 10
    }

    [Fact]
    public void Roque_LetsTheTwoLeadersSwapPlaces_OnlyWhenBought()
    {
        var m = Board();
        var artisan = Make(Faction.Player, 30, 0, None, essential: true, moveRange: 3);
        var basile = Make(Faction.Player, 22, 0, None, essential: true, companion: true);
        m.Place(new Cell(0, 0), artisan);
        m.Place(new Cell(0, 2), basile);

        // Sans le nœud : la case du camarade n'est même pas un déplacement légal.
        Assert.DoesNotContain(new Cell(0, 2), m.LegalMoves(new Cell(0, 0)));

        m.RoqueEnabled = true;
        Assert.Contains(new Cell(0, 2), m.LegalMoves(new Cell(0, 0)));
        Assert.Equal(MoveKind.Moved, m.TryMove(new Cell(0, 0), new Cell(0, 2)));
        Assert.Same(artisan, m.UnitAt(new Cell(0, 2)));
        Assert.Same(basile, m.UnitAt(new Cell(0, 0)));
    }

    /// <summary>
    /// « La puissance du rock » : le roque met la charge en réserve sur l'ARTISAN, sa prochaine attaque la
    /// dépense, et la suivante retombe à sa puissance nue. Sans le nœud (<c>RoquePower</c> à 0), rien ne change.
    /// </summary>
    [Fact]
    public void RoquePower_ChargesTheArtisanUntilHisNextAttack()
    {
        var m = Board();
        m.RoqueEnabled = true;
        m.RoquePower = 5;
        var artisan = Make(Faction.Player, 30, 10, None, essential: true, moveRange: 3);
        var basile = Make(Faction.Player, 22, 0, None, essential: true, companion: true);
        var target = Make(Faction.Enemy, 100, 0, None);
        m.Place(new Cell(0, 0), artisan);
        m.Place(new Cell(0, 2), basile);
        m.Place(new Cell(3, 2), target);

        Assert.Equal(0, artisan.RoquePower);
        m.TryMove(new Cell(0, 0), new Cell(0, 2));      // roque : l'artisan arrive en (0 2) chargé
        Assert.Equal(5, artisan.RoquePower);

        m.PassTurn();
        m.TryAttack(new Cell(0, 2), new Cell(3, 2));
        Assert.Equal(85, target.Hp);                    // 100 - (10 + 5)
        Assert.Equal(0, artisan.RoquePower);            // la charge est dépensée

        m.PassTurn();
        m.TryAttack(new Cell(0, 2), new Cell(3, 2));
        Assert.Equal(75, target.Hp);                    // 85 - 10 : puissance nue
    }

    /// <summary>La charge ne se CUMULE pas : roquer deux fois de suite ne donne pas +10.</summary>
    [Fact]
    public void RoquePower_DoesNotStackOverSeveralRoques()
    {
        var m = Board();
        m.RoqueEnabled = true;
        m.RoquePower = 5;
        var artisan = Make(Faction.Player, 30, 10, None, essential: true, moveRange: 3);
        var basile = Make(Faction.Player, 22, 0, None, essential: true, companion: true);
        var target = Make(Faction.Enemy, 100, 0, None);
        m.Place(new Cell(0, 0), artisan);
        m.Place(new Cell(0, 2), basile);
        m.Place(new Cell(3, 0), target);

        m.TryMove(new Cell(0, 0), new Cell(0, 2));
        m.PassTurn();
        m.TryMove(new Cell(0, 2), new Cell(0, 0));      // second roque, dans l'autre sens
        Assert.Equal(5, artisan.RoquePower);

        m.PassTurn();
        m.TryAttack(new Cell(0, 0), new Cell(3, 0));
        Assert.Equal(85, target.Hp);                    // 100 - (10 + 5) et non - (10 + 10)
    }

    /// <summary>
    /// Le roque charge l'ARTISAN quel que soit celui des deux meneurs qui l'a lancé : c'est le même échange vu
    /// des deux bouts, et Basile n'en profite jamais (le nœud est le sien à lui).
    /// </summary>
    [Fact]
    public void RoquePower_ChargesTheArtisan_EvenWhenBasileInitiatesTheSwap()
    {
        var m = Board();
        m.RoqueEnabled = true;
        m.RoquePower = 5;
        var artisan = Make(Faction.Player, 30, 10, None, essential: true);
        var basile = Make(Faction.Player, 22, 10, None, essential: true, companion: true, moveRange: 3);
        m.Place(new Cell(0, 0), artisan);
        m.Place(new Cell(0, 2), basile);

        m.TryMove(new Cell(0, 2), new Cell(0, 0));      // c'est BASILE qui se déplace sur l'artisan

        Assert.Equal(5, artisan.RoquePower);
        Assert.Equal(0, basile.RoquePower);
    }

    [Fact]
    public void CrossKillPower_ScalesWithTheCompanionKills()
    {
        var m = Board();
        m.CrossKillPower = 1;
        var artisan = Make(Faction.Player, 30, 10, None, essential: true);
        var basile = Make(Faction.Player, 22, 0, None, essential: true, companion: true, kills: 7);
        m.Place(new Cell(0, 0), artisan);
        m.Place(new Cell(5, 5), basile);
        m.Place(new Cell(3, 0), Make(Faction.Enemy, 40, 0, None));

        m.TryAttack(new Cell(0, 0), new Cell(3, 0));

        // 7 kills de Basile = 2 tranches de 3 → +2 puissance pour l'artisan.
        Assert.Equal(28, m.UnitAt(new Cell(3, 0))!.Hp);   // 40 - (10 + 2)
    }

    [Fact]
    public void CrossKillPower_NeverFlowsBackToTheCompanion()
    {
        var m = Board();
        m.CrossKillPower = 1;
        var artisan = Make(Faction.Player, 30, 0, None, essential: true, kills: 9);
        var basile = Make(Faction.Player, 22, 10, None, essential: true, companion: true);
        m.Place(new Cell(0, 0), artisan);
        m.Place(new Cell(5, 5), basile);
        m.Place(new Cell(5, 3), Make(Faction.Enemy, 40, 0, None));

        m.TryAttack(new Cell(5, 5), new Cell(5, 3));

        // Sens unique : les 9 kills de l'artisan ne donnent RIEN à Basile.
        Assert.Equal(30, m.UnitAt(new Cell(5, 3))!.Hp);   // 40 - 10
    }

    [Fact]
    public void ReactionEnChaine_KeepsStrikingWhileItKills()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 30, 10, new[] { Trait.ReactionEnChaine },
            domaine: Domaine.Dame, attackRange: 1, moveRange: 1));
        m.Place(new Cell(1, 0), Make(Faction.Enemy, 5, 0, None));    // meurt du coup direct
        m.Place(new Cell(2, 0), Make(Faction.Enemy, 5, 0, None));    // maillon 1 : au contact du corps
        m.Place(new Cell(3, 0), Make(Faction.Enemy, 40, 0, None));   // maillon 2 : au contact du maillon 1
        m.Place(new Cell(5, 5), Make(Faction.Enemy, 40, 0, None));   // isolé : la chaîne ne l'atteint jamais

        m.TryAttack(new Cell(0, 0), new Cell(1, 0));

        // Le coup direct tue et fait avancer l'attaquant ; la chaîne, elle, est seulement ARMÉE : elle se
        // joue maillon par maillon, au rythme des sauts (c'est la scène qui la pompe, cf. ChainFx).
        Assert.Equal(Faction.Player, m.UnitAt(new Cell(1, 0))!.Faction);   // l'attaquant a pris la place
        Assert.Equal(new Cell(2, 0), m.PendingChainTarget);                 // maillon 1 en attente, pas encore frappé
        Assert.NotNull(m.UnitAt(new Cell(2, 0)));

        while (m.ResolveNextChainLink() != null) { }                        // la scène joue les sauts

        Assert.Null(m.UnitAt(new Cell(2, 0)));                              // maillon 1 abattu…
        // …et la chaîne repart DU CORPS : (3,0) est au contact de (2,0), donc il encaisse à son tour. Elle
        // s'arrête là : il survit (40 - 10).
        Assert.Equal(30, m.UnitAt(new Cell(3, 0))!.Hp);
        Assert.Equal(40, m.UnitAt(new Cell(5, 5))!.Hp);
        Assert.False(m.HasPendingChain);
    }

    /// <summary>
    /// La chaîne s'éteint sur le premier SURVIVANT : le maillon est bien frappé, mais il n'y a pas de suivant
    /// (même si un autre ennemi est au contact).
    /// </summary>
    [Fact]
    public void ReactionEnChaine_StopsOnTheFirstSurvivor()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 30, 10, new[] { Trait.ReactionEnChaine },
            domaine: Domaine.Dame, attackRange: 1, moveRange: 1));
        m.Place(new Cell(1, 0), Make(Faction.Enemy, 5, 0, None));    // meurt du coup direct
        m.Place(new Cell(1, 1), Make(Faction.Enemy, 40, 0, None));   // maillon 1 : SURVIT
        m.Place(new Cell(2, 1), Make(Faction.Enemy, 5, 0, None));    // au contact aussi, mais jamais atteint

        m.TryAttack(new Cell(0, 0), new Cell(1, 0));
        var hit = m.ResolveNextChainLink();

        Assert.NotNull(hit);
        Assert.Equal(10, hit!.Value.Damage);
        Assert.False(hit.Value.Killed);
        Assert.False(m.HasPendingChain);                             // la chaîne s'arrête là
        Assert.Equal(5, m.UnitAt(new Cell(2, 1))!.Hp);               // le voisin est intact
    }

    [Fact]
    public void TirEnLigne_HitsEveryAlignedTargetInRange()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 30, 10, new[] { Trait.TirEnLigne }, attackRange: 3));
        m.Place(new Cell(2, 0), Make(Faction.Enemy, 40, 0, None));   // cible visée
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 40, 0, None));   // alignée sur l'autre axe
        m.Place(new Cell(5, 5), Make(Faction.Enemy, 40, 0, None));   // hors ligne ET hors portée

        m.TryAttack(new Cell(0, 0), new Cell(2, 0));

        Assert.Equal(30, m.UnitAt(new Cell(2, 0))!.Hp);
        Assert.Equal(30, m.UnitAt(new Cell(0, 2))!.Hp);
        Assert.Equal(40, m.UnitAt(new Cell(5, 5))!.Hp);
    }

    // ── Objets à lancer (sacoche) ────────────────────────────────────────────────

    private static Equipment Thrown(string id, string trait, int damage) =>
        Equipment.Of(id, id, EquipmentRarity.Common,
            new[] { EquipEffect.OfStat(EquipStat.Damage, damage), EquipEffect.OfTrait(trait) },
            satchel: true);

    [Fact]
    public void Grenade_SplashesAroundTheTarget_ForHalfDamage()
    {
        var m = Board();
        var basile = Make(Faction.Player, 22, 10, None, companion: true,
            equipment: new[] { Thrown("grenade", Trait.Grenade, 8) });
        m.Place(new Cell(0, 0), basile);
        m.Place(new Cell(3, 0), Make(Faction.Enemy, 60, 0, None));   // cible
        m.Place(new Cell(3, 1), Make(Faction.Enemy, 60, 0, None));   // éclaboussé

        m.TryAttack(new Cell(0, 0), new Cell(3, 0));

        Assert.Equal(42, m.UnitAt(new Cell(3, 0))!.Hp);   // 60 - 18 (10 + 8)
        Assert.Equal(51, m.UnitAt(new Cell(3, 1))!.Hp);   // 60 - 9 (la moitié)
    }

    [Fact]
    public void BalleRebondissante_BouncesOnAdjacentEnemiesOnly_OncePerEnemy()
    {
        var m = Board();
        var basile = Make(Faction.Player, 22, 10, None, companion: true,
            equipment: new[] { Thrown("balle", Trait.BalleRebondissante, 4) });
        m.Place(new Cell(0, 0), basile);
        m.Place(new Cell(3, 0), Make(Faction.Enemy, 60, 0, None));   // cible directe
        m.Place(new Cell(4, 1), Make(Faction.Enemy, 60, 0, None));   // COLLÉ à la cible : rebond
        m.Place(new Cell(6, 3), Make(Faction.Enemy, 60, 0, None));   // à 2 cases du dernier touché : hors de portée
        m.Place(new Cell(7, 7), Make(Faction.Enemy, 60, 0, None));   // à l'autre bout : épargné

        m.TryAttack(new Cell(0, 0), new Cell(3, 0));

        Assert.Equal(46, m.UnitAt(new Cell(3, 0))!.Hp);   // 60 - 14 : le coup DIRECT porte tout de suite…
        Assert.Equal(60, m.UnitAt(new Cell(4, 1))!.Hp);   // …le rebond attend que la balle arrive

        while (m.ResolveNextBounce() != null) { }          // la scène vide la file au fil du vol

        Assert.Equal(46, m.UnitAt(new Cell(4, 1))!.Hp);   // le rebond frappe aussi fort
        Assert.Equal(60, m.UnitAt(new Cell(6, 3))!.Hp);   // la balle ne saute QUE d'une case
        Assert.Equal(60, m.UnitAt(new Cell(7, 7))!.Hp);
    }

    /// <summary>
    /// Le coup d'un rebond n'est porté QU'AU MOMENT où la balle se pose : sans ça, tout le monde encaissait
    /// et tombait avant même de la voir partir, et l'animation survolait des cadavres.
    /// </summary>
    [Fact]
    public void BalleRebondissante_HitsOnlyWhenTheBallLands()
    {
        var m = Board();
        var basile = Make(Faction.Player, 22, 10, None, companion: true,
            equipment: new[] { Thrown("balle", Trait.BalleRebondissante, 4) });
        m.Place(new Cell(0, 0), basile);
        m.Place(new Cell(3, 0), Make(Faction.Enemy, 60, 0, None));
        var first = Make(Faction.Enemy, 8, 0, None);    // 8 PV : le rebond l'abat
        var second = Make(Faction.Enemy, 60, 0, None);
        m.Place(new Cell(4, 1), first);
        m.Place(new Cell(5, 2), second);

        m.TryAttack(new Cell(0, 0), new Cell(3, 0));

        // Rien n'a encore touché les deux suivants : ils sont intacts ET toujours sur le plateau.
        Assert.True(m.HasPendingBounce);
        Assert.Equal(8, first.Hp);
        Assert.Equal(60, second.Hp);
        Assert.NotNull(m.UnitAt(new Cell(4, 1)));

        var hop1 = m.ResolveNextBounce();
        Assert.Equal((new Cell(4, 1), 8, true), hop1);    // premier rebond : il tombe MAINTENANT
        Assert.Null(m.UnitAt(new Cell(4, 1)));
        Assert.Equal(60, second.Hp);                       // le second attend toujours son tour

        var hop2 = m.ResolveNextBounce();
        Assert.Equal((new Cell(5, 2), 14, false), hop2);
        Assert.Null(m.ResolveNextBounce());                // file vidée
        Assert.False(m.HasPendingBounce);
    }

    /// <summary>
    /// La trajectoire est exposée à la scène pour l'animer : point de départ (la cible directe) puis les
    /// rebonds DANS L'ORDRE, chacun avec ses dégâts — la balle les visite un par un.
    /// </summary>
    [Fact]
    public void BalleRebondissante_ReportsItsTrajectory_InOrder()
    {
        var m = Board();
        var basile = Make(Faction.Player, 22, 10, None, companion: true,
            equipment: new[] { Thrown("balle", Trait.BalleRebondissante, 4) });
        m.Place(new Cell(0, 0), basile);
        m.Place(new Cell(3, 0), Make(Faction.Enemy, 60, 0, None));
        m.Place(new Cell(4, 1), Make(Faction.Enemy, 60, 0, None));   // 1er rebond (collé à la cible)
        m.Place(new Cell(5, 2), Make(Faction.Enemy, 60, 0, None));   // 2e rebond (collé au 1er)

        m.TryAttack(new Cell(0, 0), new Cell(3, 0));

        // La trajectoire est connue dès l'attaque : la scène sait où faire voler la balle.
        Assert.Equal(new Cell(3, 0), m.LastBounceFrom);
        Assert.Equal(new[] { new Cell(4, 1), new Cell(5, 2) }, m.PendingBouncePath);
        // Les rebonds ne sont PAS dans les coups collatéraux : ils ont leur propre rythme.
        Assert.Empty(m.LastSplashHits);

        while (m.ResolveNextBounce() != null) { }
        Assert.Equal(new[] { new Cell(4, 1), new Cell(5, 2) }, m.LastBounceHits.Select(b => b.Cell));
        Assert.All(m.LastBounceHits, b => Assert.Equal(14, b.Damage));
    }

    [Fact]
    public void FlecheDeCupidon_TurnsTheSurvivingTarget_AndNeverABoss()
    {
        var m = Board();
        var basile = Make(Faction.Player, 22, 12, None, companion: true,
            equipment: new[] { Thrown("fleche", Trait.FlecheDeCupidon, -10) });
        m.Place(new Cell(0, 0), basile);
        var victim = Make(Faction.Enemy, 60, 0, None);
        m.Place(new Cell(3, 0), victim);

        m.TryAttack(new Cell(0, 0), new Cell(3, 0));

        Assert.Equal(58, victim.Hp);              // 60 - 2 (12 - 10)
        Assert.Equal(Faction.Player, victim.Faction);
        Assert.True(victim.Charmed);
    }

    [Fact]
    public void ThrownItem_BreaksAfterASingleAttack()
    {
        var m = Board();
        var item = Thrown("javelot", Trait.JavelotMeurtrier, 25);
        var basile = Make(Faction.Player, 22, 10, None, companion: true, equipment: new[] { item });
        m.Place(new Cell(0, 0), basile);
        m.Place(new Cell(3, 0), Make(Faction.Enemy, 80, 0, None));
        m.Place(new Cell(0, 3), Make(Faction.Enemy, 80, 0, None));

        m.TryAttack(new Cell(0, 0), new Cell(3, 0));
        Assert.Equal(45, m.UnitAt(new Cell(3, 0))!.Hp);    // 80 - 35 : le javelot a servi
        Assert.Empty(basile.Equipments);                    // …et s'est brisé
        Assert.Same(item, m.LastBrokenItem);

        Assert.Equal(10, basile.Damage);                    // puissance nue : le bonus est parti avec l'objet
    }

    [Fact]
    public void Seringue_HealsTheAttackerForEverythingItDeals()
    {
        var m = Board();
        var basile = Make(Faction.Player, 22, 10, None, companion: true,
            equipment: new[] { Thrown("seringue", Trait.Seringue, 4) });
        basile.TakeDamage(20);                                       // 2 PV : de quoi voir le drain
        m.Place(new Cell(0, 0), basile);
        m.Place(new Cell(3, 0), Make(Faction.Enemy, 60, 0, None));

        m.TryAttack(new Cell(0, 0), new Cell(3, 0));

        Assert.Equal(46, m.UnitAt(new Cell(3, 0))!.Hp);              // 60 - 14 (10 + 4)
        Assert.Equal(16, basile.Hp);                                 // 2 + 14 : TOUT revient en PV
    }

    /// <summary>Le drain INTÉGRAL de la seringue ne se cumule pas avec « Drain de vie » (50 %) : le meilleur gagne.</summary>
    [Fact]
    public void Seringue_DoesNotStackWithDrainDeVie()
    {
        var m = Board();
        var basile = Make(Faction.Player, 40, 10, new[] { Trait.DrainDeVie }, companion: true,
            equipment: new[] { Thrown("seringue", Trait.Seringue, 4) });
        basile.TakeDamage(30);                                       // 10 PV
        m.Place(new Cell(0, 0), basile);
        m.Place(new Cell(3, 0), Make(Faction.Enemy, 60, 0, None));

        m.TryAttack(new Cell(0, 0), new Cell(3, 0));

        Assert.Equal(24, basile.Hp);                                 // 10 + 14, pas 10 + 14 + 7
    }

    /// <summary>
    /// Le pool d'une SACOCHE ne contient que les objets à lancer. Eux, en revanche, sortent AUSSI d'un coffre
    /// (ils sont dans leur pool de rareté comme n'importe quel objet) : une partie classique n'a pas de
    /// sacoches et doit quand même pouvoir les trouver. Les vagues ennemies les écartent par enemyAllowed.
    /// </summary>
    [Fact]
    public void SatchelItems_AreTheirOwnPool_ButAlsoDropFromChests()
    {
        Equipments.Load(new[]
        {
            Equipment.OfStat("epee", "Épée", EquipStat.Damage, 3),
            Thrown("grenade", Trait.Grenade, 8),
        });

        Assert.Contains(Equipments.OfRarity(EquipmentRarity.Common), e => e.Id == "grenade");
        Assert.Single(Equipments.Satchel);
        Assert.Equal("grenade", Equipments.RollSatchel(new Random(1))!.Id);

        Equipments.ResetToDefaults();
    }

    /// <summary>
    /// Configuration livrée : les objets à lancer sont classés RARES (c'est à cette rareté qu'un coffre de
    /// partie classique les propose) et restent interdits aux vagues ennemies.
    /// </summary>
    [Fact]
    public void ShippedConfig_MakesThrownItemsRare_AndNeverEnemyLoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var json = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, "src", "ChessArmy.Game", "Assets", "Config", "equipment.json"));

        var thrown = EquipmentCatalog.FromJson(json).Where(e => e.Satchel).ToList();
        Assert.NotEmpty(thrown);
        foreach (var item in thrown)
        {
            Assert.Equal(EquipmentRarity.Rare, item.Rarity);
            Assert.False(item.EnemyAllowed);
        }
    }

    // ── Boss ARTISAN : réservé au joueur qui a débloqué le duo, et accompagné ────

    /// <summary>Pool de test : un boss ordinaire, et le boss ARTISAN réservé au commandant DUO.</summary>
    private static IReadOnlyList<BossDef> BossPoolWithArtisan()
    {
        var plain = new UnitClass("Brute", "brute", tier: 1, maxHp: 30, damage: 10, moveRange: 1, attackRange: 1);
        var artisan = new UnitClass("Artisan", "artisan", tier: 1, maxHp: 40, damage: 14, moveRange: 2, attackRange: 1);
        return new[]
        {
            new BossDef("Brute", "brute", Domaine.Dame,
                new Dictionary<int, UnitClass> { [1] = plain, [2] = plain, [3] = plain }),
            new BossDef("Artisan", "artisan", Domaine.Dame,
                new Dictionary<int, UnitClass> { [1] = artisan, [2] = artisan, [3] = artisan },
                companionId: "basileTest", requiresCommander: "duoTest"),
        };
    }

    /// <summary>
    /// Le boss ARTISAN n'entre dans le tirage que si le commandant DUO est DÉBLOQUÉ. Sinon il est écarté du
    /// pool, quelle que soit la graine — affronter un duo qu'on ne connaît pas encore n'aurait pas de sens.
    /// </summary>
    [Fact]
    public void ArtisanBoss_IsDrawnOnlyOnceTheDuoCommanderIsUnlocked()
    {
        var pool = BossPoolWithArtisan();
        var locked = new HashSet<string>();
        var unlocked = new HashSet<string> { "duoTest" };

        // Verrouillé : aucune graine ne peut le sortir.
        for (var seed = 0; seed < 40; seed++)
            Assert.DoesNotContain(Bosses.AssignForRun(pool, seed, phaseCount: 3, locked),
                b => b.Name == "Artisan");

        // Débloqué : il apparaît (au moins une graine le tire).
        Assert.Contains(Enumerable.Range(0, 40),
            seed => Bosses.AssignForRun(pool, seed, phaseCount: 3, unlocked).Any(b => b.Name == "Artisan"));
    }

    /// <summary>
    /// Basile REMPLACE l'escorte du plus haut tier au lieu de s'y ajouter : l'effectif reste celui des cases
    /// de spawn dessinées. Il n'est PAS essentiel (l'abattre ne gagne pas le combat) mais est marqué
    /// compagnon, ce qui le tient hors du butin de recrutement.
    /// </summary>
    [Fact]
    public void ArtisanBoss_BasileReplacesTheStrongestEscort_WithoutChangingTheHeadcount()
    {
        UseDuoRegistry();
        Bosses.Load(BossPoolWithArtisan());
        // Run d'un commandant ORDINAIRE (le joueur n'est pas le duo) qui a débloqué le duo : le boss est
        // tirable. On cherche une graine dont la PHASE 1 tombe sur l'artisan, pour rester au combat 1.
        Run? run = null;
        for (var seed = 0; seed < 40 && run is null; seed++)
        {
            var candidate = new Run(seed, commander: Commandes.ById("commandant"));
            candidate.SetUnlockedCommanders(new HashSet<string> { "duoTest" });
            if (candidate.BossOfPhase(1).Name == "Artisan")
                run = candidate;
        }
        Assert.NotNull(run);

        var wave = run!.BuildBossEnemyWave(escortCount: 4);

        Assert.Equal(5, wave.Count);                                   // 1 boss + 4 cases d'escorte : inchangé
        Assert.True(wave[0].Essential);                                // le boss en tête
        var basile = wave.Skip(1).Single(u => u.Companion);
        Assert.False(basile.Essential);                                // l'abattre ne gagne pas le combat
        Assert.Equal("basile", basile.UnitClass.Asset);
    }

    /// <summary>
    /// Les stats de Basile EN BOSS se règlent phase par phase, indépendamment de sa fiche jouable : sinon on
    /// ne pourrait pas équilibrer la rencontre sans toucher au commandant du joueur. Son NOM et son SPRITE
    /// restent les siens — c'est bien lui qu'on affronte.
    /// </summary>
    [Fact]
    public void ArtisanBoss_BasileFightsWithTheProfileDeclaredForThePhase_KeepingHisIdentity()
    {
        UseDuoRegistry();
        var artisanClass = new UnitClass("Artisan", "artisan", tier: 1, maxHp: 40, damage: 14, moveRange: 2, attackRange: 1);
        // Profil de second RÉGLÉ ici : très différent de la fiche jouable de « basileTest » (22 pv / 10 dég).
        var basileP1 = new UnitClass("porteValeur", "porteValeur", tier: 1, maxHp: 26, damage: 11,
            moveRange: 2, attackRange: 2);
        var basileP2 = new UnitClass("porteValeur", "porteValeur", tier: 1, maxHp: 44, damage: 17,
            moveRange: 2, attackRange: 3, traits: new[] { Trait.TirEnLigne });
        Bosses.Load(new[]
        {
            new BossDef("Artisan", "artisan", Domaine.Dame,
                new Dictionary<int, UnitClass> { [1] = artisanClass, [2] = artisanClass, [3] = artisanClass },
                companionId: "basileTest",
                companionProfiles: new Dictionary<int, UnitClass> { [1] = basileP1, [2] = basileP2 }),
        });

        var run = new Run(seed: 1, commander: Commandes.ById("commandant"));
        var basile = run.BuildBossEnemyWave(escortCount: 3).Single(u => u.Companion);

        // Les CHIFFRES viennent du profil de phase 1…
        Assert.Equal(26, basile.UnitClass.MaxHp);
        Assert.Equal(11, basile.UnitClass.Damage);
        Assert.Equal(2, basile.UnitClass.AttackRange);
        // …mais l'identité reste celle du second jouable (nom + sprite), jamais le porte-valeur du profil.
        Assert.Equal("basile", basile.UnitClass.Asset);
        Assert.Equal("Basile", basile.UnitClass.Name);
    }

    /// <summary>Sans profil déclaré pour le second, il retombe sur sa fiche JOUABLE (repli).</summary>
    [Fact]
    public void ArtisanBoss_WithoutACompanionProfile_BasileKeepsHisPlayableSheet()
    {
        UseDuoRegistry();
        var artisanClass = new UnitClass("Artisan", "artisan", tier: 1, maxHp: 40, damage: 14, moveRange: 2, attackRange: 1);
        Bosses.Load(new[]
        {
            new BossDef("Artisan", "artisan", Domaine.Dame,
                new Dictionary<int, UnitClass> { [1] = artisanClass }, companionId: "basileTest"),
        });

        var run = new Run(seed: 1, commander: Commandes.ById("commandant"));
        var basile = run.BuildBossEnemyWave(escortCount: 3).Single(u => u.Companion);

        Assert.Equal(CompanionDef().BaseClass.MaxHp, basile.UnitClass.MaxHp);
        Assert.Equal(CompanionDef().BaseClass.Damage, basile.UnitClass.Damage);
    }

    // ── Configuration livrée ─────────────────────────────────────────────────────

    [Fact]
    public void ShippedConfig_DeclaresTheDuoCommander_ItsCompanionAndItsTree()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var assets = System.IO.Path.Combine(dir!.FullName, "src", "ChessArmy.Game", "Assets", "Config");

        var commandes = ChessArmy.Core.Battle.Config.DomaineCatalog.CommandesFromJson(
            System.IO.File.ReadAllText(System.IO.Path.Combine(assets, "units.json")));

        var duo = commandes.Single(c => c.Id == "Commandant_duo");
        // VERROUILLÉ au départ : il s'ouvre au compteur de mises à mort d'une partie (100), pas d'emblée.
        Assert.False(duo.StartsUnlocked);
        Assert.True(duo.NoArmy);
        Assert.Equal(0, duo.ReserveSize);
        Assert.Equal(2, duo.Deployments);           // les deux meneurs, rien d'autre
        Assert.Empty(duo.StartingUnits);
        // Revenu : 3 points par mission au lieu de 2, PLUS 1 par mise à mort « à deux » (2 max par combat).
        Assert.Equal(0, duo.HealPoints);
        Assert.Equal(3, duo.MissionPoints);
        Assert.Equal(1, duo.PairKillPoints);
        Assert.Equal(2, duo.PairKillCap);
        Assert.Equal("Basile", duo.CompanionId);

        var basile = commandes.Single(c => c.Id == duo.CompanionId);
        Assert.Equal(CommandeRole.Companion, basile.Role);

        // Le compagnon n'est JAMAIS proposé au carrousel de sélection.
        Commandes.Load(commandes);
        Assert.DoesNotContain(Commandes.Playable, c => c.Role == CommandeRole.Companion);
        Assert.Null(Commandes.ById("Basile"));
        Assert.NotNull(Commandes.CompanionById("Basile"));

        var trees = CommandTreeCatalog.FromJson(
            System.IO.File.ReadAllText(System.IO.Path.Combine(assets, "commander_trees.json")));
        var tree = trees.Single(t => t.Id == duo.TreeId);
        Assert.Contains(tree.Nodes, n => n.Effects.Any(e => e.Kind == CommandEffectKind.HealKitTiles));
        Assert.Contains(tree.Nodes, n => n.Effects.Any(e => e.Kind == CommandEffectKind.SatchelChests));
        Assert.Contains(tree.Nodes, n => n.Effects.Any(e => e.Kind == CommandEffectKind.Roque));
    }

    /// <summary>
    /// Trajet COMPLET du nœud « lien d'amitié » livré : l'effet de l'arbre doit arriver jusqu'aux DEUX
    /// meneurs spawnés (commandant ET compagnon), puis diviser un vrai coup sur le plateau. Les tests de
    /// moteur ci-dessus posent le trait à la main : celui-ci vérifie la CHAÎNE (JSON → buffs → Unit).
    /// </summary>
    [Fact]
    public void ShippedConfig_LienDAmitieNode_ReachesBothLeaders_AndSplitsARealHit()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var assets = System.IO.Path.Combine(dir!.FullName, "src", "ChessArmy.Game", "Assets", "Config");

        var trees = CommandTreeCatalog.FromJson(
            System.IO.File.ReadAllText(System.IO.Path.Combine(assets, "commander_trees.json")));
        var node = trees.Single(t => t.Id == "commandantDuo").Nodes.Single(n => n.Id == "duo_lien_amitie");

        // Le nœud donne bien le trait aux deux cibles…
        var forCommander = CommandBuffs.From(node.Effects, BuffTarget.Commander, distinctPairs: 0);
        var forCompanion = CommandBuffs.From(node.Effects, BuffTarget.Companion, distinctPairs: 0);
        Assert.Contains(Trait.LienDAmitie, forCommander.Traits);
        Assert.Contains(Trait.LienDAmitie, forCompanion.Traits);

        // …et les unités spawnées le PORTENT (c'est ce que lit le moteur).
        var cls = new UnitClass("T", "t", tier: 1, maxHp: 40, damage: 0, moveRange: 2, attackRange: 1);
        var artisan = new UnitSpec(Domaine.Dame, cls, essential: true).Spawn(Faction.Enemy, forCommander);
        var basile = new UnitSpec(Domaine.Dame, cls, essential: true, companion: true).Spawn(Faction.Enemy, forCompanion);
        Assert.True(artisan.HasTrait(Trait.LienDAmitie));
        Assert.True(basile.HasTrait(Trait.LienDAmitie));

        // Coup de 18 sur l'artisan, Basile collé à lui : 9 chacun, moins 1 d'absorption = 8.
        var m = new Match(8, 8);
        m.Place(new Cell(0, 0), artisan);
        m.Place(new Cell(0, 1), basile);
        m.Place(new Cell(3, 0), Make(Faction.Player, 30, 18, None));

        // L'APERÇU annonce déjà la part de la cible, pas le coup entier (c'est le chiffre qui jaillira)…
        Assert.Equal(8, m.PreviewDamage(new Cell(3, 0), new Cell(0, 0)));
        // …et il dit aussi ce que le camarade lié va prendre : sa jauge doit l'annoncer.
        var shared = new System.Collections.Generic.List<Cell>();
        Assert.Equal(8, m.PreviewSharedDamage(new Cell(3, 0), new Cell(0, 0), shared));
        Assert.Equal(new[] { new Cell(0, 1) }, shared);

        m.TryAttack(new Cell(3, 0), new Cell(0, 0));

        Assert.Equal(32, artisan.Hp);
        Assert.Equal(32, basile.Hp);
        // …et le camarade a son propre chiffre de dégâts (sinon le partage serait invisible).
        Assert.Contains(m.LastSplashHits, h => h.Cell == new Cell(0, 1) && h.Damage == 8);
    }

    // ── « Continue sans moi » : la run survit à la perte d'un meneur ─────────────

    /// <summary>
    /// Sans le nœud, la chute d'un meneur perd le combat sur-le-champ. Avec, le combat CONTINUE tant que
    /// l'autre est debout — et ne se perd que lorsque le second tombe à son tour.
    /// </summary>
    [Fact]
    public void SoloSurvivor_AFallenLeaderNoLongerLosesTheBattle_UntilBothAreDown()
    {
        // Sans le nœud : l'artisan tombe, la partie est perdue bien que Basile tienne encore.
        var strict = Board();
        var a1 = Make(Faction.Player, 5, 0, None, essential: true);
        var b1 = Make(Faction.Player, 30, 0, None, essential: true, companion: true);
        strict.Place(new Cell(0, 0), a1);
        strict.Place(new Cell(0, 2), b1);
        strict.Place(new Cell(3, 0), Make(Faction.Enemy, 30, 30, None));
        strict.PassTurn();                                    // au tour de l'ennemi de frapper
        strict.TryAttack(new Cell(3, 0), new Cell(0, 0));
        Assert.True(strict.IsOver);
        Assert.Equal(Faction.Enemy, strict.Winner);

        // Avec le nœud : même coup, le combat continue — Basile est toujours là.
        var solo = Board();
        solo.SoloSurvivorEnabled = true;
        var a2 = Make(Faction.Player, 5, 0, None, essential: true);
        var b2 = Make(Faction.Player, 5, 0, None, essential: true, companion: true);
        solo.Place(new Cell(0, 0), a2);
        solo.Place(new Cell(0, 2), b2);
        solo.Place(new Cell(3, 0), Make(Faction.Enemy, 30, 30, None));
        solo.Place(new Cell(3, 2), Make(Faction.Enemy, 30, 30, None));   // aligné sur Basile (rangée 2)
        solo.PassTurn();
        solo.TryAttack(new Cell(3, 0), new Cell(0, 0));
        Assert.False(a2.IsAlive);
        Assert.False(solo.IsOver);                            // Basile continue seul

        // …mais la chute du SECOND décide bien la partie.
        solo.PassTurn();
        solo.TryAttack(new Cell(3, 2), new Cell(0, 2));
        Assert.True(solo.IsOver);
        Assert.Equal(Faction.Enemy, solo.Winner);
    }

    /// <summary>
    /// À la clôture du combat, le meneur tombé QUITTE la run et lègue au survivant la moitié de sa puissance
    /// et de ses PV max. Le legs porte sur les stats EFFECTIVES du mort, bonus d'arbre compris.
    /// </summary>
    /// <summary>Arbre de test réduit au seul nœud « Continue sans moi », et run du duo qui l'a acheté.</summary>
    private static Run SoloSurvivorRun()
    {
        UseDuoRegistry();
        CommandTrees.Load(CommandTreeCatalog.FromJson("""
        { "trees": [ { "id": "duoTest", "nodes": [
            { "id": "n_solo", "branch": 0, "level": 1, "effects": [ { "kind": "soloSurvivor" } ] } ] } ] }
        """));
        var run = new Run(seed: 1, commander: DuoDef("duoTest"));
        run.GrantCommandPoints(10);
        Assert.True(run.Unlock(run.Tree.ById("n_solo")!));
        Assert.True(run.SoloSurvivor);
        return run;
    }

    [Fact]
    public void SoloSurvivor_TheFallenLeaderLeavesTheRun_AndBequeathsHalfHisStats()
    {
        var run = SoloSurvivorRun();

        var artisan = run.Commanders.Single(c => !c.Companion);
        var basile = run.CompanionSpec!;
        var expectedPower = artisan.UnitClass.Damage / 2;
        var expectedHp = artisan.UnitClass.MaxHp / 2;

        run.StartBattle();
        run.CompleteCombat(new[] { artisan }, System.Array.Empty<UnitSpec>());

        Assert.DoesNotContain(artisan, run.Roster);           // il a quitté la run pour de bon
        Assert.Contains(basile, run.Roster);
        Assert.Equal(expectedPower, run.InheritedLeaderPower);
        Assert.Equal(expectedHp, run.InheritedLeaderHp);
        // …et le legs arrive bien sur les stats du survivant.
        var buffs = run.BuffsFor(basile);
        Assert.Equal(expectedPower, buffs.BonusFor(EquipStat.Damage));
        Assert.Equal(expectedHp, buffs.BonusFor(EquipStat.Hp));
    }

    /// <summary>
    /// Les DEUX meneurs tombés : rien n'est légué et le roster garde ses meneurs. La défaite est déjà
    /// prononcée ; vider le roster rendrait la run insauvable.
    /// </summary>
    [Fact]
    public void SoloSurvivor_WithBothLeadersDown_BequeathsNothing()
    {
        var run = SoloSurvivorRun();

        var leaders = run.Commanders.ToList();
        run.StartBattle();
        run.CompleteCombat(leaders, System.Array.Empty<UnitSpec>());

        Assert.Equal(0, run.InheritedLeaderPower);
        Assert.Equal(0, run.InheritedLeaderHp);
        Assert.Equal(leaders.Count, run.Commanders.Count);
    }

    /// <summary>
    /// Le legs est réclamable DÈS LA CHUTE, sans attendre la clôture du combat : c'est ce que la scène fait,
    /// et c'est ce qui permet au survivant de finir le combat en cours avec son héritage. Le montant rendu est
    /// celui à appliquer séance tenante aux pions déjà posés.
    /// </summary>
    [Fact]
    public void SoloSurvivor_TheBequestCanBeClaimedTheMomentALeaderFalls()
    {
        var run = SoloSurvivorRun();
        var artisan = run.Commanders.Single(c => !c.Companion);

        var (power, hp) = run.AbsorbFallenLeaders(new[] { artisan });

        Assert.Equal(artisan.UnitClass.Damage / 2, power);
        Assert.Equal(artisan.UnitClass.MaxHp / 2, hp);
        Assert.DoesNotContain(artisan, run.Roster);
        // IDEMPOTENT : la clôture du combat repassera dessus sans rien léguer une seconde fois.
        Assert.Equal((0, 0), run.AbsorbFallenLeaders(new[] { artisan }));
        Assert.Equal(power, run.InheritedLeaderPower);
    }

    /// <summary>
    /// Le legs est PERSISTÉ : le tombé ayant quitté le roster, sans ces deux valeurs la reprise rendrait le
    /// survivant à ses stats d'avant.
    /// </summary>
    [Fact]
    public void SoloSurvivor_TheBequestSurvivesASaveAndReload()
    {
        var run = SoloSurvivorRun();
        run.StartBattle();
        run.CompleteCombat(new[] { run.Commanders.Single(c => !c.Companion) }, System.Array.Empty<UnitSpec>());

        var reloaded = RunSave.From(run).ToRun();

        Assert.Equal(run.InheritedLeaderPower, reloaded.InheritedLeaderPower);
        Assert.Equal(run.InheritedLeaderHp, reloaded.InheritedLeaderHp);
        Assert.Single(reloaded.Commanders);                   // le tombé ne revient pas
    }

    /// <summary>
    /// OUTIL DE TEST : la map imposée par l'éditeur de sauvegarde survit à l'aller-retour disque, sinon
    /// reprendre le slot rendrait la run au tirage normal — le forçage ne tiendrait qu'un seul combat.
    /// </summary>
    [Fact]
    public void ForcedMap_SurvivesASaveAndReload()
    {
        UseDuoRegistry();
        var run = new Run(seed: 1, commander: DuoDef()) { ForcedMapName = "Boss_1_01" };

        Assert.Equal("Boss_1_01", RunSave.From(run).ToRun().ForcedMapName);
        Assert.Null(new Run(seed: 1, commander: DuoDef()).ForcedMapName);   // partie normale : aucun forçage
    }

    // ── Mise à mort « à deux » : source de points du commandant DUO ──────────────

    /// <summary>
    /// Le moteur relève QUI a réellement entamé une unité — c'est là-dessus que se juge la mise à mort « à
    /// deux ». Un coup à 0 dégât ne compte pas, et un relais (éclat, épines) compte comme un coup direct.
    /// </summary>
    [Fact]
    public void WasDamagedBy_RecordsEveryAttackerThatActuallyHurtTheVictim()
    {
        var m = Board();
        var victim = Make(Faction.Enemy, 30, 0, None);
        var a1 = Make(Faction.Player, 20, 6, None);
        var a2 = Make(Faction.Player, 20, 6, None);
        var bystander = Make(Faction.Player, 20, 6, None);
        m.Place(new Cell(0, 0), victim);
        m.Place(new Cell(3, 0), a1);
        m.Place(new Cell(0, 3), a2);

        m.TryAttack(new Cell(3, 0), new Cell(0, 0));
        Assert.True(victim.WasDamagedBy(a1));
        Assert.False(victim.WasDamagedBy(a2));

        m.PassTurn();   // la main revient au joueur : le second assaillant peut frapper à son tour
        m.TryAttack(new Cell(0, 3), new Cell(0, 0));
        Assert.True(victim.WasDamagedBy(a1));   // le premier reste inscrit : l'ordre et le temps n'y font rien
        Assert.True(victim.WasDamagedBy(a2));
        Assert.False(victim.WasDamagedBy(bystander));
    }

    /// <summary>
    /// Le JOURNAL DES MORTS ne se vide pas en cours de combat : c'est ce qui permet à la scène de ne manquer
    /// aucune mise à mort, y compris celles qu'une riposte ou une action suivante provoque.
    /// </summary>
    [Fact]
    public void DeathLog_KeepsEveryFallenUnitOfTheCombat()
    {
        var m = Board();
        var v1 = Make(Faction.Enemy, 5, 0, None);
        var v2 = Make(Faction.Enemy, 5, 0, None);
        m.Place(new Cell(0, 0), v1);
        m.Place(new Cell(0, 5), v2);
        m.Place(new Cell(3, 0), Make(Faction.Player, 20, 30, None));
        m.Place(new Cell(3, 5), Make(Faction.Player, 20, 30, None));

        m.TryAttack(new Cell(3, 0), new Cell(0, 0));
        m.PassTurn();
        m.TryAttack(new Cell(3, 5), new Cell(0, 5));

        // Le journal retient AUSSI la case de chaque mort : c'est là que la scène pose son feedback, la case
        // pouvant déjà être reprise par le tueur.
        Assert.Equal(new[] { (new Cell(0, 0), v1), (new Cell(0, 5), v2) }, m.DeathLog);
    }

    /// <summary>
    /// Le plafond de la source est PAR COMBAT : <see cref="Run.StartBattle"/> le rend. Sans ça le commandant
    /// DUO n'en toucherait que deux pour toute la partie.
    /// </summary>
    [Fact]
    public void GrantPairKillPoint_CapsPerCombat_AndResetsOnTheNextBattle()
    {
        var def = new CommandeDef(CommandeRole.Commander, Domaine.Dame,
            new UnitClass("A", "a", tier: 1, maxHp: 30, damage: 10, moveRange: 2, attackRange: 1),
            pairKillPoints: 1, pairKillCap: 2, id: "pairKillTest");
        var run = new Run(seed: 1, commander: def);
        var before = run.CommandPoints;

        run.StartBattle();
        Assert.Equal(1, run.GrantPairKillPoint());
        Assert.Equal(1, run.GrantPairKillPoint());
        Assert.Equal(0, run.GrantPairKillPoint());   // plafond du combat atteint
        Assert.Equal(before + 2, run.CommandPoints);
        Assert.Equal(2, run.PairKillEventsThisCombat);

        run.StartBattle();                            // combat suivant : le plafond est rendu
        Assert.Equal(0, run.PairKillEventsThisCombat);
        Assert.Equal(1, run.GrantPairKillPoint());
        Assert.Equal(before + 3, run.CommandPoints);
    }

    /// <summary>Un commandant qui n'a pas cette source ne gagne jamais rien par cette voie.</summary>
    [Fact]
    public void GrantPairKillPoint_GivesNothingToACommanderWithoutTheSource()
    {
        var def = new CommandeDef(CommandeRole.Commander, Domaine.Dame,
            new UnitClass("A", "a", tier: 1, maxHp: 30, damage: 10, moveRange: 2, attackRange: 1),
            id: "noPairKillTest");
        var run = new Run(seed: 1, commander: def);
        run.StartBattle();

        Assert.Equal(0, run.GrantPairKillPoint());
    }

    /// <summary>
    /// « Pas pressé » (nœud <c>duo_art_mouvement</c>, branche du LIEN) : le pas gagné va aux DEUX meneurs, pas
    /// au seul artisan. Vérifié sur la config LIVRÉE — oublier l'effet « companionStat » dans le json ne
    /// casserait rien d'autre, et le manque ne se verrait qu'en jouant.
    /// </summary>
    [Fact]
    public void ShippedConfig_PasPresseNode_GivesOneMoveToBothLeaders()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var assets = System.IO.Path.Combine(dir!.FullName, "src", "ChessArmy.Game", "Assets", "Config");

        var trees = CommandTreeCatalog.FromJson(
            System.IO.File.ReadAllText(System.IO.Path.Combine(assets, "commander_trees.json")));
        var node = trees.Single(t => t.Id == "commandantDuo").Nodes.Single(n => n.Id == "duo_art_mouvement");

        var forCommander = CommandBuffs.From(node.Effects, BuffTarget.Commander, distinctPairs: 0);
        var forCompanion = CommandBuffs.From(node.Effects, BuffTarget.Companion, distinctPairs: 0);

        // Les unités spawnées marchent une case plus loin, l'une comme l'autre.
        var cls = new UnitClass("T", "t", tier: 1, maxHp: 40, damage: 0, moveRange: 2, attackRange: 1);
        var artisan = new UnitSpec(Domaine.Dame, cls, essential: true).Spawn(Faction.Enemy, forCommander);
        var basile = new UnitSpec(Domaine.Dame, cls, essential: true, companion: true).Spawn(Faction.Enemy, forCompanion);
        Assert.Equal(3, artisan.MoveRange);
        Assert.Equal(3, basile.MoveRange);
    }
}
