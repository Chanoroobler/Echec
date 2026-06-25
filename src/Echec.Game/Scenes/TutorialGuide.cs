using Echec.Core.Map;

namespace Echec.Game.Scenes;

/// <summary>Étapes du tutoriel « combat zéro » (placement guidé puis combat guidé).</summary>
public enum TutorialStep
{
    Intro,         // annonce le tuto + les 3 phases (préparation / combat / récompense)
    PickSoldier,   // prendre le soldat dans l'inventaire
    PlaceSoldier,  // le poser dans la zone de déploiement
    ReviewCard,    // revue de la carte donnée par donnée (DÈS la pose, avant de lancer le combat)
    StartCombat,   // lancer le combat
    Move,          // une action/tour : déplacer le soldat vers l'ennemi
    Attack,        // se déplacer SUR l'ennemi pour l'attaquer
    Commander,     // encart : le commandant, sa mort = défaite
    Done,          // récap → vrai combat 1
}

/// <summary>
/// Machine à états LINÉAIRE du tutoriel guidé. État PUR : la scène l'alimente (cases scénarisées),
/// décide QUAND avancer (<see cref="Advance"/>) selon les événements, et lit l'état pour gater l'input
/// et dessiner l'overlay. Aucun accès au <c>Match</c> ni au rendu.
/// </summary>
public sealed class TutorialGuide
{
    public TutorialStep Step { get; private set; } = TutorialStep.Intro;
    public bool Finished => Step == TutorialStep.Done;

    /// <summary>Vrai pendant les étapes de PLACEMENT (avant le lancement du combat).</summary>
    public bool InPlacement => Step <= TutorialStep.StartCombat;

    /// <summary>Case du soldat manipulé (fixée à la pose, puis suivie à chaque déplacement).</summary>
    public Cell PlayerSoldier { get; set; }

    /// <summary>Case de l'ennemi à atteindre puis abattre.</summary>
    public Cell EnemySoldier { get; set; }

    /// <summary>Case du commandant (essentiel) — pour l'encart de fin.</summary>
    public Cell Commander { get; set; }

    /// <summary>En combat, seul le soldat scénarisé est sélectionnable.</summary>
    public bool CanSelectInCombat(Cell cell) => cell == PlayerSoldier;

    /// <summary>Passe à l'étape suivante (sauf si déjà terminé).</summary>
    public void Advance()
    {
        if (Step != TutorialStep.Done)
            Step++;
    }

    /// <summary>Clé de localisation de la consigne courante.</summary>
    public string InstructionKey => Step switch
    {
        TutorialStep.Intro        => "tuto.intro_title",
        TutorialStep.PickSoldier  => "tuto.pick_soldier",
        TutorialStep.PlaceSoldier => "tuto.place_soldier",
        TutorialStep.StartCombat  => "tuto.start_combat",
        TutorialStep.ReviewCard   => "tuto.card_title",
        TutorialStep.Move         => "tuto.move",
        TutorialStep.Attack       => "tuto.attack",
        TutorialStep.Commander    => "tuto.commander",
        _                         => "tuto.victory_title",
    };
}
