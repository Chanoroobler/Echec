using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Map;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// Mécaniques des TRAITS (cf. <see cref="Trait"/>), résolues dans <see cref="Match"/>. Toutes activées
/// par la simple présence du trait sur la classe — il suffit de « piocher » un trait pour qu'il agisse.
/// </summary>
public class TraitsTests
{
    // Unité de test : domaine TOUR (lignes droites) par défaut, portée de tir 3.
    private static Unit Make(Faction faction, int hp, int damage, string[] traits,
        Domaine domaine = Domaine.Tour, int attackRange = 3, int moveRange = 1, bool pierces = false,
        int kills = 0)
    {
        var cls = new UnitClass("T", "t", tier: 1, maxHp: hp, damage: damage,
            moveRange: moveRange, attackRange: attackRange, piercesAllies: pierces, traits: traits);
        return new Unit(domaine, faction, cls, kills: kills);
    }

    private static string[] None => System.Array.Empty<string>();

    private static Match Board(int size = 8) => new(size, size);

    // ── Rempart / Duelliste : réductions de dégâts ────────────────────────────────

    [Fact]
    public void Rempart_ReducesEverywhere_ExceptOrthogonalContact()
    {
        // À distance (>= 2) : -4.
        var ranged = Board();
        ranged.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        ranged.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, new[] { Trait.Rempart }));
        ranged.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(14, ranged.UnitAt(new Cell(0, 2))!.Hp);   // 20 - (10 - 4)

        // Collé EN LIGNE DROITE (contact direct) : Rempart n'agit pas.
        var melee = Board();
        melee.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        melee.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 5, new[] { Trait.Rempart }));
        melee.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(10, melee.UnitAt(new Cell(0, 1))!.Hp);    // 20 - 10
    }

    [Fact]
    public void Rempart_StillReducesDiagonalContact()
    {
        // Collé en DIAGONALE : ce n'est pas un contact direct → la réduction s'applique quand même.
        var diag = Board();
        diag.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None, domaine: Domaine.Dame));
        diag.Place(new Cell(1, 1), Make(Faction.Enemy, 20, 5, new[] { Trait.Rempart }));
        diag.TryAttack(new Cell(0, 0), new Cell(1, 1));
        Assert.Equal(14, diag.UnitAt(new Cell(1, 1))!.Hp);     // 20 - (10 - 4)

        // Même attaquant collé ORTHOGONALEMENT : la garde saute.
        var ortho = Board();
        ortho.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None, domaine: Domaine.Dame));
        ortho.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 5, new[] { Trait.Rempart }));
        ortho.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(10, ortho.UnitAt(new Cell(0, 1))!.Hp);    // 20 - 10
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

    // ── Tueur de géants : +5 si la cible a PLUS de PV ACTUELS que l'attaquant ─────────

    [Fact]
    public void TueurDeGeants_AddsFiveDamage_OnlyWhenAttackerHasLessCurrentHp()
    {
        // Cible avec plus de PV ACTUELS que l'attaquant (30 > 20) : +5.
        var more = Board();
        more.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.TueurDeGeants }));
        more.Place(new Cell(0, 2), Make(Faction.Enemy, 30, 5, None));
        more.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(15, more.UnitAt(new Cell(0, 2))!.Hp);   // 30 - (10 + 5)

        // PV actuels ÉGAUX (20 == 20) : aucun bonus.
        var equal = Board();
        equal.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.TueurDeGeants }));
        equal.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));
        equal.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(10, equal.UnitAt(new Cell(0, 2))!.Hp);  // 20 - 10

        // Gros MAX mais BAS PV actuels (cible blessée à 15) : PAS de bonus — ce sont les PV ACTUELS qui comptent
        // (l'ancienne règle « PV max » aurait donné le bonus ici).
        var wounded = Board();
        wounded.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.TueurDeGeants }));
        var bigButHurt = Make(Faction.Enemy, 100, 5, None);
        bigButHurt.TakeDamage(85);   // 15 PV actuels < 20 de l'attaquant
        wounded.Place(new Cell(0, 2), bigButHurt);
        wounded.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(5, wounded.UnitAt(new Cell(0, 2))!.Hp);  // 15 - 10 (aucun bonus)

        // Attaquant BLESSÉ (10 PV actuels) contre une cible en meilleure forme (20) : +5 même si son MAX est supérieur.
        var hurtAttacker = Board();
        var lowHp = Make(Faction.Player, 40, 10, new[] { Trait.TueurDeGeants });
        lowHp.TakeDamage(30);        // 10 PV actuels
        hurtAttacker.Place(new Cell(0, 0), lowHp);
        hurtAttacker.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));
        hurtAttacker.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(5, hurtAttacker.UnitAt(new Cell(0, 2))!.Hp);  // 20 - (10 + 5)

        // Sans le trait : aucun bonus, même contre une cible plus fraîche.
        var noTrait = Board();
        noTrait.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        noTrait.Place(new Cell(0, 2), Make(Faction.Enemy, 30, 5, None));
        noTrait.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(20, noTrait.UnitAt(new Cell(0, 2))!.Hp);  // 30 - 10
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

    // ── Berserk / Rage / auras de puissance : bonus de puissance ──────────────────

    [Fact]
    public void Berserk_AddsOnePower_PerKill()
    {
        // Sans kill : puissance brute, aucun bonus.
        var fresh = Board();
        fresh.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.Berserk }, kills: 0));
        fresh.Place(new Cell(0, 1), Make(Faction.Enemy, 30, 5, None));
        fresh.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(20, fresh.UnitAt(new Cell(0, 1))!.Hp);    // 30 - 10 (pas de bonus)

        // Avec 3 kills accumulés sur la run : +3 puissance.
        var seasoned = Board();
        seasoned.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.Berserk }, kills: 3));
        seasoned.Place(new Cell(0, 1), Make(Faction.Enemy, 30, 5, None));
        seasoned.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(17, seasoned.UnitAt(new Cell(0, 1))!.Hp); // 30 - (10 + 3)
    }

    [Fact]
    public void Rage_GainsSevenPower_WhenAnAllyDies()
    {
        var m = Board();
        var rageux = new Cell(5, 0);
        m.Place(rageux, Make(Faction.Player, 40, 10, new[] { Trait.Rage }));   // le rageux (n'agit pas ici)
        m.Place(new Cell(0, 1), Make(Faction.Player, 1, 1, None));             // allié fragile (va mourir)
        m.Place(new Cell(0, 0), Make(Faction.Enemy, 20, 5, None));             // bourreau
        m.Place(new Cell(5, 2), Make(Faction.Enemy, 100, 5, None));           // cible du rageux

        Assert.Equal(0, m.UnitAt(rageux)!.RagePower);   // aucun allié mort : pas de bonus

        // Tour ennemi : le bourreau tue l'allié fragile → le rageux gagne +7 de puissance (le combat).
        m.PassTurn();
        Assert.Equal(MoveKind.Killed, m.TryAttack(new Cell(0, 0), new Cell(0, 1)));
        Assert.Equal(7, m.UnitAt(rageux)!.RagePower);

        // De retour au joueur : le rageux frappe pour 10 + 7.
        Assert.Equal(Faction.Player, m.CurrentTurn);
        m.TryAttack(rageux, new Cell(5, 2));
        Assert.Equal(83, m.UnitAt(new Cell(5, 2))!.Hp);   // 100 - (10 + 7)
    }

    [Fact]
    public void Rage_IsNotCumulative_OnlyGrantsBonusOncePerCombat()
    {
        var m = Board();
        var rageux = new Cell(5, 0);
        m.Place(rageux, Make(Faction.Player, 40, 10, new[] { Trait.Rage }));   // le rageux (n'agit pas ici)
        m.Place(new Cell(0, 1), Make(Faction.Player, 1, 1, None));             // 1er allié fragile
        m.Place(new Cell(2, 1), Make(Faction.Player, 1, 1, None));             // 2e allié fragile
        m.Place(new Cell(0, 0), Make(Faction.Enemy, 20, 5, None));             // bourreau 1
        m.Place(new Cell(2, 0), Make(Faction.Enemy, 20, 5, None));             // bourreau 2

        // 1re mort d'allié : la Rage s'active à +7.
        m.PassTurn();
        Assert.Equal(MoveKind.Killed, m.TryAttack(new Cell(0, 0), new Cell(0, 1)));
        Assert.Equal(7, m.UnitAt(rageux)!.RagePower);

        // 2e mort d'allié : NON cumulable, le bonus reste à 7 (une seule fois par combat).
        m.PassTurn();
        Assert.Equal(MoveKind.Killed, m.TryAttack(new Cell(2, 0), new Cell(2, 1)));
        Assert.Equal(7, m.UnitAt(rageux)!.RagePower);
    }

    [Fact]
    public void AuraDePuissance_AdjacentAlly_AddsThreePower()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        m.Place(new Cell(1, 0), Make(Faction.Player, 20, 5, new[] { Trait.AuraDePuissance })); // allié adjacent
        m.Place(new Cell(0, 1), Make(Faction.Enemy, 30, 5, None));
        m.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(17, m.UnitAt(new Cell(0, 1))!.Hp);        // 30 - (10 + 3)
    }

    // ── Coups reçus (Unit.TimesHit) : source de points du commandant ───────────────

    [Fact]
    public void RecordHit_CountsOnlyLandedDamage()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));
        m.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(1, m.UnitAt(new Cell(0, 2))!.TimesHit);   // un coup encaissé

        // Une attaque totalement absorbée (dégâts nets 0 grâce à Rempart) ne compte PAS comme un coup reçu.
        var shielded = Board();
        shielded.Place(new Cell(0, 0), Make(Faction.Player, 20, 4, None));                    // 4 de dégâts
        shielded.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, new[] { Trait.Rempart }));  // -4 à distance ≥ 2 → 0
        shielded.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(20, shielded.UnitAt(new Cell(0, 2))!.Hp);        // aucun dégât
        Assert.Equal(0, shielded.UnitAt(new Cell(0, 2))!.TimesHit);   // donc pas un coup reçu
    }

    // ── Coups à distance (Unit.RangedHits) : source de points du commandant du Fou ──

    [Fact]
    public void RecordRangedHit_CountsHitAtDistanceThreeOrMore_NotCloser_NorAbsorbed()
    {
        // Coup DIRECT qui touche à portée 3 → compté sur l'attaquant.
        var far = Board();
        far.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None, attackRange: 3));
        far.Place(new Cell(0, 3), Make(Faction.Enemy, 20, 5, None));
        far.TryAttack(new Cell(0, 0), new Cell(0, 3));
        Assert.Equal(1, far.UnitAt(new Cell(0, 0))!.RangedHits);

        // Même coup à portée 2 → PAS un coup à distance.
        var near = Board();
        near.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None, attackRange: 3));
        near.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));
        near.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(0, near.UnitAt(new Cell(0, 0))!.RangedHits);

        // Coup à portée 3 mais ABSORBÉ (dégâts nets 0 par Rempart) → ne compte pas (comme un coup reçu).
        var shielded = Board();
        shielded.Place(new Cell(0, 0), Make(Faction.Player, 20, 4, None, attackRange: 3));
        shielded.Place(new Cell(0, 3), Make(Faction.Enemy, 20, 5, new[] { Trait.Rempart }));
        shielded.TryAttack(new Cell(0, 0), new Cell(0, 3));
        Assert.Equal(0, shielded.UnitAt(new Cell(0, 0))!.RangedHits);
    }

    [Fact]
    public void RecordDamage_CreditsLandedDamageToAttacker()
    {
        var m = Board();
        var attacker = Make(Faction.Player, 20, 10, None);
        m.Place(new Cell(0, 0), attacker);
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));
        m.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(10, attacker.DamageDealt);   // 10 dégâts infligés → crédités à l'attaquant

        // Attaque totalement absorbée (Rempart à distance ≥ 2) : aucun dégât compté.
        var shielded = Board();
        var weak = Make(Faction.Player, 20, 4, None);
        shielded.Place(new Cell(0, 0), weak);
        shielded.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, new[] { Trait.Rempart }));
        shielded.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(0, weak.DamageDealt);   // 4 - 4 (rempart) = 0 infligé
    }

    // ── Formes d'attaque : Transpercement ─────────────────────────────────────────

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
    public void Riposte_CountersAttacker_WhenSurviving()
    {
        var melee = Board();
        melee.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None));
        melee.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 8, new[] { Trait.Riposte }));
        melee.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(14, melee.UnitAt(new Cell(0, 1))!.Hp);   // victime survit (20 - 6)
        Assert.Equal(12, melee.UnitAt(new Cell(0, 0))!.Hp);   // attaquant contre-attaqué (20 - 8)
    }

    [Fact]
    public void Riposte_ReachesAtRange_WhenItCouldHaveAttacked()
    {
        // La riposte n'est PAS réservée au corps à corps : l'unité de test tire à 3 cases, elle rend donc
        // le coup à un assaillant distant de 2 sur la même ligne.
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None));
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 8, new[] { Trait.Riposte }));

        m.TryAttack(new Cell(0, 0), new Cell(0, 2));

        Assert.Equal(12, m.UnitAt(new Cell(0, 0))!.Hp);   // 20 - 8
    }

    [Fact]
    public void Riposte_StaysSilent_WhenItCouldNotHaveAttacked()
    {
        // Hors de portée : l'unité tire à 3, l'assaillant frappe de 4 cases. Elle encaisse sans rendre.
        var far = Board();
        far.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None, attackRange: 5));
        far.Place(new Cell(0, 4), Make(Faction.Enemy, 20, 8, new[] { Trait.Riposte }));
        far.TryAttack(new Cell(0, 0), new Cell(0, 4));
        Assert.Equal(20, far.UnitAt(new Cell(0, 0))!.Hp);

        // Mauvais MOTIF : l'unité de test est du domaine Tour (lignes droites), l'assaillant est en
        // diagonale. Elle n'aurait pas pu le viser, donc pas de riposte.
        var diagonal = Board();
        diagonal.Place(new Cell(1, 1), Make(Faction.Player, 20, 6, None, domaine: Domaine.Dame));
        diagonal.Place(new Cell(2, 2), Make(Faction.Enemy, 20, 8, new[] { Trait.Riposte }));
        diagonal.TryAttack(new Cell(1, 1), new Cell(2, 2));
        Assert.Equal(20, diagonal.UnitAt(new Cell(1, 1))!.Hp);
    }

    [Fact]
    public void Riposte_StaysSilent_WhenTheLineIsBlocked()
    {
        // Un allié de la victime s'interpose : sa ligne de tir est coupée, donc aucune riposte.
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None));
        m.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 8, None));                        // écran
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 8, new[] { Trait.Riposte }));

        m.TryAttack(new Cell(0, 0), new Cell(0, 1));   // on frappe l'écran, pas le riposteur

        Assert.Equal(20, m.UnitAt(new Cell(0, 0))!.Hp);
    }

    /// <summary>« Recule » + « Riposte » : le recul est résolu AVANT la riposte ; poussé HORS de portée,
    /// le riposteur ne rend plus le coup (il riposte depuis sa case d'ARRIVÉE, pas d'origine).</summary>
    [Fact]
    public void Recule_PushesRiposterOutOfReach_CancelsRiposte()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.Recule }));                    // attaquant repousseur
        m.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 8, new[] { Trait.Riposte }, attackRange: 1));    // riposteur au contact

        m.TryAttack(new Cell(0, 0), new Cell(0, 1));

        Assert.Null(m.UnitAt(new Cell(0, 1)));                 // repoussée hors de sa case
        Assert.Equal(14, m.UnitAt(new Cell(0, 2))!.Hp);        // glissée en (0,2), 20 - 6
        Assert.Equal(20, m.UnitAt(new Cell(0, 0))!.Hp);        // portée 1 depuis (0,2) → hors d'atteinte → PAS de riposte
        Assert.Null(m.LastRiposte);
    }

    /// <summary>« Recule » + « Riposte » : repoussée mais ENCORE à portée, la victime riposte DEPUIS sa nouvelle case.</summary>
    [Fact]
    public void Recule_RiposterStillInReach_RipostesFromNewCell()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.Recule }));
        m.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 8, new[] { Trait.Riposte }, attackRange: 3));    // portée longue

        m.TryAttack(new Cell(0, 0), new Cell(0, 1));

        Assert.Equal(14, m.UnitAt(new Cell(0, 2))!.Hp);        // repoussée en (0,2)
        Assert.Equal(12, m.UnitAt(new Cell(0, 0))!.Hp);        // riposte DEPUIS (0,2) : 20 - 8
        Assert.Equal(new Cell(0, 2), m.LastRiposte!.Value.From);
        Assert.Equal(new Cell(0, 0), m.LastRiposte!.Value.To);
    }

    // ── Soutien : Soin ────────────────────────────────────────────────────────────

    [Fact]
    public void Soin_HealsWoundedAlly_ByHalfPower()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.Soin }));   // puissance 10 → soin 5
        var ally = Make(Faction.Player, 20, 5, None);
        ally.TakeDamage(15);   // 5 PV
        m.Place(new Cell(0, 2), ally);

        Assert.Contains(new Cell(0, 2), m.HealTargets(new Cell(0, 0)));
        m.TryHeal(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(10, m.UnitAt(new Cell(0, 2))!.Hp);   // 5 + (10 / 2)
    }

    /// <summary>Le soin porte sur la puissance EFFECTIVE : les bonus de puissance du soigneur le renforcent.</summary>
    [Fact]
    public void Soin_CountsPowerBonuses()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.Soin }));
        m.Place(new Cell(1, 0), Make(Faction.Player, 20, 5, new[] { Trait.AuraDePuissance }));   // +3 puissance
        var ally = Make(Faction.Player, 20, 5, None);
        ally.TakeDamage(15);   // 5 PV
        m.Place(new Cell(0, 2), ally);

        m.TryHeal(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(11, m.UnitAt(new Cell(0, 2))!.Hp);   // 5 + ((10 + 3) / 2 = 6), et non 5 + 5
    }

    [Fact]
    public void SoinParfait_HealsWoundedAlly_ByFullPower()
    {
        // Même action que « Soin », mais le montant est la puissance ENTIÈRE au lieu de la moitié.
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.SoinParfait }));   // puissance 10 → soin 10
        var ally = Make(Faction.Player, 20, 5, None);
        ally.TakeDamage(15);   // 5 PV
        m.Place(new Cell(0, 2), ally);

        Assert.Contains(new Cell(0, 2), m.HealTargets(new Cell(0, 0)));   // il cible comme un soigneur normal
        m.TryHeal(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(15, m.UnitAt(new Cell(0, 2))!.Hp);   // 5 + 10 (et non 5 + 5)
    }

    [Fact]
    public void Soin_WorksForEssentialUnit_LikeTheCommander()
    {
        // Le commandant (unité ESSENTIELLE, qui reçoit Soin via un nœud commanderTrait) soigne comme les
        // autres : aucune exception dans le moteur pour l'essentiel, ni comme soigneur ni comme cible.
        var m = Board();
        var commander = new Unit(Domaine.Tour, Faction.Player,
            new UnitClass("C", "c", tier: 1, maxHp: 30, damage: 12, moveRange: 1, attackRange: 3,
                traits: new[] { Trait.Soin })) { IsEssential = true };
        m.Place(new Cell(0, 0), commander);
        var ally = Make(Faction.Player, 20, 5, None);
        ally.TakeDamage(15);   // 5 PV
        m.Place(new Cell(0, 2), ally);

        Assert.Contains(new Cell(0, 2), m.HealTargets(new Cell(0, 0)));
        m.TryHeal(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(11, m.UnitAt(new Cell(0, 2))!.Hp);   // 5 + (12 / 2)
    }

    [Fact]
    public void Soin_CanTargetWoundedCommander()
    {
        // Un soigneur DOIT pouvoir viser le commandant (unité essentielle) blessé, comme n'importe quel allié.
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.Soin }));
        var commander = new Unit(Domaine.Tour, Faction.Player,
            new UnitClass("C", "c", tier: 1, maxHp: 30, damage: 12, moveRange: 1, attackRange: 1))
            { IsEssential = true };
        commander.TakeDamage(10);   // blessé (20/30)
        m.Place(new Cell(0, 2), commander);

        Assert.Contains(new Cell(0, 2), m.HealTargets(new Cell(0, 0)));
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

    [Fact]
    public void Formation_ContextualPowerBonus_ReflectsAdjacentAllies()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, new[] { Trait.Formation }));
        m.Place(new Cell(1, 0), Make(Faction.Player, 20, 5, None));   // allié adjacent
        m.Place(new Cell(1, 1), Make(Faction.Player, 20, 5, None));   // allié adjacent (diagonale)
        m.Place(new Cell(3, 3), Make(Faction.Player, 20, 5, None));   // trop loin : ne compte pas

        // Le bonus contextuel exposé à l'UI doit valoir exactement ce que l'attaque inflige en plus.
        Assert.Equal(4, m.FormationPowerBonus(new Cell(0, 0)));   // 2 alliés × 2
        Assert.Equal(4, m.ContextualPowerBonus(new Cell(0, 0)));
    }

    [Fact]
    public void Formation_ContextualPowerBonus_ZeroWithoutTrait()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 6, None));   // pas de trait Formation
        m.Place(new Cell(1, 0), Make(Faction.Player, 20, 5, None));   // allié adjacent
        Assert.Equal(0, m.FormationPowerBonus(new Cell(0, 0)));
    }

    // ── Lien de puissance : +2 puissance par allié dans la portée de déplacement ───

    [Fact]
    public void LienDePuissance_AddsTwoPowerPerAllyInMoveRange()
    {
        var m = Board();
        // Porteur TOUR (lignes droites) au centre, portée de déplacement 2, portée de tir 3.
        m.Place(new Cell(3, 3), Make(Faction.Player, 20, 6, new[] { Trait.LienDePuissance }, moveRange: 2));
        m.Place(new Cell(2, 3), Make(Faction.Player, 20, 5, None));   // allié dans la portée (gauche, 1 case)
        m.Place(new Cell(3, 2), Make(Faction.Player, 20, 5, None));   // allié dans la portée (bas, 1 case)
        m.Place(new Cell(6, 3), Make(Faction.Player, 20, 5, None));   // même ligne mais à 3 cases : hors portée
        m.Place(new Cell(3, 5), Make(Faction.Enemy, 20, 5, None));    // cible (tir vertical libre)

        // 2 alliés dans la portée de déplacement → +4 puissance ; l'UI affiche la même valeur.
        Assert.Equal(4, m.LienPuissancePowerBonus(new Cell(3, 3)));
        Assert.Equal(4, m.ContextualPowerBonus(new Cell(3, 3)));

        m.TryAttack(new Cell(3, 3), new Cell(3, 5));
        Assert.Equal(10, m.UnitAt(new Cell(3, 5))!.Hp);   // 20 - (6 + 2×2)
    }

    [Fact]
    public void LienDePuissance_ZeroWithoutTrait()
    {
        var m = Board();
        m.Place(new Cell(3, 3), Make(Faction.Player, 20, 6, None, moveRange: 2));   // pas le trait
        m.Place(new Cell(2, 3), Make(Faction.Player, 20, 5, None));
        Assert.Equal(0, m.LienPuissancePowerBonus(new Cell(3, 3)));
        Assert.Equal(0, m.ContextualPowerBonus(new Cell(3, 3)));
    }

    // ── Repositionnement stratégique : un pas gauche/droite quel que soit le domaine ─

    [Fact]
    public void RepositionnementStrategique_AddsLeftRightMove_RegardlessOfDomain()
    {
        // Un FOU se déplace en diagonale : il ne peut normalement PAS atteindre les cases orthogonales
        // gauche/droite. Le trait les ajoute (déplacement seulement).
        var m = Board();
        m.Place(new Cell(2, 2), Make(Faction.Player, 20, 6,
            new[] { Trait.RepositionnementStrategique }, domaine: Domaine.Fou));
        var moves = m.LegalMoves(new Cell(2, 2));

        Assert.Contains(new Cell(1, 2), moves);          // un pas à gauche
        Assert.Contains(new Cell(3, 2), moves);          // un pas à droite
        Assert.DoesNotContain(new Cell(2, 1), moves);    // aucun pas vertical ajouté
        Assert.DoesNotContain(new Cell(2, 3), moves);

        // Sans le trait : les cases orthogonales restent hors de portée d'un Fou.
        var normal = Board();
        normal.Place(new Cell(2, 2), Make(Faction.Player, 20, 6, None, domaine: Domaine.Fou));
        Assert.DoesNotContain(new Cell(1, 2), normal.LegalMoves(new Cell(2, 2)));
        Assert.DoesNotContain(new Cell(3, 2), normal.LegalMoves(new Cell(2, 2)));
    }

    [Fact]
    public void RepositionnementStrategique_DoesNotStepOntoOccupiedOrObstacle()
    {
        // Case de gauche occupée par un allié, case de droite = eau : aucune des deux n'est ajoutée.
        var field = Battlefield.CreateFlat(8, 8);
        field[new Cell(3, 2)] = new Tile(BuiltInTiles.Water);   // à droite du porteur (2,2)
        var m = new Match(8, 8, field);
        m.Place(new Cell(2, 2), Make(Faction.Player, 20, 6,
            new[] { Trait.RepositionnementStrategique }, domaine: Domaine.Fou));
        m.Place(new Cell(1, 2), Make(Faction.Player, 20, 5, None));   // allié à gauche

        var moves = m.LegalMoves(new Cell(2, 2));
        Assert.DoesNotContain(new Cell(1, 2), moves);   // occupée
        Assert.DoesNotContain(new Cell(3, 2), moves);   // obstacle
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

    // ── Renforcement d'arbre (Rempart / Esquive) : réservé aux unités du JOUEUR ────

    /// <summary>« Rempart renforcé » (bonus +2 → réduction 6) ne s'applique qu'aux pions du JOUEUR ; un Rempart
    /// ENNEMI garde la réduction de base (4).</summary>
    [Fact]
    public void RempartReinforcement_AppliesToPlayerOnly()
    {
        // Victime JOUEUR : l'ennemi frappe à distance → réduction RENFORCÉE (6).
        var pv = new Match(8, 8, rempartBonus: 2);
        pv.Place(new Cell(0, 0), Make(Faction.Player, 20, 5, new[] { Trait.Rempart }));
        pv.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 10, None));
        pv.PassTurn();   // au tour de l'ennemi
        pv.TryAttack(new Cell(0, 2), new Cell(0, 0));
        Assert.Equal(16, pv.UnitAt(new Cell(0, 0))!.Hp);   // 20 - (10 - 6)

        // Victime ENNEMIE : le joueur frappe à distance → réduction de BASE (4), pas de bonus.
        var ev = new Match(8, 8, rempartBonus: 2);
        ev.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        ev.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, new[] { Trait.Rempart }));
        ev.TryAttack(new Cell(0, 0), new Cell(0, 2));
        Assert.Equal(14, ev.UnitAt(new Cell(0, 2))!.Hp);   // 20 - (10 - 4), et non - 6
    }

    /// <summary>« Esquive renforcée » (bonus +15 → 40%) ne s'applique qu'au JOUEUR : RNG figé à 0.30 → le pion
    /// joueur esquive (0.30 &lt; 0.40) mais un Esquive ENNEMI (0.25) encaisse.</summary>
    [Fact]
    public void EsquiveReinforcement_AppliesToPlayerOnly()
    {
        // Victime JOUEUR : l'ennemi frappe → le joueur ESQUIVE (0.30 < 0.40).
        var pv = new Match(8, 8, rng: new FixedRng(0.30), esquiveBonusPercent: 15);
        pv.Place(new Cell(0, 0), Make(Faction.Player, 20, 5, new[] { Trait.Esquive }));
        pv.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 10, None));
        pv.PassTurn();
        pv.TryAttack(new Cell(0, 1), new Cell(0, 0));
        Assert.Equal(20, pv.UnitAt(new Cell(0, 0))!.Hp);   // esquivé

        // Victime ENNEMIE : le joueur frappe → l'ennemi N'esquive PAS (0.30 >= 0.25 base).
        var ev = new Match(8, 8, rng: new FixedRng(0.30), esquiveBonusPercent: 15);
        ev.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, None));
        ev.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 5, new[] { Trait.Esquive }));
        ev.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(10, ev.UnitAt(new Cell(0, 1))!.Hp);   // 20 - 10, encaissé
    }

    /// <summary>« Tueur de géant renforcé » (bonus +2 → +7) ne s'applique qu'au JOUEUR : un ennemi garde +5.</summary>
    [Fact]
    public void TueurDeGeantsReinforcement_AppliesToPlayerOnly()
    {
        // Attaquant JOUEUR renforcé (+2) contre une cible aux PV actuels supérieurs (full : 100 > 20) : +7.
        var pv = new Match(8, 8, tueurGeantsBonus: 2);
        pv.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.TueurDeGeants }));
        pv.Place(new Cell(0, 1), Make(Faction.Enemy, 100, 5, None));
        pv.TryAttack(new Cell(0, 0), new Cell(0, 1));
        Assert.Equal(83, pv.UnitAt(new Cell(0, 1))!.Hp);   // 100 - (10 + 7)

        // Attaquant ENNEMI : le renforcement du JOUEUR ne le touche pas → +5 seulement.
        var ev = new Match(8, 8, tueurGeantsBonus: 2);
        ev.Place(new Cell(0, 0), Make(Faction.Player, 100, 5, None));
        ev.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 10, new[] { Trait.TueurDeGeants }));
        ev.PassTurn();
        ev.TryAttack(new Cell(0, 1), new Cell(0, 0));
        Assert.Equal(85, ev.UnitAt(new Cell(0, 0))!.Hp);   // 100 - (10 + 5), pas +7
    }

    /// <summary>« Formation renforcée » (bonus +1 → 3 de puissance par allié) ne s'applique qu'au JOUEUR : un
    /// pion « Formation » ennemi garde 2 par allié.</summary>
    [Fact]
    public void FormationReinforcement_AppliesToPlayerOnly()
    {
        // JOUEUR « Formation » avec 2 alliés adjacents, renforcé (+1 → 3/allié) : puissance 10 + 3*2 = 16.
        var pv = new Match(8, 8, formationBonus: 1);
        pv.Place(new Cell(1, 1), Make(Faction.Player, 20, 10, new[] { Trait.Formation }));
        pv.Place(new Cell(0, 1), Make(Faction.Player, 20, 5, None));   // allié adjacent
        pv.Place(new Cell(2, 1), Make(Faction.Player, 20, 5, None));   // allié adjacent
        pv.Place(new Cell(1, 2), Make(Faction.Enemy, 100, 5, None));   // cible (adjacente, dessous)
        pv.TryAttack(new Cell(1, 1), new Cell(1, 2));
        Assert.Equal(84, pv.UnitAt(new Cell(1, 2))!.Hp);   // 100 - (10 + 3*2)

        // ENNEMI « Formation » : le renforcement du JOUEUR ne le touche pas → 2 par allié (10 + 2*2 = 14).
        var ev = new Match(8, 8, formationBonus: 1);
        ev.Place(new Cell(1, 1), Make(Faction.Enemy, 20, 10, new[] { Trait.Formation }));
        ev.Place(new Cell(0, 1), Make(Faction.Enemy, 20, 5, None));   // allié ennemi adjacent
        ev.Place(new Cell(2, 1), Make(Faction.Enemy, 20, 5, None));   // allié ennemi adjacent
        ev.Place(new Cell(1, 2), Make(Faction.Player, 100, 5, None)); // cible joueur
        ev.PassTurn();
        ev.TryAttack(new Cell(1, 1), new Cell(1, 2));
        Assert.Equal(86, ev.UnitAt(new Cell(1, 2))!.Hp);   // 100 - (10 + 2*2), pas 3 par allié
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
    public void Orage_StrikesOtherEnemies_NotTheDirectTargetNorAllies()
    {
        var m = Board();
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.Orage }));  // attaquant
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));   // cible directe (dégâts normaux seuls)
        m.Place(new Cell(5, 5), Make(Faction.Enemy, 20, 5, None));   // autre ennemi → foudroyé (≤ 3 au total)
        m.Place(new Cell(6, 6), Make(Faction.Enemy, 20, 5, None));   // autre ennemi → foudroyé
        m.Place(new Cell(1, 0), Make(Faction.Player, 20, 5, None));  // allié → jamais foudroyé

        m.TryAttack(new Cell(0, 0), new Cell(0, 2));

        Assert.Equal(10, m.UnitAt(new Cell(0, 2))!.Hp);   // cible : 20 - 10 (pas de +3 orage sur elle)
        Assert.Equal(17, m.UnitAt(new Cell(5, 5))!.Hp);   // 20 - 3 (2 autres ennemis ≤ 3 → tous foudroyés)
        Assert.Equal(17, m.UnitAt(new Cell(6, 6))!.Hp);   // 20 - 3
        Assert.Equal(20, m.UnitAt(new Cell(1, 0))!.Hp);   // allié intact
    }

    [Fact]
    public void Orage_StrikesAtMostThreeRandomEnemies()
    {
        var m = new Match(8, 8, rng: new System.Random(1));
        m.Place(new Cell(0, 0), Make(Faction.Player, 20, 10, new[] { Trait.Orage }));  // attaquant
        m.Place(new Cell(0, 2), Make(Faction.Enemy, 20, 5, None));                     // cible directe
        var others = new[] { new Cell(5, 5), new Cell(6, 6), new Cell(7, 7), new Cell(5, 7), new Cell(7, 5) };
        foreach (var cell in others)
            m.Place(cell, Make(Faction.Enemy, 20, 5, None));   // 5 AUTRES ennemis (tous à 20 PV)

        m.TryAttack(new Cell(0, 0), new Cell(0, 2));

        // Seuls 3 des 5 sont foudroyés (−3 → 17), les 2 restants intacts (20). Quels 3 = tirage aléatoire.
        Assert.Equal(3, others.Count(c => m.UnitAt(c)!.Hp == 17));
        Assert.Equal(2, others.Count(c => m.UnitAt(c)!.Hp == 20));
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

    // ── Attaque libre : tire COMME UNE DAME (8 directions en ligne), quel que soit le domaine ───

    [Fact]
    public void AttaqueLibre_MakesUnitAttackLikeAQueen_EvenOffItsDomaine()
    {
        // Pion de domaine TOUR (par défaut) : normalement il ne tire QU'EN orthogonal.
        var m = Board();
        m.Place(new Cell(3, 3), Make(Faction.Player, 20, 6, new[] { Trait.AttaqueLibre }, attackRange: 3));
        m.Place(new Cell(3, 6), Make(Faction.Enemy, 20, 5, None));   // orthogonal (0,1)×3
        m.Place(new Cell(5, 5), Make(Faction.Enemy, 20, 5, None));   // DIAGONALE (1,1)×2 : hors d'un Tour, à portée d'une Dame
        m.Place(new Cell(5, 4), Make(Faction.Enemy, 20, 5, None));   // dc=2,dr=1 : case « cavalier », sur AUCUNE ligne

        var targets = m.AttackTargets(new Cell(3, 3));
        Assert.Contains(new Cell(3, 6), targets);        // orthogonal : ok
        Assert.Contains(new Cell(5, 5), targets);        // diagonale : Attaque libre tire comme une Dame
        Assert.DoesNotContain(new Cell(5, 4), targets);  // hors lignes : une Dame ne l'atteint pas (≠ ancien comportement « carré »)

        // Sans Attaque libre, le même pion (Tour) ne peut PAS viser la diagonale.
        var tour = Board();
        tour.Place(new Cell(3, 3), Make(Faction.Player, 20, 6, None, attackRange: 3));
        tour.Place(new Cell(5, 5), Make(Faction.Enemy, 20, 5, None));
        Assert.DoesNotContain(new Cell(5, 5), tour.AttackTargets(new Cell(3, 3)));
    }

    [Fact]
    public void AttaqueLibre_OnCavalier_AddsQueenLines_ToKnightJumps()
    {
        // Cavalier monté (archer) : attaque en L (saut) COMME UN CAVALIER, ET tire comme une Dame (Attaque libre).
        var m = Board();
        m.Place(new Cell(3, 3), Make(Faction.Player, 20, 6, new[] { Trait.AttaqueLibre }, domaine: Domaine.Cavalier, attackRange: 2));
        m.Place(new Cell(5, 4), Make(Faction.Enemy, 20, 5, None));   // saut cavalier (dc=2, dr=1), hors des lignes
        m.Place(new Cell(5, 5), Make(Faction.Enemy, 20, 5, None));   // diagonale de Dame (dc=2, dr=2), portée 2
        m.Place(new Cell(3, 5), Make(Faction.Enemy, 20, 5, None));   // ligne droite de Dame (dc=0, dr=2)

        var targets = m.AttackTargets(new Cell(3, 3));
        Assert.Contains(new Cell(5, 4), targets);   // attaque au SAUT (cavalier)
        Assert.Contains(new Cell(5, 5), targets);   // + tir en diagonale (dame)
        Assert.Contains(new Cell(3, 5), targets);   // + tir en ligne droite (dame)

        // Sans Attaque libre : le cavalier n'attaque QU'au saut (aucune ligne).
        var plain = Board();
        plain.Place(new Cell(3, 3), Make(Faction.Player, 20, 6, None, domaine: Domaine.Cavalier, attackRange: 2));
        plain.Place(new Cell(5, 4), Make(Faction.Enemy, 20, 5, None));
        plain.Place(new Cell(3, 5), Make(Faction.Enemy, 20, 5, None));
        var t2 = plain.AttackTargets(new Cell(3, 3));
        Assert.Contains(new Cell(5, 4), t2);          // saut cavalier : oui
        Assert.DoesNotContain(new Cell(3, 5), t2);    // ligne droite : non (pas d'Attaque libre)
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

    // ── Statique : ne prend jamais la place de sa cible ───────────────────────────

    [Fact]
    public void Statique_KillsWithoutTakingTheTargetPlace()
    {
        // Le pion Statique tue au contact mais NE PREND PAS la place de sa cible : il reste sur sa case,
        // et la case de la victime reste libre.
        var board = Board();
        var from = new Cell(0, 0);
        var target = new Cell(0, 1);
        board.Place(from, Make(Faction.Player, 20, 10, new[] { Trait.Statique }));
        var enemy = Make(Faction.Enemy, 20, 5, None);
        enemy.TakeDamage(enemy.Hp - 1);   // 1 PV : le prochain coup le tue
        board.Place(target, enemy);

        var kind = board.TryAttack(from, target);

        Assert.Equal(MoveKind.Killed, kind);
        Assert.Equal(Faction.Player, board.UnitAt(from)!.Faction);   // resté sur sa case
        Assert.Null(board.UnitAt(target));                          // cible morte, case LIBRE (pas d'avance)
    }

    [Fact]
    public void WithoutStatique_MeleeKillTakesThePlace()
    {
        // Contrôle : sans Statique, la même mise à mort au contact fait AVANCER l'attaquant sur la case.
        var board = Board();
        var from = new Cell(0, 0);
        var target = new Cell(0, 1);
        board.Place(from, Make(Faction.Player, 20, 10, None));
        var enemy = Make(Faction.Enemy, 20, 5, None);
        enemy.TakeDamage(enemy.Hp - 1);
        board.Place(target, enemy);

        board.TryAttack(from, target);

        Assert.Null(board.UnitAt(from));                            // a quitté sa case
        Assert.Equal(Faction.Player, board.UnitAt(target)!.Faction);   // a pris la place de la cible
    }

    // ── Queue de phénix (Renaissance) : renaissance à la mort + consommation de l'équipement ─────────

    [Fact]
    public void QueueDePhenix_RevivesAtOneHp_AndConsumesEquipment_WithoutCreditingAKill()
    {
        var board = Board();
        var phenix = Equipment.OfTrait("phenix", "Queue de phénix", Trait.Renaissance);
        var cls = new UnitClass("V", "v", tier: 1, maxHp: 10, damage: 0, moveRange: 1, attackRange: 1);
        var victim = new Unit(Domaine.Tour, Faction.Enemy, cls, equipment: phenix);
        board.Place(new Cell(0, 0), Make(Faction.Player, 20, 50, None));   // dégâts LÉTAUX (50 » 10 PV)
        board.Place(new Cell(0, 1), victim);

        board.TryAttack(new Cell(0, 0), new Cell(0, 1));

        var after = board.UnitAt(new Cell(0, 1));
        Assert.Same(victim, after);                        // toujours en jeu (ressuscité, pas retiré)
        Assert.Equal(1, after!.Hp);                        // à 1 PV
        Assert.Null(after.Equipment);                      // équipement brisé (consommé)
        Assert.Equal(0, board.UnitAt(new Cell(0, 0))!.Kills);   // aucun kill crédité à l'attaquant
    }

    // ── Impact : 5 dégâts fixes aux ennemis autour du porteur, à son déplacement OU son attaque ────

    [Fact]
    public void Impact_OnMove_HitsEnemiesAroundDestination()
    {
        var m = Board();
        m.Place(new Cell(2, 2), Make(Faction.Player, 20, 6, new[] { Trait.Impact }, moveRange: 1));
        m.Place(new Cell(4, 2), Make(Faction.Enemy, 20, 5, None));   // adjacent à la case d'arrivée (3,2)
        m.Place(new Cell(3, 3), Make(Faction.Enemy, 20, 5, None));   // adjacent aussi

        m.TryMove(new Cell(2, 2), new Cell(3, 2));

        Assert.Equal(15, m.UnitAt(new Cell(4, 2))!.Hp);   // -5 (fixe)
        Assert.Equal(15, m.UnitAt(new Cell(3, 3))!.Hp);   // -5 (fixe)
        Assert.Equal(2, m.LastImpactHits.Count);
    }

    [Fact]
    public void Impact_OnAttack_HitsEnemiesAroundAttacker_TargetSurvivesAtRange()
    {
        var m = Board();
        m.Place(new Cell(1, 1), Make(Faction.Player, 20, 6, new[] { Trait.Impact }));
        m.Place(new Cell(1, 3), Make(Faction.Enemy, 20, 5, None));   // cible à distance 2 (survit)
        m.Place(new Cell(2, 1), Make(Faction.Enemy, 20, 5, None));   // badaud adjacent à l'attaquant

        m.TryAttack(new Cell(1, 1), new Cell(1, 3));

        Assert.Equal(14, m.UnitAt(new Cell(1, 3))!.Hp);   // attaque (20-6), hors zone d'impact
        Assert.Equal(15, m.UnitAt(new Cell(2, 1))!.Hp);   // impact -5
        Assert.NotNull(m.UnitAt(new Cell(1, 1)));         // l'attaquant reste sur place (tir)
    }

    [Fact]
    public void Impact_OnKill_CentersOnTheTakenCell()
    {
        var m = Board();
        m.Place(new Cell(1, 1), Make(Faction.Player, 20, 25, new[] { Trait.Impact }));
        m.Place(new Cell(1, 2), Make(Faction.Enemy, 20, 5, None));   // tuée au contact → l'attaquant prend sa place
        var attacker = m.UnitAt(new Cell(1, 1));
        m.Place(new Cell(2, 2), Make(Faction.Enemy, 20, 5, None));   // adjacent à la case PRISE (1,2), pas à (1,1)

        m.TryAttack(new Cell(1, 1), new Cell(1, 2));

        Assert.Same(attacker, m.UnitAt(new Cell(1, 2)));  // a bien pris la place
        Assert.Null(m.UnitAt(new Cell(1, 1)));
        Assert.Equal(15, m.UnitAt(new Cell(2, 2))!.Hp);   // impact centré sur (1,2) → -5
    }

    /// <summary>L'impact est un dégât FIXE : il ignore le Rempart (même en diagonale, où Rempart réduirait normalement).</summary>
    [Fact]
    public void Impact_FixedDamage_IgnoresRempart()
    {
        var m = Board();
        m.Place(new Cell(2, 2), Make(Faction.Player, 20, 6, new[] { Trait.Impact }, moveRange: 1));
        m.Place(new Cell(4, 3), Make(Faction.Enemy, 20, 5, new[] { Trait.Rempart }));   // diagonale de la case d'arrivée (3,2)

        m.TryMove(new Cell(2, 2), new Cell(3, 2));

        Assert.Equal(15, m.UnitAt(new Cell(4, 3))!.Hp);   // -5 plein (et non -1 réduit par Rempart)
    }

    // ── Recule : repousse la cible survivante d'une case ; +5 si un obstacle l'arrête ─────────────

    [Fact]
    public void Recule_PushesSurvivingTargetBackOneCell()
    {
        var m = Board();
        m.Place(new Cell(1, 1), Make(Faction.Player, 20, 6, new[] { Trait.Recule }));
        m.Place(new Cell(1, 3), Make(Faction.Enemy, 20, 5, None));

        m.TryAttack(new Cell(1, 1), new Cell(1, 3));

        Assert.Null(m.UnitAt(new Cell(1, 3)));            // la cible a quitté sa case
        Assert.Equal(14, m.UnitAt(new Cell(1, 4))!.Hp);   // repoussée d'une case, 20-6, pas de bonus
        Assert.Equal(new Cell(1, 4), m.LastRecule!.Value.To);
        Assert.Equal(0, m.LastRecule!.Value.SlamDamage);
    }

    [Fact]
    public void Recule_SlamAgainstBoardEdge_DealsBonusDamage()
    {
        var m = Board();
        m.Place(new Cell(1, 5), Make(Faction.Player, 20, 6, new[] { Trait.Recule }));
        m.Place(new Cell(1, 7), Make(Faction.Enemy, 20, 5, None));   // dos au bord (case 8 hors plateau)

        m.TryAttack(new Cell(1, 5), new Cell(1, 7));

        Assert.Equal(9, m.UnitAt(new Cell(1, 7))!.Hp);    // reste sur place, 20 - 6 (attaque) - 5 (plaquage)
        Assert.Equal(new Cell(1, 7), m.LastRecule!.Value.To);
        Assert.Equal(5, m.LastRecule!.Value.SlamDamage);
    }

    [Fact]
    public void Recule_SlamAgainstUnit_DealsBonusDamage_OnlyToPushed()
    {
        var m = Board();
        m.Place(new Cell(1, 1), Make(Faction.Player, 20, 6, new[] { Trait.Recule }));
        m.Place(new Cell(1, 3), Make(Faction.Enemy, 20, 5, None));
        m.Place(new Cell(1, 4), Make(Faction.Player, 20, 5, None));   // bloque la case derrière la cible

        m.TryAttack(new Cell(1, 1), new Cell(1, 3));

        Assert.Equal(9, m.UnitAt(new Cell(1, 3))!.Hp);    // plaquée : 20 - 6 - 5
        Assert.Equal(20, m.UnitAt(new Cell(1, 4))!.Hp);   // l'obstacle n'encaisse rien
    }

    [Fact]
    public void Recule_DeadTarget_NoKnockback()
    {
        var m = Board();
        m.Place(new Cell(1, 1), Make(Faction.Player, 20, 25, new[] { Trait.Recule }));
        var attacker = m.UnitAt(new Cell(1, 1));
        m.Place(new Cell(1, 2), Make(Faction.Enemy, 20, 5, None));   // tuée : rien à repousser

        m.TryAttack(new Cell(1, 1), new Cell(1, 2));

        Assert.Same(attacker, m.UnitAt(new Cell(1, 2)));  // l'attaquant a pris la place
        Assert.Null(m.LastRecule);
    }

    [Fact]
    public void Recule_PushesAlongDiagonalAttackAxis()
    {
        var m = Board();
        m.Place(new Cell(2, 2), Make(Faction.Player, 20, 6, new[] { Trait.Recule }, domaine: Domaine.Dame));
        m.Place(new Cell(4, 4), Make(Faction.Enemy, 20, 5, None));   // en diagonale, distance 2

        m.TryAttack(new Cell(2, 2), new Cell(4, 4));

        Assert.Null(m.UnitAt(new Cell(4, 4)));
        Assert.Equal(14, m.UnitAt(new Cell(5, 5))!.Hp);   // repoussée en (5,5), dans l'axe du tir
    }

    /// <summary>RNG déterministe pour tester « Esquive » : <see cref="System.Random.NextDouble"/> renvoie une constante.</summary>
    private sealed class FixedRng : System.Random
    {
        private readonly double _value;
        public FixedRng(double value) => _value = value;
        protected override double Sample() => _value;
    }
}
