using System;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine.Localization;
using ChessArmy.Game.Net;
using Steamworks;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Lancement depuis l'écran Armée, SANS en sortir : tout est classé (sauf DÉFIER UN AMI, partie privée).
///  • LANCER UNE PARTIE : recherche par cote (<see cref="PvpMatchmaker.Start"/>) ; personne au bout de
///    <c>BotAfterSeconds</c> (ou pas de Steam) → bot du niveau du joueur. L'armée reste modifiable pendant ce temps.
///  • DÉFIER UN AMI activé : lobby privé + overlay Steam (le bouton réinvite pendant l'attente). Une invitation
///    acceptée (<see cref="PvpInvites"/>) bascule directement en connexion.
///  • Adversaire trouvé : l'armée sélectionnée est VERROUILLÉE (copie envoyée tout de suite), 5 s, puis le combat
///    avec elle. <see cref="PvpPrepScene"/> (choix parmi 3 armées devant la map) n'est plus sur ce parcours.
/// </summary>
public sealed partial class PvpArmyScene
{
    private enum SearchMode { Idle, Search, FriendHost, Join }

    // Adversaire trouvé mais muet (jeu fermé, pare-feu…) : on abandonne la session.
    private const float HandshakeTimeout = 20f;
    private const float LockSeconds = 5f;

    private readonly CSteamID _invite;
    private PvpMatchmaker? _mm;
    private SearchMode _mode;
    private bool _friend;
    private bool _handedOff;
    private float _handshake;
    private float _searchTime;
    private PvpBuild? _locked;
    private PvpSession? _session;
    private PvpMatchInfo? _botMatch;
    private float _countdown;
    private string? _error;

    private bool SteamOn => SteamService.IsActive;

    /// <summary>Raison pour laquelle l'armée courante ne peut pas partir (null : prête).</summary>
    private string? LaunchProblem()
    {
        if (Build.Slots.Count == 0)
            return Loc.T("pvp.empty_army");
        return PvpCatalog.Problem(Build, Profile.Owns) is null ? null : Loc.T("pvp.err_army");
    }

    private void OnLaunch()
    {
        if (Locked)
            return;
        if (_mode == SearchMode.FriendHost)
        {
            _mm?.ReopenInvite();   // « réinviter » pendant l'attente
            Context.Sounds.Play("menu_click");
            return;
        }
        if (_mode != SearchMode.Idle)
            return;
        if (LaunchProblem() is { } problem)
        {
            Say(problem, error: true);
            return;
        }
        CancelHeld();   // un pion en main retourne à sa place : l'armée est figée pendant la recherche
        Context.Sounds.Play("menu_click");
        StopMatchmaking();
        _searchTime = 0f;
        if (_friend)
        {
            if (!SteamOn)
            {
                Say(Loc.T("pvp.no_steam"), error: true);
                return;
            }
            _mode = SearchMode.FriendHost;
            _mm = new PvpMatchmaker(Profile.Rating);
            _mm.StartFriendLobby();
            return;
        }
        if (!SteamOn)
        {
            FoundBot();   // hors ligne : directement contre un bot
            return;
        }
        _mode = SearchMode.Search;
        _mm = new PvpMatchmaker(Profile.Rating);
        _mm.Start();
    }

    private void JoinInvite(CSteamID lobby)
    {
        StopMatchmaking();
        CancelHeld();
        _view = View.Build;
        _mode = SearchMode.Join;
        _friend = true;
        _searchTime = 0f;
        _mm = new PvpMatchmaker(Profile.Rating);
        _mm.JoinInvite(lobby);
    }

    private void CancelSearch()
    {
        if (Locked)
            return;
        StopMatchmaking();
        Context.Sounds.Play("menu_close");
    }

