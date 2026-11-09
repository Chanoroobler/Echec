using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Battle.Config;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Map;
using ChessArmy.Core.Pvp;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// Mode multijoueur : synchronisation des deux clients (plateau retourné chez l'invité) avec des armées LIBRES
/// de toutes les classes et équipements livrés, builds et budget, rangs, profil chiffré, valeurs obscurcies,
/// nulle, rejeu de vérification et bots.
/// </summary>
public class PvpTests : IDisposable
{
    private readonly IReadOnlyList<DomaineDef> _domaines = Domaines.All;
    private readonly IReadOnlyList<CommandeDef> _commandes = Commandes.All;
    private readonly IReadOnlyList<Equipment> _equipment = Equipments.All;
    private readonly PvpConfig _config = PvpConfig.Current;

    public PvpTests()
    {
        var config = ConfigDir();
        var units = System.IO.File.ReadAllText(System.IO.Path.Combine(config, "units.json"));
        Domaines.Load(DomaineCatalog.FromJson(units));
        Commandes.Load(DomaineCatalog.CommandesFromJson(units));
        Equipments.Load(EquipmentCatalog.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(config, "equipment.json"))));
        PvpConfig.Load(PvpConfig.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(config, "pvp.json"))));
        ObscuredInt.ResetTamperFlag();
    }

    public void Dispose()
    {
        Domaines.Load(_domaines);
        Commandes.Load(_commandes);
        Equipments.Load(_equipment);
        PvpConfig.Load(_config);
        ObscuredInt.ResetTamperFlag();
    }

    private static string ConfigDir()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, "src", "ChessArmy.Game", "Assets", "Config");
    }

    // ── Synchronisation ──────────────────────────────────────────────────────────

    private static PvpBuild RandomBuild(Random rng)
    {
        var classes = PvpCatalog.AllUnitClasses().ToList();
        var equipment = PvpCatalog.AllEquipment().ToList();
        var commanders = PvpCatalog.Commanders().ToList();
        var build = new PvpBuild { CommanderId = commanders[rng.Next(commanders.Count)].Id };
        for (var line = 0; line < 2; line++)
            for (var c = 0; c < PvpBuild.Columns; c++)
                if (rng.NextDouble() < 0.75)
                    build.Set(c, line, classes[rng.Next(classes.Count)].Class.Asset,
                        rng.NextDouble() < 0.35 ? equipment[rng.Next(equipment.Count)].Id : null);
        return build;
    }

    /// <summary>Un coup légal au hasard pour le camp qui a la main (attaque, soin, jet, déplacement), sinon passe.</summary>
    private static BattleAction RandomAction(Match match, Random rng)
    {
        var options = new List<BattleAction>();
        foreach (var (cell, unit) in match.Units().ToList())
        {
            if (unit.Faction != match.CurrentTurn)
                continue;
            options.AddRange(match.AttackTargets(cell).Select(t => BattleAction.Attack(cell, t)));
            options.AddRange(match.HealTargets(cell).Select(t => BattleAction.Heal(cell, t)));
            options.AddRange(match.ThrowTargets(cell).Select(t => BattleAction.Throw(cell, t)));
            options.AddRange(match.LegalMoves(cell).Select(t => BattleAction.Move(cell, t)));
        }
        if (options.Count == 0)
            return BattleAction.Pass;
        // Les attaques d'abord, pour que les parties se jouent vraiment (traits de combat, morts, renaissances…).
        var attacks = options.Where(o => o.Kind != BattleActionKind.Move).ToList();
        var pool = attacks.Count > 0 && rng.NextDouble() < 0.7 ? attacks : options;
        return pool[rng.Next(pool.Count)];
    }

    private static void Play(Match match, in BattleAction action)
    {
        var actor = match.CurrentTurn;
        Assert.NotEqual(MoveKind.Invalid, match.Apply(action));
        PvpSetup.AfterAction(match, actor);
        PvpSetup.ResolvePending(match);
    }

    [Fact]
    public void MirroredClients_WithRandomArmiesOfEveryClass_StayInSync()
    {
        var plies = 0;
        for (var game = 0; game < 60; game++)
        {
            var rng = new Random(game);
            var hostBuild = RandomBuild(rng);
            var guestBuild = RandomBuild(rng);
            var hostStarts = PvpSetup.HostStarts(game * 977);
            var host = PvpSetup.CreateMatch(mirrored: false, hostStarts, game);
            var guest = PvpSetup.CreateMatch(mirrored: true, !hostStarts, game);
            PvpSetup.PlaceArmy(host, hostBuild, Faction.Player);
            PvpSetup.PlaceArmy(host, guestBuild, Faction.Enemy);
            PvpSetup.PlaceArmy(guest, guestBuild, Faction.Player);
            PvpSetup.PlaceArmy(guest, hostBuild, Faction.Enemy);
            Assert.Equal(host.ComputeStateHash(), guest.ComputeStateHash(mirrored: true));

            for (var ply = 0; ply < 250 && !host.IsOver; ply++, plies++)
            {
                var hostMoves = host.CurrentTurn == Faction.Player;
                var (mover, other) = hostMoves ? (host, guest) : (guest, host);
                var action = RandomAction(mover, rng);
                Play(mover, action);
                Play(other, PvpSetup.Mirror(action));
                Assert.True(host.ComputeStateHash() == guest.ComputeStateHash(mirrored: true),
                    $"désynchronisation : partie {game}, coup {ply}, {action}");
            }
            Assert.Equal(host.IsOver, guest.IsOver);
            Assert.Equal(host.IsDraw, guest.IsDraw);
            Assert.Equal(host.Winner, guest.Winner?.Opponent());
        }
        Assert.True(plies > 1000, "les parties doivent durer assez pour que le test ait du sens");
    }

    [Fact]
    public void Replay_ReproducesTheLiveGame()
    {
        var rng = new Random(5);
        var a = RandomBuild(rng);
        var b = RandomBuild(rng);
        var live = PvpSetup.CreateMatch(mirrored: false, localStarts: true, seed: 42);
        PvpSetup.PlaceArmy(live, a, Faction.Player);
        PvpSetup.PlaceArmy(live, b, Faction.Enemy);
        var log = new List<BattleAction>();
        for (var i = 0; i < 120 && !live.IsOver; i++)
        {
            var action = RandomAction(live, rng);
            Play(live, action);
            log.Add(action);
        }
        var replay = PvpSetup.Replay(42, true, null, null, a, b, log);
        Assert.Equal(live.ComputeStateHash(), replay.ComputeStateHash());
    }

    /// <summary>Joue un coup puis, comme la scène, la règle de pendule basse (réserve 0 = toujours sous le seuil).</summary>
    private static void PlayWithCollapse(Match match, in BattleAction action, List<BattleAction>? log = null)
    {
        var actor = match.CurrentTurn;
        var announced = match.NextCollapse(actor);   // la tuile que la scène fait trembler pendant ce tour
        Play(match, action);
        log?.Add(action);
        if (PvpSetup.CollapseAfterTurn(match, actor, 0f) is { } fallen)
        {
            Assert.Equal(announced, fallen);         // c'est bien elle qui tombe après le coup
            log?.Add(BattleAction.Collapse(actor));
        }
    }

    /// <summary>Case vue depuis l'autre client (plateau retourné : lignes inversées).</summary>
    private static Cell? MirrorRow(Cell? cell) =>
        cell is { } c ? new Cell(c.Column, PvpSetup.Size - 1 - c.Row) : null;

    [Fact]
    public void Collapse_KeepsMirroredClientsInSync_AndIsReplayed()
    {
        var collapses = 0;
        for (var game = 0; game < 30; game++)
        {
            var rng = new Random(1000 + game);
            var hostBuild = RandomBuild(rng);
            var guestBuild = RandomBuild(rng);
            var hostStarts = PvpSetup.HostStarts(game * 31);
            var host = PvpSetup.CreateMatch(mirrored: false, hostStarts, game);
            var guest = PvpSetup.CreateMatch(mirrored: true, !hostStarts, game);
            PvpSetup.PlaceArmy(host, hostBuild, Faction.Player);
            PvpSetup.PlaceArmy(host, guestBuild, Faction.Enemy);
            PvpSetup.PlaceArmy(guest, guestBuild, Faction.Player);
            PvpSetup.PlaceArmy(guest, hostBuild, Faction.Enemy);
            var hostLog = new List<BattleAction>();

            for (var ply = 0; ply < 120 && !host.IsOver; ply++)
            {
                var hostMoves = host.CurrentTurn == Faction.Player;
                var (mover, other) = hostMoves ? (host, guest) : (guest, host);
                var action = RandomAction(mover, rng);
                var hostAction = hostMoves ? action : PvpSetup.Mirror(action);
                PlayWithCollapse(mover, action, hostMoves ? hostLog : null);
                PlayWithCollapse(other, PvpSetup.Mirror(action), hostMoves ? null : hostLog);
                Assert.True(host.ComputeStateHash() == guest.ComputeStateHash(mirrored: true),
                    $"désynchronisation : partie {game}, coup {ply}, {hostAction}");
                // Les deux clients annoncent la même tuile qui tremble, pour chaque camp.
                Assert.Equal(host.NextCollapse(Faction.Player), MirrorRow(guest.NextCollapse(Faction.Enemy)));
                Assert.Equal(host.NextCollapse(Faction.Enemy), MirrorRow(guest.NextCollapse(Faction.Player)));
            }
            collapses += hostLog.Count(a => a.Kind == BattleActionKind.Collapse);
            var replay = PvpSetup.Replay(game, hostStarts, null, null, hostBuild, guestBuild, hostLog);
            Assert.Equal(host.ComputeStateHash(), replay.ComputeStateHash());
            Assert.Equal(host.Winner, guest.Winner?.Opponent());
        }
        Assert.True(collapses > 100, "les effondrements doivent vraiment se produire");
    }

    [Fact]
    public void Bot_UnderLowClock_MovesItsPieceOffTheTileThatWillFall()
    {
        var tested = 0;
        for (var seed = 0; seed < 60; seed++)
        {
            var match = PvpSetup.CreateMatch(mirrored: false, localStarts: false, seed);   // le bot (Enemy) a la main
            var rng = new Random(seed);
            PvpSetup.PlaceArmy(match, RandomBuild(rng), Faction.Player);
            PvpSetup.PlaceArmy(match, RandomBuild(rng), Faction.Enemy);
            if (match.NextCollapse(Faction.Enemy) is not { } doomed || match.UnitAt(doomed) is null
                || match.LegalMoves(doomed).Count == 0)
                continue;
            tested++;
            foreach (var def in new[] { new PvpBotDef { Accuracy = 1.0 }, new PvpBotDef { Accuracy = 1.0, ForceAttack = true } })
            {
                var action = PvpBotAi.Choose(match, Faction.Enemy, def, null, new Random(seed), doomed);
                Assert.Equal(BattleActionKind.Move, action!.Value.Kind);
                Assert.Equal(doomed, action.Value.From);   // il sort la pièce qui allait tomber avec la case
            }
        }
        Assert.True(tested > 5, "assez de positions où une pièce se tient sur la case condamnée");
    }

    [Fact]
    public void Collapse_TakesTheBackRowThenTheNext_KillsWhoStandsThere_AndStops()
    {
        var match = PvpSetup.CreateMatch(mirrored: false, localStarts: true, seed: 3);
        PvpSetup.PlaceArmy(match, RandomBuild(new Random(8)), Faction.Enemy);
        var size = PvpSetup.Size;
        var back = size - 1;
        var victim = PvpCatalog.Spawn(new PvpBuildSlot(0, PvpBuild.BackLine,
            PvpCatalog.AllUnitClasses().First().Class.Asset, null), Faction.Player)!;
        match.Place(new Cell(3, back - 1), victim);
        match.Place(new Cell(3, back - 3), PvpCatalog.Spawn(new PvpBuildSlot(0, PvpBuild.BackLine,
            PvpCatalog.AllUnitClasses().First().Class.Asset, null), Faction.Player)!);   // hors des lignes : la partie continue

        // Sous le seuil mais la main n'a pas passé (le joueur a encore la main) : rien ne tombe.
        Assert.Null(PvpSetup.CollapseAfterTurn(match, Faction.Player, 0f));
        // Main passée mais réserve au seuil : rien non plus.
        Assert.Null(PvpSetup.CollapseAfterTurn(match, Faction.Enemy, _config.CollapseBelowSeconds));

        var fallen = new List<Cell>();
        for (var i = 0; i < size; i++)
            fallen.Add(match.CollapseTile(Faction.Player)!.Value);
        Assert.All(fallen, c => Assert.Equal(back, c.Row));          // la ligne du fond d'abord, en entier
        Assert.Equal(size, fallen.Distinct().Count());
        Assert.True(victim.IsAlive);
        for (var i = 0; i < size; i++)
            fallen.Add(match.CollapseTile(Faction.Player)!.Value);
        Assert.All(fallen.Skip(size), c => Assert.Equal(back - 1, c.Row));
        Assert.False(victim.IsAlive);                                    // emporté avec sa tuile
        Assert.Null(match.UnitAt(new Cell(3, back - 1)));
        Assert.False(match.CanCollapse(Faction.Player));
        Assert.Null(match.CollapseTile(Faction.Player));                 // les deux lignes sont parties : on arrête
        Assert.True(match.IsCollapsed(new Cell(0, back)));
        Assert.False(match.IsCollapsed(new Cell(0, back - 2)));
    }

    [Fact]
    public void Morale_TiltsByThePriceOfEachFallenPawn_AndWinsAtTheThreshold()
    {
        var match = PvpSetup.CreateMatch(mirrored: false, localStarts: true, seed: 5);
        Assert.Equal(_config.MoraleToWin, match.MoraleToWin);
        var back = PvpSetup.Size - 1;
        // Commandants au milieu (hors des lignes qui tombent), pions du joueur sur toute sa ligne du fond.
        match.Place(new Cell(4, 4), PvpCatalog.SpawnCommander("commandant", Faction.Player)!);
        match.Place(new Cell(4, 2), PvpCatalog.SpawnCommander("commandant", Faction.Enemy)!);
        var soldier = PvpCatalog.AllUnitClasses().First(c => c.Class.Asset == "soldat").Class;
        for (var c = 0; c < PvpSetup.Size; c++)
            match.Place(new Cell(c, back), PvpCatalog.Spawn(new PvpBuildSlot(c, PvpBuild.BackLine, "soldat", null), Faction.Player)!);
        var price = PvpConfig.Current.CostOf(soldier);   // pvp.json chargé (le champ _config est lu avant)
        Assert.True(price > 0);

        var expected = 0;
        while (!match.IsOver)
        {
            Assert.True(-expected < match.MoraleToWin, "la partie aurait dû finir au seuil");
            match.CollapseTile(Faction.Player);   // un pion du joueur tombe : la balance penche vers l'ennemi
            expected -= price;
            Assert.Equal(expected, match.Morale);
        }
        Assert.Equal(Faction.Enemy, match.Winner);
        Assert.Equal(MatchEndReason.Morale, match.EndReason);
        Assert.True(-match.Morale >= match.MoraleToWin);
        Assert.Equal(match.Morale, match.Clone().Morale);   // copiée pour les bots et le rejeu
    }

    [Fact]
    public void LoneCommander_LosesMoraleEachOfItsTurns_UntilDefeat()
    {
        var match = PvpSetup.CreateMatch(mirrored: false, localStarts: false, seed: 5);   // l'ennemi commence
        match.Place(new Cell(0, 7), PvpCatalog.SpawnCommander("commandant", Faction.Player)!);
        match.Place(new Cell(7, 0), PvpCatalog.SpawnCommander("commandant", Faction.Enemy)!);   // seul
        match.Place(new Cell(0, 6), PvpCatalog.Spawn(new PvpBuildSlot(0, PvpBuild.FrontLine, "soldat", null), Faction.Player)!);
        var step = PvpConfig.Current.LoneCommanderMorale;
        Assert.True(step > 0);

        var turns = 0;
        while (!match.IsOver && turns < 200)
        {
            var lone = match.CurrentTurn == Faction.Enemy;
            var before = match.Morale;
            Assert.True(PvpForker.Step(match, BattleAction.Pass));
            Assert.Equal(before + (lone ? step : 0), match.Morale);   // seul le camp sans pion en perd
            turns++;
        }
        Assert.Equal(Faction.Player, match.Winner);
        Assert.Equal(MatchEndReason.Morale, match.EndReason);
        Assert.Equal(match.MoraleToWin, match.Morale);
    }

    [Fact]
    public void OnlyCommandersLeft_IsADraw()
    {
        var match = PvpSetup.CreateMatch(mirrored: false, localStarts: true, seed: 5);
        // Commandants hors des lignes qui tombent ; le seul pion, sur la ligne de devant du joueur.
        match.Place(new Cell(3, 3), PvpCatalog.SpawnCommander("commandant", Faction.Player)!);
        match.Place(new Cell(7, 0), PvpCatalog.SpawnCommander("commandant", Faction.Enemy)!);
        match.Place(new Cell(0, PvpSetup.Size - 2), PvpCatalog.Spawn(new PvpBuildSlot(0, PvpBuild.FrontLine, "soldat", null), Faction.Player)!);
        Assert.True(PvpForker.Step(match, BattleAction.Pass));
        Assert.False(match.IsOver);   // il reste un pion au joueur : la partie continue

        while (match.HasPawns(Faction.Player) && !match.IsOver)
            match.CollapseTile(Faction.Player);
        Assert.True(match.IsOver);
        Assert.True(match.IsDraw);
        Assert.Null(match.Winner);
        Assert.Equal(MatchEndReason.OnlyCommanders, match.EndReason);
    }

    [Fact]
    public void Morale_IsOffOutsideMultiplayer()
    {
        var campaign = new Match(8, 8);
        Assert.Equal(0, campaign.MoraleToWin);
    }

    [Fact]
    public void Collapse_UnderTheCommander_LosesTheGame()
    {
        var match = PvpSetup.CreateMatch(mirrored: false, localStarts: true, seed: 11);
        PvpSetup.PlaceArmy(match, RandomBuild(new Random(1)), Faction.Player);
        PvpSetup.PlaceArmy(match, RandomBuild(new Random(2)), Faction.Enemy);
        for (var i = 0; i < PvpSetup.Size && !match.IsOver; i++)
            match.CollapseTile(Faction.Player);
        Assert.Equal(Faction.Enemy, match.Winner);   // le roi est sur la ligne du fond
    }

    [Fact]
    public void Resign_And_Draw_AreMirroredAndEndTheGame()
    {
        var host = PvpSetup.CreateMatch(false, true, 1);
        var guest = PvpSetup.CreateMatch(true, false, 1);
        // L'invité (Player chez lui) abandonne : chez l'hôte, c'est l'ennemi qui perd.
        var resign = BattleAction.Resign(Faction.Player);
        guest.Apply(resign);
        host.Apply(PvpSetup.Mirror(resign));
        Assert.Equal(Faction.Enemy, guest.Winner);
        Assert.Equal(Faction.Player, host.Winner);

        var m = PvpSetup.CreateMatch(false, true, 1);
        m.Apply(BattleAction.AgreeDraw);
        Assert.True(m.IsDraw && m.IsOver);
        Assert.Equal(MoveKind.Invalid, m.Apply(BattleAction.Pass));
    }

    [Fact]
    public void EndReason_And_Round_AreTracked_ForTheEndScreen()
    {
        // Pendule tombée chez l'adversaire (coup Resign reçu) : temps écoulé, pas abandon.
        var timeout = PvpSetup.CreateMatch(false, true, 1);
        Assert.Equal(MatchEndReason.None, timeout.EndReason);
        timeout.Apply(BattleAction.Resign(Faction.Enemy));
        Assert.Equal(MatchEndReason.Timeout, timeout.EndReason);

        // Abandon volontaire (bouton « Abandonner ») : raison « abandon », conservée par le miroir.
        var abandon = PvpSetup.CreateMatch(false, true, 1);
        abandon.Apply(PvpSetup.Mirror(BattleAction.Abandon(Faction.Player)));
        Assert.Equal(MatchEndReason.Resign, abandon.EndReason);
        Assert.Equal(Faction.Player, abandon.Winner);

        var left = PvpSetup.CreateMatch(false, true, 1);
        left.Concede(Faction.Enemy, MatchEndReason.Disconnect);
        left.Concede(Faction.Player, MatchEndReason.Resign);   // partie finie : rien ne change
        Assert.Equal(MatchEndReason.Disconnect, left.EndReason);
        Assert.Equal(Faction.Player, left.Winner);

        var draw = PvpSetup.CreateMatch(false, true, 1);
        draw.Apply(BattleAction.AgreeDraw);
        Assert.Equal(MatchEndReason.AgreedDraw, draw.EndReason);

        // Tours façon échecs : chaque camp joue une fois = un tour ; la copie garde le compte.
        var m = PvpSetup.CreateMatch(false, true, 1);
        m.Place(new Cell(4, 7), PvpCatalog.SpawnCommander("commandant", Faction.Player)!);
        m.Place(new Cell(4, 0), PvpCatalog.SpawnCommander("commandant", Faction.Enemy)!);
        // Un pion de chaque côté : avec les seuls commandants, la partie serait aussitôt nulle (OnlyCommanders).
        m.Place(new Cell(0, 6), PvpCatalog.Spawn(new PvpBuildSlot(0, PvpBuild.FrontLine, "soldat", null), Faction.Player)!);
        m.Place(new Cell(7, 1), PvpCatalog.Spawn(new PvpBuildSlot(7, PvpBuild.FrontLine, "soldat", null), Faction.Enemy)!);
        Assert.Equal(1, m.Round);
        m.Apply(BattleAction.Pass);
        Assert.Equal(1, m.Round);
        m.Apply(BattleAction.Pass);
        Assert.Equal(2, m.Round);
        Assert.Equal(2, m.Clone().Round);

        // Commandant adverse tué : raison « commandant tombé ».
        var kill = PvpSetup.CreateMatch(false, true, 1);
        kill.Place(new Cell(4, 7), PvpCatalog.SpawnCommander("commandant", Faction.Player)!);
        var target = PvpCatalog.SpawnCommander("commandant", Faction.Enemy)!;
        kill.Place(new Cell(4, 6), target);
        kill.Place(new Cell(0, 0), PvpCatalog.SpawnCommander("commandant", Faction.Enemy)!);
        kill.Place(new Cell(0, 6), PvpCatalog.Spawn(new PvpBuildSlot(0, PvpBuild.FrontLine, "soldat", null), Faction.Player)!);
        kill.Place(new Cell(7, 1), PvpCatalog.Spawn(new PvpBuildSlot(7, PvpBuild.FrontLine, "soldat", null), Faction.Enemy)!);
        for (var i = 0; i < 50 && !kill.IsOver; i++)
        {
            if (kill.CurrentTurn == Faction.Player) kill.Apply(BattleAction.Attack(new Cell(4, 7), new Cell(4, 6)));
            else kill.Apply(BattleAction.Pass);
        }
        Assert.True(kill.IsOver);
        Assert.Equal(MatchEndReason.LeaderDown, kill.EndReason);
    }

    [Fact]
    public void Clock_Elapsed_CountsOnlyTheTimeItRan()
    {
        var clock = new PvpClock(600f, 60f);
        clock.BeginTurn(Faction.Player);
        clock.Tick(10f);
        clock.BeginTurn(Faction.Enemy);
        clock.Tick(5f);
        Assert.Equal(15f, clock.Elapsed, 3);
        clock.Tick(100f);   // pendule tombée : elle s'arrête
        clock.Tick(30f);
        Assert.Equal(115f, clock.Elapsed, 3);
    }

    [Fact]
    public void CanOpenChest_NeedsPoints_AndSomethingToWin()
    {
        var profile = PvpProfile.CreateNew();
        Assert.True(profile.CanOpenChest(units: true));
        var rng = new Random(1);
        while (profile.OpenChest(units: true, rng) != null) { }
        Assert.False(profile.CanOpenChest(units: true));
    }

    // ── Nulle automatique ────────────────────────────────────────────────────────

    [Fact]
    public void DeadPosition_IsDetected_OnlyWhenNoContactIsPossible()
    {
        // Deux commandants face à face sur un plateau dégagé : ils peuvent se rejoindre → pas nulle.
        var open = PvpSetup.CreateMatch(false, true, 1);
        open.Place(new Cell(4, 7), PvpCatalog.SpawnCommander("commandant", Faction.Player)!);
        open.Place(new Cell(4, 0), PvpCatalog.SpawnCommander("commandant", Faction.Enemy)!);
        Assert.False(open.IsDeadPosition());

        // Même chose, mais une rivière infranchissable coupe le plateau en deux : aucun contact possible.
        var walled = Battlefield.CreateFlat(PvpSetup.Size, PvpSetup.Size);
        for (var c = 0; c < PvpSetup.Size; c++)
            for (var r = 3; r <= 4; r++)
                walled[new Cell(c, r)] = new Tile(BuiltInTiles.Mountain);
        var cut = PvpSetup.CreateMatch(false, true, 1, walled);
        cut.Place(new Cell(4, 7), PvpCatalog.SpawnCommander("commandant", Faction.Player)!);
        cut.Place(new Cell(4, 0), PvpCatalog.SpawnCommander("commandant", Faction.Enemy)!);
        Assert.True(cut.IsDeadPosition());

        PvpSetup.AfterAction(cut, Faction.Enemy);
        Assert.True(cut.IsDraw);
    }

    // ── Builds et budget ─────────────────────────────────────────────────────────

    [Fact]
    public void DefaultBuild_And_BotArmies_AreValid_AndWithinBudget()
    {
        Assert.Null(PvpCatalog.Problem(PvpProfile.DefaultBuild()));
        Assert.NotEmpty(PvpConfig.Current.Bots);
        foreach (var (rank, bot) in PvpConfig.Current.Bots)
        {
            Assert.NotEmpty(bot.Armies);
            foreach (var army in bot.Armies)
                Assert.True(PvpCatalog.Problem(army) is null, $"armée du bot {rank} : {PvpCatalog.Problem(army)}");
        }
        foreach (var key in PvpRanks.BotKeys())   // un niveau d'IA par division, de bronze-3 à master
            Assert.True(PvpConfig.Current.Bots.ContainsKey(key), $"pas de bot pour {key}");
        Assert.Equal(13, PvpRanks.BotKeys().Count());
    }

    [Fact]
    public void StartingArmy_FillsTheWholeBudget_AsDoEveryBotArmy()
    {
        // Principe d'équilibrage : l'armée de départ prend TOUS les points ; débloquer ne rend pas plus fort.
        Assert.Equal(PvpConfig.Current.Budget, PvpCatalog.Cost(PvpProfile.DefaultBuild()));
        foreach (var (rank, bot) in PvpConfig.Current.Bots)
            foreach (var army in bot.Armies)
            {
                Assert.True(PvpCatalog.Cost(army) == PvpConfig.Current.Budget,
                    $"armée du bot {rank} : {PvpCatalog.Cost(army)} points au lieu de {PvpConfig.Current.Budget}");
                Assert.True(PvpCatalog.EquipmentCost(army) <= PvpConfig.Current.EquipmentBudget,
                    $"armée du bot {rank} : {PvpCatalog.EquipmentCost(army)} points d'équipement");
            }
    }

    [Fact]
    public void BotEquipment_StartsAtSilver_AndIsRedrawnEveryGame()
    {
        var config = PvpConfig.Current;
        var rng = new Random(5);
        foreach (var key in PvpRanks.BotKeys())
        {
            var bot = config.Bots[key];
            var silverOrAbove = !key.StartsWith("bronze") && !key.StartsWith("fer");
            Assert.Equal(silverOrAbove, bot.Equipment);
            foreach (var source in bot.Armies)
            {
                var looks = new HashSet<string>();
                for (var game = 0; game < 6; game++)
                {
                    var army = bot.ArmyFor(source, rng);
                    Assert.Equal(source.Slots.Select(s => (s.Column, s.Line, s.ClassAsset)), army.Slots.Select(s => (s.Column, s.Line, s.ClassAsset)));
                    Assert.Null(PvpCatalog.Problem(army));
                    if (silverOrAbove)
                        Assert.InRange(PvpCatalog.EquipmentCost(army), config.EquipmentBudget - 1, config.EquipmentBudget);
                    else
                        Assert.All(army.Slots, s => Assert.Null(s.EquipmentId));
                    looks.Add(string.Join(",", army.Slots.Select(s => s.EquipmentId)));
                }
                if (silverOrAbove)   // la même armée ne revient pas toujours avec les mêmes équipements
                    Assert.True(looks.Count > 1, $"{key} : équipements toujours identiques");
            }
        }
    }

    [Fact]
    public void BotEquipment_RespectsMountRules_AndSmartPlacementPicksTheBestCarrier()
    {
        var rng = new Random(9);
        var rangeChecks = 0;
        foreach (var source in PvpConfig.Current.BotArmies)
            foreach (var smart in new[] { false, true })
                for (var game = 0; game < 4; game++)
                {
                    var army = PvpBotEquipment.Equip(source, rng, smart);
                    foreach (var slot in army.Slots)
                    {
                        if (slot.EquipmentId is null || PvpCatalog.FindClass(slot.ClassAsset) is not { } cls)
                            continue;
                        var item = Equipments.ById(slot.EquipmentId)!;
                        Assert.True(PvpBotEquipment.CanCarry(cls.Domaine, cls.Class, item), $"{slot.ClassAsset} ne peut pas porter {item.Id}");
                        // Placement malin : aucun pion resté sans objet n'en aurait mieux profité.
                        if (!smart)
                            continue;
                        var fit = PvpBotEquipment.Fit(item, cls.Class, slot);
                        foreach (var other in army.Slots.Where(s => s.EquipmentId is null))
                            if (PvpCatalog.FindClass(other.ClassAsset) is { } c && PvpBotEquipment.CanCarry(c.Domaine, c.Class, item))
                                Assert.True(PvpBotEquipment.Fit(item, c.Class, other) <= fit + 1e-9,
                                    $"{item.Id} sur {slot.ClassAsset} alors que {other.ClassAsset} est libre");
                        rangeChecks++;
                    }
                }
        Assert.True(rangeChecks > 0);
    }

    [Fact]
    public void BotStyles_EachNamePlaysExactlyOneStyle()
    {
        var styles = PvpConfig.Current.BotStyles;
        Assert.True(styles.Count >= 3, "au moins trois styles de jeu");
        var names = styles.SelectMany(s => s.Names).ToList();
        Assert.True(names.Count >= 12);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(styles, s => Assert.NotEmpty(s.Names));
    }

    [Fact]
    public void BotArmies_AreAboutForty_AllDifferent_AndSharedFromSilverUp()
    {
        var armies = PvpConfig.Current.BotArmies;
        Assert.True(armies.Count >= 35, $"{armies.Count} armées de bots");
        Assert.Equal(armies.Count, armies.Select(ArmyKey).Distinct().Count());
        foreach (var (key, bot) in PvpConfig.Current.Bots)
            if (!IsBeginnerRank(key))   // réserve commune d'Argent à Master : seule l'intelligence change
                Assert.Equal(armies.Select(ArmyKey), bot.Armies.Select(ArmyKey));
    }

    [Fact]
    public void BronzeAndIronBots_PlayBasePawns_WithAtMostOneOtherPiece()
    {
        // Pour qu'un nouveau joueur ne soit pas perdu : pions de base (classe de base de chaque domaine), une pièce différente max.
        var config = PvpConfig.Current;
        var baseAssets = Domaines.All.Select(d => d.BaseClass.Asset).ToHashSet();
        var beginner = config.Bots["bronze-3"].Armies;
        Assert.True(beginner.Count >= 15, $"{beginner.Count} armées débutant");
        Assert.Equal(beginner.Count, beginner.Select(ArmyKey).Distinct().Count());
        Assert.True(beginner.Select(a => a.CommanderId).Distinct().Count() >= 3, "commandants variés");
        foreach (var army in beginner)
        {
            Assert.Null(PvpCatalog.Problem(army));
            Assert.Equal(config.Budget, PvpCatalog.Cost(army));
            Assert.All(army.Slots, s => Assert.Null(s.EquipmentId));
            var others = army.Slots.Count(s => !baseAssets.Contains(s.ClassAsset));
            Assert.True(others <= 1, $"{ArmyKey(army)} : {others} pièces non de base");
        }
        Assert.Contains(beginner, a => a.Slots.Any(s => !baseAssets.Contains(s.ClassAsset)));   // la pièce différente varie
        Assert.True(beginner.SelectMany(a => a.Slots).Select(s => s.ClassAsset).Where(c => !baseAssets.Contains(c)).Distinct().Count() > 1);
        foreach (var (key, bot) in config.Bots)
            if (IsBeginnerRank(key))
                Assert.Equal(beginner.Select(ArmyKey), bot.Armies.Select(ArmyKey));
    }

    private static bool IsBeginnerRank(string key) => key.StartsWith("bronze") || key.StartsWith("fer");

    private static string ArmyKey(PvpBuild a) => a.CommanderId + "|" + string.Join(",",
        a.Slots.Select(s => $"{s.Column}{s.Line}{s.ClassAsset}+{s.EquipmentId}"));

    [Fact]
    public void ShippedCosts_CoverEveryClassAndEquipment()
    {
        // Les coûts viennent de l'outil d'équilibrage : une classe ou un équipement oublié coûterait le défaut.
        foreach (var (_, cls) in PvpCatalog.AllUnitClasses())
        {
            Assert.True(PvpConfig.Current.UnitCost.ContainsKey(cls.Asset), $"pas de coût pour {cls.Asset}");
            // Règle de l'auteur : chaque pion coûte entre 3 et 8 points (on ajuste les stats multi, pas la règle).
            Assert.InRange(PvpConfig.Current.CostOf(cls), 3, 8);
        }
        foreach (var eq in PvpCatalog.AllEquipment())
        {
            Assert.True(PvpConfig.Current.EquipmentCost.ContainsKey(eq.Id), $"pas de coût pour {eq.Id}");
            // Règle de l'auteur : un équipement coûte de 1 à 5 points (on affaiblit l'objet en multi, pas la règle).
            Assert.InRange(PvpConfig.Current.CostOf(eq), 1, 5);
        }
        foreach (var c in PvpCatalog.Commanders())
            Assert.True(PvpConfig.Current.CommanderCost.ContainsKey(c.Id), $"pas de coût pour le commandant {c.Id}");
        Assert.Equal(0, PvpConfig.Current.CommanderCost.Values.Min());   // le plus faible coûte 0
    }

    [Fact]
    public void MultiEquipmentStats_ApplyInMultiOnly()
    {
        // pvp.json « equipmentStats » remplace le bonus de la stat en multi ; equipment.json (le solo) ne bouge pas.
        var config = PvpConfig.Current.WithEquipmentStats(new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["LoupLegendaire"] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Damage"] = 1 },
        });
        var previous = PvpConfig.Current;
        PvpConfig.Load(config);
        try
        {
            var solo = Equipments.ById("LoupLegendaire")!;
            var multi = PvpCatalog.FindEquipment("LoupLegendaire")!;
            Assert.Equal(1, multi.BonusFor(EquipStat.Damage));
            Assert.Equal(solo.BonusFor(EquipStat.Hp), multi.BonusFor(EquipStat.Hp));   // stat non citée : inchangée
            Assert.Equal(solo.Traits, multi.Traits);
            Assert.NotEqual(1, solo.BonusFor(EquipStat.Damage));
            var unit = PvpCatalog.Spawn(new PvpBuildSlot(0, 1, "soldat", "LoupLegendaire"), Faction.Player)!;
            Assert.Equal(1, unit.Equipments!.BonusFor(EquipStat.Damage));
        }
        finally
        {
            PvpConfig.Load(previous);
        }
    }

    [Fact]
    public void ShippedMultiStats_NoTwoClassesAreIdentical()
    {
        // L'outil d'équilibrage arrondit PV et dégâts : deux pièces d'un même domaine peuvent retomber sur les mêmes
        // valeurs (Cavalier et Cavalier lourd l'ont fait). Une évolution doit toujours se distinguer de son parent.
        var signatures = PvpCatalog.AllUnitClasses()
            .Select(x => (x.Class.Asset, Key: string.Join("|", x.Domaine, x.Class.MaxHp, x.Class.Damage, x.Class.MoveRange,
                x.Class.AttackRange, x.Class.MinAttackRange, x.Class.PiercesAllies, x.Class.AttackDomaine,
                string.Join(",", x.Class.Traits.OrderBy(t => t, StringComparer.Ordinal)), PvpConfig.Current.CostOf(x.Class))))
            .ToList();
        var duplicates = signatures.GroupBy(s => s.Key)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" = ", g.Select(s => s.Asset)))
            .ToList();
        Assert.True(duplicates.Count == 0, "Pions identiques en multi (stats, traits et coût) : " + string.Join(" ; ", duplicates));
    }

    [Fact]
    public void Duo_ComesWithBasile_AndLosingEitherLosesTheGame()
    {
        var build = new PvpBuild { CommanderId = "Commandant_duo" };
        Assert.True(build.HasCompanion);
        // La case de Basile (à gauche du commandant) est réservée : aucun pion ne peut y aller.
        Assert.False(build.Set(PvpBuild.CompanionColumn, PvpBuild.BackLine, Domaines.Dame.BaseClass.Asset));

        var match = PvpSetup.CreateMatch(mirrored: false, localStarts: false, seed: 1);
        PvpSetup.PlaceArmy(match, build, Faction.Player);
        PvpSetup.PlaceArmy(match, new PvpBuild { CommanderId = "commandant" }, Faction.Enemy);
        var basile = match.UnitAt(new Cell(PvpBuild.CompanionColumn, PvpSetup.Size - 1))!;
        Assert.True(basile.IsEssential && basile.IsCompanion);
        Assert.True(match.UnitAt(new Cell(PvpBuild.KingColumn, PvpSetup.Size - 1))!.IsEssential);

        // Basile tombe sous une attaque ennemie (son commandant est indemne) : la partie est perdue.
        basile.TakeDamage(basile.Hp - 1);
        var attackerCell = new Cell(PvpBuild.CompanionColumn, PvpSetup.Size - 2);
        match.Place(attackerCell, PvpCatalog.Spawn(new PvpBuildSlot(0, 0, Domaines.Dame.BaseClass.Asset, null), Faction.Enemy)!);
        Assert.NotEqual(MoveKind.Invalid,
            match.Apply(BattleAction.Attack(attackerCell, new Cell(PvpBuild.CompanionColumn, PvpSetup.Size - 1))));
        Assert.Equal(Faction.Enemy, match.Winner);

        // Un pion resté sur la case de Basile (commandant changé après coup) rend l'armée invalide.
        var stale = new PvpBuild { CommanderId = "commandant" };
        stale.Set(PvpBuild.CompanionColumn, PvpBuild.BackLine, Domaines.Dame.BaseClass.Asset);
        stale.CommanderId = "Commandant_duo";
        Assert.NotNull(PvpCatalog.Problem(stale));
    }

    [Fact]
    public void ExcludedEquipment_IsUnavailableInMulti_ButStaysInTheGame()
    {
        // Séisme, Arc légendaire et Impact rare : retirés du MULTI seulement (le solo les garde).
        foreach (var id in new[] { "seisme", "arcLegendaire", "ImpactRare" })
        {
            Assert.NotNull(Equipments.ById(id));
            Assert.DoesNotContain(PvpCatalog.AllEquipment(), e => e.Id == id);
            var build = PvpProfile.DefaultBuild();
            build.Set(0, PvpBuild.FrontLine, Domaines.Dame.BaseClass.Asset, id);
            Assert.NotNull(PvpCatalog.Problem(build));
        }
        Assert.DoesNotContain(PvpProfile.CreateNew().LockedEquipment(), id => id is "seisme" or "arcLegendaire" or "ImpactRare");
    }

    [Fact]
    public void Balance_Simulation_IsSymmetric_ForIdenticalArmies()
    {
        // Contrôle de la méthode d'équilibrage : 4 Soldats contre 4 Soldats doit tourner autour de 50 %.
        var soldat = PvpCatalog.FindClass(Domaines.Dame.BaseClass.Asset)!.Value;
        var piece = PvpBalance.Of(soldat.Domaine, soldat.Class);
        var rate = PvpBalance.WinRate(piece, 4, new[] { piece }, 4, games: 60, seed: 3);
        Assert.InRange(rate, 0.35, 0.65);
    }

    [Fact]
    public void Build_OverBudget_IsRejected_AndKingCellIsReserved()
    {
        var build = new PvpBuild();
        // La pièce la plus chère (coût mesuré, pas son tier) sur toute la ligne de devant.
        var priciest = PvpCatalog.AllUnitClasses().OrderByDescending(c => PvpConfig.Current.CostOf(c.Class)).First().Class.Asset;
        Assert.False(build.Set(PvpBuild.KingColumn, PvpBuild.BackLine, priciest));
        for (var c = 0; c < PvpBuild.Columns; c++)
            build.Set(c, PvpBuild.FrontLine, priciest);
        Assert.Equal("budget d'armée dépassé", PvpCatalog.Problem(build));

        var copy = PvpBuild.FromJson(build.ToJson())!;
        Assert.Equal(build.Slots.Count, copy.Slots.Count);
        Assert.Equal(PvpCatalog.Cost(build), PvpCatalog.Cost(copy));
    }

    [Fact]
    public void Commanders_UseTheirMultiStats()
    {
        var bastion = PvpCatalog.SpawnCommander("Commandant_lancier", Faction.Player)!;
        Assert.True(bastion.IsEssential);
        Assert.Equal(PvpConfig.Current.Commanders["Commandant_lancier"].Hp, bastion.MaxHp);
        Assert.Empty(bastion.Class.Traits);   // en multi, les commandants n'ont pas de pouvoir (pour l'instant)
    }

    // ── Rangs et cote ────────────────────────────────────────────────────────────

    [Fact]
    public void Ranks_HaveDivisions_AndEloIsSymmetric()
    {
        Assert.Equal("bronze", PvpRanks.FromRating(PvpConfig.Current.StartRating).Id);
        Assert.Equal(3, PvpRanks.FromRating(810).Division);
        Assert.Equal(1, PvpRanks.FromRating(1090).Division);
        Assert.Equal(new PvpRank("fer", 3, 1), PvpRanks.FromRating(1100));
        Assert.Equal(0, PvpRanks.FromRating(2400).Division);
        Assert.Equal("master", PvpRanks.FromRating(2400).Id);

        var win = PvpRanks.Updated(1000, 1000, 1.0) - 1000;
        var loss = PvpRanks.Updated(1000, 1000, 0.0) - 1000;
        Assert.Equal(win, -loss);
        Assert.Equal(25, win);                                         // cotes égales : K / 2 (K = 50)
        Assert.Equal(0, PvpRanks.Updated(1000, 1000, 0.5) - 1000);     // nulle à cotes égales
    }

    // ── Profil ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Profile_RoundTrips_AndRejectsTamperingOrAnotherOwner()
    {
        var profile = PvpProfile.CreateNew();
        var reward = profile.RecordResult(PvpOutcome.Win, kills: 3, opponentRating: 1000, ranked: true);
        Assert.Equal(3 * PvpConfig.Current.PointsPerKill + PvpConfig.Current.PointsForWin, reward.Points);
        Assert.True(reward.RatingAfter > reward.RatingBefore);

        var bytes = profile.Encode("steam-1");
        var back = PvpProfile.Decode(bytes, "steam-1");
        Assert.NotNull(back);
        Assert.Equal(profile.Points, back!.Points);
        Assert.Equal(profile.Rating, back.Rating);
        Assert.Equal(1, back.Wins);

        Assert.Null(PvpProfile.Decode(bytes, "steam-2"));   // profil copié depuis un autre compte
        var forged = (byte[])bytes.Clone();
        forged[forged.Length / 2] ^= 0x40;
        Assert.Null(PvpProfile.Decode(forged, "steam-1"));   // un seul octet retouché
    }

    [Fact]
    public void Training_KeepsRatingAndRecord_HalvesPoints_AndGoesToChallenges()
    {
        var profile = PvpProfile.CreateNew();
        var config = PvpConfig.Current;
        var (rating, points) = (profile.Rating, profile.Points);
        var t = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var reward = profile.RecordResult(PvpOutcome.Win, kills: 3, opponentRating: 2000, ranked: false, "Aldric",
            bot: true, t, config.TrainingPointsFactor, training: true);

        Assert.False(reward.Ranked);
        Assert.Equal(rating, profile.Rating);                     // la cote ne bouge jamais
        Assert.Equal(PvpProfile.ScaledPoints(3 * config.PointsPerKill, 0.5f) + PvpProfile.ScaledPoints(config.PointsForWin, 0.5f),
            reward.Points);
        Assert.Equal(7 + 15, reward.Points);                      // 15 → 7 (arrondi vers le bas), 30 → 15
        Assert.Equal(points + reward.Points, profile.Points);
        Assert.Equal(0, profile.Games + profile.Wins);            // hors du bilan V/D/N du classement
        Assert.Empty(profile.RankedHistory);
        Assert.Single(profile.FriendlyHistory);
        Assert.Equal(0, profile.Streak);
    }

    [Fact]
    public void Rating_NeverGoesAboveTheCap()
    {
        var max = PvpConfig.Current.MaxRating;
        Assert.Equal(max, PvpRanks.Updated(max - 5, max, 1.0));   // la victoire rapporterait 25 : plafonnée
        Assert.Equal(max, PvpRanks.Updated(max, max, 1.0));
        Assert.True(PvpRanks.Updated(max, max, 0.0) < max);       // une défaite fait toujours descendre

        // Série de victoires au plafond : le bonus est rogné, la cote reste au plafond.
        var profile = PvpProfile.CreateNew();
        var t = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 400 && profile.Rating < max; i++)
            profile.RecordResult(PvpOutcome.Win, 0, max, ranked: true, "a", bot: false, t.AddMinutes(i));
        Assert.Equal(max, profile.Rating);
        var reward = profile.RecordResult(PvpOutcome.Win, 0, max, ranked: true, "b", bot: false, t.AddDays(1));
        Assert.Equal(max, profile.Rating);
        Assert.Equal(0, reward.StreakBonus);
    }

    [Fact]
    public void TutorialMatch_IsPendingForANewPlayer_AndSurvivesTheFile()
    {
        var fresh = PvpProfile.CreateNew();
        Assert.False(fresh.TutorialMatchDone);                       // tout premier LANCER : partie tuto
        Assert.False(PvpProfile.Decode(fresh.Encode("steam-1"), "steam-1")!.TutorialMatchDone);

        fresh.TutorialMatchDone = true;
        Assert.True(PvpProfile.Decode(fresh.Encode("steam-1"), "steam-1")!.TutorialMatchDone);

        // Un joueur qui a déjà joué (fichier d'avant le tuto) n'est pas renvoyé au tuto.
        var veteran = PvpProfile.CreateNew();
        veteran.RecordResult(PvpOutcome.Win, 0, 1000, ranked: true);
        Assert.True(PvpProfile.Decode(veteran.Encode("steam-1"), "steam-1")!.TutorialMatchDone);

        Assert.NotNull(PvpConfig.Current.TutorialArmy);
        Assert.True(PvpConfig.Current.Bots.ContainsKey("tuto"));
    }

    [Fact]
    public void FriendlyChallenge_EarnsNoPoints()
    {
        var profile = PvpProfile.CreateNew();
        var points = profile.Points;
        var reward = profile.RecordResult(PvpOutcome.Win, kills: 4, opponentRating: 1000, ranked: false,
            PvpConfig.Current.FriendlyPointsFactor);
        Assert.Equal(0, reward.Points);
        Assert.Equal(points, profile.Points);
    }

    [Fact]
    public void RatingFor_EveryBotLevel_LandsInThatDivision()
    {
        var keys = PvpRanks.BotKeys().ToList();
        Assert.Equal(13, keys.Count);
        foreach (var key in keys)
        {
            var rating = PvpRanks.RatingFor(key);
            Assert.NotNull(rating);
            Assert.Equal(key, PvpRanks.BotKey(rating!.Value));
        }
        Assert.Null(PvpRanks.RatingFor("inconnu"));
    }

    [Fact]
    public void PlaytestSave_IsNotReadByTheFullGame()
    {
        // Playtest / Debug : fichier et clé historiques, inchangés (les saves existantes restent lisibles).
        Assert.Equal("pvp_steam-1.dat", PvpSaveSlot.FileName("steam-1", fullGame: false));
        Assert.Equal("steam-1", PvpSaveSlot.OwnerKey("steam-1", fullGame: false));
        Assert.NotEqual(PvpSaveSlot.FileName("steam-1", false), PvpSaveSlot.FileName("steam-1", true));

        var profile = PvpProfile.CreateNew();
        var playtest = profile.Encode(PvpSaveSlot.OwnerKey("steam-1", fullGame: false));
        Assert.NotNull(PvpProfile.Decode(playtest, PvpSaveSlot.OwnerKey("steam-1", fullGame: false)));
        // Un fichier playtest renommé en save du jeu complet est refusé (autre clé) : repart de zéro.
        Assert.Null(PvpProfile.Decode(playtest, PvpSaveSlot.OwnerKey("steam-1", fullGame: true)));

        var full = profile.Encode(PvpSaveSlot.OwnerKey("steam-1", fullGame: true));
        Assert.NotNull(PvpProfile.Decode(full, PvpSaveSlot.OwnerKey("steam-1", fullGame: true)));
        Assert.Null(PvpProfile.Decode(full, PvpSaveSlot.OwnerKey("steam-1", fullGame: false)));
    }

    [Fact]
    public void History_KeepsTheLast20_NewestFirst_AndSurvivesTheFile()
    {
        var profile = PvpProfile.CreateNew();
        var t0 = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 25; i++)
            profile.RecordResult(PvpOutcome.Win, i, 950, ranked: true, $"adv{i}", bot: i % 2 == 0, t0.AddMinutes(i));
        profile.RecordResult(PvpOutcome.Loss, 1, 950, ranked: false, "ami", bot: false, t0.AddMinutes(30));

        Assert.Equal(PvpProfile.HistorySize, profile.RankedHistory.Count);
        Assert.Equal("adv24", profile.RankedHistory[0].Opponent);   // la plus récente en tête
        Assert.Equal("adv5", profile.RankedHistory[^1].Opponent);   // les 5 plus anciennes sont parties
        Assert.True(profile.RankedHistory.Zip(profile.RankedHistory.Skip(1)).All(p => p.First.When > p.Second.When));
        Assert.Single(profile.FriendlyHistory);                        // les défis entre amis à part
        Assert.Equal(0, profile.FriendlyHistory[0].RatingDelta);

        var back = PvpProfile.Decode(profile.Encode("steam-1"), "steam-1")!;
        Assert.Equal(profile.RankedHistory, back.RankedHistory);
        Assert.Equal(profile.FriendlyHistory, back.FriendlyHistory);
        Assert.Equal(25, back.Streak);
        Assert.Equal(profile.BestRating, back.BestRating);
        Assert.Equal(profile.WeekDelta(t0), back.WeekDelta(t0));
    }

    [Fact]
    public void WinStreak_GivesBonusFromTheThirdWin()
    {
        var profile = PvpProfile.CreateNew();
        var day = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        int Win(string who)
        {
            var r = profile.RecordResult(PvpOutcome.Win, 0, profile.Rating, true, who, false, day);
            return r.RatingAfter - r.RatingBefore;
        }
        Assert.Equal(25, Win("a"));                                    // cotes égales : Elo seul
        Assert.Equal(25, Win("b"));
        Assert.Equal(25 + 15, Win("c"));                               // 3e victoire d'affilée : +15
        Assert.Equal(25 + 15, Win("d"));
        profile.RecordResult(PvpOutcome.Win, 0, profile.Rating, false, "ami", false, day);   // amical : hors série
        Assert.Equal(25 + 15, Win("e"));
        profile.RecordResult(PvpOutcome.Loss, 0, profile.Rating, true, "f", false, day);
        Assert.Equal(25, Win("g"));                                    // série cassée : on repart de 1
    }

    [Fact]
    public void Streak_BestRating_AndWeekDelta()
    {
        var profile = PvpProfile.CreateNew();
        var day = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        profile.RecordResult(PvpOutcome.Win, 0, 950, true, "a", false, day);
        profile.RecordResult(PvpOutcome.Win, 0, 950, true, "b", false, day);
        Assert.Equal(2, profile.Streak);
        var best = profile.Rating;
        profile.RecordResult(PvpOutcome.Loss, 0, 950, true, "c", false, day.AddDays(1));
        profile.RecordResult(PvpOutcome.Loss, 0, 950, true, "d", false, day.AddDays(1));
        Assert.Equal(-2, profile.Streak);
        Assert.Equal(best, profile.BestRating);                        // la meilleure cote ne redescend pas
        profile.RecordResult(PvpOutcome.Win, 0, 950, false, "ami", false, day.AddDays(1));
        Assert.Equal(-2, profile.Streak);                              // un défi entre amis ne compte pas
        profile.RecordResult(PvpOutcome.Draw, 0, profile.Rating, true, "e", false, day.AddDays(1));
        Assert.Equal(0, profile.Streak);                               // une nulle remet à zéro

        var all = profile.Rating - PvpConfig.Current.StartRating;
        Assert.Equal(all, profile.WeekDelta(day.AddDays(1)));
        var firstDay = profile.RankedHistory.Where(g => g.Opponent is "a" or "b").Sum(g => g.RatingDelta);
        Assert.Equal(all - firstDay, profile.WeekDelta(day.AddDays(7)));   // le 1er jour est sorti de la semaine
    }

    [Fact]
    public void OldProfileFile_IsMigrated()
    {
        var bytes = PvpProfile.EncodeLegacyV1("steam-1", points: 40, rating: 1012, games: 10, wins: 6);
        var profile = PvpProfile.Decode(bytes, "steam-1");
        Assert.NotNull(profile);
        Assert.Equal(1012, profile!.Rating);
        Assert.Equal(1012, profile.BestRating);
        Assert.Equal(6, profile.Wins);
        Assert.Empty(profile.RankedHistory);
        Assert.Equal(0, profile.Streak);
        Assert.Equal(0, profile.WeekDelta(DateTime.UtcNow));
        Assert.NotNull(PvpProfile.Decode(profile.Encode("steam-1"), "steam-1"));
    }

    [Fact]
    public void Tutorial_ShownOnce_SurvivesTheFile_AndOldFilesStillRead()
    {
        var fresh = PvpProfile.CreateNew();
        Assert.True(fresh.TutorialPending);                           // nouveau profil : tuto à montrer

        var old = PvpProfile.Decode(PvpProfile.EncodeLegacyV1("steam-1", 40, 1012, 10, 6), "steam-1")!;
        Assert.True(old.TutorialPending);                             // ancien fichier sans version : montré une fois

        fresh.MarkTutorialSeen();
        Assert.False(fresh.TutorialPending);
        var back = PvpProfile.Decode(fresh.Encode("steam-1"), "steam-1")!;
        Assert.False(back.TutorialPending);                           // relu : ne se rouvre plus
        Assert.Equal(PvpProfile.CurrentTutorialVersion, back.TutorialVersion);
    }

    [Fact]
    public void Tutorial_Navigation()
    {
        var t = new PvpTutorial();
        Assert.True(t.IsFirst);
        Assert.Equal(PvpTutorialZone.None, t.Zone);
        Assert.False(t.Previous());                                   // rien avant la première
        t.Next();
        Assert.Equal("profile", t.Id);
        Assert.True(t.Previous());
        Assert.True(t.IsFirst);
        for (var i = 0; i < PvpTutorial.Count - 1; i++)
            t.Next();
        Assert.True(t.IsLast);
        Assert.Equal("rules", t.Id);                                  // les règles du combat en dernier
        t.Next();
        Assert.True(t.Finished);
        t.Restart();
        Assert.Equal(0, t.Index);
        t.Skip();
        Assert.True(t.Finished);
        Assert.Equal(PvpTutorial.Count, PvpTutorial.Steps.Select(s => s.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(950, "bronze", 1, 50, 100)]    // Bronze II → Bronze I
    [InlineData(1050, "fer", 3, 50, 100)]      // Bronze I → Fer III
    [InlineData(1100, "fer", 2, 0, 100)]       // Fer III, tout juste
    [InlineData(1999, "master", 0, 99, 100)]   // Or I → Master
    [InlineData(500, "bronze", 2, 0, 100)]     // sous 800 : toujours Bronze III, barre vide
    public void DivisionProgress(int rating, string nextId, int nextDivision, int done, int needed)
    {
        var (next, d, n) = PvpRanks.Progress(rating);
        Assert.NotNull(next);
        Assert.Equal((nextId, nextDivision), (next!.Value.Id, next.Value.Division));
        Assert.Equal((done, needed), (d, n));
    }

    [Fact]
    public void Master_HasNoNextDivision_AndRangesComeFromConfig()
    {
        Assert.Null(PvpRanks.Progress(2300).Next);
        Assert.Equal(3, PvpRanks.FromRating(500).Division);
        Assert.Equal((0, (int?)1099), PvpRanks.Range(0));
        Assert.Equal((1100, (int?)1399), PvpRanks.Range(1));
        Assert.Null(PvpRanks.Range(PvpConfig.Current.Ranks.Count - 1).To);
    }

    [Fact]
    public void Chests_NeverGiveDuplicates_AndGreyOutWhenEmpty()
    {
        var profile = PvpProfile.CreateNew();
        var rng = new Random(3);
        var seen = new HashSet<string>();
        var total = profile.LockedEquipment().Count;
        for (var i = 0; i < total; i++)
        {
            profile.RecordResult(PvpOutcome.Win, 50, 1000, ranked: false);   // de quoi payer
            var item = profile.OpenChest(units: false, rng);
            Assert.NotNull(item);
            Assert.True(seen.Add(item!), $"doublon : {item}");
            Assert.True(profile.Owns(item!));
        }
        profile.RecordResult(PvpOutcome.Win, 50, 1000, ranked: false);
        Assert.Empty(profile.LockedEquipment());
        Assert.Null(profile.OpenChest(units: false, rng));
    }

    [Fact]
    public void StartingProfile_CanOpenOneChestOfEachKind()
    {
        var profile = PvpProfile.CreateNew();
        var rng = new Random(1);
        Assert.NotNull(profile.OpenChest(units: true, rng));
        Assert.NotNull(profile.OpenChest(units: false, rng));
        foreach (var id in PvpConfig.Current.StartingCommanders)
            Assert.True(profile.Owns(id));
        Assert.True(profile.Owns(Domaines.Dame.BaseClass.Asset));   // T1 toujours disponibles
    }

    [Fact]
    public void ObscuredInt_HidesTheValue_AndDetectsMemoryEdits()
    {
        var o = new ObscuredInt(1250);
        Assert.Equal(1250, o.Value);
        Assert.False(ObscuredInt.Tampered);

        // Simule Cheat Engine : on retouche les octets cachés.
        object boxed = o;
        var field = typeof(ObscuredInt).GetField("_hidden",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.NotEqual(1250, (int)field.GetValue(boxed)!);   // jamais la valeur en clair
        field.SetValue(boxed, (int)field.GetValue(boxed)! ^ 0x7FF);
        Assert.Equal(0, ((ObscuredInt)boxed).Value);
        Assert.True(ObscuredInt.Tampered);
    }

    // ── Bots ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TacticalBot_TakesTheKing_WhenItCan()
    {
        var match = PvpSetup.CreateMatch(false, localStarts: false, seed: 1);
        match.Place(new Cell(4, 4), PvpCatalog.SpawnCommander("commandant", Faction.Player)!);
        var soldat = new PvpBuildSlot(0, 0, Domaines.Dame.BaseClass.Asset, null);
        // Un soldat ennemi au contact du commandant affaibli : la mise à mort est le seul bon coup.
        match.Place(new Cell(4, 3), PvpCatalog.Spawn(soldat, Faction.Enemy)!);
        match.Place(new Cell(0, 0), PvpCatalog.SpawnCommander("commandant", Faction.Enemy)!);
        var king = match.UnitAt(new Cell(4, 4))!;
        while (king.Hp > 5)
            king.TakeDamage(5);

        var bot = new PvpBotController(new PvpBotDef { Accuracy = 1.0 }, 1);
        Assert.True(bot.TryGetAction(match, out var action));
        Assert.Equal(BattleAction.Attack(new Cell(4, 3), new Cell(4, 4)), action);
    }

    [Fact]
    public void Bot_PlaysAnObviousMoveFaster_ThanItsUsualThinkingTime()
    {
        // Réflexion normale de 30 s, coup évident sans délai : la prise du commandant part tout de suite.
        var def = new PvpBotDef { Accuracy = 1.0, MinDelaySeconds = 30, MaxDelaySeconds = 30, ObviousMinDelaySeconds = 0, ObviousMaxDelaySeconds = 0 };
        var match = PvpSetup.CreateMatch(false, localStarts: false, seed: 1);
        match.Place(new Cell(4, 4), PvpCatalog.SpawnCommander("commandant", Faction.Player)!);
        var soldat = new PvpBuildSlot(0, 0, Domaines.Dame.BaseClass.Asset, null);
        match.Place(new Cell(4, 3), PvpCatalog.Spawn(soldat, Faction.Enemy)!);
        match.Place(new Cell(0, 0), PvpCatalog.SpawnCommander("commandant", Faction.Enemy)!);
        var king = match.UnitAt(new Cell(4, 4))!;
        while (king.Hp > 5)
            king.TakeDamage(5);
        Assert.Equal(BattleAction.Attack(new Cell(4, 3), new Cell(4, 4)), PvpBotAi.Obvious(match, Faction.Enemy, new Random(1), out var certain));
        Assert.True(certain);
        Assert.True(new PvpBotController(def, 1).TryGetAction(match, out var action));
        Assert.Equal(BattleAction.Attack(new Cell(4, 3), new Cell(4, 4)), action);

        // Rien d'évident (pièces loin les unes des autres) : il prend son temps de réflexion habituel.
        var calm = PvpSetup.CreateMatch(false, localStarts: false, seed: 1);
        calm.Place(new Cell(4, 7), PvpCatalog.SpawnCommander("commandant", Faction.Player)!);
        calm.Place(new Cell(3, 1), PvpCatalog.Spawn(soldat, Faction.Enemy)!);
        calm.Place(new Cell(4, 0), PvpCatalog.SpawnCommander("commandant", Faction.Enemy)!);
        Assert.Null(PvpBotAi.Obvious(calm, Faction.Enemy, new Random(1), out _));
        Assert.False(new PvpBotController(def, 1).TryGetAction(calm, out _));
    }

    [Fact]
    public void BotLevel_FollowsTheDivision_FromBronze3ToMaster()
    {
        var bots = PvpConfig.Current.Bots;
        Assert.Same(bots["bronze-3"], PvpRanks.BotFor(0));
        Assert.Same(bots["master"], PvpRanks.BotFor(5000));
        // Monter d'une division change de niveau d'IA (jamais le même bot sur deux divisions voisines).
        var previous = PvpRanks.BotFor(0);
        var changes = 0;
        for (var rating = 0; rating <= 2200; rating += 10)
        {
            var bot = PvpRanks.BotFor(rating);
            if (!ReferenceEquals(bot, previous)) changes++;
            previous = bot;
        }
        Assert.Equal(12, changes);
    }

    [Fact]
    public void Bot_WaitsItsDisplayedThinkingTime_BeforePlaying()
    {
        var army = PvpConfig.Current.BotArmies[0];
        var match = PvpSetup.CreateMatch(false, localStarts: false, seed: 3);
        PvpSetup.PlaceArmy(match, army, Faction.Player);
        PvpSetup.PlaceArmy(match, army, Faction.Enemy);
        var log = new List<BattleAction>();
        var bot = new PvpBotController(new PvpBotDef { ForceAttack = true, MinDelaySeconds = 0.3, MaxDelaySeconds = 0.3 },
            1, new PvpForker(3, false, null, null, army, army), log);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(bot.TryGetAction(match, out _));   // il « réfléchit »
        BattleAction action;
        while (!bot.TryGetAction(match, out action))
            System.Threading.Thread.Sleep(10);
        Assert.True(clock.Elapsed.TotalSeconds >= 0.29, $"a joué au bout de {clock.Elapsed.TotalSeconds:0.00} s");
        Assert.NotEqual(MoveKind.Invalid, match.Apply(action));
    }

    /// <summary>Partie entre deux bots qui ne calculent pas, jouée comme dans la scène (règles de fin de coup
    /// comprises) ; renvoie le journal et rappelle <paramref name="atEachPly"/> avant chaque coup.</summary>
    private static List<BattleAction> PlayLive(PvpBuild a, PvpBuild b, int seed, PvpBotDef def, int plies,
        Action<Match, List<BattleAction>>? atEachPly = null)
    {
        var match = PvpSetup.CreateMatch(false, localStarts: seed % 2 == 0, seed);
        PvpSetup.PlaceArmy(match, a, Faction.Player);
        PvpSetup.PlaceArmy(match, b, Faction.Enemy);
        var rng = new Random(seed);
        var log = new List<BattleAction>();
        for (var ply = 0; ply < plies && !match.IsOver; ply++)
        {
            atEachPly?.Invoke(match, log);
            var actor = match.CurrentTurn;
            var action = PvpBotAi.Choose(match, actor, def, null, rng) ?? BattleAction.Pass;
            if (match.Apply(action) == MoveKind.Invalid)
            {
                action = BattleAction.Pass;
                match.Apply(action);
            }
            log.Add(action);
            PvpSetup.AfterAction(match, actor);
            PvpSetup.ResolvePending(match);
        }
        return log;
    }

    [Fact]
    public void Forker_CopiesTheLiveGameExactly_WithEveryBotArmy()
    {
        // Les bots qui calculent jouent sur des COPIES rejouées : elles doivent être identiques à la vraie partie
        // (aléa du moteur, équipements, traits, Séisme, effets en chaîne compris), avec toutes les armées des bots.
        var armies = PvpConfig.Current.BotArmies;
        for (var i = 0; i < armies.Count; i++)
        {
            var a = PvpCatalog.RandomEquipment(armies[i], new Random(i));   // équipements tirés comme en jeu
            var b = PvpCatalog.RandomEquipment(armies[(i * 7 + 3) % armies.Count], new Random(i + 500));
            var seed = 1000 + i;
            var forker = new PvpForker(seed, seed % 2 == 0, null, null, a, b);
            PlayLive(a, b, seed, new PvpBotDef { Caution = 0.5 }, 120, (live, log) =>
            {
                if (log.Count % 9 != 0)
                    return;
                var copy = forker.Fork(log, log.Count)!;
                Assert.Equal(live.ComputeStateHash(), copy.ComputeStateHash());
            });
        }
    }

    [Fact]
    public void SeededRandom_DrawsExactlyLikeSystemRandom_AndCopiesItsState()
    {
        foreach (var seed in new[] { 0, 1, 42, -7, 123456789, int.MaxValue, int.MinValue })
        {
            var reference = new Random(seed);
            var mine = new SeededRandom(seed);
            for (var i = 0; i < 2000; i++)
            {
                switch (i % 4)
                {
                    case 0: Assert.Equal(reference.Next(), mine.Next()); break;
                    case 1: Assert.Equal(reference.Next(i + 1), mine.Next(i + 1)); break;
                    case 2: Assert.Equal(reference.Next(-i, i * 3 + 1), mine.Next(-i, i * 3 + 1)); break;
                    default: Assert.Equal(reference.NextDouble(), mine.NextDouble()); break;
                }
            }
            var copy = mine.Clone();
            for (var i = 0; i < 100; i++)
                Assert.Equal(mine.Next(1000), copy.Next(1000));
        }
    }

    [Fact]
    public void MatchClone_PlaysExactlyLikeTheOriginal_WithEveryBotArmy()
    {
        // La recherche des bots joue sur des copies (Match.Clone) : une copie faite à n'importe quel moment doit
        // ensuite évoluer exactement comme la vraie partie (aléa, traits, équipements, effets en chaîne compris).
        var armies = PvpConfig.Current.BotArmies;
        for (var i = 0; i < armies.Count; i++)
        {
            var a = PvpCatalog.RandomEquipment(armies[i], new Random(i));
            var b = PvpCatalog.RandomEquipment(armies[(i * 11 + 5) % armies.Count], new Random(i + 900));
            var seed = 2000 + i;
            var copies = new List<(Match Copy, int From)>();
            PlayLive(a, b, seed, new PvpBotDef { Caution = 0.5 }, 140, (live, log) =>
            {
                // Chaque copie rejoue les coups joués depuis sa création : même empreinte que la vraie partie.
                foreach (var (copy, from) in copies)
                {
                    if (!copy.IsOver)
                        PvpForker.Step(copy, log[^1]);
                    Assert.Equal(live.ComputeStateHash(), copy.ComputeStateHash());
                }
                if (log.Count % 13 == 0 && !live.IsOver)
                    copies.Add((live.Clone(), log.Count));
            });
        }
    }

    [Fact]
    public void MatchClone_SharesNoMutableState()
    {
        var a = PvpCatalog.RandomEquipment(PvpConfig.Current.BotArmies[0], new Random(1));
        var forker = new PvpForker(3, true, null, null, a, PvpConfig.Current.BotArmies[1]);
        var match = forker.Start();
        var copy = match.Clone();
        var shared = new HashSet<string> { "_terrain", "_cover", "_lastSlide", "_brokenItem" };   // jamais modifiés
        foreach (var field in typeof(Match).GetFields(System.Reflection.BindingFlags.Instance
                     | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            if (field.FieldType.IsValueType || shared.Contains(field.Name))
                continue;
            var original = field.GetValue(match);
            if (original != null)
                Assert.False(ReferenceEquals(original, field.GetValue(copy)), $"Match.{field.Name} partagé par la copie");
        }
        foreach (var (cell, unit) in match.Units())
        {
            Assert.NotSame(unit, copy.UnitAt(cell));
            Assert.Equal(unit.Hp, copy.UnitAt(cell)!.Hp);
        }
    }

    [Fact]
    public void BronzeBot_AlwaysAttacks_WhenOneOfItsPiecesCan()
    {
        // Plancher de tous les rangs : même le plus maladroit attaque dès qu'il le peut (mais pas toujours la
        // meilleure attaque : avec killChance 0, il en prend une au hasard).
        var def = new PvpBotDef { ForceAttack = true, KillChance = 0, Caution = 0, Accuracy = 0.3 };
        var armies = PvpConfig.Current.BotArmies;
        var checks = 0;
        for (var i = 0; i < 10; i++)
            PlayLive(armies[i], armies[(i + 5) % armies.Count], 50 + i, def, 150, (live, _) =>
            {
                if (live.IsOver)
                    return;
                var side = live.CurrentTurn;
                var canAttack = PvpBotAi.ScoreActions(live, side, 1, new Random(1))
                    .Any(s => s.Action.Kind == BattleActionKind.Attack);
                if (!canAttack)
                    return;
                checks++;
                var choice = PvpBotAi.Choose(live, side, def, null, new Random(checks));
                Assert.Equal(BattleActionKind.Attack, choice!.Value.Kind);
            });
        Assert.True(checks > 50, $"seulement {checks} positions avec une attaque possible");
    }

    [Fact]
    public void ThinkingBots_AlwaysReturnALegalMove()
    {
        var armies = PvpConfig.Current.BotArmies;
        foreach (var rank in new[] { "argent-2", "or-2" })
        {
            var def = PvpConfig.Current.Bots[rank] with { Accuracy = 1 };
            var a = armies[1];
            var b = armies[2];
            var forker = new PvpForker(9, playerStarts: false, null, null, a, b);   // PlayLive : graine impaire = Enemy commence
            var plies = 0;
            PlayLive(a, b, 9, new PvpBotDef { Caution = 0.5 }, 60, (live, log) =>
            {
                if (log.Count % 15 != 0 || live.IsOver)
                    return;
                var action = PvpBotAi.Search(forker, log, live.CurrentTurn, def, PvpConfig.Current.BotWeights, new Random(2));
                Assert.NotNull(action);
                var copy = forker.Fork(log, log.Count)!;
                Assert.NotEqual(MoveKind.Invalid, copy.Apply(action!.Value));
                plies++;
            });
            Assert.True(plies >= 3);
        }
    }

    // ── Classement Steam ─────────────────────────────────────────────────────────
    [Fact]
    public void Formation_RoundTripsThroughTheSteamDetails_WithEveryBotArmy()
    {
        var rng = new Random(4);
        foreach (var source in PvpConfig.Current.BotArmies)
        {
            var army = PvpCatalog.RandomEquipment(source, rng);
            var details = PvpLeaderboard.Encode(12, 7, 2, army);
            Assert.True(details.Length <= PvpLeaderboard.MaxDetails);
            var (w, l, d, build) = PvpLeaderboard.Decode(details);
            Assert.Equal((12, 7, 2), (w, l, d));
            Assert.NotNull(build);
            Assert.Equal(army.CommanderId, build!.CommanderId);
            Assert.Equal(army.Slots.OrderBy(s => s.Column).ThenBy(s => s.Line),
                build.Slots.OrderBy(s => s.Column).ThenBy(s => s.Line));
        }
    }

    [Fact]
    public void Hashes_HaveNoCollision_InTheCurrentCatalog()
    {
        var classes = PvpCatalog.AllUnitClasses().Select(c => c.Class.Asset).Distinct().ToList();
        Assert.Equal(classes.Count, classes.Select(PvpLeaderboard.Hash24).Distinct().Count());
        var ids = PvpCatalog.AllEquipment().Select(e => e.Id).Concat(PvpCatalog.Commanders().Select(c => c.Id)).Distinct().ToList();
        Assert.Equal(ids.Count, ids.Select(PvpLeaderboard.Hash).Distinct().Count());
    }

    [Fact]
    public void UnknownOrOverBudgetFormation_IsNotCopied_ButTheRecordStays()
    {
        var details = PvpLeaderboard.Encode(3, 1, 0, PvpProfile.DefaultBuild());
        details[PvpLeaderboard.MaxDetails > 6 ? 5 : 0] = 0x7FFFFF00;   // classe inconnue
        var (w, l, _, build) = PvpLeaderboard.Decode(details);
        Assert.Equal((3, 1), (w, l));
        Assert.Null(build);
    }

    [Fact]
    public void Bot_CopiesPlayersOfItsOwnRank_OftenButNotAlways()
    {
        var army = PvpConfig.Current.BotArmies[0];
        var bronze2 = PvpRanks.BotKey(950);
        var entries = new List<PvpLeaderboardEntry>
        {
            new(1, "moi", 1, 950, 0, 0, 0, army),                          // le joueur local : jamais copié
            new(2, "autre rang", 2, 2100, 0, 0, 0, PvpConfig.Current.BotArmies[^1]),
        };
        var rng = new Random(1);
        Assert.Null(PvpLeaderboard.PickPlayerFormation(entries, bronze2, exceptPlayer: 1, rng));

        for (var i = 0; i < 6; i++)
            entries.Add(new((ulong)(10 + i), "joueur", 3 + i, 960, 1, 1, 0, PvpConfig.Current.BotArmies[(10 + i) % PvpConfig.Current.BotArmies.Count]));
        var copied = Enumerable.Range(0, 400).Count(_ => PvpLeaderboard.PickPlayerFormation(entries, bronze2, 1, rng) != null);
        Assert.InRange(copied, 260, 340);   // 75 % : en priorité, mais pas toujours
    }
}
