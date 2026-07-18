using System.Collections.Generic;

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
/// Le niveau est choisi à la CRÉATION d'une partie (écran de sélection du commandant) puis figé et persisté
/// avec elle : cf. <see cref="Campaign.Run.Difficulty"/>. Le cœur (<see cref="EnemyAi"/>, <see cref="Match"/>)
/// ne lit jamais un réglage global — la couche Game passe la valeur explicitement, pour que les tests
/// restent déterministes.
/// </summary>
/// <param name="AiAccuracy">
/// Probabilité (0..1) que l'IA joue réellement son MEILLEUR coup. Sinon elle « rate » sa décision et descend
/// d'un cran de priorité (renonce au kill parfait pour une attaque simple, etc.). 1 = jeu parfait.
/// </param>
/// <param name="TierShift">
/// Décalage de puissance appliqué à CHAQUE vague ennemie : <c>-1</c> rétrograde UN pion du tier le plus haut,
/// <c>+1</c> promeut UN pion du tier le plus bas, <c>0</c> ne touche à rien. L'EFFECTIF ne change jamais —
/// seule la composition en tiers bouge. La table de campagne (campaign.json) est calée sur Normal.
/// Cf. <see cref="Campaign.Run.AdjustTiers"/>.
/// </param>
public sealed record DifficultySettings(double AiAccuracy, int TierShift)
{
    /// <summary>IA maladroite (1 coup sur 2 raté) et une vague affaiblie d'un tier.</summary>
    public static readonly DifficultySettings Facile = new(AiAccuracy: 0.50, TierShift: -1);

    /// <summary>Défaut : l'IA rate un coup sur quatre, la vague est celle de la table telle quelle.</summary>
    public static readonly DifficultySettings Normal = new(AiAccuracy: 0.75, TierShift: 0);

    /// <summary>Jeu parfait, et une vague renforcée d'un tier.</summary>
    public static readonly DifficultySettings Difficile = new(AiAccuracy: 1.00, TierShift: +1);

    /// <summary>Tous les niveaux, dans l'ordre croissant : c'est l'ordre du sélecteur de l'écran de sélection.</summary>
    public static readonly IReadOnlyList<Difficulty> AllLevels =
        new[] { Difficulty.Facile, Difficulty.Normal, Difficulty.Difficile };

    public static DifficultySettings For(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Facile => Facile,
        Difficulty.Difficile => Difficile,
        _ => Normal,
    };
}
