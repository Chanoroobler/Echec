using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// Préparation multijoueur (lockstep) : un combat doit être ENTIÈREMENT rejouable depuis sa graine et la suite de
/// ses coups (<see cref="BattleAction"/> via <see cref="Match.Apply"/>), IA comprise, et l'empreinte d'état
/// (<see cref="Match.ComputeStateHash"/>) doit concorder coup après coup.
/// </summary>
public class LockstepTests
{
    private static Match Setup(int seed)
    {
        var match = new Match(8, 8, rng: new Random(seed));
        match.Place(new Cell(1, 7), Units.Of(Domaine.Dame, Faction.Player));
        match.Place(new Cell(4, 7), Units.Of(Domaine.Cavalier, Faction.Player));
        match.Place(new Cell(6, 6), Units.Of(Domaine.Dame, Faction.Player));
        match.Place(new Cell(2, 0), Units.Of(Domaine.Dame, Faction.Enemy));
        match.Place(new Cell(5, 1), Units.Of(Domaine.Cavalier, Faction.Enemy));
        match.Place(new Cell(7, 0), Units.Of(Domaine.Dame, Faction.Enemy));
        return match;
    }

    /// <summary>Coup « joueur » scripté et déterministe : première attaque possible, sinon premier déplacement.</summary>
    private static BattleAction ScriptedPlayerAction(Match match)
    {
        var buffer = new List<Cell>();
        foreach (var (cell, unit) in match.Units())
        {
            if (unit.Faction != Faction.Player)
                continue;
            match.AttackTargets(cell, buffer);
            if (buffer.Count > 0)
                return BattleAction.Attack(cell, buffer[0]);
        }
        foreach (var (cell, unit) in match.Units())
        {
            if (unit.Faction != Faction.Player)
                continue;
            match.LegalMoves(cell, buffer);
            if (buffer.Count > 0)
                return BattleAction.Move(cell, buffer[buffer.Count - 1]);
        }
        return BattleAction.Pass;
    }

    [Fact]
    public void SameSeedAndActions_ReplayToSameState()
    {
        const int seed = 1234;
        var live = Setup(seed);
        var ai = new AiBattleController(() => Array.Empty<Cell>(), EnemyAi.PerfectAccuracy);
        var log = new List<BattleAction>();
        var hashes = new List<ulong>();

        for (var turn = 0; turn < 40 && !live.IsOver; turn++)
        {
            BattleAction action;
            if (live.CurrentTurn == Faction.Player)
                action = ScriptedPlayerAction(live);
            else
                Assert.True(ai.TryGetAction(live, out action));
            Assert.NotEqual(MoveKind.Invalid, live.Apply(action));
            log.Add(action);
            hashes.Add(live.ComputeStateHash());
        }
        Assert.True(log.Count > 4, "le combat doit durer quelques coups pour que le test ait du sens");

        // Le second client ne reçoit QUE la graine et les coups (sérialisés), et doit retrouver chaque état.
        var replica = Setup(seed);
        var wire = new byte[BattleAction.SerializedSize];
        for (var i = 0; i < log.Count; i++)
        {
            log[i].Write(wire);
            Assert.NotEqual(MoveKind.Invalid, replica.Apply(BattleAction.Read(wire)));
            Assert.Equal(hashes[i], replica.ComputeStateHash());
        }
    }

    [Fact]
    public void Pvp_MirroredClients_StayInSync()
    {
        // Hôte et invité jouent chacun « en bas » : l'invité a le plateau retourné, et reçoit les coups de
        // l'hôte retournés (et inversement). L'empreinte, calculée dans le repère de l'hôte, doit concorder.
        var host = PvpSetup.CreateMatch(isHost: true, seed: 7);
        var guest = PvpSetup.CreateMatch(isHost: false, seed: 7);
        PvpSetup.PlaceArmies(host);
        PvpSetup.PlaceArmies(guest);
        Assert.Equal(host.ComputeStateHash(), guest.ComputeStateHash(mirrored: true));

        var played = 0;
        for (; played < 60 && !host.IsOver; played++)
        {
            var hostMoves = host.CurrentTurn == Faction.Player;
            Assert.Equal(hostMoves ? Faction.Enemy : Faction.Player, guest.CurrentTurn);
            var (mover, other) = hostMoves ? (host, guest) : (guest, host);
            var action = ScriptedPlayerAction(mover);
            Assert.NotEqual(MoveKind.Invalid, mover.Apply(action));
            Assert.NotEqual(MoveKind.Invalid, other.Apply(PvpSetup.Mirror(action)));
            Assert.Equal(host.ComputeStateHash(), guest.ComputeStateHash(mirrored: true));
        }
        Assert.True(played > 10);
        Assert.Equal(host.IsOver, guest.IsOver);
    }

    [Fact]
    public void Pvp_Army_IsChessLayout()
    {
        var match = PvpSetup.CreateMatch(isHost: true, seed: 1);
        PvpSetup.PlaceArmies(match);
        Assert.Equal(32, match.Units().Count());
        Assert.True(match.UnitAt(new Cell(4, 7))!.IsEssential);   // le roi du joueur local
        Assert.True(match.UnitAt(new Cell(4, 0))!.IsEssential);   // le roi adverse
        Assert.Equal("Archer", match.UnitAt(new Cell(3, 7))!.Class.Name);
        Assert.Equal(Faction.Enemy, PvpSetup.CreateMatch(isHost: false, seed: 1).CurrentTurn);
    }

    [Fact]
    public void StateHash_ChangesWhenStateChanges()
    {
        var a = Setup(1);
        var b = Setup(1);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());

        b.Apply(BattleAction.Pass);
        Assert.NotEqual(a.ComputeStateHash(), b.ComputeStateHash());
    }

    [Theory]
    [InlineData(BattleActionKind.Move, 0, 7, 3, 4)]
    [InlineData(BattleActionKind.Attack, 5, 1, 5, 6)]
    [InlineData(BattleActionKind.Pass, 0, 0, 0, 0)]
    public void BattleAction_RoundTrips(BattleActionKind kind, int fc, int fr, int tc, int tr)
    {
        var action = new BattleAction(kind, new Cell(fc, fr), new Cell(tc, tr));
        Span<byte> wire = stackalloc byte[BattleAction.SerializedSize];
        action.Write(wire);
        Assert.Equal(action, BattleAction.Read(wire));
    }
}
