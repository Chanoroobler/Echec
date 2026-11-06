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

    /// <summary>
    /// Progression dans la division courante, vers la suivante : <c>Done</c> / <c>Needed</c> points (barre de MON
    /// RANG). La division la plus basse de Bronze démarre en vrai à 0, mais sa barre se compte comme les autres
    /// (sous la borne, elle reste vide). Master (sans division suivante) : <c>Next</c> nul.
    /// </summary>
    public static (PvpRank? Next, int Done, int Needed) Progress(int rating)
    {
        var ranks = PvpConfig.Current.Ranks;
        var current = FromRating(rating);
        var rank = ranks[current.TierIndex];
        int lower, upper;
        if (current.Division == 0)
        {
            if (current.TierIndex + 1 >= ranks.Count)
                return (null, 0, 0);
            lower = rank.From;
            upper = ranks[current.TierIndex + 1].From;
        }
        else
        {
            var end = current.TierIndex + 1 < ranks.Count ? ranks[current.TierIndex + 1].From : rank.From + rank.Span;
            var start = end - rank.Span;
            var step = Math.Max(1, rank.Span / rank.Divisions);
            var slice = rank.Divisions - current.Division;
            lower = start + slice * step;
            upper = slice == rank.Divisions - 1 ? end : lower + step;
        }
        var needed = Math.Max(1, upper - lower);
        return (FromRating(upper), Math.Clamp(rating - lower, 0, needed), needed);
    }

    /// <summary>Plage de cote d'un palier (pvp.json « ranks ») : de <c>From</c> à <c>To</c> inclus (null = sans fin).</summary>
    public static (int From, int? To) Range(int tierIndex)
    {
        var ranks = PvpConfig.Current.Ranks;
        return (ranks[tierIndex].From, tierIndex + 1 < ranks.Count ? ranks[tierIndex + 1].From - 1 : null);
    }

    /// <summary>Clé du niveau de bot d'un rang : « bronze-3 » (palier + division) ou « master » (sans division).</summary>
    public static string BotKey(PvpRank rank) => rank.Division > 0 ? $"{rank.Id}-{rank.Division}" : rank.Id;

    /// <summary>Niveau de bot à utiliser pour cette cote (clé de pvp.json « bots ») : un niveau PAR DIVISION, pour
    /// qu'on sente monter la force de l'adversaire à chaque palier.</summary>
    public static string BotKey(int rating) => BotKey(FromRating(rating));

    /// <summary>Toutes les clés de niveau de bot, du plus faible (Bronze 3) au plus fort (Master).</summary>
    public static System.Collections.Generic.IEnumerable<string> BotKeys()
    {
        foreach (var rank in PvpConfig.Current.Ranks)
        {
            if (rank.Divisions <= 1 || rank.Span <= 0)
                yield return rank.Id;
            else
                for (var d = rank.Divisions; d >= 1; d--)
                    yield return $"{rank.Id}-{d}";
        }
    }

    /// <summary>Niveau de bot pour cette cote : celui de la division, sinon celui du palier, sinon un défaut.</summary>
    public static PvpBotDef BotFor(int rating)
    {
        var config = PvpConfig.Current;
        var rank = FromRating(rating);
        return config.Bots.TryGetValue(BotKey(rank), out var def) ? def
            : config.Bots.TryGetValue(rank.Id, out var tier) ? tier
            : PvpBotDef.Default with { Armies = config.BotArmies };
    }

    /// <summary>
    /// Nouvelle cote après une partie (Elo) : <paramref name="score"/> = 1 victoire, 0.5 nulle, 0 défaite.
    /// </summary>
    public static int Updated(int rating, int opponentRating, double score)
    {
        var expected = 1.0 / (1.0 + Math.Pow(10, (opponentRating - rating) / 400.0));
        return Math.Max(0, (int)Math.Round(rating + PvpConfig.Current.EloK * (score - expected)));
    }
}
