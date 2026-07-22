using System.Collections.Generic;

namespace Echec.Core.Battle;

/// <summary>Rôle d'une unité COMMANDE : meneur dont la mort décide la partie.</summary>
public enum CommandeRole
{
    Commander, // commandant du joueur : sa mort = défaite
    Boss       // boss ennemi : le tuer = victoire du combat de boss
}

/// <summary>
/// Définition d'une unité COMMANDE (commandant joueur ou boss ennemi). COMMANDE est un
/// RÔLE à part, hors des 5 domaines : l'unité emprunte le motif de déplacement d'un des
/// 5 domaines via <see cref="Movement"/> — et ce motif peut varier d'un meneur à l'autre.
/// Les stats (asset + PV + dégâts + portées) sont portées par <see cref="BaseClass"/>.
/// Chargée depuis units.json (repli codé dans <see cref="Commandes"/>).
/// </summary>
public sealed class CommandeDef
{
    /// <summary>Pions de départ par défaut : deux Soldats (base du domaine Dame).</summary>
    private static readonly IReadOnlyList<Domaine> DefaultStartingUnits = new[] { Domaine.Dame, Domaine.Dame };

    public CommandeDef(CommandeRole role, Domaine movement, UnitClass baseClass,
        int deployments = 5, int reserveSize = 8, string treeId = "commandant", int fusionPoints = 0,
        string? id = null, IReadOnlyList<Domaine>? startingUnits = null, bool startsUnlocked = true,
        int onHitPoints = 0, int onHitCap = int.MaxValue)
    {
        StartsUnlocked = startsUnlocked;
        Role = role;
        Movement = movement;
        BaseClass = baseClass;
        Deployments = deployments;
        ReserveSize = reserveSize;
        TreeId = treeId;
        FusionPoints = fusionPoints;
        OnHitPoints = onHitPoints;
        OnHitCap = onHitCap;
        Id = string.IsNullOrWhiteSpace(id) ? baseClass.Asset : id!;
        StartingUnits = startingUnits ?? DefaultStartingUnits;
    }

    /// <summary>
    /// COMMANDANT : identifiant STABLE, indépendant du sprite et du nom affiché. C'est lui qui est persisté
    /// dans la sauvegarde et qui sert aux déblocages — renommer l'asset ou le nom ne doit rien casser.
    /// Absent du JSON → replié sur l'asset (compatibilité).
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// COMMANDANT : pions avec lesquels il DÉMARRE une campagne, en plus de lui-même. Chaque entrée est un
    /// domaine, dont la classe de BASE est recrutée (cf. <see cref="Campaign.Run.Reset"/>). Liste vide =
    /// il commence seul. Absent du JSON → deux Soldats (le départ historique).
    /// </summary>
    public IReadOnlyList<Domaine> StartingUnits { get; }

    /// <summary>
    /// COMMANDANT : jouable dès le départ. <c>false</c> = VERROUILLÉ — l'écran de sélection le montre en
    /// silhouette noire et refuse de lancer la partie avec lui. La condition de déblocage reste à définir ;
    /// quand elle existera, c'est le seul point à faire évoluer (profil du joueur plutôt que donnée figée).
    /// </summary>
    public bool StartsUnlocked { get; }

    public CommandeRole Role { get; }

    /// <summary>Domaine (parmi les 5) dont l'unité emprunte le motif de déplacement.</summary>
    public Domaine Movement { get; }

    public UnitClass BaseClass { get; }

    /// <summary>
    /// COMMANDANT : nombre de pions posables sur le plateau au placement, COMMANDANT COMPRIS. Base propre à
    /// chaque commandant, augmentée par les nœuds « déploiement » de son arbre (cf. <see cref="Campaign.Run.DeployLimit"/>).
    /// </summary>
    public int Deployments { get; }

    /// <summary>
    /// COMMANDANT : taille de base de la réserve (pions du roster HORS commandant). Augmentée par les nœuds
    /// « réserve » de son arbre (cf. <see cref="Campaign.Run.ReserveLimit"/>).
    /// </summary>
    public int ReserveSize { get; }

    /// <summary>
    /// COMMANDANT : identifiant de son <see cref="Command.CommandTree"/> — chaque commandant a le sien
    /// (cf. <c>commander_trees.json</c>). Sans objet pour un boss.
    /// </summary>
    public string TreeId { get; }

    /// <summary>
    /// COMMANDANT : points de commandement gagnés à CHAQUE fusion. C'est UNE des sources de gain PROPRES au
    /// commandant, en plus des <see cref="Campaign.Run.PointsPerMission"/> de chaque mission réussie.
    /// 0 = pas de bonus de fusion (le commandant gagne alors ses points autrement, cf. <see cref="OnHitPoints"/>).
    /// </summary>
    public int FusionPoints { get; }

    /// <summary>
    /// COMMANDANT : points de commandement gagnés à CHAQUE fois qu'il se fait TOUCHER en combat (source de
    /// gain alternative à <see cref="FusionPoints"/>), plafonnée à <see cref="OnHitCap"/> coups par combat.
    /// 0 = ce commandant ne gagne pas de points sur coup reçu. Cf. <see cref="Campaign.Run.GrantCommanderHitPoints"/>.
    /// </summary>
    public int OnHitPoints { get; }

    /// <summary>COMMANDANT : nombre MAX de coups reçus comptabilisés par combat pour <see cref="OnHitPoints"/>.</summary>
    public int OnHitCap { get; }

    public string Name => BaseClass.Name;
}
