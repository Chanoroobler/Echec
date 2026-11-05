using System;
using Steamworks;

namespace ChessArmy.Game.Net;

/// <summary>
/// Recherche automatique d'un adversaire (POC) par les lobbies Steam : on cherche un lobby PvP public avec une
/// place libre ; s'il y en a un on le rejoint (on devient l'INVITÉ), sinon on crée le sien et on attend (on est
/// l'HÔTE). Deux joueurs qui créent chacun leur lobby au même instant se retrouvent grâce à un nouveau balayage
/// régulier : celui dont le lobby a le plus GRAND identifiant abandonne le sien et rejoint l'autre.
///
/// Dès qu'un adversaire est trouvé, <see cref="Session"/> est prête ; c'est l'appelant qui en prend possession
/// (et qui la libère), le matchmaker ne la libère jamais.
/// </summary>
public sealed class PvpMatchmaker : IDisposable
{
    // Version du protocole dans le filtre : deux builds incompatibles ne se trouvent jamais.
    private const string ModeKey = "chessarmy_mode";
    private const string ModeValue = "pvp_poc_1";
    private const float RescanInterval = 4f;

    /// <summary>Seul pair dont on accepte la session P2P entrante (l'adversaire de la partie en cours).</summary>
    internal static CSteamID AllowedPeer = CSteamID.Nil;
    private static Callback<SteamNetworkingMessagesSessionRequest_t>? s_sessionRequest;

    private readonly CallResult<LobbyMatchList_t> _listResult;
    private readonly CallResult<LobbyCreated_t> _createResult;
    private readonly CallResult<LobbyEnter_t> _enterResult;
    private readonly Callback<LobbyChatUpdate_t> _chatUpdate;
    private readonly Random _seeds = new();

    private CSteamID _ownLobby = CSteamID.Nil;   // lobby créé par nous, en attente d'un adversaire
    private bool _requestPending;
    private float _rescanTimer;
    private bool _disposed;

    public PvpMatchmaker()
    {
        _listResult = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
        _createResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        _enterResult = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
        _chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
        s_sessionRequest ??= Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
    }

    /// <summary>Adversaire trouvé : la partie est prête à se lancer une fois <see cref="PvpSession.Started"/>.</summary>
    public PvpSession? Session { get; private set; }

    /// <summary>Échec définitif (Steam absent, création de lobby refusée) : on ne cherche plus.</summary>
    public bool Failed { get; private set; }

    /// <summary>Recherche automatique (lobby public).</summary>
    public void Start()
    {
        if (!SteamService.IsActive)
        {
            Failed = true;
            return;
        }
        RequestList();
    }

    // Défi entre amis : lobby PRIVÉ (seuls les invités le rejoignent), jamais de balayage public.
    private bool _friendMode;

