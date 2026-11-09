using System;
using Steamworks;

namespace ChessArmy.Game.Net;

/// <summary>
/// Recherche d'un adversaire par les lobbies Steam.
///
/// RECHERCHE CLASSÉE (<see cref="Start"/>) : on cherche un lobby public avec une place libre et une cote PROCHE ;
/// l'écart toléré s'élargit avec le temps d'attente, si bien qu'avec beaucoup de joueurs on tombe sur quelqu'un
/// de son niveau et qu'avec peu on finit par trouver n'importe qui. Sans lobby à rejoindre, on crée le sien et on
/// attend (on est l'HÔTE). Deux hôtes qui s'attendent se retrouvent grâce au balayage régulier : celui dont le
/// lobby a le plus GRAND identifiant abandonne le sien et rejoint l'autre. C'est l'écran qui bascule sur un bot
/// au bout de <c>BotAfterSeconds</c>, ou <c>BotAfterBusySeconds</c> si d'autres joueurs sont en ligne
/// (<see cref="OthersOnline"/>).
///
/// DÉFI ENTRE AMIS (<see cref="StartFriendLobby"/> / <see cref="JoinInvite"/>) : lobby privé + invitation Steam.
///
/// Dès qu'un adversaire est trouvé, <see cref="Session"/> est prête ; c'est l'appelant qui en prend possession
/// (et qui la libère), le matchmaker ne la libère jamais.
/// </summary>
public sealed class PvpMatchmaker : IDisposable
{
    // Version du protocole dans le filtre : deux builds incompatibles ne se trouvent jamais.
    private const string ModeKey = "chessarmy_mode";
    private const string ModeValue = "pvp_2";
    private const string RatingKey = "rating";
    private const float RescanInterval = 4f;
    private const int BaseRatingGap = 150;
    private const int GapPerSecond = 20;

    /// <summary>Seul pair dont on accepte la session P2P entrante (l'adversaire de la partie en cours).</summary>
    internal static CSteamID AllowedPeer = CSteamID.Nil;
    private static Callback<SteamNetworkingMessagesSessionRequest_t>? s_sessionRequest;

    private readonly CallResult<LobbyMatchList_t> _listResult;
    private readonly CallResult<LobbyCreated_t> _createResult;
    private readonly CallResult<LobbyEnter_t> _enterResult;
    private readonly Callback<LobbyChatUpdate_t> _chatUpdate;
    private readonly CallResult<NumberOfCurrentPlayers_t> _playersResult;
    private readonly Random _seeds = new();
    private readonly int _rating;

    private CSteamID _ownLobby = CSteamID.Nil;   // lobby créé par nous, en attente d'un adversaire
    private bool _requestPending;
    private bool _joinPending;      // entrée dans le lobby d'un autre joueur en vol
    private bool _handlesDisposed;
    private float _rescanTimer;
    private bool _friendMode;
    private bool _disposed;

    /// <param name="rating">Cote du joueur local (sert au rapprochement et part dans la poignée de main).</param>
    public PvpMatchmaker(int rating)
    {
        _rating = rating;
        _listResult = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
        _createResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        _enterResult = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
        _chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
        _playersResult = CallResult<NumberOfCurrentPlayers_t>.Create(OnPlayerCount);
        s_sessionRequest ??= Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
    }

    /// <summary>Adversaire trouvé : la partie est prête à se lancer une fois <see cref="PvpSession.Started"/>.</summary>
    public PvpSession? Session { get; private set; }

    /// <summary>Échec définitif (Steam absent, création de lobby refusée, invitation impossible).</summary>
    public bool Failed { get; private set; }

    /// <summary>Une entrée dans le lobby d'un autre joueur est en vol : un adversaire est peut-être à une frame.</summary>
    public bool Joining => _joinPending;

    /// <summary>Temps passé à chercher (recherche classée).</summary>
    public float SearchTime { get; private set; }

    /// <summary>Écart de cote toléré en ce moment.</summary>
    private int RatingGap => BaseRatingGap + (int)(SearchTime * GapPerSecond);

