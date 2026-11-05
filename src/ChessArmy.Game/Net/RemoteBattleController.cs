using ChessArmy.Core.Battle;

namespace ChessArmy.Game.Net;

/// <summary>
/// Camp adverse joué par un humain distant : son coup est « prêt » quand il arrive du réseau. Le coup reçu est
/// exprimé dans le repère de l'adversaire ; il est retourné ici dans le repère local (cf. <see cref="PvpSetup"/>).
/// <see cref="ExpectedHash"/> garde l'empreinte annoncée par l'adversaire, à comparer après application.
/// </summary>
public sealed class RemoteBattleController : IBattleController
{
    private readonly PvpSession _session;

    public RemoteBattleController(PvpSession session) => _session = session;

    /// <summary>Empreinte (repère de l'hôte) que l'adversaire a obtenue après le dernier coup reçu.</summary>
    public ulong ExpectedHash { get; private set; }

    public bool TryGetAction(Match match, out BattleAction action)
    {
        if (!_session.TryReceiveAction(out var remote, out var hash))
        {
            action = default;
            return false;
        }
        ExpectedHash = hash;
        action = PvpSetup.Mirror(remote);
        return true;
    }
}
