#if DEBUG
using System;
using System.Globalization;
using System.Linq;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Game.Net;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Raccourci de TEST (Debug) : <c>ChessArmy.Game -pvpbot [minutes]</c> ouvre directement un combat multi contre un
/// bot de ton rang, avec ton armée sélectionnée. Le BOT joue le premier coup, pendule de <c>minutes</c> (5 par
/// défaut). Partie non enregistrée : ni points, ni cote, ni classement.
/// </summary>
internal static class PvpBotTest
{
    public static bool TryStart(GameContext ctx)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => string.Equals(a, "-pvpbot", StringComparison.OrdinalIgnoreCase));
        if (i < 0)
            return false;
        var minutes = i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0
            ? m : 5f;

        var profile = PvpProfileStore.Current;
        var build = Enumerable.Range(0, PvpProfile.BuildSlots)
            .Prepend(Math.Clamp(profile.SelectedBuild, 0, PvpProfile.BuildSlots - 1))
            .Select(s => profile.Builds[s])
            .FirstOrDefault(b => b != null && PvpCatalog.Problem(b, profile.Owns) is null)
            ?? PvpProfile.DefaultBuild();

        var bot = PvpMatchInfo.ForBot(build, profile.Rating, new Random());
        var info = new PvpMatchInfo
        {
            Bot = bot.Bot, BotCopiedFrom = bot.BotCopiedFrom, OpponentName = bot.OpponentName,
            OpponentRating = bot.OpponentRating, LocalRating = bot.LocalRating, Seed = bot.Seed,
            LocalStarts = false,   // le bot commence
            LocalBuild = build, OpponentBuild = bot.OpponentBuild,
            Test = true, ReserveSeconds = minutes * 60f,
        };
        Console.WriteLine($"pvpbot : {info.OpponentName} ({PvpRanks.BotKey(profile.Rating)}), pendule {minutes} min, le bot commence");
        ctx.Scenes.Change(new GameplayScene(ctx, info));
        return true;
    }
}
#endif
