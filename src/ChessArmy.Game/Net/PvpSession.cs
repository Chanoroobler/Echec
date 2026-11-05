using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Pvp;
using Steamworks;

namespace ChessArmy.Game.Net;

/// <summary>
/// Partie PvP en cours avec UN adversaire Steam : échange P2P via SteamNetworkingMessages (fiable, canal 0),
/// présence suivie par le lobby Steam qui a servi à se trouver (cf. <see cref="PvpMatchmaker"/>).
///
/// Protocole (1er octet = type) :
///   <see cref="MsgHello"/> [cote int32]                       invité → hôte, répété jusqu'au départ
///   <see cref="MsgStart"/> [graine int32][cote int32]          hôte → invité : la partie commence
///   <see cref="MsgBuild"/> [JSON UTF-8]                        l'armée choisie (après la map, avant le combat)
///   <see cref="MsgAction"/> [coup 5][empreinte 8][réserve ms 4] un coup, dans le repère de l'ÉMETTEUR
///   <see cref="MsgDrawOffer"/> / <see cref="MsgDrawReply"/> [0|1]  proposition d'égalité et réponse
///   <see cref="MsgHeartbeat"/>                                 « toujours là », chaque seconde (déconnexion)
///   <see cref="MsgLeave"/>                                     l'émetteur quitte (abandon)
/// </summary>
public sealed class PvpSession : IDisposable
{
    private const byte MsgStart = 1;
    private const byte MsgAction = 2;
    private const byte MsgLeave = 3;
    private const byte MsgHello = 4;
    private const byte MsgBuild = 5;
    private const byte MsgHeartbeat = 6;
    private const byte MsgDrawOffer = 8;
    private const byte MsgDrawReply = 9;
    private const int Channel = 0;
    private const int BufferSize = 8192;
    private const int ActionSize = 1 + BattleAction.SerializedSize + sizeof(ulong) + sizeof(int);
    private const float HelloInterval = 1f;
    private const float HeartbeatInterval = 1f;

    private readonly CSteamID _lobby;
    private SteamNetworkingIdentity _remote;
    private readonly Queue<(BattleAction Action, ulong Hash, float Reserve)> _incoming = new();
    private readonly IntPtr[] _receiveBuffer = new IntPtr[16];
    private readonly byte[] _scratch = new byte[BufferSize];
    // Tampon d'envoi ÉPINGLÉ une fois pour toutes : aucun tableau alloué par message.
    private readonly byte[] _sendBuffer = new byte[BufferSize];
    private GCHandle _sendPin;
    private readonly Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;
    private readonly Callback<LobbyChatUpdate_t> _lobbyUpdate;
    private float _helloTimer;
    private float _heartbeatTimer;
    private bool _disposed;

