using System;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Pvp;

namespace ChessArmy.Game.Net;

/// <summary>
/// Tout ce qu'il faut pour lancer un combat multi : l'adversaire (humain via <see cref="Session"/>, ou bot), les
/// deux armées, la graine (map + aléa), qui joue en premier et si la partie est classée. Construit par l'écran
/// de choix d'armée (<see cref="Scenes.PvpPrepScene"/>), consommé par <see cref="Scenes.GameplayScene"/>.
/// </summary>
public sealed class PvpMatchInfo
{
    /// <summary>Adversaire humain ; null contre un bot.</summary>
    public PvpSession? Session { get; init; }

    public bool IsBot => Session == null;

    /// <summary>Niveau du bot (contre un bot seulement).</summary>
    public PvpBotDef? Bot { get; init; }

    /// <summary>Joueur dont le bot a copié l'armée (null = armée par défaut du rang).</summary>
    public string? BotCopiedFrom { get; init; }

    /// <summary>Défi entre amis : rapporte des points, pas de cote.</summary>
    public bool Friendly { get; init; }

    public bool Ranked => !Friendly;

    public string OpponentName { get; init; } = "";
    public int OpponentRating { get; init; }
    public int LocalRating { get; init; }

    /// <summary>Graine de l'hôte : map, aléa du moteur et tirage du premier joueur.</summary>
    public int Seed { get; init; }

    /// <summary>Plateau retourné (invité d'une partie humaine).</summary>
    public bool Mirrored { get; init; }

    /// <summary>Le joueur local joue le premier coup.</summary>
    public bool LocalStarts { get; init; }

    public PvpBuild LocalBuild { get; init; } = new();
    public PvpBuild OpponentBuild { get; init; } = new();

    /// <summary>Nom affiché de l'adversaire : celui d'un bot porte « (BOT) », et l'armée copiée s'il y en a une.</summary>
    public string DisplayName => IsBot
        ? BotCopiedFrom == null ? $"{OpponentName} (BOT)" : $"{OpponentName} (BOT, {BotCopiedFrom})"
        : OpponentName;

    private static readonly string[] BotNames =
    {
        "Aldric", "Bérénice", "Corwin", "Daphné", "Edmond", "Fiora", "Gaspard", "Hélène", "Isidore", "Jeanne",
        "Kilian", "Léontine", "Mathis", "Noémie", "Octave", "Perrine",
    };

    /// <summary>Partie contre un bot du niveau de <paramref name="rating"/> (armée tirée parmi celles de son rang).</summary>
    public static PvpMatchInfo ForBot(PvpBuild localBuild, int rating, Random rng)
    {
        var config = PvpConfig.Current;
        var key = PvpRanks.BotKey(rating);
        var bot = config.Bots.TryGetValue(key, out var def) ? def
            : config.Bots.Values.FirstOrDefault() ?? new PvpBotDef(0.75, false, Array.Empty<PvpBuild>());
        var army = bot.Armies.Count > 0 ? bot.Armies[rng.Next(bot.Armies.Count)].Clone() : PvpProfile.DefaultBuild();
        return new PvpMatchInfo
        {
            Bot = bot,
            OpponentName = BotNames[rng.Next(BotNames.Length)],
            OpponentRating = rating,
            LocalRating = rating,
            Seed = rng.Next(),
            LocalStarts = rng.Next(2) == 0,
            LocalBuild = localBuild,
            OpponentBuild = army,
        };
    }

    /// <summary>Même partie, avec l'armée finalement choisie par le joueur local.</summary>
    public PvpMatchInfo WithLocalBuild(PvpBuild build) => new()
    {
        Session = Session, Bot = Bot, BotCopiedFrom = BotCopiedFrom, Friendly = Friendly,
        OpponentName = OpponentName, OpponentRating = OpponentRating, LocalRating = LocalRating,
        Seed = Seed, Mirrored = Mirrored, LocalStarts = LocalStarts,
        LocalBuild = build, OpponentBuild = OpponentBuild,
    };

    /// <summary>Partie contre l'adversaire de <paramref name="session"/>, une fois les deux armées connues.</summary>
    public static PvpMatchInfo ForSession(PvpSession session, PvpBuild localBuild) => new()
    {
        Session = session,
        Friendly = session.Friendly,
        OpponentName = session.OpponentName,
        OpponentRating = session.OpponentRating,
        LocalRating = session.LocalRating,
        Seed = session.Seed,
        Mirrored = !session.IsHost,
        LocalStarts = PvpSetup.HostStarts(session.Seed) == session.IsHost,
        LocalBuild = localBuild,
        OpponentBuild = session.OpponentBuild ?? new PvpBuild(),
    };
}
