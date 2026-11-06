using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;

namespace ChessArmy.Core.Pvp;

/// <summary>
/// Fabrique de COPIES d'une partie multi pour que le bot puisse essayer des coups sans toucher la vraie : la partie
/// est rejouée depuis sa graine, ses deux armées et le journal des coups (exactement comme la revalidation, cf.
/// <see cref="PvpSetup.Replay"/>), puis les coups essayés sont joués par-dessus. Rejouer est très rapide (quelques
/// microsecondes par coup) et garantit une copie EXACTE, aléa du moteur compris, sans dupliquer tout l'état du
/// <see cref="Match"/>. Seule différence : la détection de position morte (une nulle de fin de partie, coûteuse)
/// n'est pas refaite, la recherche n'en a pas besoin.
/// Toujours dans le repère de l'hôte (plateau non retourné), seul repère des parties contre un bot.
/// </summary>
public sealed class PvpForker
{
    private readonly int _seed;
    private readonly bool _playerStarts;
    private readonly Battlefield? _terrain;   // jamais modifié par le moteur : partagé sans risque entre copies
    private readonly Cell[] _cover;
    private readonly PvpBuild _player;
    private readonly PvpBuild _enemy;

    public PvpForker(int seed, bool playerStarts, Battlefield? terrain, IEnumerable<Cell>? cover, PvpBuild player,
        PvpBuild enemy)
    {
        _seed = seed;
        _playerStarts = playerStarts;
        _terrain = terrain;
        _cover = cover?.ToArray() ?? Array.Empty<Cell>();
        _player = player.Clone();
        _enemy = enemy.Clone();
    }

    /// <summary>Partie neuve, armées posées, aucun coup joué.</summary>
    public Match Start()
    {
        var match = PvpSetup.CreateMatch(mirrored: false, _playerStarts, _seed, _terrain, _cover);
        PvpSetup.PlaceArmy(match, _player, Faction.Player);
        PvpSetup.PlaceArmy(match, _enemy, Faction.Enemy);
        return match;
    }

    /// <summary>Copie de la partie après les <paramref name="count"/> premiers coups de <paramref name="log"/>, puis
    /// les coups <paramref name="extra"/>. Null si un coup essayé est invalide.</summary>
    public Match? Fork(IReadOnlyList<BattleAction> log, int count, IReadOnlyList<BattleAction>? extra = null)
    {
        var match = Start();
        for (var i = 0; i < count && i < log.Count; i++)
            Step(match, log[i]);
        if (extra != null)
            foreach (var action in extra)
                if (!Step(match, action))
                    return null;
        return match;
    }

    /// <summary>Joue un coup avec les règles de fin de coup du multi (Séisme, effets en chaîne), sans la détection
    /// de position morte. Faux si la partie est finie ou le coup invalide.</summary>
    public static bool Step(Match match, in BattleAction action)
    {
        if (match.IsOver)
            return false;
        var actor = match.CurrentTurn;
        if (match.Apply(action) == MoveKind.Invalid)
            return false;
        if (!match.IsOver && match.CurrentTurn != actor)
            match.ApplySeismes(match.CurrentTurn);
        PvpSetup.ResolvePending(match);
        return true;
    }
}
