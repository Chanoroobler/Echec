using System.Collections.Generic;

namespace ChessArmy.Core.Battle.Config;

/// <summary>Racine du fichier de configuration des unités (units.json).</summary>
public sealed class UnitsConfig
{
    public List<DomaineConfig> Domaines { get; set; } = new();

    /// <summary>
    /// Classes EXCLUSIVES (cf. <see cref="ExclusiveClasses"/>) : mêmes arbres que les domaines, mais hors de
    /// tout tirage et de toute fusion — elles n'existent que par l'arbre de commandement qui les amène (le
    /// PAYSAN du commandant Brute). Le champ <c>domaine</c> y donne le motif de déplacement emprunté.
    /// </summary>
    public List<DomaineConfig> Exclusives { get; set; } = new();

    /// <summary>Unités COMMANDE (commandant, boss) : rôle + domaine de mouvement + stats.</summary>
    public List<CommandeConfig> Commandes { get; set; } = new();
}

/// <summary>
/// Une unité COMMANDE : son rôle (Commander/Boss), le domaine dont elle emprunte le
/// déplacement, et ses stats (mêmes champs qu'une classe de base).
/// </summary>
public sealed class CommandeConfig
{
    public string Role { get; set; } = "";
    public string Domaine { get; set; } = "";
    public string Name { get; set; } = "";
    public string Asset { get; set; } = "";
    public int Hp { get; set; }
    public int Damage { get; set; }
    public int MoveRange { get; set; }
    public int AttackRange { get; set; }

    /// <summary>COMMANDANT : tire à travers ses alliés (comme le Lancier). Absent/false = ligne bloquée.
    /// (Pour un boss, se déclare par phase dans <see cref="Phases"/>.)</summary>
    public bool PiercesAllies { get; set; }

    /// <summary>COMMANDANT : portée de tir MINIMALE (« X à Y » : X). Absent → 1 (peut frapper au contact).</summary>
    public int? MinAttackRange { get; set; }

    /// <summary>COMMANDANT : domaine du pattern d'ATTAQUE s'il diffère du déplacement. Absent → attaque = déplacement.</summary>
    public string? AttackDomaine { get; set; }

    /// <summary>
    /// BOSS, format HÉRITÉ (une seule variante) : phase de campagne (1..3) à laquelle ce boss est réservé,
    /// ou <c>0</c>/absent. Sert uniquement quand <see cref="Phases"/> est absent (repli sans traits). Ignoré
    /// pour un Commander et pour un boss qui déclare <see cref="Phases"/>.
    /// </summary>
    public int Phase { get; set; }

    /// <summary>
    /// BOSS : profils par phase — clé <c>"1"</c>..<c>"3"</c> → stats + traits propres à cette phase. C'est le
    /// format recommandé : un même boss peut être plus fort (et gagner des traits) selon la phase où il tombe.
    /// Un boss n'est éligible qu'aux phases qu'il déclare ici. Prioritaire sur les champs plats
    /// (<see cref="Hp"/>/<see cref="Damage"/>/…). Ignoré pour un Commander.
    /// </summary>
    public Dictionary<string, BossPhaseConfig>? Phases { get; set; }

    /// <summary>
    /// BOSS : ids d'équipement (cf. equipment.json) dans lesquels il pioche, sans doublon, autant de fois que
    /// la phase le demande (<see cref="BossPhaseConfig.EquipmentCount"/>). Pool CHOISI À LA MAIN : ni la rareté
    /// ni le drapeau <c>enemyAllowed</c> ne le filtrent — c'est le designer qui décide ce qu'un boss peut porter.
    /// Absent → le boss ne s'équipe jamais. Ignoré pour un Commander.
    /// </summary>
    public List<string>? EquipmentPool { get; set; }

    /// <summary>COMMANDANT : pions posables au placement, commandant compris. Absent → 5.</summary>
    public int? Deployments { get; set; }

    /// <summary>COMMANDANT : taille de base de la réserve (hors commandant). Absent → 8.</summary>
    public int? ReserveSize { get; set; }

    /// <summary>COMMANDANT : id de son arbre dans commander_trees.json. Absent → « commandant ».</summary>
    public string? Tree { get; set; }

    /// <summary>COMMANDANT : points de commandement gagnés à chaque fusion (une source de gain propre). Absent → 0.</summary>
    public int? FusionPoints { get; set; }

    /// <summary>COMMANDANT : points gagnés à chaque coup REÇU en combat (source alternative). Absent → 0.</summary>
    public int? OnHitPoints { get; set; }

    /// <summary>COMMANDANT : plafond de coups reçus comptabilisés par combat pour <c>onHitPoints</c>. Absent → illimité.</summary>
    public int? OnHitCap { get; set; }

    /// <summary>COMMANDANT : points gagnés à chaque coup DIRECT porté à distance (portée &gt;= 2) sur un ennemi (source alternative). Absent → 0.</summary>
    public int? RangedHitPoints { get; set; }

