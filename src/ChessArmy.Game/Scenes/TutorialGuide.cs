using ChessArmy.Core.Map;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Étapes du tutoriel « combat zéro », dans l'ordre : un placement guidé, un combat guidé, puis une
/// PRÉPARATION guidée (fusion → équipement → arbre de commandement) qui rejoue la vraie boucle d'avant-combat.
/// </summary>
public enum TutorialStep
{
    Intro,         // annonce le tuto + les 3 phases (préparation / combat / récompense)
    PickSoldier,   // prendre le soldat dans l'inventaire
    PlaceSoldier,  // le poser dans la zone de déploiement
    ReviewCard,    // revue de la carte donnée par donnée (DÈS la pose, avant de lancer le combat)
    StartCombat,   // lancer le combat
    Chest,         // aller sur le coffre : c'est comme ça qu'on trouve un équipement (l'ennemi attend)
    Move,          // une action/tour : déplacer le soldat vers l'ennemi
    Attack,        // se déplacer SUR l'ennemi pour l'attaquer
    Commander,     // encart : le commandant, sa mort = défaite

    // ── Préparation guidée (retour en phase de placement, sur la même map) ──────────────────────
    FusionIntro,   // encart : 3 pions identiques fusionnent en une unité évoluée
    FusionDo,      // empiler 3 soldats en réserve, puis choisir l'évolution
    DeployFused,   // poser l'unité évoluée sur le plateau (un pion s'équipe une fois déployé)
    EquipIntro,    // encart : les équipements, trouvés dans les coffres
    EquipDo,       // glisser l'équipement prêté sur le pion déployé
    TreeIntro,     // encart : l'arbre de commandement et ses points
    TreeOpen,      // ouvrir l'arbre par le bouton COMMANDEMENT du panneau (comme en vraie partie)
    TreeDo,        // acheter un nœud de niveau 1

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

    /// <summary>
    /// Vrai pendant la PRÉPARATION guidée qui suit le combat (fusion, équipement, arbre, récap). La scène y
    /// revient en phase de placement et ouvre elle-même les modales : ces étapes doivent donc être
    /// évaluées AVANT les retours anticipés de la boucle de placement.
    /// </summary>
    public bool InPreparation => Step >= TutorialStep.FusionIntro;

    /// <summary>Vrai pour les étapes qui n'attendent qu'une validation (encart plein écran) : placement gelé.</summary>
    public bool IsBriefing => Step is TutorialStep.FusionIntro or TutorialStep.EquipIntro
        or TutorialStep.TreeIntro or TutorialStep.Done;

    /// <summary>Case du soldat manipulé (fixée à la pose, puis suivie à chaque déplacement).</summary>
    public Cell PlayerSoldier { get; set; }

    /// <summary>Case de l'ennemi à atteindre puis abattre.</summary>
    public Cell EnemySoldier { get; set; }

    /// <summary>Case du commandant (essentiel) — pour l'encart de fin.</summary>
    public Cell Commander { get; set; }

    /// <summary>Case du coffre de la map de tuto (leçon « équipement »), si elle en porte un.</summary>
    public Cell? Chest { get; set; }

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
        TutorialStep.Chest        => "tuto.chest",
        TutorialStep.Move         => "tuto.move",
        TutorialStep.Attack       => "tuto.attack",
        TutorialStep.Commander    => "tuto.commander",
        TutorialStep.FusionIntro  => "tuto.fusion_title",
        TutorialStep.FusionDo     => "tuto.fusion_do",
        TutorialStep.DeployFused  => "tuto.deploy_fused",
        TutorialStep.EquipIntro   => "tuto.equip_title",
        TutorialStep.EquipDo      => "tuto.equip_do",
        TutorialStep.TreeIntro    => "tuto.tree_title",
        TutorialStep.TreeOpen     => "tuto.tree_open",
        TutorialStep.TreeDo       => "tuto.tree_do",
        _                         => "tuto.victory_title",
    };
}