    /// <summary>Recherche classée (lobby public).</summary>
    public void Start()
    {
        if (!SteamService.IsActive)
        {
            Failed = true;
            return;
        }
        _playersResult.Set(SteamUserStats.GetNumberOfCurrentPlayers());   // y a-t-il du monde ? (délai du bot)
        RequestList();
    }

    /// <summary>D'autres joueurs sont sur le jeu (compte Steam des joueurs en jeu, nous exclu, ou un lobby multi
    /// d'un autre joueur vu au balayage) : le bot attend alors plus longtemps (<see cref="PvpConfig.BotDelay"/>).</summary>
    public bool OthersOnline { get; private set; }

    private void OnPlayerCount(NumberOfCurrentPlayers_t result, bool ioFailure)
    {
        if (!_disposed && !ioFailure && result.m_bSuccess != 0 && result.m_cPlayers > 1)
            OthersOnline = true;   // le compte nous inclut
    }

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
        _joinPending = true;
    }

    /// <summary>À appeler chaque frame tant qu'aucun adversaire n'est trouvé : relance le balayage des lobbies.</summary>
    public void Update(float dt)
    {
        if (_disposed || Failed || _friendMode || Session != null)
            return;
        SearchTime += dt;
        if (_requestPending || _ownLobby == CSteamID.Nil)
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
        SteamMatchmaking.AddRequestLobbyListNearValueFilter(RatingKey, _rating);   // les plus proches d'abord
        SteamMatchmaking.AddRequestLobbyListResultCountFilter(20);
        _listResult.Set(SteamMatchmaking.RequestLobbyList());
        _requestPending = true;
        _rescanTimer = RescanInterval;
    }

    private bool RatingFits(CSteamID lobby) =>
        !int.TryParse(SteamMatchmaking.GetLobbyData(lobby, RatingKey), out var other)
        || Math.Abs(other - _rating) <= RatingGap;

    private void OnLobbyList(LobbyMatchList_t result, bool ioFailure)
    {
        _requestPending = false;
        if (_disposed)
        {
            DisposeHandles();
            return;
        }
        if (Session != null)
            return;
        if (!ioFailure)
        {
            for (var i = 0; i < result.m_nLobbiesMatching; i++)
            {
                var lobby = SteamMatchmaking.GetLobbyByIndex(i);
                if (lobby != _ownLobby)
                    OthersOnline = true;   // un autre joueur cherche (même trop loin en cote pour l'instant)
                if (lobby == _ownLobby || !RatingFits(lobby))
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
                _joinPending = true;
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
            DisposeHandles();
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
        SteamMatchmaking.SetLobbyData(_ownLobby, RatingKey, _rating.ToString());
        SteamMatchmaking.SetLobbyData(_ownLobby, ModeKey, ModeValue);   // visible des recherches à partir d'ici
        _rescanTimer = RescanInterval;
    }

    private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
    {
        _requestPending = false;
        _joinPending = false;
        var lobby = new CSteamID(result.m_ulSteamIDLobby);
        var ok = !ioFailure
                 && result.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
        if (_disposed)
        {
            // Recherche abandonnée (bot, annulation) pendant l'entrée : on ressort aussitôt, l'hôte en face voit
            // un départ et recherche à nouveau au lieu d'attendre une poignée de main qui ne viendra jamais.
            if (ok) SteamMatchmaking.LeaveLobby(lobby);
            DisposeHandles();
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
        Session = new PvpSession(lobby, owner, isHost: false, friendly: _friendMode, _rating);
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
        Session = new PvpSession(_ownLobby, guest, isHost: true, friendly: _friendMode, _rating);
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
        _chatUpdate.Dispose();
        // Création ou entrée de lobby en vol : on garde les résultats d'appel vivants pour que leur réponse quitte
        // le lobby obtenu (sinon on y resterait : lobby fantôme, hôte d'en face bloqué en poignée de main).
        if (!_requestPending)
            DisposeHandles();
    }

    private void DisposeHandles()
    {
        if (_handlesDisposed)
            return;
        _handlesDisposed = true;
        _listResult.Dispose();
        _createResult.Dispose();
        _enterResult.Dispose();
        _playersResult.Dispose();
    }
}
