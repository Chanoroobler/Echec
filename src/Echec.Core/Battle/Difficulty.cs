namespace Echec.Core.Battle;

/// <summary>Niveau de difficulté de la partie.</summary>
public enum Difficulty
{
    Facile,
    Normal,
    Difficile,
}

/// <summary>
/// Réglages dérivés du <see cref="Difficulty"/>. Pour l'instant un seul levier : la PRÉCISION de l'IA
/// (cf. <see cref="AiAccuracy"/>). Les autres leviers d'équilibrage prévus (multiplicateur de dégâts
/// ennemis, règle anti-one-shot, plafond de cibles de Tempête — cf. <c>docs/equilibrage.md</c>) viendront
/// s'ajouter ici plutôt que d'être éparpillés.
///
/// Le niveau courant est un réglage GLOBAL (<see cref="Current"/>) : il n'est pas encore exposé dans le
/// menu Options, on l'ajuste dans le code. Le cœur (<see cref="EnemyAi"/>, <see cref="Match"/>) ne le lit
/// jamais tout seul — la couche Game passe la valeur explicitement, pour que les tests restent déterministes.
/// </summary>
/// <param name="AiAccuracy">
/// Probabilité (0..1) que l'IA joue réellement son MEILLEUR coup. Sinon elle « rate » sa décision et descend
/// d'un cran de priorité (renonce au kill parfait pour une attaque simple, etc.). 1 = jeu parfait.
/// </param>
public sealed record DifficultySettings(double AiAccuracy)
{
    /// <summary>IA maladroite : une fois sur deux elle laisse passer son meilleur coup.</summary>
    public static readonly DifficultySettings Facile = new(AiAccuracy: 0.50);

    /// <summary>Défaut : l'IA laisse passer son meilleur coup une fois sur quatre.</summary>
    public static readonly DifficultySettings Normal = new(AiAccuracy: 0.75);

    /// <summary>Jeu parfait : l'IA prend toujours la meilleure décision disponible.</summary>
    public static readonly DifficultySettings Difficile = new(AiAccuracy: 1.00);

    /// <summary>Niveau courant. Pas encore réglable en jeu (pas d'entrée dans le menu Options).</summary>
    public static Difficulty Current { get; set; } = Difficulty.Normal;

    /// <summary>Réglages du niveau courant.</summary>
    public static DifficultySettings Active => For(Current);

    public static DifficultySettings For(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Facile => Facile,
        Difficulty.Difficile => Difficile,
        _ => Normal,
    };
}
