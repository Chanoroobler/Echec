using System;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine.Localization;

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

    /// <summary>Défi entre amis : ni cote ni points (« friendlyPointsFactor » de pvp.json).</summary>
    public bool Friendly { get; init; }

    /// <summary>ENTRAÎNEMENT : bot d'un rang choisi (<see cref="TrainingKey"/>), sans cote, points réduits
    /// (« trainingPointsFactor »), hors du bilan V/D/N.</summary>
    public bool Training => TrainingKey != null;

    /// <summary>Division du bot d'entraînement (« or-2 », « master »…) ; null hors entraînement.</summary>
    public string? TrainingKey { get; init; }

    /// <summary>Partie TUTO : bot « tuto » à petite armée, bulles d'explication des règles, rien d'enregistré.</summary>
    public bool Tutorial { get; init; }

    public bool Ranked => !Friendly && !Test && !Training && !Tutorial;

    /// <summary>La partie compte dans le profil (points, historique) : ni test, ni tuto.</summary>
    public bool Recorded => !Test && !Tutorial;

    /// <summary>Part des points gagnée selon le mode (1 en partie classée).</summary>
    public float PointsFactor => Tutorial ? 0f
        : Training ? PvpConfig.Current.TrainingPointsFactor
        : Friendly ? PvpConfig.Current.FriendlyPointsFactor : 1f;

    /// <summary>Partie de TEST (raccourci Debug <c>-pvpbot</c>) : rien n'est enregistré, ni profil ni classement.</summary>
    public bool Test { get; init; }

    /// <summary>Réserve de la pendule imposée (secondes) ; null = celle de pvp.json.</summary>
    public float? ReserveSeconds { get; init; }

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
        ? BotCopiedFrom == null ? $"{OpponentName} ({Loc.T("pvp.bot_tag")})" : $"{OpponentName} ({Loc.T("pvp.bot_tag")}, {BotCopiedFrom})"
        : OpponentName;

    private static readonly string[] BotNames =
    {
        "Aldric", "Bérénice", "Corwin", "Daphné", "Edmond", "Fiora", "Gaspard", "Hélène", "Isidore", "Jeanne",
        "Kilian", "Léontine", "Mathis", "Noémie", "Octave", "Perrine",
    };

    /// <summary>Partie contre un bot du niveau de <paramref name="rating"/> (armée tirée parmi celles de son rang).</summary>
    public static PvpMatchInfo ForBot(PvpBuild localBuild, int rating, Random rng) =>
        ForBot(localBuild, rating, rating, null, rng);

    /// <summary>Clé de la case TUTO de l'entraînement (et du REJOUER d'une partie tuto).</summary>
    public const string TutorialKey = "tuto";

    /// <summary>Partie TUTO : le joueur commence, contre le bot « tuto » et sa petite armée (pvp.json « tutorialArmy »).</summary>
    public static PvpMatchInfo ForTutorial(PvpBuild localBuild, int localRating, Random rng)
    {
        var config = PvpConfig.Current;
        var bot = config.Bots.TryGetValue(TutorialKey, out var def) ? def
            : PvpBotDef.Default with { Accuracy = 0.15, Recklessness = 1, Caution = 0, Equipment = false };
        return new PvpMatchInfo
        {
            Bot = bot,
            Tutorial = true,
            TrainingKey = null,
            OpponentName = Loc.T("pvp.tutorial.bot_name"),
            OpponentRating = 0,
            LocalRating = localRating,
            Seed = rng.Next(),
            LocalStarts = true,
            ReserveSeconds = 3600,   // aucune pression du temps (la pendule basse n'arrive jamais) : on apprend
            LocalBuild = localBuild,
            OpponentBuild = (config.TutorialArmy ?? PvpProfile.DefaultBuild()).WithoutEquipment(),
        };
    }

    /// <summary>ENTRAÎNEMENT contre le bot de la division <paramref name="botKey"/> (cote du joueur inchangée).</summary>
    public static PvpMatchInfo ForTraining(PvpBuild localBuild, string botKey, int localRating, Random rng) =>
        ForBot(localBuild, PvpRanks.RatingFor(botKey) ?? localRating, localRating, botKey, rng);

    private static PvpMatchInfo ForBot(PvpBuild localBuild, int rating, int localRating, string? trainingKey, Random rng)
    {
        var config = PvpConfig.Current;
        var bot = PvpRanks.BotFor(rating);   // un niveau d'IA par division
        var pool = bot.Armies.Count > 0 ? bot.Armies : config.BotArmies;
        var army = pool.Count > 0 ? bot.ArmyFor(pool[rng.Next(pool.Count)], rng) : PvpProfile.DefaultBuild();   // équipements tirés à chaque partie (Argent+)
        // En priorité, la formation EXACTE d'un vrai joueur du même rang (classement Steam) ; peu de joueurs =
        // souvent une armée habituelle, pour ne pas croiser toujours la même.
        if (PvpLeaderboard.PickPlayerFormation(PvpLeaderboardService.Entries, PvpRanks.BotKey(rating),
                PvpLeaderboardService.LocalId, rng) is { } copied)
            army = copied;
        // Un nom = toujours le même style de jeu (agressif, prudent, garde du commandant…).
        var styles = config.BotStyles.Where(s => s.Names.Count > 0).ToList();
        var style = styles.Count > 0 ? styles[rng.Next(styles.Count)] : null;
        var name = style != null ? style.Names[rng.Next(style.Names.Count)] : BotNames[rng.Next(BotNames.Length)];
        return new PvpMatchInfo
        {
            Bot = bot with { Style = style },
            OpponentName = name,
            OpponentRating = rating,
            LocalRating = localRating,
            TrainingKey = trainingKey,
            Seed = rng.Next(),
            LocalStarts = rng.Next(2) == 0,
            LocalBuild = localBuild,
            OpponentBuild = army,
        };
    }

    /// <summary>Même partie, avec l'armée finalement choisie par le joueur local.</summary>
    public PvpMatchInfo WithLocalBuild(PvpBuild build) => new()
    {
        Session = Session, Bot = Bot, BotCopiedFrom = BotCopiedFrom, Friendly = Friendly, TrainingKey = TrainingKey,
        Tutorial = Tutorial,
        OpponentName = OpponentName, OpponentRating = OpponentRating, LocalRating = LocalRating,
        Seed = Seed, Mirrored = Mirrored, LocalStarts = LocalStarts, Test = Test, ReserveSeconds = ReserveSeconds,
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