    internal PvpSession(CSteamID lobby, CSteamID opponent, bool isHost, bool friendly, int localRating)
    {
        _lobby = lobby;
        Opponent = opponent;
        IsHost = isHost;
        Friendly = friendly;
        LocalRating = localRating;
        _remote = new SteamNetworkingIdentity();
        _remote.SetSteamID(opponent);
        OpponentName = SteamFriends.GetFriendPersonaName(opponent);
        _sendPin = GCHandle.Alloc(_sendBuffer, GCHandleType.Pinned);
        PvpMatchmaker.AllowedPeer = opponent;   // seule session P2P acceptée (cf. PvpMatchmaker.OnSessionRequest)
        _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(_ => Silence = Math.Max(Silence, 5f));
        _lobbyUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyUpdate);
    }

    /// <summary>L'hôte (créateur du lobby) tire la graine ; le premier joueur en dépend (cf. PvpSetup.HostStarts).</summary>
    public bool IsHost { get; }

    /// <summary>Défi entre amis : rapporte des points mais n'est pas classé.</summary>
    public bool Friendly { get; }

    public CSteamID Opponent { get; }
    public string OpponentName { get; }
    public int LocalRating { get; }
    public int OpponentRating { get; private set; }

    /// <summary>Graine de la partie : tirée par l'hôte, reçue par l'invité (<see cref="Started"/>).</summary>
    public int Seed { get; private set; }

    /// <summary>Les deux joueurs se sont salués : on peut passer au choix des armées.</summary>
    public bool Started { get; private set; }

    /// <summary>L'adversaire a QUITTÉ (abandon volontaire, menu) : victoire immédiate.</summary>
    public bool OpponentLeft { get; private set; }

    /// <summary>Secondes écoulées depuis le dernier message de l'adversaire (déconnexion au-delà de quelques s).</summary>
    public float Silence { get; private set; }

    /// <summary>Armée de l'adversaire, une fois reçue.</summary>
    public PvpBuild? OpponentBuild { get; private set; }

    /// <summary>L'adversaire propose l'égalité (en attente de notre réponse).</summary>
    public bool DrawOffered { get; set; }

    /// <summary>Réponse de l'adversaire à NOTRE proposition d'égalité (null = pas encore répondu).</summary>
    public bool? DrawAnswer { get; set; }

    /// <summary>Hôte : graine de la partie, envoyée à l'invité dès qu'il s'annonce (<see cref="MsgHello"/>).</summary>
    internal void PrepareAsHost(int seed) => Seed = seed;

    /// <summary>À appeler chaque frame : relève les messages et entretient la présence.</summary>
    public void Update(float dt)
    {
        if (_disposed)
            return;
        Silence += dt;
        if (!IsHost && !Started)
        {
            _helloTimer -= dt;
            if (_helloTimer <= 0f)
            {
                _helloTimer = HelloInterval;
                _sendBuffer[0] = MsgHello;
                BitConverter.TryWriteBytes(_sendBuffer.AsSpan(1), LocalRating);
                Send(1 + sizeof(int));
            }
        }
        else if (Started)
        {
            _heartbeatTimer -= dt;
            if (_heartbeatTimer <= 0f)
            {
                _heartbeatTimer = HeartbeatInterval;
                _sendBuffer[0] = MsgHeartbeat;
                Send(1);
            }
        }

        int count;
        while ((count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, _receiveBuffer, _receiveBuffer.Length)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(_receiveBuffer[i]);
                if (msg.m_identityPeer.GetSteamID() == Opponent && msg.m_cbSize > 0 && msg.m_cbSize <= BufferSize)
                {
                    Marshal.Copy(msg.m_pData, _scratch, 0, msg.m_cbSize);
                    Silence = 0f;
                    Handle(_scratch.AsSpan(0, msg.m_cbSize));
                }
                SteamNetworkingMessage_t.Release(_receiveBuffer[i]);
            }
        }
    }

    private void Handle(ReadOnlySpan<byte> bytes)
    {
        switch (bytes[0])
        {
            case MsgHello when IsHost && bytes.Length >= 1 + sizeof(int):
                OpponentRating = BitConverter.ToInt32(bytes.Slice(1, sizeof(int)));
                Started = true;   // l'invité est là et nous entend : on répond par le départ (à chaque « bonjour »)
                _sendBuffer[0] = MsgStart;
                BitConverter.TryWriteBytes(_sendBuffer.AsSpan(1), Seed);
                BitConverter.TryWriteBytes(_sendBuffer.AsSpan(1 + sizeof(int)), LocalRating);
                Send(1 + 2 * sizeof(int));
                break;
            case MsgStart when !IsHost && bytes.Length >= 1 + 2 * sizeof(int):
                Seed = BitConverter.ToInt32(bytes.Slice(1, sizeof(int)));
                OpponentRating = BitConverter.ToInt32(bytes.Slice(1 + sizeof(int), sizeof(int)));
                Started = true;
                break;
            case MsgBuild:
                OpponentBuild = PvpBuild.FromJson(Encoding.UTF8.GetString(bytes[1..])) ?? new PvpBuild();
                break;
            case MsgAction when bytes.Length >= ActionSize:
                var action = BattleAction.Read(bytes.Slice(1, BattleAction.SerializedSize));
                var hash = BitConverter.ToUInt64(bytes.Slice(1 + BattleAction.SerializedSize, sizeof(ulong)));
                var reserveMs = BitConverter.ToInt32(bytes.Slice(1 + BattleAction.SerializedSize + sizeof(ulong), sizeof(int)));
                _incoming.Enqueue((action, hash, reserveMs / 1000f));
                break;
            case MsgDrawOffer:
                DrawOffered = true;
                break;
            case MsgDrawReply when bytes.Length >= 2:
                DrawAnswer = bytes[1] != 0;
                break;
            case MsgLeave:
                OpponentLeft = true;
                break;
        }
    }

    /// <summary>Envoie l'armée choisie pour cette partie.</summary>
    public void SendBuild(PvpBuild build)
    {
        _sendBuffer[0] = MsgBuild;
        var json = build.ToJson();
        var length = Encoding.UTF8.GetBytes(json, 0, json.Length, _sendBuffer, 1);
        Send(1 + length);
    }

    /// <summary>Envoie un coup local (repère local), l'empreinte de l'état qui en résulte (repère de l'hôte) et
    /// la réserve de temps restante de l'émetteur.</summary>
    public void SendAction(in BattleAction action, ulong hashAfter, float reserveSeconds)
    {
        _sendBuffer[0] = MsgAction;
        action.Write(_sendBuffer.AsSpan(1, BattleAction.SerializedSize));
        BitConverter.TryWriteBytes(_sendBuffer.AsSpan(1 + BattleAction.SerializedSize), hashAfter);
        BitConverter.TryWriteBytes(_sendBuffer.AsSpan(1 + BattleAction.SerializedSize + sizeof(ulong)),
            (int)(reserveSeconds * 1000));
        Send(ActionSize);
    }

    public void SendDrawOffer()
    {
        DrawAnswer = null;
        _sendBuffer[0] = MsgDrawOffer;
        Send(1);
    }

    public void SendDrawReply(bool accept)
    {
        DrawOffered = false;
        _sendBuffer[0] = MsgDrawReply;
        _sendBuffer[1] = (byte)(accept ? 1 : 0);
        Send(2);
    }

    /// <summary>Coup adverse reçu (dans le repère de l'adversaire : à retourner avant de l'appliquer), avec
    /// l'empreinte annoncée et la réserve de temps de l'adversaire après ce coup.</summary>
    public bool TryReceiveAction(out BattleAction action, out ulong hashAfter, out float reserveSeconds)
    {
        if (_incoming.TryDequeue(out var next))
        {
            (action, hashAfter, reserveSeconds) = next;
            return true;
        }
        action = default;
        hashAfter = 0;
        reserveSeconds = 0;
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
        var change = update.m_rgfChatMemberStateChange;
        // Départ volontaire (ou exclusion) = abandon. Coupure (crash, réseau) = on attend son retour (Silence).
        if ((change & (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft
                             | EChatMemberStateChange.k_EChatMemberStateChangeKicked
                             | EChatMemberStateChange.k_EChatMemberStateChangeBanned)) != 0)
            OpponentLeft = true;
        else if ((change & (uint)EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0)
            Silence = Math.Max(Silence, 5f);
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
