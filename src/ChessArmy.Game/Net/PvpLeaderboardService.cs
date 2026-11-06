using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Pvp;
using Steamworks;

namespace ChessArmy.Game.Net;

/// <summary>
/// Classement multi chez Steam (tableau « pvp_cote », trié par cote décroissante). Après chaque partie CLASSÉE (jamais
/// un défi entre amis), le jeu envoie la cote du joueur et, dans les détails de l'entrée, son bilan et la formation
/// jouée (<see cref="PvpLeaderboard.Encode"/>). Trois lectures, une par onglet de l'écran CLASSEMENT :
///  • <see cref="Top"/> : Global, les <see cref="TopCount"/> premiers (MONDIAL, 8 par page) ;
///  • <see cref="Friends"/> : Friends, les amis Steam classés, soi compris (AMIS) ;
///  • <see cref="Around"/> : GlobalAroundUser ±50 (AUTOUR DE MOI en montre 3 au-dessus et 4 en dessous ; ce sont
///    aussi les joueurs de même niveau que les bots imitent, cf. <see cref="PvpMatchInfo.ForBot"/>).
/// Sans Steam : vide (<see cref="LoadState.NoSteam"/>). Tout est asynchrone : les écrans relisent l'état à chaque image.
/// </summary>
public static class PvpLeaderboardService
{
    public enum LoadState { NoSteam, Loading, Ready, Failed }

    private const string BoardName = "pvp_cote";
    public const int TopCount = 50;       // haut du classement (MONDIAL : 7 pages de 8)
    private const int AroundCount = 50;   // joueurs au-dessus et au-dessous de soi (mêmes rangs)
    private const int Downloads = 3;

    private static CallResult<LeaderboardFindResult_t>? s_find;
    private static CallResult<LeaderboardScoresDownloaded_t>? s_downloadTop;
    private static CallResult<LeaderboardScoresDownloaded_t>? s_downloadFriends;
    private static CallResult<LeaderboardScoresDownloaded_t>? s_downloadAround;
    private static CallResult<LeaderboardScoreUploaded_t>? s_upload;
    private static SteamLeaderboard_t s_board;
    private static bool s_found;
    private static bool s_finding;
    private static (int Rating, int[] Details)? s_pendingUpload;
    private static bool s_pendingRefresh;
    private static int s_answers;
    private static bool s_anyFailure;
    private static readonly HashSet<ulong> s_nameRequested = new();

    public static LoadState State { get; private set; } = LoadState.NoSteam;

    /// <summary>MONDIAL : les premiers du classement, du meilleur au moins bon.</summary>
    public static IReadOnlyList<PvpLeaderboardEntry> Top { get; private set; } = Array.Empty<PvpLeaderboardEntry>();

    /// <summary>AMIS : amis Steam ayant une cote, soi compris, triés.</summary>
    public static IReadOnlyList<PvpLeaderboardEntry> Friends { get; private set; } = Array.Empty<PvpLeaderboardEntry>();

    /// <summary>Joueurs autour de soi (±50), triés ; soi compris si on est classé.</summary>
    public static IReadOnlyList<PvpLeaderboardEntry> Around { get; private set; } = Array.Empty<PvpLeaderboardEntry>();

    /// <summary>Toutes les entrées connues sans doublon (formations copiées par les bots).</summary>
    public static IReadOnlyList<PvpLeaderboardEntry> Entries { get; private set; } = Array.Empty<PvpLeaderboardEntry>();

    /// <summary>Entrée du joueur local (place mondiale), null s'il n'est pas classé ou pas encore lu.</summary>
    public static PvpLeaderboardEntry? Mine { get; private set; }

    /// <summary>Identifiant Steam du joueur local (0 sans Steam).</summary>
    public static ulong LocalId => SteamService.IsActive ? SteamUser.GetSteamID().m_SteamID : 0;

    /// <summary>Relit le classement (écran Classement, entrée dans le multi, après un envoi).</summary>
    public static void Refresh()
    {
#if DEBUG
        if (s_fake)
            return;
#endif
        if (!SteamService.IsActive)
        {
            State = LoadState.NoSteam;
            return;
        }
        State = LoadState.Loading;
        if (!s_found)
        {
            s_pendingRefresh = true;
            Find();
            return;
        }
        s_answers = 0;
        s_anyFailure = false;
        s_downloadTop ??= CallResult<LeaderboardScoresDownloaded_t>.Create((r, f) => OnDownloaded(r, f, list => Top = list));
        s_downloadFriends ??= CallResult<LeaderboardScoresDownloaded_t>.Create((r, f) => OnDownloaded(r, f, list => Friends = list));
        s_downloadAround ??= CallResult<LeaderboardScoresDownloaded_t>.Create((r, f) => OnDownloaded(r, f, list => Around = list));
        s_downloadTop.Set(SteamUserStats.DownloadLeaderboardEntries(s_board,
            ELeaderboardDataRequest.k_ELeaderboardDataRequestGlobal, 1, TopCount));
        s_downloadFriends.Set(SteamUserStats.DownloadLeaderboardEntries(s_board,
            ELeaderboardDataRequest.k_ELeaderboardDataRequestFriends, 0, 0));
        s_downloadAround.Set(SteamUserStats.DownloadLeaderboardEntries(s_board,
            ELeaderboardDataRequest.k_ELeaderboardDataRequestGlobalAroundUser, -AroundCount, AroundCount));
    }

