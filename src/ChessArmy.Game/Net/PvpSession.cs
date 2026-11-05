using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ChessArmy.Core.Battle;
using Steamworks;

namespace ChessArmy.Game.Net;

/// <summary>
/// Partie PvP en cours avec UN adversaire Steam (POC) : échange des coups en P2P via SteamNetworkingMessages
/// (fiable, canal 0), présence suivie par le lobby Steam qui a servi à se trouver (cf. <see cref="PvpMatchmaker"/>).
///
/// Protocole (1er octet = type) :
///   <see cref="MsgHello"/>                             invité → hôte, répété jusqu'au départ : « je suis là »
///   <see cref="MsgStart"/> [graine int32]              hôte → invité : la partie commence
///   <see cref="MsgAction"/> [coup 5 o.][empreinte 8 o.] coup joué, dans le repère de l'ÉMETTEUR, + empreinte de
///                                                       l'état après coup, dans le repère de l'hôte
///   <see cref="MsgLeave"/>                             l'émetteur quitte la partie
/// </summary>
public sealed class PvpSession : IDisposable
{
    private const byte MsgStart = 1;
    private const byte MsgAction = 2;
    private const byte MsgLeave = 3;
    private const byte MsgHello = 4;
    private const int Channel = 0;
    private const int MaxMessageSize = 1 + BattleAction.SerializedSize + sizeof(ulong);

    private readonly CSteamID _lobby;
    private SteamNetworkingIdentity _remote;
    private readonly Queue<(BattleAction Action, ulong Hash)> _incoming = new();
    private readonly IntPtr[] _receiveBuffer = new IntPtr[16];
    private readonly byte[] _scratch = new byte[MaxMessageSize];
    // Tampon d'envoi ÉPINGLÉ une fois pour toutes : aucun tableau alloué par message.
    private readonly byte[] _sendBuffer = new byte[MaxMessageSize];
    private GCHandle _sendPin;
    private readonly Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;
    private readonly Callback<LobbyChatUpdate_t> _lobbyUpdate;
    private bool _disposed;

