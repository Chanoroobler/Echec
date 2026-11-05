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
        foreach (var rank in PvpConfig.Current.Ranks)
            Assert.True(PvpConfig.Current.Bots.ContainsKey(rank.Id), $"pas de bot pour le rang {rank.Id}");
    }

    [Fact]
    public void Build_OverBudget_IsRejected_AndKingCellIsReserved()
    {
        var build = new PvpBuild();
        var t3 = PvpCatalog.AllUnitClasses().First(c => c.Class.Tier == 3).Class.Asset;
        Assert.False(build.Set(PvpBuild.KingColumn, PvpBuild.BackLine, t3));
        for (var c = 0; c < PvpBuild.Columns; c++)
            build.Set(c, PvpBuild.FrontLine, t3);
        Assert.Equal("budget dépassé", PvpCatalog.Problem(build));

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
        Assert.True(bastion.HasTrait(Trait.Rempart));
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
        Assert.True(win > 0);
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

        var bot = new PvpBotController(new PvpBotDef(1.0, true, Array.Empty<PvpBuild>()), 1);
        Assert.True(bot.TryGetAction(match, out var action));
        Assert.Equal(BattleAction.Attack(new Cell(4, 3), new Cell(4, 4)), action);
    }
}
