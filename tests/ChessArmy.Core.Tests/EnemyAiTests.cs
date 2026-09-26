using System;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// IA ennemie : comportement NORMAL (fonce) vs DÉFENSIF (garde de mission spéciale). Le tour ennemi
/// s'obtient en faisant passer le tour joueur (<see cref="Match.PassTurn"/>). Aléa figé pour le déterminisme.
/// </summary>
public class EnemyAiTests
{
    private static readonly Cell[] NoGuards = Array.Empty<Cell>();
    private static Random Rng() => new(0);

    private static int Chebyshev(Cell a, Cell b) =>
        Math.Max(Math.Abs(a.Column - b.Column), Math.Abs(a.Row - b.Row));

    /// <summary>Match 8×8 avec un joueur et un ennemi placés, tour passé au camp ennemi.</summary>
    private static Match EnemyTurn(Cell playerCell, Cell enemyCell, out Unit enemy)
    {
        var match = new Match(8, 8);
        match.Place(playerCell, Units.Soldat(Faction.Player));
        enemy = Units.Soldat(Faction.Enemy);
        match.Place(enemyCell, enemy);
        match.PassTurn();   // Player -> Enemy
        Assert.Equal(Faction.Enemy, match.CurrentTurn);
        return match;
    }

    [Fact]
    public void Normal_DistantPlayer_Advances()
    {
        var player = new Cell(0, 7);
        var from = new Cell(4, 0);
        var match = EnemyTurn(player, from, out _);   // AiKind.Normal par défaut

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);
        Assert.True(Chebyshev(action.Value.To, player) < Chebyshev(from, player),
            "un pion normal se rapproche du joueur lointain");
    }

    [Fact]
    public void Defensive_NoGuardCells_StillMoves()
    {
        // Exigence : l'ennemi doit BOUGER quoi qu'il arrive. Même un garde sans case à garder et joueur
        // lointain fait un coup légal (repli anti-blocage), plutôt que de passer son tour.
        var match = EnemyTurn(new Cell(0, 7), new Cell(4, 0), out var enemy);
        enemy.AiKind = AiKind.Defensif;

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);
    }

    [Fact]
    public void Defensive_AlreadyAtGuard_StillMoves_StaysAdjacent()
    {
        // Garde déjà au contact de son paysan, joueur lointain : il doit tout de même BOUGER (tourner
        // autour), sans se poser sur le paysan.
        var guard = new Cell(4, 4);
        var from = new Cell(4, 3);   // déjà adjacent au paysan
        var match = EnemyTurn(new Cell(0, 0), from, out var enemy);
        enemy.AiKind = AiKind.Defensif;

        var action = EnemyAi.ChooseAction(match, new[] { guard }, Rng());

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);
        Assert.NotEqual(from, action!.Value.To);    // il a bougé (case différente)
        Assert.NotEqual(guard, action.Value.To);    // jamais sur le paysan
    }

    [Fact]
    public void Defensive_PlayerInRange_Attacks()
    {
        // Un joueur au contact : même un garde défensif frappe.
        var player = new Cell(4, 4);
        var from = new Cell(4, 3);
        var match = EnemyTurn(player, from, out var enemy);
        enemy.AiKind = AiKind.Defensif;

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());

        Assert.NotNull(action);
        Assert.True(action!.Value.IsAttack);
        Assert.Equal(player, action.Value.To);
    }

    [Fact]
    public void Defensive_DistantPlayer_MovesTowardGuardCell_ButNeverOntoIt()
    {
        // Joueur loin (pas d'alerte) : le garde se rapproche du paysan à sécuriser, sans se poser dessus.
        var guard = new Cell(4, 4);
        var from = new Cell(4, 0);
        var match = EnemyTurn(new Cell(0, 7), from, out var enemy);
        enemy.AiKind = AiKind.Defensif;

        var action = EnemyAi.ChooseAction(match, new[] { guard }, Rng());

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);
        Assert.NotEqual(guard, action.Value.To);   // ne saute JAMAIS sur le paysan
        Assert.True(Chebyshev(action.Value.To, guard) < Chebyshev(from, guard),
            "le garde se rapproche de la case gardée");
    }

    [Fact]
    public void Offensive_PlayerInRange_Attacks()
    {
        // Assaillant : il attaque tout de même un joueur déjà à portée.
        var player = new Cell(4, 4);
        var from = new Cell(4, 3);
        var match = EnemyTurn(player, from, out var enemy);
        enemy.AiKind = AiKind.Offensif;

        var action = EnemyAi.ChooseAction(match, new[] { new Cell(0, 0) }, Rng());

        Assert.NotNull(action);
        Assert.True(action!.Value.IsAttack);
        Assert.Equal(player, action.Value.To);
    }

    [Fact]
    public void Offensive_DistantPlayer_MovesTowardPaysan()
    {
        // Joueur loin : l'assaillant fonce vers le paysan (réduit la distance), sans attaquer.
        var paysan = new Cell(4, 4);
        var from = new Cell(4, 0);
        var match = EnemyTurn(new Cell(0, 0), from, out var enemy);
        enemy.AiKind = AiKind.Offensif;

        var action = EnemyAi.ChooseAction(match, new[] { paysan }, Rng());

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);
        Assert.True(Chebyshev(action.Value.To, paysan) < Chebyshev(from, paysan));
    }

    [Fact]
    public void Offensive_AdjacentToPaysan_JumpsOntoIt()
    {
        // À une case du paysan : l'assaillant SAUTE DESSUS (se pose sur la case pour le capturer).
        var paysan = new Cell(4, 4);
        var from = new Cell(4, 3);
        var match = EnemyTurn(new Cell(0, 0), from, out var enemy);
        enemy.AiKind = AiKind.Offensif;

        var action = EnemyAi.ChooseAction(match, new[] { paysan }, Rng());

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);
        Assert.Equal(paysan, action.Value.To);   // contrairement au défensif, il se POSE sur le paysan
    }

    [Fact]
    public void Offensive_CanCaptureAndAttack_PrefersCapture()
    {
        // Objectif de mission : capturer un paysan À PORTÉE passe AVANT une attaque disponible sur un joueur.
        var player = new Cell(3, 3);   // adjacent → attaque possible
        var from = new Cell(4, 3);
        var paysan = new Cell(4, 4);   // adjacent → capture possible (se poser dessus)
        var match = EnemyTurn(player, from, out var enemy);
        enemy.AiKind = AiKind.Offensif;

        var action = EnemyAi.ChooseAction(match, new[] { paysan }, Rng());

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);
        Assert.Equal(paysan, action.Value.To);   // capture plutôt qu'attaque
    }

    [Fact]
    public void Offensive_PlayerReachable_EngagesInsteadOfRushingPaysan()
    {
        // Agressif : un joueur atteignable en un coup → l'offensif se JETTE dessus (se met au contact) au
        // lieu de filer vers le paysan lointain (l'engagement passe avant l'avancée).
        var player = new Cell(4, 4);
        var from = new Cell(4, 2);    // à 2 cases : un pas l'amène au contact
        var paysan = new Cell(0, 0);  // loin
        var match = EnemyTurn(player, from, out var enemy);
        enemy.AiKind = AiKind.Offensif;

        var action = EnemyAi.ChooseAction(match, new[] { paysan }, Rng());

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);
        Assert.Equal(1, Chebyshev(action.Value.To, player));   // se met à portée d'attaque du joueur
    }

    [Fact]
    public void Offensive_NoPaysans_AdvancesTowardPlayer()
    {
        // Plus de paysan à capturer : l'assaillant se rabat sur le joueur.
        var player = new Cell(0, 7);
        var from = new Cell(4, 0);
        var match = EnemyTurn(player, from, out var enemy);
        enemy.AiKind = AiKind.Offensif;

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);
        Assert.True(Chebyshev(action.Value.To, player) < Chebyshev(from, player));
    }

    [Fact]
    public void Match_EliminationSuppressed_KillingLastEnemyDoesNotWin()
    {
        // Mode objectif (mission spéciale) : anéantir les ennemis ne gagne PAS le combat.
        var match = new Match(8, 8, eliminationEndsGame: false);
        var playerCell = new Cell(4, 4);
        var enemyCell = new Cell(4, 3);
        match.Place(playerCell, Units.Soldat(Faction.Player));
        var enemy = Units.Soldat(Faction.Enemy);
        enemy.TakeDamage(enemy.Hp - 1);   // 1 PV : l'attaque le tue
        match.Place(enemyCell, enemy);

        var kind = match.TryAttack(playerCell, enemyCell);

        Assert.Equal(MoveKind.Killed, kind);
        Assert.Null(match.Winner);      // pas de victoire par élimination
        Assert.False(match.IsOver);
    }

    [Fact]
    public void Match_EliminationDefault_KillingLastEnemyWins()
    {
        // Contrôle : par défaut (escarmouche), anéantir les ennemis gagne le combat.
        var match = new Match(8, 8);
        var playerCell = new Cell(4, 4);
        var enemyCell = new Cell(4, 3);
        match.Place(playerCell, Units.Soldat(Faction.Player));
        var enemy = Units.Soldat(Faction.Enemy);
        enemy.TakeDamage(enemy.Hp - 1);
        match.Place(enemyCell, enemy);

        match.TryAttack(playerCell, enemyCell);

        Assert.Equal(Faction.Player, match.Winner);
    }

    /// <summary>Ennemi au contact d'un joueur à 1 PV : le kill est disponible ce tour-ci.</summary>
    private static Match KillAvailable(Cell playerCell, Cell enemyCell)
    {
        var match = new Match(8, 8);
        var player = Units.Soldat(Faction.Player);
        player.TakeDamage(player.Hp - 1);   // un coup suffit à le tuer
        match.Place(playerCell, player);
        match.Place(enemyCell, Units.Soldat(Faction.Enemy));
        match.PassTurn();   // Player -> Enemy
        return match;
    }

    [Fact]
    public void Accuracy_Perfect_TakesTheKill()
    {
        var player = new Cell(4, 4);
        var match = KillAvailable(player, new Cell(4, 3));

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng(), EnemyAi.PerfectAccuracy);

        Assert.NotNull(action);
        Assert.True(action!.Value.IsAttack);
        Assert.Equal(player, action.Value.To);
    }

    [Fact]
    public void Accuracy_Zero_LoneKiller_StillTakesKill()
    {
        // Maladresse maximale MAIS un seul ennemi : la règle « rater un kill = jouer un AUTRE pion » ne peut
        // pas s'appliquer (il n'y a pas d'autre pion) → le kill se fait quand même (l'ennemi doit agir).
        var player = new Cell(4, 4);
        var match = KillAvailable(player, new Cell(4, 3));

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng(), accuracy: 0.0);

        Assert.NotNull(action);
        Assert.True(action!.Value.IsAttack);
        Assert.Equal(player, action.Value.To);
    }

    [Fact]
    public void Accuracy_Zero_NonLethalAttack_IsNeverSkipped()
    {
        // Le meilleur coup est une attaque qui NE TUE PAS (joueur plein PV au contact) : même maladresse
        // maximale, l'IA la joue toujours (règle : taper-sans-tuer se fait toujours).
        var player = new Cell(4, 4);
        var match = EnemyTurn(player, new Cell(4, 3), out _);   // Normal, joueur plein PV

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng(), accuracy: 0.0);

        Assert.NotNull(action);
        Assert.True(action!.Value.IsAttack);
        Assert.Equal(player, action.Value.To);
    }

    [Fact]
    public void Accuracy_Zero_KillBlunder_AnotherPawnPlays_NotTheKiller()
    {
        // Un tueur au contact d'un joueur à 1 PV, et un second ennemi loin. Maladresse maximale : l'IA
        // renonce au kill et joue l'AUTRE pion (il avance) — jamais le tueur, jamais le kill.
        var match = new Match(8, 8);
        var weak = Units.Soldat(Faction.Player);
        weak.TakeDamage(weak.Hp - 1);                 // 1 PV
        match.Place(new Cell(4, 4), weak);
        var killer = new Cell(4, 3);                  // au contact → peut tuer
        match.Place(killer, Units.Soldat(Faction.Enemy));
        var other = new Cell(0, 0);                   // loin → ne peut qu'avancer
        match.Place(other, Units.Soldat(Faction.Enemy));
        match.PassTurn();

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng(), accuracy: 0.0);

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);       // pas de kill
        Assert.Equal(other, action.Value.From);     // c'est l'AUTRE pion qui joue
    }

    [Fact]
    public void Accuracy_Zero_KillBlunder_AnotherPawnTakesItsNonLethalAttack()
    {
        // Un tueur (joueur à 1 PV au contact) et un AUTRE ennemi au contact d'un joueur PLEIN PV. Maladresse
        // maximale : pas de kill ; l'autre pion prend son attaque NON-LÉTALE (priorité au coup non-létal).
        var match = new Match(8, 8);
        var weak = Units.Soldat(Faction.Player);
        weak.TakeDamage(weak.Hp - 1);
        match.Place(new Cell(4, 4), weak);
        var killer = new Cell(4, 3);
        match.Place(killer, Units.Soldat(Faction.Enemy));

        var healthy = new Cell(1, 1);
        match.Place(healthy, Units.Soldat(Faction.Player));   // plein PV
        var attacker = new Cell(1, 2);                        // au contact → attaque non-létale
        match.Place(attacker, Units.Soldat(Faction.Enemy));
        match.PassTurn();

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng(), accuracy: 0.0);

        Assert.NotNull(action);
        Assert.True(action!.Value.IsAttack);
        Assert.Equal(attacker, action.Value.From);   // l'autre pion attaque
        Assert.Equal(healthy, action.Value.To);       // le joueur plein PV (pas le kill)
    }

    [Fact]
    public void Accuracy_Zero_SingleRank_StillActs()
    {
        // Exigence maintenue malgré la maladresse : l'ennemi BOUGE quoi qu'il arrive. Un garde sans case à
        // garder et joueur lointain n'a qu'UN seul rang de coups (repli) — il le joue au lieu de ne rien faire.
        var match = EnemyTurn(new Cell(0, 7), new Cell(4, 0), out var enemy);
        enemy.AiKind = AiKind.Defensif;

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng(), accuracy: 0.0);

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);
    }

    // ── Sentinelle : postée (mirador), elle tire mais ne bouge jamais ────────────

    /// <summary>
    /// Tant qu'un camarade MOBILE tient encore, la sentinelle ne propose AUCUN déplacement — pas même le
    /// repli anti-blocage qui fait bouger toutes les autres IA. C'est l'autre pion qui joue.
    /// </summary>
    [Fact]
    public void Sentinelle_DistantPlayer_StaysPut()
    {
        var player = new Cell(0, 7);
        var match = EnemyTurn(player, new Cell(4, 0), out var posted);
        posted.AiKind = AiKind.Sentinelle;
        match.Place(new Cell(6, 0), Units.Soldat(Faction.Enemy));   // camarade mobile : le poste tient

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());

        Assert.NotNull(action);
        Assert.Equal(new Cell(6, 0), action!.Value.From);
    }

    /// <summary>
    /// DERNIER CARRÉ : quand il ne reste QUE des sentinelles, elles quittent leur poste et avancent comme des
    /// normales. Sans ça le combat se figerait jusqu'à la limite de tours.
    /// </summary>
    [Fact]
    public void Sentinelle_AsLastStand_LeavesItsPostAndAdvances()
    {
        var player = new Cell(0, 7);
        var from = new Cell(4, 0);
        var match = EnemyTurn(player, from, out var posted);
        posted.AiKind = AiKind.Sentinelle;

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());

        Assert.NotNull(action);
        Assert.False(action!.Value.IsAttack);
        Assert.True(Chebyshev(action.Value.To, player) < Chebyshev(from, player),
            "dernier carré : la sentinelle descend et se rapproche du joueur");
    }

    /// <summary>Le dernier carré se déclenche à la MORT du dernier mobile, pas seulement au premier tour.</summary>
    [Fact]
    public void Sentinelle_WhenTheLastMobileAllyFalls_TheyStartMoving()
    {
        var player = new Cell(0, 7);
        var from = new Cell(4, 0);
        var match = EnemyTurn(player, from, out var posted);
        posted.AiKind = AiKind.Sentinelle;
        var mobile = Units.Soldat(Faction.Enemy);
        match.Place(new Cell(6, 0), mobile);

        Assert.Equal(new Cell(6, 0), EnemyAi.ChooseAction(match, NoGuards, Rng())!.Value.From);

        match.Remove(new Cell(6, 0));   // le mobile tombe : les sentinelles restent seules

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());
        Assert.NotNull(action);
        Assert.Equal(from, action!.Value.From);
    }

    /// <summary>Elle attaque quand même ce qui vient à sa portée : elle tient un poste, elle ne dort pas.</summary>
    [Fact]
    public void Sentinelle_PlayerInRange_Attacks()
    {
        var player = new Cell(4, 1);
        var match = EnemyTurn(player, new Cell(4, 0), out var enemy);
        enemy.AiKind = AiKind.Sentinelle;

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());

        Assert.NotNull(action);
        Assert.True(action!.Value.IsAttack);
        Assert.Equal(player, action.Value.To);
    }

    /// <summary>Une sentinelle ne bloque pas ses camarades : les autres IA jouent normalement à côté d'elle.</summary>
    [Fact]
    public void Sentinelle_DoesNotStopTheRestOfTheArmy()
    {
        var player = new Cell(0, 7);
        var match = EnemyTurn(player, new Cell(4, 0), out var posted);
        posted.AiKind = AiKind.Sentinelle;
        var mobile = Units.Soldat(Faction.Enemy);
        match.Place(new Cell(6, 0), mobile);

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());

        Assert.NotNull(action);
        Assert.Equal(new Cell(6, 0), action!.Value.From);   // c'est l'AUTRE pion qui joue
    }

    // ── BOSS EN RETRAIT derrière son escorte ─────────────────────────────────────────────────────

    private static Unit Boss() => new(Domaine.Dame, Faction.Enemy, Domaines.Dame.BaseClass) { IsEssential = true };

    /// <summary>Escorte fragile (1 PV) : un seul coup du joueur l'abat, pour vider l'escorte en test.</summary>
    private static Unit FragileEscort() =>
        new(Domaine.Dame, Faction.Enemy, new UnitClass("E", "e", tier: 1, maxHp: 1, damage: 1, moveRange: 1, attackRange: 1));

    [Fact]
    public void Boss_WithItsEscort_NeverAdvances()
    {
        // Personne à portée : tout le monde « avance ». Le boss, lui, laisse son escorte y aller — quel que
        // soit le tirage aléatoire parmi les pions candidats.
        for (var seed = 0; seed < 40; seed++)
        {
            var match = new Match(8, 8);
            match.Place(new Cell(0, 7), Units.Soldat(Faction.Player));
            match.Place(new Cell(4, 0), Boss());
            match.Place(new Cell(1, 0), Units.Soldat(Faction.Enemy));
            match.Place(new Cell(6, 0), Units.Soldat(Faction.Enemy));
            match.PassTurn();

            var action = EnemyAi.ChooseAction(match, NoGuards, new Random(seed));

            Assert.NotNull(action);
            Assert.NotEqual(new Cell(4, 0), action!.Value.From);
        }
    }

    [Fact]
    public void Boss_InRetreat_StillStrikesWhatComesInRange()
    {
        var match = new Match(8, 8);
        match.Place(new Cell(4, 1), Units.Soldat(Faction.Player));   // au contact du boss
        match.Place(new Cell(4, 0), Boss());
        match.Place(new Cell(0, 7), Units.Soldat(Faction.Enemy));    // escorte loin de tout
        match.PassTurn();

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());

        Assert.NotNull(action);
        Assert.True(action!.Value.IsAttack);
        Assert.Equal(new Cell(4, 0), action.Value.From);   // en retrait n'est pas désarmé
    }

    [Fact]
    public void Boss_OnceMostOfItsEscortHasFallen_JoinsTheFight()
    {
        var bossMoved = false;
        for (var seed = 0; seed < 40 && !bossMoved; seed++)
        {
            var match = new Match(8, 8);
            match.Place(new Cell(0, 6), Units.Soldat(Faction.Player));
            match.Place(new Cell(7, 6), Units.Soldat(Faction.Player));
            match.Place(new Cell(4, 0), Boss());
            match.Place(new Cell(0, 5), FragileEscort());
            match.Place(new Cell(7, 5), FragileEscort());
            match.Place(new Cell(2, 0), Units.Soldat(Faction.Enemy));

            // Le joueur abat deux escortes sur trois : il n'en reste qu'une, sous la moitié.
            match.TryAttack(new Cell(0, 6), new Cell(0, 5));
            match.PassTurn();
            match.TryAttack(new Cell(7, 6), new Cell(7, 5));
            Assert.Equal(Faction.Enemy, match.CurrentTurn);

            var action = EnemyAi.ChooseAction(match, NoGuards, new Random(seed));
            bossMoved = action is { From: var from } && from == new Cell(4, 0);
        }

        Assert.True(bossMoved, "le boss doit se mettre en marche quand son escorte a cédé");
    }

    [Fact]
    public void Boss_OnceHit_LeavesItsRetreat_EvenWithItsWholeEscort()
    {
        var bossMoved = false;
        for (var seed = 0; seed < 40 && !bossMoved; seed++)
        {
            var match = new Match(8, 8);
            var sniper = new Unit(Domaine.Tour, Faction.Player, Domaines.Tour.BaseClass);   // tire à distance
            match.Place(new Cell(4, 2), sniper);
            var boss = new Unit(Domaine.Dame, Faction.Enemy,
                new UnitClass("B", "b", tier: 1, maxHp: 60, damage: 5, moveRange: 1, attackRange: 1)) { IsEssential = true };
            match.Place(new Cell(4, 0), boss);
            match.Place(new Cell(0, 0), Units.Soldat(Faction.Enemy));
            match.Place(new Cell(7, 0), Units.Soldat(Faction.Enemy));

            match.TryAttack(new Cell(4, 2), new Cell(4, 0));   // le tireur le touche hors de sa portée
            Assert.True(boss.TimesHit > 0);
            Assert.Equal(Faction.Enemy, match.CurrentTurn);

            var action = EnemyAi.ChooseAction(match, NoGuards, new Random(seed));
            bossMoved = action is { From: var from } && from == new Cell(4, 0);
        }

        Assert.True(bossMoved, "touché, le boss doit sortir de sa réserve malgré son escorte intacte");
    }

    // ── RÉACTION du boss attaqué : lui seul joue ──────────────────────────────────────────────────

    private static Unit ToughBoss(int attackRange = 1) =>
        new(Domaine.Dame, Faction.Enemy,
            new UnitClass("B", "b", tier: 1, maxHp: 60, damage: 5, moveRange: 1, attackRange: attackRange))
        { IsEssential = true };

    [Fact]
    public void HitBoss_StrikesBack_EvenIfAnEscortCouldKill()
    {
        var match = new Match(8, 8);
        match.Place(new Cell(4, 1), Units.Soldat(Faction.Player));    // frappe le boss au contact
        var boss = ToughBoss();
        match.Place(new Cell(4, 0), boss);
        var weak = new Unit(Domaine.Dame, Faction.Player,
            new UnitClass("W", "w", tier: 1, maxHp: 1, damage: 1, moveRange: 1, attackRange: 1));
        match.Place(new Cell(0, 6), weak);
        match.Place(new Cell(0, 7), Units.Soldat(Faction.Enemy));    // escorte qui pourrait l'achever

        match.TryAttack(new Cell(4, 1), new Cell(4, 0));
        Assert.True(boss.HitSinceOwnTurn);

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());

        Assert.NotNull(action);
        Assert.Equal(new Cell(4, 0), action!.Value.From);   // c'est le boss qui joue, pas l'escorte
        Assert.True(action.Value.IsAttack);
        Assert.Equal(new Cell(4, 1), action.Value.To);
    }

    [Fact]
    public void HitBoss_WithNobodyToStrike_HidesOnTheSafestCell_AndNobodyElsePlays()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var match = new Match(8, 8);
            // Le Lancier tire à 2 cases en ligne : le boss (portée 1) ne peut pas lui répondre.
            match.Place(new Cell(4, 3), new Unit(Domaine.Tour, Faction.Player, Domaines.Tour.BaseClass));
            match.Place(new Cell(4, 1), ToughBoss());
            match.Place(new Cell(0, 7), Units.Soldat(Faction.Enemy));
            match.Place(new Cell(0, 6), Units.Soldat(Faction.Player));   // l'escorte pourrait frapper celui-ci

            match.TryAttack(new Cell(4, 3), new Cell(4, 1));
            var action = EnemyAi.ChooseAction(match, NoGuards, new Random(seed));

            Assert.NotNull(action);
            Assert.Equal(new Cell(4, 1), action!.Value.From);   // seul le boss bouge
            Assert.False(action.Value.IsAttack);
            var threat = match.Units().Where(u => u.Unit.Faction == Faction.Player)
                .Count(u => match.ThreatenedCells(u.Cell).Contains(action.Value.To));
            Assert.Equal(0, threat);   // il s'est mis à l'abri
        }
    }

    [Fact]
    public void HitBoss_ReactsOnlyOnce_ThenTheArmyPlaysAgain()
    {
        var match = new Match(8, 8);
        match.Place(new Cell(4, 1), Units.Soldat(Faction.Player));
        var boss = ToughBoss();
        match.Place(new Cell(4, 0), boss);
        match.Place(new Cell(0, 7), Units.Soldat(Faction.Enemy));

        match.TryAttack(new Cell(4, 1), new Cell(4, 0));
        var reaction = EnemyAi.ChooseAction(match, NoGuards, Rng())!.Value;
        match.TryAttack(reaction.From, reaction.To);   // la riposte du boss clôt le tour ennemi

        Assert.False(boss.HitSinceOwnTurn);   // le coup a reçu sa réponse : il est oublié
    }

    [Fact]
    public void Boss_WithoutAnyEscort_FightsNormally()
    {
        var match = new Match(8, 8);
        match.Place(new Cell(0, 7), Units.Soldat(Faction.Player));
        match.Place(new Cell(4, 0), Boss());
        match.PassTurn();

        var action = EnemyAi.ChooseAction(match, NoGuards, Rng());

        Assert.NotNull(action);
        Assert.Equal(new Cell(4, 0), action!.Value.From);
    }
}
