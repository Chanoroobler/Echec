using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Equip;
using Echec.Core.Map;
using Xunit;

namespace Echec.Core.Tests;

/// <summary>
/// Mécaniques des TRAITS (cf. <see cref="Trait"/>), résolues dans <see cref="Match"/>. Toutes activées
/// par la simple présence du trait sur la classe — il suffit de « piocher » un trait pour qu'il agisse.
/// </summary>
public class TraitsTests
{
    // Unité de test : domaine TOUR (lignes droites) par défaut, portée de tir 3.
    private static Unit Make(Faction faction, int hp, int damage, string[] traits,
        Domaine domaine = Domaine.Tour, int attackRange = 3, int moveRange = 1, bool pierces = false)
    {
        var cls = new UnitClass("T", "t", tier: 1, maxHp: hp, damage: damage,
            moveRange: moveRange, attackRange: attackRange, piercesAllies: pierces, traits: traits);
        return new Unit(domaine, faction, cls);
    }

    private static string[] None => System.Array.Empty<string>();

    private static Match Board(int size = 8) => new(size, size);

    // ── Rempart / Duelliste : réductions de dégâts ────────────────────────────────

    [Fact]
    public void Rempart_ReducesRangedDamageByFour_NotMelee()
    {
        // À distance (>= 2) : -4.
        var ranged = Board();
        ranged.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        ranged.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, new[] { Trait.Rempart }));
        ranged.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(14, ranged.UnitAt(new Cell(0, 2))!.Hp);   // 20 - (10 - 4)

        // Au corps à corps (distance 1) : Rempart n'agit pas.
        var melee = Board();
        melee.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        melee.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 5, new[] { Trait.Rempart }));
        melee.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(10, melee.UnitAt(new Cell(0, 1))!.Hp);    // 20 - 10
    }

    [Fact]
    public void Duelliste_ReducesMeleeDamageByFour_NotRanged()
    {
        var melee = Board();
        melee.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        melee.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 5, new[] { Trait.Duelliste }));
        melee.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(14, melee.UnitAt(new Cell(0, 1))!.Hp);    // 20 - (10 - 4)

        var ranged = Board();
        ranged.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        ranged.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, new[] { Trait.Duelliste }));
        ranged.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(10, ranged.UnitAt(new Cell(0, 2))!.Hp);   // 20 - 10
    }

    [Fact]
    public void AuraDeRempart_GrantsRempartToAdjacentAlly()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));                 // cible sans Rempart propre
        m.Place(new Cell(1, 2), Make(Faction.Enemy, 20, 5, new[] { Trait.AuraDeRempart })); // allié adjacent
        m.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(14, m.UnitAt(new Cell(0, 2))!.Hp);        // -4 grâce à l'aura (distance 2)
    }

    // ── Rage / Bénédiction : bonus de puissance ───────────────────────────────────

    [Fact]
    public void Rage_AddsSixPower_WhenBelowThreshold()
    {
        var low = Board();
        var rager = Make(Faction.Player, 20, 10, new[] { Trait.Rage });
        rager.TakeDamage(15);   // 5 PV (< 10)
        low.Place(new Cell(0, 0), rager);
        low.Place(new Cell(0, 1), Make(Faction.Enemy, 30, 5, None));
        low.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(14, low.UnitAt(new Cell(0, 1))!.Hp);      // 30 - (10 + 6)

        var high = Board();
        high.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.Rage }));   // 20 PV (>= 10)
        high.Place(new Cell(0, 1), Make(Faction.Enemy, 30, 5, None));
        high.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(20, high.UnitAt(new Cell(0, 1))!.Hp);     // 30 - 10 (pas de bonus)
    }

    [Fact]
    public void Benediction_AdjacentAlly_AddsFivePower()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        m.Place(new Cell(1, 0), Make(Faction.Player, 20, 5, new[] { Trait.Benediction })); // allié adjacent
        m.Place(new Cell(0, 1), Make(Faction.Enemy, 30, 5, None));
        m.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(15, m.UnitAt(new Cell(0, 1))!.Hp);        // 30 - (10 + 5)
    }

    // ── Bouclier divin : protège de la mort ───────────────────────────────────────

    [Fact]
    public void BouclierDivin_AdjacentAlly_PreventsFatalDamage()
    {
        var shielded = Board();
        shielded.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        shielded.Place(new Cell(0, 2), Make(Faction.Enemy, 5, 5, None));                       // mourrait (10 >= 5)
        shielded.Place(new Cell(1, 2), Make(Faction.Enemy, 20, 5, new[] { Trait.BouclierDivin }));
        shielded.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(1, shielded.UnitAt(new Cell(0, 2))!.Hp);  // PV bloqués à 1

        var unshielded = Board();
        unshielded.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        unshielded.Place(new Cell(0, 2), Make(Faction.Enemy, 5, 5, None));
        unshielded.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Null(unshielded.UnitAt(new Cell(0, 2)));        // sans bouclier : mort, case vidée
    }

    // ── Formes d'attaque : Transpercement / Dégâts de zone ────────────────────────

    [Fact]
    public void Transpercement_AlsoHitsUnitBehindTarget()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.Transpercement }));
        m.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 5, None));   // cible
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));   // juste derrière
        m.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(14, m.UnitAt(new Cell(0, 1))!.Hp);
        Assert.Equal(14, m.UnitAt(new Cell(0, 2))!.Hp);             // touché par transpercement
    }

    [Fact]
    public void DegatsDeZone_SplashesEnemiesAroundTarget()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.DegatsDeZone }));
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));   // cible
        m.Place(new Cell(1, 2), Make(Faction.Enemy, 20, 5, None));   // adjacent à la cible → éclaboussé
        m.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(14, m.UnitAt(new Cell(0, 2))!.Hp);
        Assert.Equal(14, m.UnitAt(new Cell(1, 2))!.Hp);
    }

    // ── Déplacement : Franchissement ──────────────────────────────────────────────

    [Fact]
    public void Franchissement_MovesThroughUnits_ButNotOntoThem()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.Franchissement }, moveRange: 3));
        m.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 5, None));   // sur le chemin
        var moves = m.LegalMoves(new Cell(0, 0));

        Assert.DoesNotContain(new Cell(0, 1), moves);   // ne se pose pas SUR l'unité
        Assert.Contains(new Cell(0, 2), moves);         // mais l'enjambe
        Assert.Contains(new Cell(0, 3), moves);

        var blocked = Board();
        blocked.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None, moveRange: 3));
        blocked.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 5, None));
        Assert.DoesNotContain(new Cell(0, 2), blocked.LegalMoves(new Cell(0, 0)));   // sans trait : bloqué
    }

    [Fact]
    public void Franchissement_MovesThroughTerrainObstacles_ButNotOntoThem()
    {
        var field = Battlefield.CreateFlat(8, 8);
        field[new Cell(0, 1)] = new Tile(BuiltInTiles.Water);      // obstacle sur le chemin
        field[new Cell(0, 2)] = new Tile(BuiltInTiles.Mountain);   // second obstacle enchaîné

        var m = new Match(8, 8, field);
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.Franchissement }, moveRange: 3));
        var moves = m.LegalMoves(new Cell(0, 0));

        Assert.DoesNotContain(new Cell(0, 1), moves);   // ne s'arrête pas dans l'eau
        Assert.DoesNotContain(new Cell(0, 2), moves);   // ni sur la montagne
        Assert.Contains(new Cell(0, 3), moves);         // mais traverse les deux et se pose au-delà

        var normal = new Match(8, 8, field);            // sans le trait : l'eau borne le déplacement
        normal.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None, moveRange: 3));
        Assert.DoesNotContain(new Cell(0, 3), normal.LegalMoves(new Cell(0, 0)));
    }

    // ── Réactions : Interception / Riposte ────────────────────────────────────────

    [Fact]
    public void Interception_HitsEnemyMovingIntoRange()
    {
        var m = Board();
        m.Place(new Cell(0, 5), Make(Faction.Player, 20, 6, None, moveRange: 5));            // mobile (joueur)
        m.Place(new Cell(3, 3), Make(Faction.Enemy, 20, 7, new[] { Trait.Interception }));   // intercepteur
        m.TryMove(new Cell(0, 5), new Cell(3, 5));   // entre dans la colonne 3, à portée de l'intercepteur
        Assert.Equal(13, m.UnitAt(new Cell(3, 5))!.Hp);   // 20 - 7
    }

    [Fact]
    public void Riposte_CountersMeleeAttacker_WhenSurviving()
    {
        var melee = Board();
        melee.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None));
        melee.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 8, new[] { Trait.Riposte }));
        melee.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(14, melee.UnitAt(new Cell(0, 1))!.Hp);   // victime survit (20 - 6)
        Assert.Equal(12, melee.UnitAt(new Cell(0, 0))!.Hp);   // attaquant contre-attaqué (20 - 8)

        var ranged = Board();
        ranged.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None));
        ranged.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 8, new[] { Trait.Riposte }));
        ranged.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(20, ranged.UnitAt(new Cell(0, 0))!.Hp);  // pas de riposte à distance
    }

    // ── Soutien : Soin ────────────────────────────────────────────────────────────

    [Fact]
    public void Soin_HealsWoundedAlly_ByPower()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 5, new[] { Trait.Soin }));
        var ally = Make(Faction.Player, 20, 5, None);
        ally.TakeDamage(15);   // 5 PV
        m.Place(new Cell(0, 2), ally);

        Assert.Contains(new Cell(0, 2), m.HealTargets(new Cell(0, 0)));
        m.TryHeal(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(10, m.UnitAt(new Cell(0, 2))!.Hp);   // 5 + 5
    }

    [Fact]
    public void Soin_IgnoresFullHealthAlly()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 5, new[] { Trait.Soin }));
        m.Place(new Cell(0, 2), Make(Faction.Player, 20, 5, None));   // PV pleins
        Assert.Empty(m.HealTargets(new Cell(0, 0)));
    }

    // ── Traverse allié = PiercesAllies ────────────────────────────────────────────

    [Fact]
    public void TraverseAllie_MapsToPiercesAllies()
    {
        var pierces = Make(Faction.Player, 20, 5, None, pierces: true);
        var normal = Make(Faction.Player, 20, 5, None, pierces: false);
        Assert.True(pierces.HasTrait(Trait.TraverseAllie));
        Assert.False(normal.HasTrait(Trait.TraverseAllie));
    }

    [Fact]
    public void TraverseAllie_GrantedByEquipment_PiercesAllyToHitEnemyBehind()
    {
        // Pion SANS traverse natif : le tir est bloqué par l'allié… sauf s'il porte un équipement qui octroie
        // « Traverse allié » (le moteur lit le trait via HasTrait, donc l'équipement l'active).
        var cls = new UnitClass("T", "t", tier: 1, maxHp: 20, damage: 5, moveRange: 1, attackRange: 3);
        var lance = Equipment.OfTrait("lance", "Lance", Trait.TraverseAllie);

        var equipped = Board();
        equipped.Place(new Cell(0, 0), new Unit(Domaine.Tour, Faction.Player, cls, lance));
        equipped.Place(new Cell(0, 1), Make(Faction.Player, 20, 5, None));   // allié sur la ligne de tir
        equipped.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));    // ennemi DERRIÈRE l'allié
        var targets = equipped.AttackTargets(new Cell(0, 0));
        Assert.Contains(new Cell(0, 2), targets);       // traverse l'allié grâce à l'équipement
        Assert.DoesNotContain(new Cell(0, 1), targets); // l'allié n'est jamais une cible

        // Contrôle : le MÊME pion sans équipement ne traverse pas (l'allié borne la ligne).
        var bare = Board();
        bare.Place(new Cell(0, 0), new Unit(Domaine.Tour, Faction.Player, cls));
        bare.Place(new Cell(0, 1), Make(Faction.Player, 20, 5, None));
        bare.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));
        Assert.DoesNotContain(new Cell(0, 2), bare.AttackTargets(new Cell(0, 0)));
    }

    // ── Zone morte : contact interdit en ligne droite (portée min 2) ───────────────

    [Fact]
    public void ZoneMorte_CannotHitAdjacent_ButHitsAtRangeTwo()
    {
        var adjacent = Board();
        adjacent.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.ZoneMorte }));
        adjacent.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 5, None));   // au contact
        Assert.DoesNotContain(new Cell(0, 1), adjacent.AttackTargets(new Cell(0, 0)));

        var far = Board();
        far.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.ZoneMorte }));
        far.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));        // distance 2
        Assert.Contains(new Cell(0, 2), far.AttackTargets(new Cell(0, 0)));
    }

    // ── Balistique : tir par-dessus la montagne ───────────────────────────────────

    [Fact]
    public void Balistique_ShootsOverMountain_WhereNormalFireIsBlocked()
    {
        var field = Battlefield.CreateFlat(8, 8);
        field[new Cell(0, 1)] = new Tile(BuiltInTiles.Mountain);   // obstacle entre tireur et cible

        var balistic = new Match(8, 8, field);
        balistic.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.Balistique }));
        balistic.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));
        Assert.Contains(new Cell(0, 2), balistic.AttackTargets(new Cell(0, 0)));   // ignore la montagne

        var normal = new Match(8, 8, field);
        normal.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None));
        normal.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));
        Assert.DoesNotContain(new Cell(0, 2), normal.AttackTargets(new Cell(0, 0))); // montagne = ligne coupée
    }

    // ── Vol : déplacement par-dessus l'eau ────────────────────────────────────────

    [Fact]
    public void Vol_MovesOverWater_WhereNormalMovementIsBlocked()
    {
        var field = Battlefield.CreateFlat(8, 8);
        field[new Cell(0, 1)] = new Tile(BuiltInTiles.Water);

        var flyer = new Match(8, 8, field);
        flyer.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.Vol }, moveRange: 3));
        Assert.Contains(new Cell(0, 2), flyer.LegalMoves(new Cell(0, 0)));   // franchit l'eau

        var normal = new Match(8, 8, field);
        normal.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None, moveRange: 3));
        Assert.DoesNotContain(new Cell(0, 2), normal.LegalMoves(new Cell(0, 0))); // l'eau borne le déplacement
    }

    // ── Formation : +2 puissance par allié adjacent ───────────────────────────────

    [Fact]
    public void Formation_AddsTwoPowerPerAdjacentAlly()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.Formation }));
        m.Place(new Cell(1, 0), Make(Faction.Player, 20, 5, None));   // allié adjacent (hors ligne de tir)
        m.Place(new Cell(1, 1), Make(Faction.Player, 20, 5, None));   // allié adjacent (diagonale)
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));    // cible

        m.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(10, m.UnitAt(new Cell(0, 2))!.Hp);   // 20 - (6 + 2×2 alliés)
    }

    // ── Esquive : 25 % d'annuler l'attaque (RNG injecté) ──────────────────────────

    [Fact]
    public void Esquive_NegatesAttack_WhenRollUnderChance_HitsOtherwise()
    {
        var dodged = new Match(8, 8, rng: new FixedRng(0.0));   // 0 < 0.25 → esquive
        dodged.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        dodged.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, new[] { Trait.Esquive }));
        dodged.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(20, dodged.UnitAt(new Cell(0, 2))!.Hp);   // aucun dégât

        var hit = new Match(8, 8, rng: new FixedRng(0.99));     // 0.99 >= 0.25 → touché
        hit.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        hit.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, new[] { Trait.Esquive }));
        hit.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(10, hit.UnitAt(new Cell(0, 2))!.Hp);      // 20 - 10
    }

    // ── Embrochage : touche aussi les ennemis adjacents à la cible ────────────────

    [Fact]
    public void Embrochage_AlsoHitsEnemyAdjacentToTarget()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.Embrochage }));
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));   // cible
        m.Place(new Cell(1, 2), Make(Faction.Enemy, 20, 5, None));   // adjacent à la cible → embroché

        m.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(14, m.UnitAt(new Cell(0, 2))!.Hp);   // cible : 20 - 6
        Assert.Equal(14, m.UnitAt(new Cell(1, 2))!.Hp);   // voisin : 20 - 6
    }

    // ── Drain de vie : soigne l'attaquant de 50 % des dégâts ──────────────────────

    [Fact]
    public void DrainDeVie_HealsAttackerHalfDamageDealt()
    {
        var m = Board();
        var drainer = Make(Faction.Player, 20, 10, new[] { Trait.DrainDeVie });
        drainer.TakeDamage(15);   // 5 PV avant l'attaque
        m.Place(new Cell(0, 0), drainer);
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));

        m.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(10, m.UnitAt(new Cell(0, 2))!.Hp);   // cible : 20 - 10
        Assert.Equal(10, drainer.Hp);                     // 5 + (10 / 2)
    }

    // ── Orage / Tempête : foudre AoE à l'attaque ──────────────────────────────────

    [Fact]
    public void Orage_StrikesAllOtherEnemies_NotTheDirectTargetNorAllies()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.Orage }));  // attaquant
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));   // cible directe (dégâts normaux seuls)
        m.Place(new Cell(5, 5), Make(Faction.Enemy, 20, 5, None));   // autre ennemi → foudroyé
        m.Place(new Cell(6, 6), Make(Faction.Enemy, 20, 5, None));   // autre ennemi → foudroyé
        m.Place(new Cell(1, 0), Make(Faction.Player, 20, 5, None));  // allié → jamais foudroyé

        m.TryAttack(new Cell(0, 0), new Cell(0, 2));

        Assert.Equal(10, m.UnitAt(new Cell(0, 2))!.Hp);   // cible : 20 - 10 (pas de +3 orage sur elle)
        Assert.Equal(17, m.UnitAt(new Cell(5, 5))!.Hp);   // 20 - 3
        Assert.Equal(17, m.UnitAt(new Cell(6, 6))!.Hp);   // 20 - 3
        Assert.Equal(20, m.UnitAt(new Cell(1, 0))!.Hp);   // allié intact
    }

    [Fact]
    public void Tempete_StrikesOtherEnemiesForSix()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.Tempete }));
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));   // cible directe
        m.Place(new Cell(5, 5), Make(Faction.Enemy, 20, 5, None));   // foudroyé

        m.TryAttack(new Cell(0, 0), new Cell(0, 2));

        Assert.Equal(10, m.UnitAt(new Cell(0, 2))!.Hp);   // 20 - 10
        Assert.Equal(14, m.UnitAt(new Cell(5, 5))!.Hp);   // 20 - 6 (tempête)
    }

    [Fact]
    public void Orage_CanKillOtherEnemies()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.Orage }));
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));   // cible directe (survit)
        m.Place(new Cell(5, 5), Make(Faction.Enemy, 2, 5, None));    // 2 PV → foudroyé à mort

        m.TryAttack(new Cell(0, 0), new Cell(0, 2));

        Assert.Null(m.UnitAt(new Cell(5, 5)));            // retiré du plateau
    }

    // ── Attaque libre : cible tout ennemi dans le carré de portée (hors contact) ───

    [Fact]
    public void AttaqueLibre_TargetsAnyEnemyInSquareZone_ExceptDirectMelee()
    {
        var m = Board();
        m.Place(new Cell(3, 3), Make(Faction.Player, 20, 6, new[] { Trait.AttaqueLibre, Trait.ZoneMorte }, attackRange: 3));
        m.Place(new Cell(5, 4), Make(Faction.Enemy, 20, 5, None));   // dc=2, dr=1 : case « cavalier » (hors lignes)
        m.Place(new Cell(4, 4), Make(Faction.Enemy, 20, 5, None));   // diagonale adjacente : PAS zone morte (classique)
        m.Place(new Cell(3, 4), Make(Faction.Enemy, 20, 5, None));   // orthogonal adjacent = corps-à-corps DIRECT → exclu
        m.Place(new Cell(3, 7), Make(Faction.Enemy, 20, 5, None));   // Chebyshev 4 → hors portée

        var targets = m.AttackTargets(new Cell(3, 3));
        Assert.Contains(new Cell(5, 4), targets);        // atteint une case impossible pour une Dame
        Assert.Contains(new Cell(4, 4), targets);        // diagonale au contact : autorisée (zone morte classique)
        Assert.DoesNotContain(new Cell(3, 4), targets);  // corps-à-corps direct (case droite) exclu
        Assert.DoesNotContain(new Cell(3, 7), targets);  // hors portée

        // Contraste : une Dame (sans Attaque libre) ne peut PAS viser la case « cavalier ».
        var dame = Board();
        dame.Place(new Cell(3, 3), Make(Faction.Player, 20, 6, new[] { Trait.ZoneMorte }, domaine: Domaine.Dame, attackRange: 3));
        dame.Place(new Cell(5, 4), Make(Faction.Enemy, 20, 5, None));
        Assert.DoesNotContain(new Cell(5, 4), dame.AttackTargets(new Cell(3, 3)));
    }

    [Fact]
    public void AttaqueLibre_RespectsLineOfFire_BlockedByUnitOrMountain()
    {
        // Unité interposée : l'ennemi DERRIÈRE un premier ennemi n'est pas visé.
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.AttaqueLibre, Trait.ZoneMorte }, attackRange: 3));
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));   // à portée, ligne dégagée
        m.Place(new Cell(0, 3), Make(Faction.Enemy, 20, 5, None));   // masqué par l'unité en (0,2)
        var targets = m.AttackTargets(new Cell(0, 0));
        Assert.Contains(new Cell(0, 2), targets);
        Assert.DoesNotContain(new Cell(0, 3), targets);

        // Montagne interposée : coupe la ligne… sauf pour un tir balistique.
        var field = Battlefield.CreateFlat(8, 8);
        field[new Cell(0, 1)] = new Tile(BuiltInTiles.Mountain);

        var blocked = new Match(8, 8, field);
        blocked.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.AttaqueLibre, Trait.ZoneMorte }, attackRange: 3));
        blocked.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));
        Assert.DoesNotContain(new Cell(0, 2), blocked.AttackTargets(new Cell(0, 0)));   // montagne coupe

        var ballistic = new Match(8, 8, field);
        ballistic.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.AttaqueLibre, Trait.ZoneMorte, Trait.Balistique }, attackRange: 3));
        ballistic.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));
        Assert.Contains(new Cell(0, 2), ballistic.AttackTargets(new Cell(0, 0)));       // balistique ignore la montagne
    }

    /// <summary>RNG déterministe pour tester « Esquive » : <see cref="System.Random.NextDouble"/> renvoie une constante.</summary>
    private sealed class FixedRng : System.Random
    {
        private readonly double _value;
        public FixedRng(double value) => _value = value;
        protected override double Sample() => _value;
    }
}
