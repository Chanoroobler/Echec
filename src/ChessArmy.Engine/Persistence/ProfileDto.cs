namespace ChessArmy.Engine.Persistence;

/// <summary>
/// État de profil GLOBAL (un fichier <c>profile.json</c>, indépendant des slots de progression).
/// Sert à savoir si le joueur a déjà entamé une campagne : la toute PREMIÈRE bénéficie d'un
/// déblocage progressif plus doux des types ennemis (cf. <c>Run.FirstRun</c>).
/// </summary>
public sealed class ProfileDto
{
    /// <summary>Vrai dès que le joueur a démarré sa première campagne.</summary>
    public bool HasPlayedBefore { get; set; }

    /// <summary>
    /// Méta-progression : assets des unités déjà OBTENUES par le joueur (toutes parties confondues).
    /// Sert à révéler une évolution sur la carte de fusion seulement une fois qu'on l'a possédée.
    /// </summary>
    public System.Collections.Generic.List<string> DiscoveredUnits { get; set; } = new();

    /// <summary>
    /// Méta-progression : ids des équipements déjà OBTENUS par le joueur (toutes parties confondues).
    /// Sert au codex : un item reste en silhouette tant qu'on ne l'a jamais ramassé.
    /// </summary>
    public System.Collections.Generic.List<string> DiscoveredEquipment { get; set; } = new();

    /// <summary>
    /// Méta-progression : ids des COMMANDANTS débloqués (cf. <c>CommandeDef.Id</c>), en battant leur boss lié
    /// en dernière phase. Ces commandants deviennent jouables dans le carrousel de sélection. Les commandants
    /// déjà ouverts par défaut (<c>unlocked: true</c> dans units.json) n'ont pas besoin d'y figurer.
    /// </summary>
    public System.Collections.Generic.List<string> UnlockedCommanders { get; set; } = new();

    /// <summary>
    /// Méta-progression : nombre TOTAL de coffres ouverts par le joueur, toutes parties confondues. Sert à
    /// débloquer le commandant MARCHAND (cf. <c>SaveService.ChestUnlockThreshold</c>), contrôlé à la fin de
    /// chaque partie. Absent d'un vieux profil → 0.
    /// </summary>
    public int ChestsOpened { get; set; }

    /// <summary>
    /// Méta-progression : nombre TOTAL d'ennemis abattus par le joueur, toutes parties confondues. Sert à
    /// débloquer le commandant DUO (cf. <c>SaveService.KillUnlockThreshold</c>), contrôlé à la fin de chaque
    /// partie. Absent d'un vieux profil → 0.
    /// </summary>
    public int EnemiesKilled { get; set; }

    /// <summary>
    /// Méta-progression : pour chaque commandant (<c>CommandeDef.Id</c>), la difficulté la PLUS HAUTE avec
    /// laquelle la campagne a été GAGNÉE — valeur de l'énumération <c>Difficulty</c> (0 = Facile). Comme on
    /// ne garde que le maximum, terminer un niveau élevé vaut d'office pour tous les niveaux en dessous.
    /// Absent d'un vieux profil ou commandant absent de la table → jamais gagné.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, int> CommanderWins { get; set; } = new();

    /// <summary>
    /// Historique PAR COMMANDANT (<c>CommandeDef.Id</c>) affiché sur l'écran de sélection : parties lancées,
    /// gagnées, ennemis tués et temps de jeu. Absent d'un vieux profil ou commandant absent → tout à 0.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, CommanderHistoryDto> CommanderHistory { get; set; } = new();
}

/// <summary>Compteurs d'historique d'UN commandant (cf. <see cref="ProfileDto.CommanderHistory"/>). Tuto exclu.</summary>
public sealed class CommanderHistoryDto
{
    /// <summary>Parties lancées avec ce commandant (clic sur LANCER).</summary>
    public int RunsStarted { get; set; }

    /// <summary>Campagnes gagnées avec ce commandant.</summary>
    public int RunsWon { get; set; }

    /// <summary>Ennemis abattus pendant les parties avec ce commandant.</summary>
    public int EnemiesKilled { get; set; }

    /// <summary>Temps de jeu cumulé (secondes, hors menus pause) avec ce commandant.</summary>
    public double PlayTimeSeconds { get; set; }

    public CommanderHistoryDto Clone() => (CommanderHistoryDto)MemberwiseClone();
}