    /// <summary>
    /// Défier un ami : crée un lobby privé puis ouvre la fenêtre d'invitation de l'overlay Steam. L'ami qui
    /// accepte rejoint ce lobby (<see cref="JoinInvite"/> de son côté) ; on est l'hôte.
    /// </summary>
    public void StartFriendLobby()
    {
        _friendMode = true;
        if (!SteamService.IsActive)
        {
            Failed = true;
            return;
        }
        _createResult.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, 2));
        _requestPending = true;
    }

    /// <summary>Rouvre la fenêtre d'invitation de l'overlay (ami oublié, fenêtre fermée trop vite).</summary>
    public void ReopenInvite()
    {
        if (_ownLobby != CSteamID.Nil)
            SteamFriends.ActivateGameOverlayInviteDialog(_ownLobby);
    }

    /// <summary>Rejoindre le lobby d'un ami dont on a accepté l'invitation (on est l'invité).</summary>
    public void JoinInvite(CSteamID lobby)
    {
        _friendMode = true;   // un échec ne relance PAS une recherche publique
        if (!SteamService.IsActive)
        {
            Failed = true;
            return;
        }
        _enterResult.Set(SteamMatchmaking.JoinLobby(lobby));
        _requestPending = true;
    }

    /// <summary>À appeler chaque frame tant qu'aucun adversaire n'est trouvé : relance le balayage des lobbies.</summary>
    public void Update(float dt)
    {
        if (_disposed || Failed || _friendMode || Session != null || _requestPending || _ownLobby == CSteamID.Nil)
            return;
        _rescanTimer -= dt;
        if (_rescanTimer <= 0f)
            RequestList();
    }

    /// <summary>L'adversaire trouvé n'a jamais répondu : on oublie cette session et on recherche.</summary>
    public void Restart()
    {
        Session?.Dispose();
        Session = null;
        _ownLobby = CSteamID.Nil;
        if (_friendMode)
            Failed = true;   // défi : pas de repli sur une recherche publique, l'écran propose de recommencer
        else
            RequestList();
    }

    private void RequestList()
    {
        SteamMatchmaking.AddRequestLobbyListStringFilter(ModeKey, ModeValue, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
        SteamMatchmaking.AddRequestLobbyListResultCountFilter(10);
        _listResult.Set(SteamMatchmaking.RequestLobbyList());
        _requestPending = true;
        _rescanTimer = RescanInterval;
    }

    private void OnLobbyList(LobbyMatchList_t result, bool ioFailure)
    {
        _requestPending = false;
        if (_disposed || Session != null)
            return;
        if (!ioFailure)
        {
            for (var i = 0; i < result.m_nLobbiesMatching; i++)
            {
                var lobby = SteamMatchmaking.GetLobbyByIndex(i);
                if (lobby == _ownLobby)
                    continue;
                if (_ownLobby != CSteamID.Nil)
                {
                    // Deux hôtes qui s'attendent : seul celui au plus GRAND lobby cède (sinon chacun rejoindrait
                    // l'autre et ils se croiseraient).
                    if (lobby.m_SteamID > _ownLobby.m_SteamID)
                        continue;
                    SteamMatchmaking.LeaveLobby(_ownLobby);
                    _ownLobby = CSteamID.Nil;
                }
                _enterResult.Set(SteamMatchmaking.JoinLobby(lobby));
                _requestPending = true;
                return;
            }
        }
        if (_ownLobby == CSteamID.Nil)
        {
            _createResult.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 2));
            _requestPending = true;
        }
    }

    private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
    {
        _requestPending = false;
        if (_disposed)
        {
            if (!ioFailure && result.m_eResult == EResult.k_EResultOK)
                SteamMatchmaking.LeaveLobby(new CSteamID(result.m_ulSteamIDLobby));
            return;
        }
        if (ioFailure || result.m_eResult != EResult.k_EResultOK)
        {
            Failed = true;
            return;
        }
        _ownLobby = new CSteamID(result.m_ulSteamIDLobby);
        if (_friendMode)
        {
            ReopenInvite();   // défi : on invite tout de suite un ami (le lobby privé n'est pas cherchable)
            return;
        }
        SteamMatchmaking.SetLobbyData(_ownLobby, ModeKey, ModeValue);   // visible des recherches à partir d'ici
        _rescanTimer = RescanInterval;
    }

    private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
    {
        _requestPending = false;
        var lobby = new CSteamID(result.m_ulSteamIDLobby);
        var ok = !ioFailure
                 && result.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
        if (_disposed)
        {
            if (ok) SteamMatchmaking.LeaveLobby(lobby);
            return;
        }
        var owner = ok ? SteamMatchmaking.GetLobbyOwner(lobby) : CSteamID.Nil;
        if (!ok || owner == SteamUser.GetSteamID() || owner == CSteamID.Nil)
        {
            // Lobby plein entre-temps, ou déserté par son hôte : on repart à la recherche (invitation : échec).
            if (ok) SteamMatchmaking.LeaveLobby(lobby);
            if (_friendMode)
            {
                Failed = true;
                return;
            }
            RequestList();
            return;
        }
        Session = new PvpSession(lobby, owner, isHost: false);
    }

    private void OnChatUpdate(LobbyChatUpdate_t update)
    {
        if (_disposed || Session != null || _ownLobby == CSteamID.Nil || update.m_ulSteamIDLobby != _ownLobby.m_SteamID)
            return;
        if ((update.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) == 0)
            return;
        var guest = new CSteamID(update.m_ulSteamIDUserChanged);
        if (guest == SteamUser.GetSteamID())
            return;
        SteamMatchmaking.SetLobbyJoinable(_ownLobby, false);   // complet : plus personne ne le trouve
        Session = new PvpSession(_ownLobby, guest, isHost: true);
        Session.PrepareAsHost(_seeds.Next());
        _ownLobby = CSteamID.Nil;   // désormais possédé par la session (elle le quittera)
    }

    private static void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
    {
        var who = request.m_identityRemote;
        if (AllowedPeer != CSteamID.Nil && who.GetSteamID() == AllowedPeer)
            SteamNetworkingMessages.AcceptSessionWithUser(ref who);
    }

    /// <summary>Arrête la recherche. Un lobby encore en attente est quitté ; la <see cref="Session"/> trouvée,
    /// elle, appartient à l'appelant.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownLobby != CSteamID.Nil)
            SteamMatchmaking.LeaveLobby(_ownLobby);
        _ownLobby = CSteamID.Nil;
        _listResult.Dispose();
        _createResult.Dispose();
        _enterResult.Dispose();
        _chatUpdate.Dispose();
    }
}
