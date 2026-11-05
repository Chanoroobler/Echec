using System;

namespace ChessArmy.Core.Pvp;

/// <summary>Rang affiché : identifiant du palier (« bronze »…) et division (3 = la plus basse, 0 = sans division).</summary>
public readonly record struct PvpRank(string Id, int Division, int TierIndex);

/// <summary>Cote (Elo) et rangs à divisions du ladder multi (cf. pvp.json « ranks »).</summary>
public static class PvpRanks
{
    /// <summary>Rang correspondant à une cote : le dernier palier atteint, découpé en divisions égales.</summary>
    public static PvpRank FromRating(int rating)
    {
        var ranks = PvpConfig.Current.Ranks;
        var index = 0;
        for (var i = 0; i < ranks.Count; i++)
            if (rating >= ranks[i].From)
                index = i;
        var rank = ranks[index];
        if (rank.Divisions <= 1 || rank.Span <= 0)
            return new PvpRank(rank.Id, 0, index);

        // Bronze démarre à 0 mais sa vraie plage est sous le palier suivant : on ancre les divisions sur la FIN.
        var end = index + 1 < ranks.Count ? ranks[index + 1].From : rank.From + rank.Span;
        var start = end - rank.Span;
        var step = Math.Max(1, rank.Span / rank.Divisions);
        var slice = Math.Clamp((rating - start) / step, 0, rank.Divisions - 1);
        return new PvpRank(rank.Id, rank.Divisions - slice, index);
    }

    /// <summary>Niveau de bot à utiliser pour cette cote (clé de pvp.json « bots »).</summary>
    public static string BotKey(int rating) => FromRating(rating).Id;

    /// <summary>
    /// Nouvelle cote après une partie (Elo) : <paramref name="score"/> = 1 victoire, 0.5 nulle, 0 défaite.
    /// </summary>
    public static int Updated(int rating, int opponentRating, double score)
    {
        var expected = 1.0 / (1.0 + Math.Pow(10, (opponentRating - rating) / 400.0));
        return Math.Max(0, (int)Math.Round(rating + PvpConfig.Current.EloK * (score - expected)));
    }
}
