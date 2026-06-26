using System.Linq;
using Echec.Core.Battle;
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
}
