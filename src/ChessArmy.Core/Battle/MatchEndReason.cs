namespace ChessArmy.Core.Battle;

/// <summary>Pourquoi une partie s'est terminée (affiché sur l'écran de fin du multi).</summary>
public enum MatchEndReason : byte
{
    /// <summary>Partie en cours.</summary>
    None,

    /// <summary>Commandant tombé ou camp anéanti.</summary>
    LeaderDown,

    /// <summary>Pendule tombée (réserve ou limite du coup).</summary>
    Timeout,

    /// <summary>Abandon volontaire (coup « Resign » ou sortie du joueur).</summary>
    Resign,

    /// <summary>Adversaire déconnecté sans revenir à temps.</summary>
    Disconnect,

    /// <summary>Égalité proposée et acceptée.</summary>
    AgreedDraw,

    /// <summary>Plus aucun contact possible entre les deux camps.</summary>
    DeadPosition,

    /// <summary>Les deux camps tombent dans le même coup.</summary>
    MutualDestruction,
}