    /// <summary>COMMANDANT : plafond de coups à distance comptabilisés par combat pour <c>rangedHitPoints</c>. Absent → illimité.</summary>
    public int? RangedHitCap { get; set; }

    /// <summary>COMMANDANT : points gagnés à chaque SAUT par-dessus une unité ou un obstacle (domaine Cavalier, source alternative). Absent → 0.</summary>
    public int? JumpPoints { get; set; }

    /// <summary>COMMANDANT : plafond de sauts comptabilisés par combat pour <c>jumpPoints</c>. Absent → illimité.</summary>
    public int? JumpCap { get; set; }

    /// <summary>COMMANDANT : points gagnés à chaque BUTIN ramassé en combat, coffre ouvert OU tuile recrue (Marchand). Absent → 0.</summary>
    public int? LootPoints { get; set; }

    /// <summary>COMMANDANT : plafond de butins comptabilisés par combat pour <c>lootPoints</c>. Absent → illimité.</summary>
    public int? LootCap { get; set; }

    /// <summary>COMMANDANT : points gagnés à chaque MISSION RÉUSSIE. Absent → <c>Run.PointsPerMission</c> (2).</summary>
    public int? MissionPoints { get; set; }

    /// <summary>COMMANDANT : points gagnés chaque fois qu'un de ses meneurs REPREND DES PV en combat (DUO). Absent → 0.</summary>
    public int? HealPoints { get; set; }

    /// <summary>COMMANDANT : plafond de soins comptabilisés par combat pour <c>healPoints</c>. Absent → illimité.</summary>
    public int? HealCap { get; set; }

    /// <summary>COMMANDANT DUO : points gagnés à chaque mise à mort où ses DEUX meneurs ont frappé le mort. Absent → 0.</summary>
    public int? PairKillPoints { get; set; }

    /// <summary>COMMANDANT : plafond de mises à mort « à deux » comptabilisées par combat pour <c>pairKillPoints</c>. Absent → illimité.</summary>
    public int? PairKillCap { get; set; }

    /// <summary>COMMANDANT : points gagnés chaque fois qu'une unité ALLIÉE tombe en combat (BRUTE). Absent → 0.</summary>
    public int? AllyDeathPoints { get; set; }

    /// <summary>COMMANDANT : plafond de pertes comptabilisées par combat pour <c>allyDeathPoints</c>. Absent → illimité.</summary>
    public int? AllyDeathCap { get; set; }

    /// <summary>
    /// COMMANDANT DUO : id du SECOND meneur, une entrée de rôle <c>Companion</c> du même fichier. Il entre en
    /// jeu avec le commandant et sa mort perd la run, comme la sienne. Absent → commandant solo.
    /// </summary>
    public string? Companion { get; set; }

    /// <summary>
    /// COMMANDANT : joue SANS ARMÉE (ni réserve, ni recrutement, ni fusion, ni équipement, ni relance, ni
    /// mission spéciale ; coffres et tuiles recrue retirés des maps). Absent → <c>false</c>.
    /// </summary>
    public bool? NoArmy { get; set; }

    /// <summary>COMMANDANT : traits de combat NATIFS (cf. Trait), avant tout nœud d'arbre. Absent → aucun.
    /// (Pour un boss, les traits se déclarent par phase dans <see cref="Phases"/>.)</summary>
    public List<string>? Traits { get; set; }

    /// <summary>
    /// COMMANDANT : identifiant stable, persisté dans la sauvegarde (indépendant du nom et de l'asset).
    /// Absent → l'asset fait office d'id. Ignoré pour un boss.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// COMMANDANT : pions de départ, en plus de lui-même — une liste de DOMAINES dont la classe de base est
    /// recrutée (ex. <c>[ "Dame", "Dame" ]</c> = deux Soldats). Absent → deux Soldats. Liste vide = seul.
    /// </summary>
    public List<string>? StartingUnits { get; set; }

    /// <summary>
    /// COMMANDANT : disponible dès le départ. <c>false</c> = verrouillé (silhouette noire dans le carrousel,
    /// impossible à lancer). Absent → <c>true</c>. Le déblocage se fait en battant le boss lié (cf.
    /// <see cref="UnlocksCommander"/>) en dernière phase, mémorisé dans le profil.
    /// </summary>
    public bool? Unlocked { get; set; }

    /// <summary>COMMANDANT : jouable dès le départ en version DÉMO (même si <c>unlocked</c> vaut false). Absent → false.</summary>
    public bool? DemoUnlocked { get; set; }

    /// <summary>
    /// COMMANDANT : difficulté de prise en main, de 1 (accessible) à 3 (exigeant), affichée en étoiles sur
    /// l'écran de sélection. Indépendante du niveau de difficulté de la partie. Absent → 1.
    /// </summary>
    public int? Difficulty { get; set; }