    internal PvpSession(CSteamID lobby, CSteamID opponent, bool isHost)
    {
        _lobby = lobby;
        Opponent = opponent;
        IsHost = isHost;
        _remote = new SteamNetworkingIdentity();
        _remote.SetSteamID(opponent);
        OpponentName = SteamFriends.GetFriendPersonaName(opponent);
        _sendPin = GCHandle.Alloc(_sendBuffer, GCHandleType.Pinned);
        PvpMatchmaker.AllowedPeer = opponent;   // seule session P2P acceptée (cf. PvpMatchmaker.OnSessionRequest)
        _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(_ => OpponentLeft = true);
        _lobbyUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyUpdate);
    }

    /// <summary>L'hôte (créateur du lobby) joue le premier coup et choisit la graine.</summary>
    public bool IsHost { get; }

    public CSteamID Opponent { get; }

    public string OpponentName { get; }

    /// <summary>Graine du combat : tirée par l'hôte, reçue par l'invité (<see cref="Started"/>).</summary>
    public int Seed { get; private set; }

    /// <summary>Vrai dès que la partie peut démarrer (immédiat pour l'hôte, à réception du départ pour l'invité).</summary>
    public bool Started { get; private set; }

    /// <summary>L'adversaire a quitté (abandon, fermeture du jeu, perte de connexion).</summary>
    public bool OpponentLeft { get; private set; }

    /// <summary>Hôte : graine du combat, envoyée à l'invité dès qu'il s'annonce (<see cref="MsgHello"/>).</summary>
    internal void PrepareAsHost(int seed) => Seed = seed;

    private void SendStart()
    {
        _sendBuffer[0] = MsgStart;
        BitConverter.TryWriteBytes(_sendBuffer.AsSpan(1), Seed);
        Send(1 + sizeof(int));
    }

    private const float HelloInterval = 1f;
    private float _helloTimer;

    /// <summary>
    /// À appeler chaque frame : relève les messages reçus. Poignée de main : l'invité RÉPÈTE « bonjour » chaque
    /// seconde jusqu'au départ, l'hôte répond à chacun par le départ (graine). Envoyer à l'autre accepte aussi
    /// implicitement sa session P2P : les deux sens s'ouvrent sans dépendre de l'ordre d'arrivée des callbacks.
    /// </summary>
    public void Update(float dt)
    {
        if (_disposed)
            return;
        if (!IsHost && !Started)
        {
            _helloTimer -= dt;
            if (_helloTimer <= 0f)
            {
                _helloTimer = HelloInterval;
                _sendBuffer[0] = MsgHello;
                Send(1);
            }
        }
        int count;
        while ((count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, _receiveBuffer, _receiveBuffer.Length)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(_receiveBuffer[i]);
                if (msg.m_identityPeer.GetSteamID() == Opponent && msg.m_cbSize > 0)
                {
                    var size = Math.Min(msg.m_cbSize, _scratch.Length);
                    Marshal.Copy(msg.m_pData, _scratch, 0, size);
                    Handle(_scratch.AsSpan(0, size));
                }
                SteamNetworkingMessage_t.Release(_receiveBuffer[i]);
            }
        }
    }

    private void Handle(ReadOnlySpan<byte> bytes)
    {
        switch (bytes[0])
        {
            case MsgStart when bytes.Length >= 1 + sizeof(int) && !IsHost:
                Seed = BitConverter.ToInt32(bytes.Slice(1, sizeof(int)));
                Started = true;
                break;
            case MsgAction when bytes.Length >= MaxMessageSize:
                var action = BattleAction.Read(bytes.Slice(1, BattleAction.SerializedSize));
                var hash = BitConverter.ToUInt64(bytes.Slice(1 + BattleAction.SerializedSize, sizeof(ulong)));
                _incoming.Enqueue((action, hash));
                break;
            case MsgHello when IsHost:
                Started = true;   // l'invité est là et nous entend : on lance (et on répond à chaque « bonjour »)
                SendStart();
                break;
            case MsgLeave:
                OpponentLeft = true;
                break;
        }
    }

    /// <summary>Envoie un coup local (repère local) et l'empreinte de l'état qui en résulte (repère de l'hôte).</summary>
    public void SendAction(in BattleAction action, ulong hashAfter)
    {
        _sendBuffer[0] = MsgAction;
        action.Write(_sendBuffer.AsSpan(1, BattleAction.SerializedSize));
        BitConverter.TryWriteBytes(_sendBuffer.AsSpan(1 + BattleAction.SerializedSize), hashAfter);
        Send(MaxMessageSize);
    }

    /// <summary>Coup adverse reçu (dans le repère de l'adversaire : à retourner avant de l'appliquer).</summary>
    public bool TryReceiveAction(out BattleAction action, out ulong hashAfter)
    {
        if (_incoming.TryDequeue(out var next))
        {
            (action, hashAfter) = next;
            return true;
        }
        action = default;
        hashAfter = 0;
        return false;
    }

    private void Send(int length)
    {
        if (_disposed)
            return;
        SteamNetworkingMessages.SendMessageToUser(ref _remote, _sendPin.AddrOfPinnedObject(), (uint)length,
            Constants.k_nSteamNetworkingSend_Reliable, Channel);
    }

    private void OnLobbyUpdate(LobbyChatUpdate_t update)
    {
        if (update.m_ulSteamIDLobby != _lobby.m_SteamID || update.m_ulSteamIDUserChanged != Opponent.m_SteamID)
            return;
        const uint gone = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft
                                 | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                                 | EChatMemberStateChange.k_EChatMemberStateChangeKicked
                                 | EChatMemberStateChange.k_EChatMemberStateChangeBanned);
        if ((update.m_rgfChatMemberStateChange & gone) != 0)
            OpponentLeft = true;
    }

    /// <summary>Quitte la partie : prévient l'adversaire, sort du lobby (l'adversaire le voit aussi par là).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        if (!OpponentLeft)
        {
            _sendBuffer[0] = MsgLeave;
            Send(1);
        }
        _disposed = true;
        _sessionFailed.Dispose();
        _lobbyUpdate.Dispose();
        SteamMatchmaking.LeaveLobby(_lobby);
        PvpMatchmaker.AllowedPeer = CSteamID.Nil;
        // La session P2P n'est PAS fermée ici : le message « je quitte » est peut-être encore en file. Steam la
        // referme seule après inactivité.
        _sendPin.Free();
    }
}
