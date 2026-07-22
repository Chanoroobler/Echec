using System;
using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Map;
using Xunit;

namespace Echec.Core.Tests;

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
}