    /// <summary>Arrête la recherche en cours ; une session pas encore passée au combat est quittée.</summary>
    private void StopMatchmaking()
    {
        if (_mm != null)
        {
            _mm.Dispose();
            if (!_handedOff)
                _mm.Session?.Dispose();
            _mm = null;
        }
        if (!_handedOff)
            _session?.Dispose();
        _session = null;
        _mode = SearchMode.Idle;
        _handshake = 0f;
    }

    private void UpdateMatch(float dt)
    {
        if (_locked != null)
        {
            UpdateLocked(dt);
            return;
        }

        // Invitation d'ami acceptée pendant qu'on est ici : elle prime sur ce qu'on faisait.
        if (PvpInvites.TryTake(out var invite))
        {
            JoinInvite(invite);
            return;
        }
        if (_mm is not { } mm)
            return;
        _searchTime += dt;
        mm.Update(dt);
        if (_mode == SearchMode.Search
            && PvpBotFallback.ShouldUseBot(mm.SearchTime, PvpConfig.Current.BotAfterSeconds, mm.Failed,
                hasOpponent: mm.Session != null, joining: mm.Joining))
        {
            FoundBot();   // personne à notre niveau (ou Steam indisponible) : un bot prend le relais
            return;
        }
        if (mm.Session is not { } session)
            return;
        session.Update(dt);
        if (session.Started)
        {
            Found(session);
            return;
        }
        _handshake += dt;
        if (session.OpponentLeft || _handshake > HandshakeTimeout)
        {
            _handshake = 0f;
            mm.Restart();   // recherche : on cherche à nouveau ; défi / invitation : échec affiché
        }
    }

    /// <summary>Armée qui part : celle de l'onglet courant si elle est jouable, sinon l'armée de départ.</summary>
    private PvpBuild LockBuild()
    {
        CancelHeld();
        _view = View.Build;
        _revealOpen = false;
        Profile.SelectedBuild = _tab;
        PvpProfileStore.Save();
        return PvpCatalog.Problem(Build, Profile.Owns) is null ? Build.Clone() : PvpProfile.DefaultBuild();
    }

    private void Found(PvpSession session)
    {
        // On quitte la recherche (le lobby) mais on garde la session : c'est elle qui porte la partie.
        _mm?.Dispose();
        _mm = null;
        _mode = SearchMode.Idle;
        _session = session;
        _locked = LockBuild();
        session.SendBuild(_locked);
        _countdown = LockSeconds;
        Context.Sounds.Play("battle_start");
    }

    private void FoundBot()
    {
        StopMatchmaking();
        _locked = LockBuild();
        _botMatch = PvpMatchInfo.ForBot(_locked, Profile.Rating, _rng);
        _countdown = LockSeconds;
        Context.Sounds.Play("battle_start");
    }

    private void UpdateLocked(float dt)
    {
        if (_session != null)
        {
            _session.Update(dt);
            if (_session.OpponentLeft || _session.Silence > 4f + PvpConfig.Current.ReconnectSeconds)
            {
                _error = Loc.T("pvp.opponent_left");
                return;
            }
        }
        _countdown -= dt;
        if (_countdown > 0f)
            return;

        if (_session == null)
        {
            Launch(_botMatch!);
            return;
        }
        if (_session.OpponentBuild is not { } opponent)
            return;   // armée adverse pas encore reçue : « en attente de l'armée adverse »
        if (PvpCatalog.Problem(opponent) is { } problem)
        {
            _error = Loc.T("pvp.invalid_army", problem);
            return;
        }
        Launch(PvpMatchInfo.ForSession(_session, _locked!));
    }

    private void Launch(PvpMatchInfo info)
    {
        _handedOff = true;
        _leaving = true;
        Context.Scenes.Change(new GameplayScene(Context, info));
    }

    /// <summary>Après un message d'erreur (adversaire parti, armée invalide) : retour au build, armée déverrouillée.</summary>
    private void ResetAfterError()
    {
        _error = null;
        _locked = null;
        _botMatch = null;
        StopMatchmaking();   // quitte aussi la session
        Context.Sounds.Play("menu_close");
    }
}