    /// <summary>Après une partie classée : cote (score, remplace toujours l'ancienne : elle peut baisser), bilan et
    /// formation jouée.</summary>
    public static void Upload(PvpProfile profile, PvpBuild build)
    {
        if (!SteamService.IsActive)
            return;
        s_pendingUpload = (profile.Rating, PvpLeaderboard.Encode(profile.Wins, profile.Losses, profile.Draws, build));
        if (s_found)
            SendUpload();
        else
            Find();
    }

#if DEBUG
    private static bool s_fake;
    private static ulong s_fakeLocal;

    /// <summary>Captures d'écran de développement : classement factice (sans Steam), <paramref name="local"/> = soi.</summary>
    internal static void UseFake(IReadOnlyList<PvpLeaderboardEntry> top, IReadOnlyList<PvpLeaderboardEntry> friends,
        IReadOnlyList<PvpLeaderboardEntry> around, ulong local)
    {
        s_fake = true;
        s_fakeLocal = local;
        Top = top;
        Friends = friends;
        Around = around;
        Publish();
        State = LoadState.Ready;
    }

    /// <summary>Captures : simule l'absence de Steam.</summary>
    internal static void UseOffline()
    {
        s_fake = true;
        Top = Friends = Around = Entries = Array.Empty<PvpLeaderboardEntry>();
        Mine = null;
        State = LoadState.NoSteam;
    }
#endif

    /// <summary>Vrai si cette entrée est celle du joueur local.</summary>
    public static bool IsLocal(PvpLeaderboardEntry entry)
    {
#if DEBUG
        if (s_fake)
            return entry.PlayerId == s_fakeLocal;
#endif
        return SteamService.IsActive && entry.PlayerId == LocalId;
    }

    /// <summary>Nom Steam d'un joueur (demandé à Steam la première fois s'il n'est pas un ami ; « ? » en attendant).</summary>
    public static string NameOf(PvpLeaderboardEntry entry)
    {
        if (!SteamService.IsActive || entry.Name != "?")
            return entry.Name;
        var id = new CSteamID(entry.PlayerId);
        var name = SteamFriends.GetFriendPersonaName(id);
        if (string.IsNullOrEmpty(name) || name == "[unknown]")
        {
            if (s_nameRequested.Add(entry.PlayerId))
                SteamFriends.RequestUserInformation(id, true);
            return "?";
        }
        return name;
    }

    private static void Find()
    {
        if (s_finding)
            return;
        s_finding = true;
        s_find ??= CallResult<LeaderboardFindResult_t>.Create(OnFound);
        s_find.Set(SteamUserStats.FindOrCreateLeaderboard(BoardName,
            ELeaderboardSortMethod.k_ELeaderboardSortMethodDescending, ELeaderboardDisplayType.k_ELeaderboardDisplayTypeNumeric));
    }

    private static void OnFound(LeaderboardFindResult_t result, bool ioFailure)
    {
        s_finding = false;
        if (ioFailure || result.m_bLeaderboardFound == 0)
        {
            State = LoadState.Failed;
            return;
        }
        s_board = result.m_hSteamLeaderboard;
        s_found = true;
        if (s_pendingUpload != null)
            SendUpload();
        else if (s_pendingRefresh)
            Refresh();
        s_pendingRefresh = false;
    }

    private static void SendUpload()
    {
        if (s_pendingUpload is not { } up)
            return;
        s_pendingUpload = null;
        s_upload ??= CallResult<LeaderboardScoreUploaded_t>.Create((_, _) => Refresh());   // relu avec sa nouvelle cote
        s_upload.Set(SteamUserStats.UploadLeaderboardScore(s_board,
            ELeaderboardUploadScoreMethod.k_ELeaderboardUploadScoreMethodForceUpdate, up.Rating, up.Details, up.Details.Length));
    }

    private static void OnDownloaded(LeaderboardScoresDownloaded_t result, bool ioFailure,
        Action<IReadOnlyList<PvpLeaderboardEntry>> store)
    {
        var list = new List<PvpLeaderboardEntry>();
        if (!ioFailure)
        {
            var details = new int[PvpLeaderboard.MaxDetails];
            for (var i = 0; i < result.m_cEntryCount; i++)
            {
                if (!SteamUserStats.GetDownloadedLeaderboardEntry(result.m_hSteamLeaderboardEntries, i, out var e, details, details.Length))
                    continue;
                var (w, l, d, build) = PvpLeaderboard.Decode(details.Take(Math.Min(e.m_cDetails, details.Length)).ToArray());
                list.Add(new PvpLeaderboardEntry(e.m_steamIDUser.m_SteamID, "?", e.m_nGlobalRank, e.m_nScore, w, l, d, build));
            }
        }
        s_anyFailure |= ioFailure;
        store(list.OrderBy(e => e.GlobalRank).ToList());
        if (++s_answers < Downloads)
            return;   // on attend les trois lectures
        Publish();
        State = s_anyFailure && Entries.Count == 0 ? LoadState.Failed : LoadState.Ready;
    }

    /// <summary>Union des trois listes (sans doublon) et entrée du joueur local.</summary>
    private static void Publish()
    {
        var all = new Dictionary<ulong, PvpLeaderboardEntry>();
        foreach (var e in Top.Concat(Friends).Concat(Around))
            all[e.PlayerId] = e;
        Entries = all.Values.OrderBy(e => e.GlobalRank).ToList();
        Mine = Entries.FirstOrDefault(IsLocal);
    }
}
