using System;

namespace ChessArmy.Core.Pvp;

/// <summary>
/// Bascule d'une recherche classée sur un bot (peu de joueurs : on ne fait pas attendre). Une seule des deux
/// issues gagne : tant qu'un adversaire humain est en cours de poignée de main, le bot n'est pas lancé ; une
/// entrée dans le lobby d'un autre joueur déjà partie au moment du délai a <see cref="JoinGraceSeconds"/> pour aboutir.
/// </summary>
public static class PvpBotFallback
{
    /// <summary>Sursis accordé à une entrée de lobby en vol quand le délai tombe (un humain vaut mieux qu'un bot).</summary>
    public const float JoinGraceSeconds = 3f;

    /// <param name="searchTime">Temps de recherche écoulé.</param>
    /// <param name="botAfterSeconds">Délai avant le bot (<see cref="PvpConfig.BotAfterSeconds"/>).</param>
    /// <param name="failed">La recherche Steam a échoué pour de bon (Steam absent, lobby refusé).</param>
    /// <param name="hasOpponent">Un adversaire humain est trouvé (poignée de main en cours) : il prime.</param>
    /// <param name="joining">Une entrée dans le lobby d'un autre joueur est en vol.</param>
    public static bool ShouldUseBot(float searchTime, float botAfterSeconds, bool failed, bool hasOpponent, bool joining)
    {
        if (hasOpponent)
            return false;
        if (failed)
            return true;
        if (searchTime < botAfterSeconds)
            return false;
        return !joining || searchTime >= botAfterSeconds + JoinGraceSeconds;
    }

    /// <summary>Secondes affichées avant le bot (arrondi au-dessus, jamais négatif).</summary>
    public static int SecondsLeft(float searchTime, float botAfterSeconds) =>
        Math.Max(0, (int)Math.Ceiling(botAfterSeconds - searchTime));
}