    /// <summary>
    /// BOSS : id du COMMANDANT débloqué en battant ce boss en dernière phase (cf. <see cref="Id"/> d'un
    /// Commander). Absent → ce boss ne débloque personne (ex. la Brute). Ignoré pour un Commander.
    /// </summary>
    public string? UnlocksCommander { get; set; }

    /// <summary>
    /// BOSS : id d'une entrée de rôle <c>Companion</c> qui l'ACCOMPAGNE. Elle remplace l'escorte du plus haut
    /// tier (l'effectif ne bouge pas) et n'est pas essentielle. Absent → boss seul.
    /// Le champ <see cref="Companion"/> sert au même rôle pour un COMMANDANT (son second meneur).
    /// </summary>
    public string? BossCompanion { get; set; }

    /// <summary>
    /// BOSS : stats + traits du SECOND (<see cref="BossCompanion"/>) par phase (« 1 ».. « 3 »), mêmes champs
    /// qu'un profil de boss. Se règle indépendamment du second JOUABLE, qu'on ne peut pas équilibrer pour une
    /// rencontre de boss sans toucher au commandant du joueur. Absent → il combat avec sa fiche jouable.
    /// Le nom et le sprite viennent toujours du second lui-même : seuls les chiffres sont lus ici.
    /// </summary>
    public Dictionary<string, BossPhaseConfig>? CompanionPhases { get; set; }

    /// <summary>
    /// BOSS : id du COMMANDANT que le joueur doit avoir DÉBLOQUÉ pour que ce boss entre dans le tirage.
    /// Absent → toujours tirable.
    /// </summary>
    public string? RequiresCommander { get; set; }
}

/// <summary>
/// Stats + traits d'UN boss pour UNE phase (mêmes champs qu'une classe, hors évolutions). Le nom, l'asset
/// (sprite) et le domaine de déplacement restent portés par le <see cref="CommandeConfig"/> parent : seules
/// les stats et les traits changent d'une phase à l'autre.
/// </summary>
public sealed class BossPhaseConfig
{
    public int Hp { get; set; }
    public int Damage { get; set; }
    public int MoveRange { get; set; }
    public int AttackRange { get; set; }

    /// <summary>Portée de tir MINIMALE (« X à Y » : X). Absent → 1 (peut frapper au contact).</summary>
    public int? MinAttackRange { get; set; }

    /// <summary>Tire à travers ses alliés sans les toucher (comme le Lancier). Absent/false = ligne bloquée.</summary>
    public bool PiercesAllies { get; set; }

    /// <summary>Traits de combat actifs sur ce boss à cette phase (cf. Trait). Absent → aucun.</summary>
    public List<string>? Traits { get; set; }

    /// <summary>Domaine du pattern d'ATTAQUE s'il diffère du déplacement. Absent → attaque = déplacement.</summary>
    public string? AttackDomaine { get; set; }

    /// <summary>
    /// BOSS : nombre d'équipements portés à cette phase, tirés dans <see cref="CommandeConfig.EquipmentPool"/>
    /// (jamais deux fois le même). Absent/0 → boss nu. Sert aux boss dont la montée en puissance passe par
    /// l'ÉQUIPEMENT plutôt que par des traits (le Marchand).
    /// </summary>
    public int? EquipmentCount { get; set; }
}

/// <summary>Un domaine et sa classe de base (le motif de déplacement reste en code).</summary>
public sealed class DomaineConfig
{
    public string Domaine { get; set; } = "";
    public ClassConfig BaseClass { get; set; } = new();
}

/// <summary>Stats d'une classe : asset + PV + dégâts + portées, et évolutions optionnelles.</summary>
public sealed class ClassConfig
{
    public string Name { get; set; } = "";
    public string Asset { get; set; } = "";
    public int Hp { get; set; }
    public int Damage { get; set; }
    public int MoveRange { get; set; }
    public int AttackRange { get; set; }

    /// <summary>Tire à travers ses alliés sans les toucher (Lancier). Absent/false = ligne bloquée par les alliés.</summary>
    public bool PiercesAllies { get; set; }

    /// <summary>Portée de tir MINIMALE (« X à Y » : X). Absent → 1 (peut frapper au contact).</summary>
    public int? MinAttackRange { get; set; }

    /// <summary>Traits/particularités (hors « Traverse allié » = piercesAllies). Absent → aucun.</summary>
    public List<string>? Traits { get; set; }

    /// <summary>
    /// Domaine du pattern d'ATTAQUE s'il diffère du déplacement (ex. cavalier monté : déplacement Cavalier
    /// en L, mais attaque « Dame » en lignes comme un archer). Absent → attaque = déplacement.
    /// </summary>
    public string? AttackDomaine { get; set; }

    /// <summary>Sous-classes (arbre d'évolution) ; null/absent = feuille.</summary>
    public List<ClassConfig>? Evolutions { get; set; }
}
