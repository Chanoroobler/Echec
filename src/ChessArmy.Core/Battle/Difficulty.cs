using System.Collections.Generic;

namespace ChessArmy.Core.Battle;

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
/// <param name="EnemyEquipBonus">
/// Pions ennemis ÉQUIPÉS par vague, en écart au barème de base de la phase (cf.
/// <see cref="Campaign.Run.EnemyEquipCount"/>) : un écart PAR PHASE (index 0..2 = phase 1..3), <c>0</c> = le
/// barème tel quel, <c>+1</c> = un porteur de plus. <c>null</c> = AUCUN ennemi équipé, quelle que soit la
/// phase. Le boss n'est jamais concerné (il est <c>Essential</c>, comme le commandant du joueur).
/// </param>
/// <param name="PaysansRequired">
/// MISSION « LIBÉRER » : nombre MINIMUM de paysans à libérer pour que la mission compte. En dessous, la run
/// est PERDUE, comme si le commandant était tombé. <c>0</c> = aucune exigence, la mission ne peut pas être
/// ratée. Le quota des missions « protéger » est distinct (cf. <see cref="PaysansRequiredProtect"/>).
/// </param>
/// <param name="PaysansRequiredProtect">
/// MISSION « PROTÉGER » : nombre MINIMUM de paysans à préserver de la capture. Même sanction et même
/// convention que <see cref="PaysansRequired"/> (<c>0</c> = aucune exigence), mais un barème plus exigeant.
/// </param>
/// <param name="PaysansRequiredSave">
/// MISSION « SAUVER » (course contre l'IA) : nombre MINIMUM de paysans à récupérer avant que l'ennemi ne les
/// capture. Même convention que les autres (<c>0</c> = aucune exigence). Dès qu'il devient impossible de tenir
/// ce quota (trop de captures), la run est PERDUE sans attendre la fin de la mission.
/// </param>
/// <param name="AllowRestart">
/// Autorise « Recommencer la mission » dans le menu pause. <c>false</c> = run sans filet : le joueur assume
/// ses erreurs, l'option est retirée du menu (couche Game). Sert aussi à afficher la mention dans
/// l'infobulle de difficulté.
/// </param>
public sealed record DifficultySettings(double AiAccuracy, int TierShift, int[]? EnemyEquipBonus,
    int PaysansRequired, int PaysansRequiredProtect, int PaysansRequiredSave, bool AllowRestart)
{
    /// <summary>IA maladroite (1 coup sur 2 raté), vague affaiblie d'un tier, aucun ennemi équipé, aucun quota ; recommencer autorisé.</summary>
    public static readonly DifficultySettings Facile =
        new(AiAccuracy: 0.50, TierShift: -1, EnemyEquipBonus: null,
            PaysansRequired: 0, PaysansRequiredProtect: 0, PaysansRequiredSave: 0, AllowRestart: true);

    /// <summary>Défaut : l'IA rate un coup sur quatre, vague de la table, équipement de base ; 1 paysan à libérer, 2 à protéger, 2 à sauver ; recommencer autorisé.</summary>
    public static readonly DifficultySettings Normal =
        new(AiAccuracy: 0.75, TierShift: 0, EnemyEquipBonus: new[] { 0, 0, 0 },
            PaysansRequired: 1, PaysansRequiredProtect: 2, PaysansRequiredSave: 2, AllowRestart: true);

    /// <summary>Jeu parfait, vague renforcée d'un tier, plus de pions équipés en fin de run (phase 1 +1, phase 2 +1, phase 3 +3) ; 2 paysans à libérer, 3 à protéger, 3 à sauver ; run sans filet (pas de « recommencer »).</summary>
    public static readonly DifficultySettings Difficile =
        new(AiAccuracy: 1.00, TierShift: +1, EnemyEquipBonus: new[] { 1, 1, 3 },
            PaysansRequired: 2, PaysansRequiredProtect: 3, PaysansRequiredSave: 3, AllowRestart: false);

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
