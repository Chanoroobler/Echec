using System;
using Steamworks;

namespace ChessArmy.Game.Net;

/// <summary>
/// POC multijoueur : disponibilité du mode et invitations d'amis reçues par Steam.
///
/// Une invitation acceptée arrive de deux façons : jeu déjà lancé → callback <see cref="GameLobbyJoinRequested_t"/> ;
/// jeu fermé → Steam le lance avec <c>+connect_lobby &lt;id&gt;</c> sur la ligne de commande. Dans les deux cas le
/// lobby est mis de côté (<see cref="TryTake"/>) : le menu principal ou l'écran multijoueur le rejoint dès qu'il
/// a la main (une invitation reçue en pleine campagne attend le retour au menu).
/// </summary>
public static class PvpInvites
{
    /// <summary>Mode multijoueur compilé dans cette build (PLAYTEST et Debug seulement pour le POC).</summary>
#if PLAYTEST || DEBUG
    public const bool Compiled = true;
#else
    public const bool Compiled = false;
#endif

    private static Callback<GameLobbyJoinRequested_t>? s_joinRequested;
    private static CSteamID s_pending = CSteamID.Nil;

    /// <summary>À appeler une fois Steam initialisé. Sans effet si le mode n'est pas compilé ou Steam absent.</summary>
    public static void Init()
    {
        if (!Compiled || !SteamService.IsActive)
            return;
        s_joinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => s_pending = r.m_steamIDLobby);
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(args[i + 1], out var id))
                s_pending = new CSteamID(id);
    }

    /// <summary>Lobby d'une invitation acceptée et pas encore rejointe (une seule fois).</summary>
    public static bool TryTake(out CSteamID lobby)
    {
        lobby = s_pending;
        if (lobby == CSteamID.Nil)
            return false;
        s_pending = CSteamID.Nil;
        return true;
    }
}
