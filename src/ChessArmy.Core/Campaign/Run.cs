using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Command;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Map;

namespace ChessArmy.Core.Campaign;

/// <summary>Phase courante de la boucle de jeu.</summary>
public enum RunPhase
{
    Placement,    // on déploie ses unités avant le combat
    Battle,       // combat en cours (géré par le Match)
    Recruitment,  // après une victoire : on choisit une unité
    Victory,      // boss vaincu : campagne gagnée
    Defeat        // commandant tombé ou armée détruite
}

/// <summary>
/// État de la campagne (première boucle de gameplay) : inventaire du joueur,
/// numéro de combat, génération des vagues ennemies (difficulté croissante) et du
/// draft de recrutement. Boucle : Placement → Battle → Recruitment → Placement …
///
/// Une run = <see cref="PhaseCount"/> phases de <see cref="MissionsPerPhase"/> missions, jouées
/// selon le rythme par phase <see cref="PhaseLayouts"/> (phases 2-3 : Escarmouche ×2, Speciale,
/// Escarmouche ×2, Boss ; phase 1 : spéciale décalée au slot 4), soit <see cref="TotalCombats"/>
/// combats et 3 boss. Seul le boss FINAL (phase 3) gagne la run ;
/// les boss des phases 1-2 enchaînent vers le recrutement. <see cref="CombatNumber"/> (1..18) est le
/// seul curseur : <see cref="PhaseIndex"/> et <see cref="MissionInPhase"/> en dérivent.
///
/// Persistance : permadeath (les unités mortes quittent l'inventaire) + soin complet
/// (les survivantes reviennent à PV pleins, via un nouveau Spawn au combat suivant).
/// </summary>
public sealed class Run
{
    /// <summary>Nombre de phases d'une run.</summary>
    public const int PhaseCount = 3;

    /// <summary>
    /// ⚙️ PLAYTEST — la run se termine (VICTOIRE) au boss de CETTE phase. Mettre <c>= 2</c> pour s'arrêter au
    /// boss de la phase 2 ; <c>= PhaseCount</c> (3) pour la campagne COMPLÈTE. Toute la machinerie des 3 phases
    /// (vagues, boss, coffres) reste EN PLACE : seule la fin de run est avancée. Réglé sur <see cref="PhaseCount"/>
    /// pour que le boss de phase 3 — et le déblocage de commandant qui en dépend — soit atteignable.
    /// </summary>
    public static int EndAtPhase = PhaseCount;

    /// <summary>
    /// Plafond de TIER des unités (IA et fusion). <see cref="MaxTier"/> (3) = aucun plafond. Abaissé à 2
    /// par le mode DÉMO (cf. GameSettings.IsDemo, poussé au boot par la couche Game) : l'IA ne fielde jamais
    /// de tier 3 et la fusion T2→T3 est coupée. Réglé côté Game car le Core ne voit pas les réglages.
    /// </summary>
    public static int MaxUnitTier = MaxTier;

    /// <summary>Missions par phase (rythme <see cref="PhaseLayout"/>).</summary>
    public const int MissionsPerPhase = 6;

    /// <summary>Nombre total de combats d'une run (1..<see cref="TotalCombats"/>).</summary>
    public const int TotalCombats = PhaseCount * MissionsPerPhase; // 18

    public const int DraftSize = 3;

    /// <summary>Points de commandement gagnés à chaque mission réussie (cf. <see cref="CommandPoints"/>).</summary>
    public const int PointsPerMission = 2;

    /// <summary>
    /// Rythme STANDARD d'une phase (6 slots) : deux escarmouches, une mission spéciale, deux escarmouches,
    /// un boss. Utilisé par les phases 2-3. La spéciale est déjà TYPÉE <see cref="CombatType.Speciale"/>
    /// mais générée comme une escarmouche tant qu'elle n'a pas de contenu propre.
    /// </summary>
    private static readonly CombatType[] StandardPhaseLayout =
    {
        CombatType.Escarmouche, CombatType.Escarmouche, CombatType.Speciale,
        CombatType.Escarmouche, CombatType.Escarmouche, CombatType.Boss,
    };

    /// <summary>
    /// Rythme PROPRE À CHAQUE PHASE, indexé <c>[PhaseIndex-1]</c>. La PHASE 1 décale la mission spéciale
    /// au slot 4 (trois escarmouches d'échauffement d'abord) ; les phases 2-3 gardent le
    /// <see cref="StandardPhaseLayout"/> (spéciale au slot 3).
    /// </summary>
    private static readonly CombatType[][] PhaseLayouts =
    {
        new[] // Phase 1 : spéciale décalée au slot 4.
        {
            CombatType.Escarmouche, CombatType.Escarmouche, CombatType.Escarmouche,
            CombatType.Speciale, CombatType.Escarmouche, CombatType.Boss,
        },
        StandardPhaseLayout,
        StandardPhaseLayout,
    };

    // ORDRE D'INTRODUCTION des types ennemis : un nouveau type est débloqué à chaque combat —
    // Soldat (Dame), Lancier (Tour), Cavalier (Cavalier), Mage (Fou). Le Cavalier arrive TÔT
    // (rapide : move 3 + saut) pour varier la menace ; le Mage (le plus punitif, one-shot à portée 3)
    // est introduit EN DERNIER, le temps que le joueur apprenne. Le pool grandit d'un type par combat
    // (cf. UnlockedDomaines) : tout est ouvert dès la fin de la phase 1, les phases 2-3 tirant parmi
    // les 4 domaines. Comme le recrutement propose les ennemis VAINCUS (voir BuildDraft), ces domaines
    // deviennent jouables au fil des déblocages.
    private static readonly Domaine[] IntroOrder =
        { Domaine.Dame, Domaine.Tour, Domaine.Cavalier, Domaine.Fou };

    // Effectif + tiers + taille de map par (phase, mission) : externalisés dans Assets/Config/campaign.json
    // (cf. CampaignPlan, réglage « facile » sans recompiler). L'ancienne table WaveTiers en est le repli codé.

    private readonly List<UnitSpec> _roster = new();
    private readonly List<UnitSpec> _draft = new();

    /// <summary>
    /// Équipements POSSÉDÉS mais NON équipés (inventaire de la run). Les équipements équipés vivent sur
    /// leur <see cref="UnitSpec"/> (collés au pion). Alimenté par les coffres, vidé en posant un équipement.
    /// </summary>
    private readonly List<Equipment> _equipment = new();

    /// <summary>Nœuds de l'arbre de commandement ACHETÉS pendant cette run (ids, cf. <see cref="CommandNode.Id"/>).</summary>
    private readonly HashSet<string> _unlocked = new();

    /// <summary>
    /// RELANCES disponibles : le joueur en gagne 1 à chaque nouvelle PHASE (cumulables, non perdues) et
    /// en CASSANT un équipement (<see cref="AddReroll"/>). Une relance échange un pion contre un autre du
    /// MÊME TIER tiré au hasard (<see cref="RerollUnit"/>). Persistée dans <see cref="RunSave"/>.
    /// </summary>
    private int _rerolls;

    /// <summary>
    /// Graine de la run, SAUVEGARDÉE. La vague ennemie et le terrain de chaque combat en dérivent de
    /// façon déterministe (cf. <see cref="CombatRng"/>) : « Continuer » régénère donc EXACTEMENT le
    /// même combat (mêmes ennemis, même terrain) qu'avant de quitter.
    /// </summary>
    public int Seed { get; private set; }

    /// <summary>
    /// Vrai = TOUTE PREMIÈRE campagne du joueur : déblocage des types ennemis plus doux (combat 1 =
    /// soldats seuls, tout débloqué au combat 5). Faux = campagnes suivantes : départ soldat+lancier,
    /// tout débloqué dès le combat 4. Persisté dans <see cref="RunSave"/> pour qu'une reprise garde le
    /// même rythme. Cf. <see cref="BuildEnemyWave"/>.
    /// </summary>
    public bool FirstRun { get; private set; }

    /// <summary>
    /// « Pitié » LÉGENDAIRE : bonus cumulé (en %) à la chance de tirer un légendaire au prochain coffre.
    /// +<see cref="LegendaryPityStep"/> par coffre qui n'en donne pas, REMIS À ZÉRO au drop d'un légendaire.
    /// Persisté dans la sauvegarde (survit d'un combat à l'autre). Cf. <see cref="ResolveChestRarity"/>.
    /// </summary>
    public int LegendaryPity { get; private set; }

    /// <summary>
    /// « Pitié » RARE : bonus cumulé (en %) à la chance de tirer un rare au prochain coffre.
    /// +<see cref="RarePityStep"/> par coffre qui n'en donne pas, REMIS À ZÉRO au drop d'un rare.
    /// Persisté dans la sauvegarde. Cf. <see cref="ResolveChestRarity"/>.
    /// </summary>
    public int RarePity { get; private set; }

    /// <summary>
    /// Niveau de difficulté de CETTE campagne : choisi à la création, figé pour toute la run et persisté
    /// avec elle. Pilote la précision de l'IA (cf. <see cref="DifficultySettings.AiAccuracy"/>).
    /// </summary>
    public Difficulty Difficulty { get; private set; }

    /// <param name="commander">Commandant choisi par le joueur. <c>null</c> → le commandant par défaut.</param>
    /// <param name="difficulty">Niveau figé pour toute la campagne.</param>
    public Run(int? seed = null, bool firstRun = false, CommandeDef? commander = null,
        Difficulty difficulty = Difficulty.Normal)
    {
        Seed = seed ?? new Random().Next();
        FirstRun = firstRun;
        Difficulty = difficulty;
        CommanderDef = commander ?? Commandes.Commander;
        Reset();
    }

    /// <summary>Inventaire du joueur (commandant inclus).</summary>
    public IReadOnlyList<UnitSpec> Roster => _roster;

    /// <summary>Nombre de pions dans la réserve (roster HORS commandant), comparé à <see cref="ReserveLimit"/>.</summary>
    public int ReserveCount => _roster.Count(u => !u.Essential);

    /// <summary>Vrai si la réserve est pleine (<see cref="ReserveLimit"/> pions non-commandant).</summary>
    public bool IsReserveFull => ReserveCount >= ReserveLimit;

    // ─── ARBRE DE COMMANDEMENT ────────────────────────────────────────────────────────────────────
    // Le joueur gagne PointsPerMission points à chaque mission réussie, plus le bonus PROPRE à son
    // commandant (CommandeDef.FusionPoints à chaque fusion, pour le commandant de départ). Il les dépense
    // pendant le PLACEMENT dans l'arbre de son commandant (CommandTrees.For) : les nœuds achetés modifient
    // ses plafonds (ReserveLimit / DeployLimit), ses bonus d'unité (BuffsFor) et la fusion (FusionRecruits).
    // Tout est PROPRE À LA RUN : sauvegardé avec elle, remis à zéro à la campagne suivante.

    /// <summary>Définition du commandant du joueur : ses plafonds de base, son arbre, sa source de points.</summary>
    public CommandeDef CommanderDef { get; private set; } = Commandes.Commander;

    /// <summary>
    /// Remplace le commandant de la run (n'affecte QUE <see cref="CommanderDef"/> ; l'appelant enchaîne un
    /// <see cref="Reset"/> pour reconstruire le roster). Sert au TUTORIEL : il se joue TOUJOURS avec le
    /// commandant de départ (le Foudroyeur), puis on rend à la run le commandant réellement choisi avant de
    /// lancer la campagne. <c>null</c> → commandant par défaut.
    /// </summary>
    public void SetCommander(CommandeDef? def) => CommanderDef = def ?? Commandes.Commander;

    /// <summary>Arbre de commandement du commandant courant.</summary>
    public CommandTree Tree => CommandTrees.For(CommanderDef);

    /// <summary>Points de commandement disponibles (non dépensés).</summary>
    public int CommandPoints { get; private set; }

    /// <summary>
    /// Statistiques CUMULÉES de la run (récap de fin : dégâts par classe, tués, perdus, déblocages…).
    /// Alimentée par la scène de jeu et PERSISTÉE avec la run (cf. <see cref="RunSave"/>) : elle survit à un
    /// « Continuer » et entre sessions. Neuve à chaque nouvelle campagne (<see cref="Reset"/>).
    /// </summary>
    public RunStats Stats { get; private set; } = new();

    /// <summary>Ids des nœuds achetés (l'ordre n'a pas de sens).</summary>
    public IReadOnlyCollection<string> UnlockedNodes => _unlocked;

    public bool IsUnlocked(string nodeId) => _unlocked.Contains(nodeId);

    /// <summary>Effets cumulés de tous les nœuds achetés.</summary>
    public IEnumerable<CommandEffect> ActiveEffects => Tree.EffectsOf(_unlocked);

    /// <summary>
    /// Nœuds achetés dont AU MOINS UN effet agit sur la cible demandée (<paramref name="commander"/> vrai =
    /// le commandant, faux = les troupes). Les nœuds de logistique (réserve, déploiement, bonus de fusion)
    /// n'y figurent jamais : ils ne touchent aucune unité. Sert à afficher sur la carte d'un pion les
    /// améliorations qui le concernent réellement.
    /// </summary>
    public IReadOnlyList<CommandNode> ActiveNodesFor(bool commander) =>
        Tree.Nodes
            .Where(n => _unlocked.Contains(n.Id)
                        && n.Effects.Any(e => commander ? e.TargetsCommander : e.TargetsUnits))
            .ToList();

    /// <summary>
    /// Nombre de PAIRES DE CLASSES DISTINCTES du roster hors commandant (classes distinctes ÷ 2, arrondi
    /// vers le bas) : c'est le multiplicateur des bonus « par paire » (<see cref="CommandScale.PerDistinctPair"/>).
    /// Compte la réserve ET les pions déployés — le roster est le même objet dans les deux cas.
    /// </summary>
    public int DistinctPairs =>
        _roster.Where(u => !u.Essential).Select(u => u.UnitClass).Distinct().Count() / 2;

    /// <summary>
    /// Nombre d'unités du <paramref name="domaine"/> dans le roster HORS commandant (réserve ET pions
    /// déployés — même objet). Multiplicateur des bonus « par unité de domaine »
    /// (<see cref="CommandScale.PerDomaineUnit"/>), figé au roster de la phase de placement.
    /// </summary>
    public int DomaineUnitCount(Domaine domaine) =>
        _roster.Count(u => !u.Essential && u.Domaine == domaine);

    /// <summary>
    /// Vrai si <paramref name="node"/> est achetable MAINTENANT : en placement, pas déjà pris, prérequis
    /// satisfait (un nœud du niveau inférieur dans la même branche) et assez de points.
    /// </summary>
    public bool CanUnlock(CommandNode node) =>
        Phase == RunPhase.Placement
        && !IsUnlocked(node.Id)
        && Tree.ById(node.Id) != null
        && Tree.PrerequisiteMet(node, _unlocked)
        && CommandPoints >= node.Cost;

    /// <summary>
    /// Offre des points de commandement HORS mission : sert au tutoriel, qui prête au joueur de quoi
    /// acheter son premier nœud. Un montant négatif est ignoré.
    /// </summary>
    public void GrantCommandPoints(int amount) => CommandPoints += Math.Max(0, amount);

    /// <summary>
    /// Source de points « sur coup reçu » : crédite <c>CommandeDef.OnHitPoints</c> par fois que le COMMANDANT
    /// a été touché ce combat (<paramref name="commanderHits"/>), plafonné à <c>CommandeDef.OnHitCap</c> coups.
    /// Sans effet pour un commandant dont ce n'est pas la source (OnHitPoints = 0). À appeler à la clôture
    /// d'un combat non perdu (le commandant est alors vivant).
    /// </summary>
    public void GrantCommanderHitPoints(int commanderHits)
    {
        if (CommanderDef.OnHitPoints <= 0 || commanderHits <= 0)
            return;
        CommandPoints += Math.Min(commanderHits, CommanderDef.OnHitCap) * CommanderDef.OnHitPoints;
    }

    /// <summary>
    /// Source de points « sur coup à distance » (commandant du Fou) : crédite <c>CommandeDef.RangedHitPoints</c>
    /// par coup DIRECT que le COMMANDANT a porté à distance ce combat (<paramref name="commanderRangedHits"/>,
    /// cf. <see cref="Battle.Unit.RangedHits"/>), plafonné à <c>CommandeDef.RangedHitCap</c>. Sans effet pour un
    /// commandant dont ce n'est pas la source (RangedHitPoints = 0). À appeler à la clôture d'un combat non perdu.
    /// </summary>
    public void GrantCommanderRangedHitPoints(int commanderRangedHits)
    {
        if (CommanderDef.RangedHitPoints <= 0 || commanderRangedHits <= 0)
            return;
        CommandPoints += Math.Min(commanderRangedHits, CommanderDef.RangedHitCap) * CommanderDef.RangedHitPoints;
    }

    /// <summary>Achète <paramref name="node"/> (dépense ses points). Faux — et rien ne change — si <see cref="CanUnlock"/> est faux.</summary>
    public bool Unlock(CommandNode node)
    {
        if (!CanUnlock(node))
            return false;
        CommandPoints -= node.Cost;
        _unlocked.Add(node.Id);
        return true;
    }

    /// <summary>Total d'un effet de méta sur les nœuds achetés (slots de réserve/déploiement, recrues de fusion).</summary>
    private int TotalOf(CommandEffectKind kind) =>
        ActiveEffects.Where(e => e.Kind == kind).Sum(e => e.Amount);

    /// <summary>
    /// Nombre MAXIMAL de pions dans la réserve (roster HORS commandant) : base du commandant + nœuds
    /// « réserve ». Au-delà, il faut fusionner ou supprimer pour recruter/récupérer de nouveaux pions.
    /// </summary>
    public int ReserveLimit => CommanderDef.ReserveSize + TotalOf(CommandEffectKind.ReserveSlots);

    /// <summary>
    /// Nombre MAXIMAL de pions posables sur le plateau au placement, COMMANDANT COMPRIS : base du
    /// commandant + nœuds « déploiement ». Borné par ailleurs par les cases de déploiement de la map.
    /// </summary>
    public int DeployLimit => CommanderDef.Deployments + TotalOf(CommandEffectKind.DeploySlots);

    /// <summary>Unités tier 1 offertes EN PLUS à chaque fusion (nœud « fusion »). 0 = aucun bonus.</summary>
    public int FusionRecruits => TotalOf(CommandEffectKind.FusionRecruit);

    /// <summary>Unités tier 1 (déjà vues) données en réserve PAR unité tier 2+ tombée au combat (nœud « relève »). 0 = aucun.</summary>
    public int EliteDeathRecruits => TotalOf(CommandEffectKind.EliteDeathRecruit);

    /// <summary>
    /// Bonus d'arbre applicables à <paramref name="spec"/> : ceux du commandant s'il est essentiel, ceux
    /// des troupes sinon. À passer à <see cref="UnitSpec.Spawn"/> — les bonus « par paire » sont figés au
    /// roster du moment, donc recalculés à chaque phase de placement.
    /// </summary>
    public CommandBuffs BuffsFor(UnitSpec spec) =>
        CommandBuffs.From(ActiveEffects, spec.Essential, DistinctPairs, spec.Domaine, DomaineUnitCount);

    /// <summary>Les 3 options de recrutement (vides hors phase de recrutement).</summary>
    public IReadOnlyList<UnitSpec> Draft => _draft;

    /// <summary>Équipements possédés mais non équipés (inventaire). Posables sur les pions en phase Équipement.</summary>
    public IReadOnlyList<Equipment> EquipmentInventory => _equipment;

    /// <summary>
    /// Vrai si le joueur possède au moins un équipement — en inventaire OU déjà posé sur un pion (sinon
    /// la phase Équipement est sautée). On l'ouvre même si tout est équipé, pour pouvoir réagencer/retirer.
    /// </summary>
    public bool HasEquipment => _equipment.Count > 0 || _roster.Any(u => u.Equipment != null);

    /// <summary>Relances disponibles (cf. <see cref="RerollUnit"/>, <see cref="AddReroll"/>).</summary>
    public int Rerolls => _rerolls;

    /// <summary>Vrai s'il reste au moins une relance à dépenser.</summary>
    public bool HasReroll => _rerolls > 0;

    public int CombatNumber { get; private set; }
    public RunPhase Phase { get; private set; }

    /// <summary>Phase courante (1..<see cref="PhaseCount"/>), dérivée de <see cref="CombatNumber"/>.</summary>
    public int PhaseIndex => (CombatNumber - 1) / MissionsPerPhase + 1;

    /// <summary>Rang de la mission dans sa phase (1..<see cref="MissionsPerPhase"/>).</summary>
    public int MissionInPhase => (CombatNumber - 1) % MissionsPerPhase + 1;

    /// <summary>Nature de la mission courante selon le rythme de la phase (<see cref="PhaseLayouts"/>).</summary>
    public CombatType CurrentMission => PhaseLayouts[PhaseIndex - 1][MissionInPhase - 1];

    /// <summary>Vrai si la mission courante est un combat de boss (dernière de chaque phase).</summary>
    public bool IsBossCombat => CurrentMission == CombatType.Boss;

    /// <summary>Vrai pour le boss FINAL (boss de la dernière phase) : seul à conclure la run en victoire.</summary>
    public bool IsFinalBoss => IsBossCombat && PhaseIndex == PhaseCount;

    /// <summary>Nature de la mission au rang <paramref name="missionInPhase"/> (1..<see cref="MissionsPerPhase"/>)
    /// dans la phase <paramref name="phaseIndex"/> (1..<see cref="PhaseCount"/>) — cf. <see cref="PhaseLayouts"/>
    /// (le rythme diffère en phase 1). Sert à la frise UI.</summary>
    public static CombatType MissionKindAt(int phaseIndex, int missionInPhase) =>
        PhaseLayouts[phaseIndex - 1][missionInPhase - 1];

    /// <summary>
    /// Effectif ennemi TOTAL d'une mission (phase 1..3, rang 1..6) = escortes de la table + le boss
    /// éventuel. Sert à l'UI (tooltip de la frise) ; cohérent avec <see cref="BuildEnemyWave"/>.
    /// </summary>
    public static int EnemyCount(int phaseIndex, int missionInPhase) =>
        CampaignPlan.For(phaseIndex, missionInPhase).Tiers.Count
        + (MissionKindAt(phaseIndex, missionInPhase) == CombatType.Boss ? 1 : 0);

    public UnitSpec Commander => _roster.First(u => u.Essential);

    /// <summary>
    /// (Re)démarre une campagne : le commandant COURANT (celui choisi à la création — <see cref="Reset"/> n'en
    /// change pas) et ses pions de départ, combat 1, arbre de commandement vierge.
    /// </summary>
    public void Reset()
    {
        _roster.Clear();
        _roster.Add(ToSpec(CommanderDef));
        foreach (var domaine in CommanderDef.StartingUnits)
            _roster.Add(new UnitSpec(domaine, Domaines.Of(domaine).BaseClass));
        _draft.Clear();
        _equipment.Clear();
        _unlocked.Clear();
        CommandPoints = 0;
        CombatNumber = 1;
        _rerolls = 1;               // 1 relance offerte à l'ouverture de la phase 1
        LegendaryPity = 0;
        RarePity = 0;
        Stats = new RunStats();     // récap remis à zéro pour la nouvelle campagne
        _aiFreshT2 = null;          // nouveauté IA retirée à neuf : recalculée au 1er combat qui aligne le tier
        _aiFreshT3 = null;
        Phase = RunPhase.Placement;
    }

    /// <summary>
    /// 1 relance par PHASE (cumulable) : accordée quand le combat courant OUVRE une nouvelle phase (sa
    /// 1re mission). Appelée APRÈS l'avancée de <see cref="CombatNumber"/> ; la phase 1 est offerte au
    /// <see cref="Reset"/>. Persistée avec la run (aucun double-compte à la reprise).
    /// </summary>
    private void GrantPhaseRerollIfNewPhase()
    {
        if (MissionInPhase == 1)
            _rerolls++;
    }

    /// <summary>
    /// Reconstruit une run à partir d'une sauvegarde (inventaire + numéro de combat). La run reprend
    /// en phase de PLACEMENT : la vague ennemie et le terrain sont regénérés au combat courant (la
    /// sauvegarde n'a lieu qu'en placement, donc aucun état de combat / de recrutement à restaurer).
    /// </summary>
    public static Run Restore(IReadOnlyList<UnitSpec> roster, int combatNumber, int seed, bool firstRun,
        IReadOnlyList<Equipment>? inventory = null, int legendaryPity = 0, int rarePity = 0,
        int commandPoints = 0, IReadOnlyList<string>? unlockedNodes = null, int rerolls = 0,
        string? commanderId = null, Difficulty difficulty = Difficulty.Normal, RunStats? stats = null,
        IReadOnlyList<string>? aiFreshTier2 = null, IReadOnlyList<string>? aiFreshTier3 = null)
    {
        var run = new Run(seed, firstRun, difficulty: difficulty);
        if (stats != null)
            run.Stats = stats;   // récap repris de la sauvegarde (sinon compteur neuf du constructeur)
        // Nouveauté IA figée pour la run (null = à recalculer au 1er combat du tier concerné) : la reprise
        // rejoue ainsi EXACTEMENT la même vague, même après la découverte-à-l'apparition du combat sauvegardé.
        run._aiFreshT2 = aiFreshTier2?.ToList();
        run._aiFreshT3 = aiFreshTier3?.ToList();
        run._roster.Clear();
        run._roster.AddRange(roster);
        run.CommanderDef = ResolveCommander(roster, commanderId);
        run._equipment.Clear();
        if (inventory != null)
            run._equipment.AddRange(inventory);
        run._unlocked.Clear();
        // Nœuds inconnus de l'arbre courant (JSON modifié depuis la sauvegarde) : ignorés silencieusement.
        foreach (var id in unlockedNodes ?? Array.Empty<string>())
            if (run.Tree.ById(id) != null)
                run._unlocked.Add(id);
        run.CommandPoints = Math.Max(0, commandPoints);
        run._rerolls = Math.Max(0, rerolls);
        run.CombatNumber = combatNumber;
        run.LegendaryPity = System.Math.Max(0, legendaryPity);
        run.RarePity = System.Math.Max(0, rarePity);
        run.Phase = RunPhase.Placement;
        run._draft.Clear();
        return run;
    }

    /// <summary>
    /// Retrouve la définition du commandant sauvegardé. On lit d'abord son ID (persisté depuis la v3 de la
    /// sauvegarde) ; à défaut on retombe sur l'ASSET de sa classe, ce qui couvre les sauvegardes antérieures.
    /// Dernier repli : le commandant par défaut. Le roster ne conserve que des <see cref="UnitSpec"/> ;
    /// les plafonds et l'arbre vivent, eux, sur la <see cref="CommandeDef"/>.
    /// </summary>
    private static CommandeDef ResolveCommander(IReadOnlyList<UnitSpec> roster, string? commanderId)
    {
        if (Commandes.ById(commanderId) is { } byId)
            return byId;

        var asset = roster.FirstOrDefault(u => u.Essential)?.UnitClass.Asset;
        return Commandes.All.FirstOrDefault(c => c.Role == CommandeRole.Commander && c.BaseClass.Asset == asset)
               ?? Commandes.Commander;
    }

    /// <summary>
    /// Terrain du combat courant : herbe + obstacles (eau/montagne) aléatoires dans la zone neutre,
    /// symétriques. Tiré du RNG du run → varie d'un combat à l'autre, reproductible si seed fixé.
    /// </summary>
    public Battlefield BuildBattlefield(int width, int height) =>
        TerrainGenerator.Generate(width, height, CombatRng(0));

    /// <summary>
    /// Tire un pion tier 1 ALÉATOIRE parmi les domaines dont la classe de base a DÉJÀ ÉTÉ VUE
    /// (<paramref name="isSeen"/> = méta-progression : asset déjà rencontré dans une run), SANS l'ajouter à
    /// l'armée — l'appelant l'ajoute via <see cref="AddUnit"/> (ex. tuile recrue). AUCUN gating par combat :
    /// n'importe quel tier 1 déjà vu peut sortir à tout moment. Profil neuf (rien de vu) : repli sur le Pion.
    /// (Tier 2+ selon la progression : à venir.)
    /// </summary>
    public UnitSpec RollSeenTier1(Random rng, Func<string, bool> isSeen)
    {
        var pool = IntroOrder.Where(d => isSeen(Domaines.Of(d).BaseClass.Asset)).ToList();
        var domaine = pool.Count > 0 ? pool[rng.Next(pool.Count)] : Domaine.Dame;
        return new UnitSpec(domaine, Domaines.Of(domaine).BaseClass);
    }

    /// <summary>
    /// Une recrue pour l'effet d'arbre <paramref name="e"/> : la classe de base de son domaine s'il en
    /// précise un (ex. « recrute un Lancier » → domaine Tour), sinon un tier 1 déjà vu au hasard
    /// (<see cref="RollSeenTier1"/>).
    /// </summary>
    private UnitSpec RecruitFor(CommandEffect e, Random rng, Func<string, bool> isSeen) =>
        e.Domaine is { } d ? new UnitSpec(d, Domaines.Of(d).BaseClass) : RollSeenTier1(rng, isSeen);

    /// <summary>
    /// Recrues offertes par les nœuds « fusion » de l'arbre à CHAQUE fusion, une par recrue (le domaine
    /// précisé par l'effet, sinon un tier 1 déjà vu). L'appelant les ajoute au roster dans la limite de la
    /// réserve (une fusion libère deux places, donc le plafond ne mord jamais pour la 1re recrue).
    /// </summary>
    public IEnumerable<UnitSpec> FusionRecruitSpecs(Random rng, Func<string, bool> isSeen)
    {
        foreach (var e in ActiveEffects.Where(x => x.Kind == CommandEffectKind.FusionRecruit))
            for (var i = 0; i < e.Amount; i++)
                yield return RecruitFor(e, rng, isSeen);
    }

    /// <summary>Points de % gagnés par mission (à partir de la mission 2) sur la chance de recruter un T2.</summary>
    private const int Tier2RecruitChancePerCombat = 5;

    /// <summary>
    /// Chance (en %) qu'une recrue de TUILE soit un TIER 2 : 0 % à la mission 1, puis
    /// +<see cref="Tier2RecruitChancePerCombat"/> % par mission à partir de la mission 2. Bornée à [0, 100].
    /// Voir <see cref="RollSeenRecruit"/>.
    /// </summary>
    public int Tier2RecruitChance => Math.Clamp((CombatNumber - 1) * Tier2RecruitChancePerCombat, 0, 100);

    /// <summary>
    /// Gabarits TIER 2 DÉJÀ DÉCOUVERTS (<paramref name="isSeen"/> = méta-progression : les T2 sont découverts
    /// à la fusion), tous domaines confondus. Vide tant qu'aucun T2 n'a été découvert. Voir <see cref="RollSeenRecruit"/>.
    /// </summary>
    public IReadOnlyList<UnitSpec> SeenTier2Recruits(Func<string, bool> isSeen)
    {
        var pool = new List<UnitSpec>();
        foreach (var def in Domaines.All)
            foreach (var t2 in def.BaseClass.Evolutions)   // évolutions directes de la base = tier 2
                if (isSeen(t2.Asset))
                    pool.Add(new UnitSpec(def.Id, t2));
        return pool;
    }

    /// <summary>
    /// Tire la recrue d'une TUILE RECRUE : avec une probabilité de <see cref="Tier2RecruitChance"/> %, un tier 2
    /// au hasard parmi les <see cref="SeenTier2Recruits">T2 déjà découverts</see> ; sinon — ou si aucun T2 n'est
    /// encore découvert (pool vide) — un tier 1 comme <see cref="RollSeenTier1"/>. La chance monte avec la
    /// progression mais reste sans effet tant que le joueur n'a découvert aucun T2.
    /// </summary>
    public UnitSpec RollSeenRecruit(Random rng, Func<string, bool> isSeen)
    {
        if (rng.Next(100) < Tier2RecruitChance)
        {
            var tier2 = SeenTier2Recruits(isSeen);
            if (tier2.Count > 0)
                return tier2[rng.Next(tier2.Count)];
        }
        return RollSeenTier1(rng, isSeen);
    }

    // Chances de rareté à l'ouverture d'un coffre, par PHASE (index 0..2 = phase 1..3), en %. Le reste = commun.
    private static readonly int[] LegendaryChanceByPhase = { 5, 10, 15 };
    private static readonly int[] RareChanceByPhase = { 30, 40, 50 };
    /// <summary>Bonus de « pitié » ajouté par coffre qui ne donne pas de légendaire (cf. <see cref="LegendaryPity"/>).</summary>
    private const int LegendaryPityStep = 2;
    /// <summary>Bonus de « pitié » ajouté par coffre qui ne donne pas de rare (cf. <see cref="RarePity"/>).</summary>
    private const int RarePityStep = 3;

    /// <summary>Nombre d'exemplaires d'un même équipement à partir duquel il devient RARE au coffre (anti-doublon).</summary>
    private const int DuplicateThreshold = 1;

    /// <summary>Poids de tirage au coffre d'un équipement déjà possédé <see cref="DuplicateThreshold"/> fois ou plus
    /// (1 = normal). &lt; 1 → moins probable, sans jamais être impossible.</summary>
    private const double DuplicateWeight = 0.25;

    /// <summary>
    /// Butin d'un coffre : tire une RARETÉ (phase + pitié, cf. <see cref="ResolveChestRarity"/>) puis un
    /// équipement de cette rareté. Un item DÉJÀ POSSÉDÉ en double (inventaire + posés sur les pions) est
    /// nettement moins probable (anti-doublon, cf. <see cref="EquipmentDropWeight"/>) sans être exclu. Si le
    /// pool d'une rareté est vide, on retombe sur celle juste en dessous (légendaire → rare → commun). Null
    /// seulement si AUCUN équipement n'est défini. Met à jour la pitié.
    /// </summary>
    public Equipment? RollChestEquipment(Random rng)
    {
        var rarity = ResolveChestRarity(rng.NextDouble() * 100.0);
        var owned = OwnedEquipmentCounts();
        for (var r = (int)rarity; r >= 0; r--)
            if (Equipments.Roll((EquipmentRarity)r, rng, e => EquipmentDropWeight(e, owned)) is { } item)
                return item;
        return null;
    }

    /// <summary>Pions ennemis ÉQUIPÉS par vague en difficulté NORMALE, par phase (index 0..2 = phase 1..3).</summary>
    private static readonly int[] EnemyEquipByPhase = { 1, 3, 3 };

    /// <summary>
    /// Combats du DÉBUT de campagne sans aucun ennemi équipé : le joueur découvre d'abord le jeu nu.
    /// </summary>
    private const int NoEquipCombats = 2;

    /// <summary>
    /// Nombre EXACT de pions ennemis équipés dans la vague courante : le barème de la phase
    /// (<see cref="EnemyEquipByPhase"/>) décalé par le bonus de difficulté PROPRE À LA PHASE
    /// (<see cref="DifficultySettings.EnemyEquipBonus"/>). Zéro en facile, et zéro sur les
    /// <see cref="NoEquipCombats"/> premiers combats quelle que soit la difficulté.
    /// </summary>
    public int EnemyEquipCount()
    {
        if (DifficultySettings.For(Difficulty).EnemyEquipBonus is not { } bonus)
            return 0;
        if (CombatNumber <= NoEquipCombats)
            return 0;

        var phase = Math.Clamp(PhaseIndex, 1, PhaseCount) - 1;
        return Math.Max(0, EnemyEquipByPhase[phase] + bonus[phase]);
    }

    /// <summary>
    /// ÉQUIPE exactement <see cref="EnemyEquipCount"/> pions de la vague, tirés au sort sans doublon (et
    /// plafonnés à l'effectif disponible). Le BOSS est épargné : il est <c>Essential</c>, comme le
    /// commandant du joueur que <see cref="CanEquip"/> refuse d'équiper — d'où l'appel AVANT son insertion
    /// en tête de vague.
    ///
    /// Le tirage utilise un SEL de RNG propre (3) : il reste déterministe — reprendre la partie régénère la
    /// même vague avec le même équipement, sans rien ajouter à la sauvegarde — tout en ne décalant ni le
    /// terrain (sel 0), ni la composition de la vague (sel 1), ni l'ordre des cases (sel 2).
    /// </summary>
    private void EquipEnemies(List<UnitSpec> wave)
    {
        var carriers = wave.Where(s => !s.Essential).ToList();
        var count = Math.Min(EnemyEquipCount(), carriers.Count);
        if (count <= 0)
            return;

        var rng = CombatRng(3);
        Shuffle(carriers, rng);   // qui porte l'objet est aléatoire ; COMBIEN en portent ne l'est pas
        for (var i = 0; i < count; i++)
            carriers[i].Equipment = RollEnemyEquipment(rng, carriers[i]);
    }

    /// <summary>
    /// Équipement d'UN pion ennemi : COMMUN ou RARE, jamais légendaire — un légendaire sur un ennemi de
    /// passage serait hors de proportion. La chance « rare » de la phase départage les deux, donc la menace
    /// monte avec la campagne. Deux différences avec <see cref="RollChestEquipment"/> : la « PITIÉ » n'est
    /// ni lue ni modifiée (elle appartient aux coffres du joueur — la toucher ici fausserait ses drops), et
    /// l'anti-doublon ne s'applique pas (il compte ce que le JOUEUR possède). Repli sur le commun si le pool
    /// rare est vide. Les équipements marqués <see cref="Equipment.EnemyAllowed"/> = false sont EXCLUS du tirage
    /// (réservés au joueur) ; si le filtre vide une rareté, on retombe sur celle du dessous, puis rien.
    ///
    /// Le tirage respecte les MÊMES restrictions de domaine que le joueur (cf. <see cref="CanEquip"/>) : un
    /// objet interdit au porteur — typiquement des bottes (mouvement) ou un arc (portée) sur un cavalier — est
    /// exclu de SON tirage. D'où le passage du <paramref name="spec"/> : le filtre dépend du pion équipé.
    /// </summary>
    private Equipment? RollEnemyEquipment(Random rng, UnitSpec spec)
    {
        var phase = Math.Clamp(PhaseIndex, 1, PhaseCount) - 1;
        var rarity = rng.NextDouble() * 100.0 < RareChanceByPhase[phase]
            ? EquipmentRarity.Rare
            : EquipmentRarity.Common;

        for (var r = (int)rarity; r >= 0; r--)
            if (Equipments.Roll((EquipmentRarity)r, rng, filter: e => e.EnemyAllowed && CanEquip(spec, e)) is { } item)
                return item;
        return null;
    }

    /// <summary>Nombre d'exemplaires de chaque équipement POSSÉDÉ (inventaire + posés sur les pions), par id.</summary>
    private Dictionary<string, int> OwnedEquipmentCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in _equipment)
            counts[e.Id] = counts.GetValueOrDefault(e.Id) + 1;
        foreach (var u in _roster)
            if (u.Equipment is { } e)
                counts[e.Id] = counts.GetValueOrDefault(e.Id) + 1;
        return counts;
    }

    /// <summary>Poids de tirage d'un équipement au coffre : réduit s'il est déjà possédé en double (anti-doublon).</summary>
    private static double EquipmentDropWeight(Equipment e, IReadOnlyDictionary<string, int> owned) =>
        owned.GetValueOrDefault(e.Id) >= DuplicateThreshold ? DuplicateWeight : 1.0;

    /// <summary>
    /// Détermine la rareté d'un coffre à partir d'un tirage <paramref name="roll"/> dans [0,100) et MET À JOUR
    /// la pitié : chances = <c>base de la phase + pitié</c> (légendaire d'abord, puis rare, sinon commun).
    /// Un coffre sans légendaire ajoute <see cref="LegendaryPityStep"/> à <see cref="LegendaryPity"/> (remis à
    /// zéro sur un légendaire) ; un coffre sans rare ajoute <see cref="RarePityStep"/> à <see cref="RarePity"/>
    /// (remis à zéro sur un rare). Les deux compteurs sont INDÉPENDANTS. Exposé pour le tirage ET les tests.
    /// </summary>
    public EquipmentRarity ResolveChestRarity(double roll)
    {
        var p = Math.Clamp(PhaseIndex, 1, PhaseCount) - 1;
        var legendaryChance = LegendaryChanceByPhase[p] + LegendaryPity;
        var rareChance = RareChanceByPhase[p] + RarePity;

        var rarity =
            roll < legendaryChance ? EquipmentRarity.Legendary
            : roll < legendaryChance + rareChance ? EquipmentRarity.Rare
            : EquipmentRarity.Common;

        LegendaryPity = rarity == EquipmentRarity.Legendary ? 0 : LegendaryPity + LegendaryPityStep;
        RarePity = rarity == EquipmentRarity.Rare ? 0 : RarePity + RarePityStep;
        return rarity;
    }

    /// <summary>
    /// Assets des classes de base tier 1 DÉBLOQUÉES au combat courant : à marquer « vues » (méta-progression)
    /// quand la vague apparaît, pour que la tuile recrue puisse les proposer ensuite à tout moment.
    /// </summary>
    public IEnumerable<string> UnlockedTier1Assets()
    {
        var reach = FirstRun ? CombatNumber : CombatNumber + 1;
        var unlocked = Math.Min(reach, IntroOrder.Length);
        for (var i = 0; i < unlocked; i++)
            yield return Domaines.Of(IntroOrder[i]).BaseClass.Asset;
    }

    /// <summary>Ajoute un pion à l'armée (réserve). Utilisé hors recrutement (ex. récompense d'une tuile recrue).</summary>
    public void AddUnit(UnitSpec spec) => _roster.Add(spec);

    /// <summary>
    /// Supprime définitivement un pion de la réserve (jamais le commandant), pour faire de la place sous le
    /// plafond <see cref="ReserveLimit"/>. Son équipement éventuel retourne à l'inventaire. Faux si le pion
    /// est essentiel ou absent du roster.
    /// </summary>
    public bool DeleteUnit(UnitSpec spec)
    {
        if (spec.Essential || !_roster.Contains(spec))
            return false;
        if (spec.Equipment is { } e)   // l'équipement du pion supprimé n'est pas perdu
        {
            _equipment.Add(e);
            spec.Equipment = null;
        }
        return _roster.Remove(spec);
    }

    // ─── RELANCE ─────────────────────────────────────────────────────────────────────────────────
    // Le joueur gagne 1 relance par PHASE (cumulable, cf. GrantPhaseRerollIfNewPhase) et peut en gagner
    // en CASSANT un équipement (AddReroll). RerollUnit dépense une relance pour échanger un pion contre
    // un autre du MÊME TIER, tiré parmi les classes DÉJÀ DÉCOUVERTES, la classe relancée exclue.

    /// <summary>
    /// Ajoute une relance : appelé quand le joueur CASSE un équipement pour en gagner une (sous-phase
    /// Équipement). L'équipement détruit ne revient pas à l'inventaire — c'est à l'appelant de ne pas le rendre.
    /// </summary>
    public void AddReroll() => _rerolls++;

    /// <summary>
    /// RELANCE <paramref name="spec"/> : le retire du roster et le remplace par un pion du MÊME TIER tiré au
    /// hasard parmi les classes DÉJÀ DÉCOUVERTES (<paramref name="isSeen"/>, tous domaines confondus), la
    /// classe relancée EXCLUE. Consomme une relance. L'équipement éventuel du pion retourne à l'inventaire
    /// (comme <see cref="DeleteUnit"/>). Renvoie le nouveau gabarit (ajouté au roster), ou <c>null</c> — SANS
    /// rien consommer — si impossible : plus de relance, pion essentiel/absent, ou aucun remplaçant découvert.
    /// L'effectif du roster est INCHANGÉ (retire 1, ajoute 1) : le plafond de réserve reste respecté.
    /// </summary>
    public UnitSpec? RerollUnit(UnitSpec spec, Random rng, Func<string, bool> isSeen)
    {
        if (_rerolls <= 0 || spec.Essential || !_roster.Contains(spec))
            return null;

        var pool = SeenClassesAtTier(spec.UnitClass.Tier, isSeen)
            .Where(x => x.Class != spec.UnitClass)   // « sauf la pièce relancée »
            .ToList();
        if (pool.Count == 0)
            return null;

        if (spec.Equipment is { } e)   // l'équipement du pion relancé n'est pas perdu
        {
            _equipment.Add(e);
            spec.Equipment = null;
        }
        _roster.Remove(spec);

        var pick = pool[rng.Next(pool.Count)];
        var replacement = new UnitSpec(pick.Domaine, pick.Class);
        _roster.Add(replacement);
        _rerolls--;
        return replacement;
    }

    /// <summary>Classes du tier donné DÉJÀ DÉCOUVERTES (méta-progression), tous domaines confondus, avec leur domaine.</summary>
    private static IEnumerable<(Domaine Domaine, UnitClass Class)> SeenClassesAtTier(int tier, Func<string, bool> isSeen)
    {
        foreach (var def in Domaines.All)
            foreach (var cls in ClassesAtTier(def.Id, tier))
                if (isSeen(cls.Asset))
                    yield return (def.Id, cls);
    }

    // ─── ÉQUIPEMENT ──────────────────────────────────────────────────────────────────────────────
    // Un équipement est « collé au pion » : posé sur un UnitSpec, il le suit d'un combat à l'autre et
    // disparaît avec lui (permadeath — voir CompleteCombat). L'inventaire (_equipment) ne contient que les
    // équipements NON équipés. La fusion rend les équipements des 3 pions à l'inventaire (l'évolution sort nue).

    /// <summary>Ajoute un équipement à l'inventaire (ex. butin d'un coffre).</summary>
    public void AddEquipment(Equipment equipment) => _equipment.Add(equipment);

    /// <summary>Retire un exemplaire d'équipement de l'inventaire (faux s'il n'y en a aucun).</summary>
    public bool RemoveEquipment(Equipment equipment) => _equipment.Remove(equipment);

    /// <summary>
    /// Vrai si <paramref name="spec"/> peut recevoir <paramref name="equipment"/> : pion non essentiel
    /// (le commandant ne s'équipe jamais). Un trait déjà natif de la classe est AUTORISÉ (il ne s'empile
    /// pas : sans effet supplémentaire, mais les éventuels bonus de stat de l'objet s'appliquent). Un objet
    /// « Attaque libre » (tir comme une Dame) est refusé au domaine Dame (redondant). Restrictions du domaine
    /// Cavalier (monté) : objet de PORTÉE refusé aux cavaliers de mêlée (sauf archer monté), objet de MOUVEMENT
    /// refusé à TOUS les cavaliers. Les autres équipements de stat passent toujours.
    /// </summary>
    public bool CanEquip(UnitSpec spec, Equipment equipment)
    {
        if (spec.Essential)
            return false;

        // « Attaque libre » fait tirer COMME UNE DAME : sans objet (et interdit) sur un pion déjà de domaine Dame.
        if (spec.Domaine == Domaine.Dame && equipment.GrantsTrait(Trait.AttaqueLibre))
            return false;

        // Le domaine Cavalier (monté) refuse deux familles d'objets :
        if (spec.Domaine == Domaine.Cavalier)
        {
            // • PORTÉE (arc) : aucun sens sur un cavalier de mêlée (lance/épée à cheval) — mais OK pour
            //   l'archer monté, déjà un tireur, repéré par sa zone morte de près (trait « Zone morte »).
            if (equipment.BonusFor(EquipStat.AttackRange) > 0 && !ClassHasTrait(spec.UnitClass, Trait.ZoneMorte))
                return false;
            // • MOUVEMENT (bottes) : la monture donne déjà la mobilité — interdit à TOUS les cavaliers,
            //   sans exception (l'archer monté non plus).
            if (equipment.BonusFor(EquipStat.MoveRange) > 0)
                return false;
        }
        return true;
    }

    /// <summary>Vrai si la classe possède NATIVEMENT ce trait (liste de traits, ou PiercesAllies pour « Traverse allié »).</summary>
    private static bool ClassHasTrait(UnitClass cls, string trait)
    {
        if (trait == Trait.TraverseAllie)
            return cls.PiercesAllies;
        return cls.Traits.Contains(trait);
    }

    /// <summary>
    /// Équipe <paramref name="spec"/> avec <paramref name="equipment"/> (pris dans l'inventaire) pendant
    /// le placement. Un seul équipement par pion ; le commandant n'en porte jamais (cf. <see cref="CanEquip"/>
    /// pour les restrictions de domaine). Si le pion en portait déjà un, l'ancien retourne à l'inventaire.
    /// Renvoie faux si la phase / le pion / l'item l'interdit.
    /// </summary>
    public bool Equip(UnitSpec spec, Equipment equipment)
    {
        if (Phase != RunPhase.Placement || !CanEquip(spec, equipment))
            return false;
        if (!_equipment.Remove(equipment))
            return false;
        if (spec.Equipment is { } old)
            _equipment.Add(old);
        spec.Equipment = equipment;
        return true;
    }

    /// <summary>Retire l'équipement de <paramref name="spec"/> et le rend à l'inventaire (sans effet s'il n'en a pas).</summary>
    public void Unequip(UnitSpec spec)
    {
        if (spec.Equipment is { } e)
        {
            _equipment.Add(e);
            spec.Equipment = null;
        }
    }

    // ── NOUVEAUTÉ IA (méta-progression des tiers 2-3) ─────────────────────────────────────────────
    // L'IA ne pioche ses T2/T3 QUE parmi les classes ÉLIGIBLES : celles déjà DÉCOUVERTES + un petit lot de
    // classes ENCORE INCONNUES (la « nouveauté » de la run). Ce lot est rendu éligible mais N'entre au codex
    // QUE si un exemplaire est réellement placé sur le plateau (la scène découvre à l'apparition) : une
    // nouveauté jamais alignée reste inconnue et pourra ressortir plus tard. Il est tiré UNE fois par run, au
    // 1er combat qui aligne le tier, puis FIGÉ et persisté (<see cref="RunSave"/>) pour que « Continuer »
    // rejoue la même vague. Taille = max(apport par run, socle − nb déjà découvert), bornée par le stock
    // inconnu restant :
    //   • profil vierge → on garantit le socle (2 en T2, 4 en T3) ;
    //   • ensuite → au moins l'apport de nouveauté par run (1 en T2, 2 en T3), donc du neuf à chaque run.
    // Comme la découverte-à-l'apparition grossit le set découvert d'une run à l'autre, ce même calcul donne
    // « au moins 4 T3 à la 1re run, puis +2 par run » sans compteur dédié.

    /// <summary>Apport de nouveauté (classes inconnues rendues éligibles à l'IA) par run, tier 2.</summary>
    public const int AiFreshTier2PerRun = 1;
    /// <summary>Socle minimal de classes T2 éligibles à l'IA au 1er combat qui en aligne (profil vierge).</summary>
    public const int AiMinEligibleTier2 = 2;
    /// <summary>Apport de nouveauté par run, tier 3 (« débloque 2 T3 possibles » à chaque nouvelle run).</summary>
    public const int AiFreshTier3PerRun = 2;
    /// <summary>Socle minimal de classes T3 éligibles à l'IA au 1er combat qui en aligne (au moins 4 à la 1re run).</summary>
    public const int AiMinEligibleTier3 = 4;

    // Nouveauté tirée pour CETTE run (null = pas encore calculée pour ce tier). Persistée dans RunSave.
    private List<string>? _aiFreshT2;
    private List<string>? _aiFreshT3;

    /// <summary>Nouveauté IA tier 2 tirée pour cette run (null si pas encore calculée) — pour la persistance/les tests.</summary>
    public IReadOnlyList<string>? AiFreshTier2 => _aiFreshT2;
    /// <summary>Nouveauté IA tier 3 tirée pour cette run (null si pas encore calculée) — pour la persistance/les tests.</summary>
    public IReadOnlyList<string>? AiFreshTier3 => _aiFreshT3;

    /// <summary>
    /// Classes du tier données rendues éligibles à l'IA au titre de la NOUVEAUTÉ de la run (cf. section
    /// ci-dessus). Calculée à la demande (1er combat qui aligne le tier), puis mémorisée et figée. Vide hors T2/T3.
    /// </summary>
    private IReadOnlyList<string> AiFreshFor(int tier, Func<string, bool> isSeen)
    {
        if (tier == 2)
            return _aiFreshT2 ??= RollAiFresh(2, isSeen, AiFreshTier2PerRun, AiMinEligibleTier2);
        if (tier == 3)
            return _aiFreshT3 ??= RollAiFresh(3, isSeen, AiFreshTier3PerRun, AiMinEligibleTier3);
        return Array.Empty<string>();
    }

    /// <summary>
    /// Tire les classes INCONNUES du tier à rendre éligibles à l'IA cette run. Déterministe pour une graine
    /// donnée (indépendant du numéro de combat : tirage stable quel que soit le combat déclencheur, et
    /// reproductible entre sessions). Vide si tout le tier est déjà découvert.
    /// </summary>
    private List<string> RollAiFresh(int tier, Func<string, bool> isSeen, int perRun, int floor)
    {
        var unknown = new List<string>();
        var discovered = 0;
        foreach (var def in Domaines.All)
            foreach (var cls in ClassesAtTier(def.Id, tier))
                if (isSeen(cls.Asset)) discovered++;
                else unknown.Add(cls.Asset);

        var want = Math.Min(Math.Max(perRun, floor - discovered), unknown.Count);
        if (want <= 0)
            return new List<string>();

        var rng = new Random(unchecked(Seed * 7919 + tier * 2731));
        for (var i = unknown.Count - 1; i > 0; i--)   // Fisher-Yates déterministe
        {
            var j = rng.Next(i + 1);
            (unknown[i], unknown[j]) = (unknown[j], unknown[i]);
        }
        return unknown.Take(want).ToList();
    }

    /// <summary>
    /// Vague ennemie du combat courant (le placement est assuré par la scène). L'effectif et la
    /// composition en TIERS viennent de la table maître <see cref="CampaignPlan"/>, indexée par
    /// (<see cref="PhaseIndex"/>, <see cref="MissionInPhase"/>) — TOUJOURS exacts et déterministes. Pour
    /// chaque tier requis : on tire un domaine dans le pool débloqué (<see cref="UnlockedDomaines"/>),
    /// puis une <see cref="UnitClass"/> de CE tier (<see cref="ClassesAtTier"/>). Aux tiers 2-3, si
    /// <paramref name="isSeen"/> est fourni (méta-progression), l'IA ne pioche QUE parmi les classes
    /// ÉLIGIBLES : découvertes + nouveauté de la run (filtre dur, cf. <see cref="PickEnemy"/> /
    /// <see cref="AiFreshFor"/>). Sur une mission boss, le pion <see cref="BossDef"/>
    /// est ajouté EN TÊTE. RNG déterministe (<see cref="CombatRng"/>) : « Continuer » rejoue la même vague
    /// tant que la découverte n'a pas changé (l'effectif et les tiers, eux, ne bougent jamais).
    /// </summary>
    public List<UnitSpec> BuildEnemyWave(Func<string, bool>? isSeen = null)
    {
        var rng = CombatRng(1);   // RNG déterministe propre à la vague de CE combat (reprise = même vague)
        var wave = new List<UnitSpec>();

        var pool = UnlockedDomaines();
        var counts = new Dictionary<UnitClass, int>();   // pour éviter au max plus de 2 fois la même classe
        foreach (var tier in AdjustTiers(CampaignPlan.For(PhaseIndex, MissionInPhase).Tiers, Difficulty))
            wave.Add(PickEnemy(rng, pool, tier, isSeen, counts));
        Shuffle(wave, rng);   // position aléatoire des types dans la vague (déterministe)
        EquipEnemies(wave);   // AVANT l'insertion du boss : lui n'est jamais équipé

        // Mission boss : le boss ASSIGNÉ à la phase courante est placé EN TÊTE (la scène le pose en premier).
        // Cf. BossSpecFor / BossOfPhase (tirage déterministe de 3 boss distincts par run).
        if (IsBossCombat)
            wave.Insert(0, BossSpecFor(PhaseIndex));

        return wave;
    }

    /// <summary>
    /// Vague d'une MISSION SPÉCIALE : EXACTEMENT <paramref name="count"/> ennemis (= le nombre de spawns
    /// dessinés sur la map), et non l'effectif fixe de la table. Les TIERS viennent de <paramref name="fixedTiers"/>
    /// s'il est fourni (composition FIXÉE dans l'éditeur, cf. <see cref="Map.MapData.EnemyTiers"/>), sinon du
    /// gabarit de la mission (<see cref="CampaignPlan"/>). Déterministe (<see cref="CombatRng"/>) : reprise = même vague.
    /// </summary>
    public List<UnitSpec> BuildSpecialEnemyWave(int count, Func<string, bool>? isSeen = null,
        IReadOnlyList<int>? fixedTiers = null) =>
        BuildScaledWave(count, isSeen, fixedTiers);

    /// <summary>
    /// Vague d'un combat BOSS sur MAP DESSINÉE : le pion <see cref="BossDef"/> EN TÊTE (la scène le
    /// pose sur une case B) + EXACTEMENT <paramref name="escortCount"/> escortes calées sur les autres cases
    /// de spawn de la map, pour que CHAQUE case dessinée soit occupée. Tiers = <paramref name="fixedTiers"/> si
    /// fourni (composition FIXÉE dans l'éditeur), sinon le gabarit de la mission (<see cref="CampaignPlan"/>).
    /// Déterministe (reprise = même vague). Hors map dessinée, c'est <see cref="BuildEnemyWave"/> (effectif
    /// FIXE de la table) qui s'applique — le boss y est déjà inséré en tête.
    /// </summary>
    public List<UnitSpec> BuildBossEnemyWave(int escortCount, Func<string, bool>? isSeen = null,
        IReadOnlyList<int>? fixedTiers = null)
    {
        var wave = BuildScaledWave(escortCount, isSeen, fixedTiers);
        wave.Insert(0, BossSpecFor(PhaseIndex));   // boss assigné à la phase, en tête (la scène le pose sur une case B)
        return wave;
    }

    /// <summary>
    /// Vague de <paramref name="count"/> ennemis dont les TIERS suivent le gabarit de la mission courante
    /// (<see cref="CampaignPlan"/>, cyclé si besoin) : la difficulté reste calée sur (phase, mission), l'effectif
    /// étant piloté par l'appelant (nb de cases de spawn de la map). Déterministe (<see cref="CombatRng"/>).
    /// Sert aux vagues « pilotées par la map » (mission spéciale et escortes de boss dessiné).
    /// </summary>
    private List<UnitSpec> BuildScaledWave(int count, Func<string, bool>? isSeen, IReadOnlyList<int>? fixedTiers = null)
    {
        var rng = CombatRng(1);
        var wave = new List<UnitSpec>();
        if (count <= 0)
            return wave;

        var pool = UnlockedDomaines();
        var counts = new Dictionary<UnitClass, int>();   // éviter au max plus de 2 fois la même classe
        // Tiers FIXÉS par la map (calque « tiers » de l'éditeur, boss/spéciale) s'ils existent, sinon le
        // gabarit de la mission (campaign.json). Cyclés si l'effectif dépasse la liste fournie.
        var template = fixedTiers is { Count: > 0 } ? fixedTiers : CampaignPlan.For(PhaseIndex, MissionInPhase).Tiers;

        // On déroule d'ABORD la vague réelle, puis on lui applique la difficulté UNE seule fois. Ajuster le
        // gabarit avant de le cycler multiplierait le décalage par le nombre de cycles.
        var tiers = new List<int>(count);
        for (var k = 0; k < count; k++)
            tiers.Add(template[k % template.Count]);
        tiers = AdjustTiers(tiers, Difficulty).ToList();

        foreach (var tier in tiers)
            wave.Add(PickEnemy(rng, pool, tier, isSeen, counts));
        Shuffle(wave, rng);
        EquipEnemies(wave);   // le boss est ajouté par l'appelant APRÈS : il n'est jamais équipé
        return wave;
    }

    /// <summary>Bornes de tier d'un pion (la table de campagne ne sort jamais de 1..3).</summary>
    private const int MinTier = 1, MaxTier = 3;

    /// <summary>
    /// Applique la DIFFICULTÉ à la composition en tiers d'une vague. La table de campagne
    /// (<c>campaign.json</c>) est calée sur <see cref="Difficulty.Normal"/>, qui ne change donc rien ;
    /// <see cref="Difficulty.Facile"/> RÉTROGRADE un pion du tier le plus haut et
    /// <see cref="Difficulty.Difficile"/> PROMEUT un pion du tier le plus bas — soit, sur une vague
    /// <c>{1,1,1,1,2,2,2}</c> : <c>{1,1,1,1,1,2,2}</c> en facile, <c>{1,1,1,2,2,2,2}</c> en difficile.
    ///
    /// UN SEUL pion est touché par vague, et l'EFFECTIF ne bouge jamais : réduire le nombre d'ennemis
    /// serait un levier bien plus fort (il change le rythme des tours et la pression sur le plateau).
    /// Quand il n'y a rien à faire — tout est déjà au tier 1 en facile, ou au tier 3 en difficile — la
    /// vague est renvoyée telle quelle : on ne compense pas ailleurs.
    ///
    /// Le boss d'une mission n'est PAS concerné : il est ajouté à part, hors de cette liste.
    /// </summary>
    public static IReadOnlyList<int> AdjustTiers(IReadOnlyList<int> tiers, Difficulty difficulty)
    {
        var shift = DifficultySettings.For(difficulty).TierShift;
        if (shift == 0 || tiers.Count == 0)
            return tiers;

        // Facile : on vise le pion le plus FORT (le seul qu'on puisse affaiblir sans passer sous le tier 1).
        // Difficile : on vise le plus FAIBLE — miroir exact, et ça évite de sortir un T3 dès la phase 1.
        var target = 0;
        for (var i = 1; i < tiers.Count; i++)
            if (shift < 0 ? tiers[i] > tiers[target] : tiers[i] < tiers[target])
                target = i;

        var shifted = tiers[target] + shift;
        if (shifted < MinTier || shifted > MaxTier)
            return tiers;   // déjà au plancher (tout T1) ou au plafond (tout T3)

        var adjusted = tiers.ToList();
        adjusted[target] = shifted;
        return adjusted;
    }

    /// <summary>Nombre MAX d'exemplaires d'une même classe qu'on cherche à ne pas dépasser dans une vague.</summary>
    private const int MaxSameUnit = 2;

    /// <summary>
    /// Tire un ennemi de tier <paramref name="tier"/> parmi le pool débloqué. Aux tiers 2-3 AVEC
    /// méta-progression (<paramref name="isSeen"/>), l'IA ne pioche QUE parmi les classes ÉLIGIBLES : déjà
    /// découvertes OU nouveauté de la run (<see cref="AiFreshFor"/>) — filtre DUR, sans repli sur tout le
    /// catalogue (garanti non vide par le socle de nouveauté). Au tier 1 (ou sans méta), tout le pool reste
    /// ouvert. Dans tous les cas on ÉVITE AU MAXIMUM les doublons : on tire d'abord parmi les classes
    /// (éligibles) qui n'ont pas encore <see cref="MaxSameUnit"/> exemplaires dans <paramref name="counts"/> ;
    /// si TOUTES sont saturées (pool trop petit), on autorise un exemplaire de plus (le tirage ne bloque
    /// jamais). Renvoie une classe du bon tier dans tous les cas (effectif/tiers de la table préservés).
    /// </summary>
    private UnitSpec PickEnemy(Random rng, IReadOnlyList<Domaine> pool, int tier,
        Func<string, bool>? isSeen, Dictionary<UnitClass, int> counts)
    {
        tier = Math.Min(tier, MaxUnitTier);   // mode démo : plafonne le tier des ennemis (jamais de T3)
        var metaTier = tier >= 2 && isSeen != null;
        var fresh = metaTier ? AiFreshFor(tier, isSeen!) : Array.Empty<string>();

        var all = new List<(Domaine Domaine, UnitClass Class)>();
        var eligible = new List<(Domaine Domaine, UnitClass Class)>();
        foreach (var domaine in pool)
            foreach (var cls in ClassesAtTier(domaine, tier))
            {
                all.Add((domaine, cls));
                if (metaTier && (isSeen!(cls.Asset) || fresh.Contains(cls.Asset)))
                    eligible.Add((domaine, cls));
            }

        // T2/T3 méta : UNIQUEMENT l'éligible (non vide grâce au socle de nouveauté) ; sinon tout. On évite les
        // doublons À L'INTÉRIEUR de ce sous-ensemble (jamais de repli sur « tout » qui casserait le filtre dur).
        var preferred = metaTier && eligible.Count > 0 ? eligible : all;
        var notMaxed = preferred.Where(x => counts.GetValueOrDefault(x.Class, 0) < MaxSameUnit).ToList();
        var from = notMaxed.Count > 0 ? notMaxed : preferred;

        var pick = from[rng.Next(from.Count)];
        counts[pick.Class] = counts.GetValueOrDefault(pick.Class, 0) + 1;
        return new UnitSpec(pick.Domaine, pick.Class);
    }

    /// <summary>
    /// Domaines ennemis DÉBLOQUÉS au combat courant (pool où l'on tire les types de la vague). Un type
    /// de plus par combat dans l'ordre d'<see cref="IntroOrder"/>, la première campagne
    /// (<see cref="FirstRun"/>) démarrant un cran plus doux (soldat seul au combat 1). Tout est ouvert
    /// dès la fin de la phase 1 : les phases 2-3 tirent parmi les 4 domaines.
    /// </summary>
    private IReadOnlyList<Domaine> UnlockedDomaines()
    {
        var reach = FirstRun ? CombatNumber : CombatNumber + 1;
        var unlocked = Math.Min(reach, IntroOrder.Length);
        return IntroOrder.Take(unlocked).ToList();
    }

    /// <summary>
    /// Toutes les <see cref="UnitClass"/> de tier <paramref name="tier"/> dans l'arbre du domaine
    /// (parcours en profondeur de la classe de base + ses évolutions). Sert à composer les vagues :
    /// un tier requis → une classe de ce tier tirée ici. Jamais vide pour un tier de l'arbre (1..3).
    /// </summary>
    public static IReadOnlyList<UnitClass> ClassesAtTier(Domaine domaine, int tier)
    {
        var result = new List<UnitClass>();
        CollectTier(Domaines.Of(domaine).BaseClass, tier, result);
        return result;
    }

    private static void CollectTier(UnitClass node, int tier, List<UnitClass> acc)
    {
        if (node.Tier == tier)
            acc.Add(node);
        foreach (var evolution in node.Evolutions)
            CollectTier(evolution, tier, acc);
    }

    /// <summary>Fin du placement : on passe au combat.</summary>
    public void StartBattle()
    {
        if (Phase == RunPhase.Placement)
            Phase = RunPhase.Battle;
    }

    /// <summary>Repasse en phase de placement SANS avancer le combat (fin du tutoriel → combat 1).</summary>
    public void ReturnToPlacement() => Phase = RunPhase.Placement;

    /// <summary>
    /// Combat gagné. <paramref name="casualties"/> = gabarits du roster morts pendant le combat
    /// (retirés : permadeath). <paramref name="defeatedEnemies"/> = ennemis vaincus DANS L'ORDRE de
    /// leur mort ; le recrutement propose les 3 derniers (le boss n'y figure jamais). Seul le boss
    /// FINAL (phase 3) → victoire ; tout le reste — y compris les boss des phases 1-2 — → recrutement.
    /// </summary>
    public void CompleteCombat(IEnumerable<UnitSpec> casualties, IReadOnlyList<UnitSpec> defeatedEnemies)
    {
        var dead = new HashSet<UnitSpec>(casualties);
        _roster.RemoveAll(u => !u.Essential && dead.Contains(u));
        CommandPoints += PointsPerMission;   // toute mission réussie, boss et spéciale comprises

        if (IsBossCombat && PhaseIndex >= EndAtPhase)   // boss de la phase de FIN → victoire (cf. EndAtPhase) ; les boss avant enchaînent
        {
            Phase = RunPhase.Victory;
            return;
        }

        BuildDraft(defeatedEnemies);
        Phase = RunPhase.Recruitment;
    }

    /// <summary>
    /// « Relève » (nœud d'arbre TROUPES) : pour chaque unité de TIER 2+ tombée au combat (parmi
    /// <paramref name="casualties"/>), fait arriver en réserve un pion tier 1 par recrue du nœud — un tier 1
    /// DÉJÀ VU au hasard, ou la classe de base d'un DOMAINE précisé par l'effet (ex. un Lancier). Dans la
    /// limite du plafond de réserve. Sans effet si le nœud n'est pas acheté. À appeler APRÈS
    /// <see cref="CompleteCombat"/> / <see cref="CompleteSpecialNoDraft"/> (les pertes retirées ont libéré la
    /// place). Renvoie les recrues ajoutées (pour un éventuel retour visuel).
    /// </summary>
    public IReadOnlyList<UnitSpec> GrantEliteDeathReplacements(
        IEnumerable<UnitSpec> casualties, Random rng, Func<string, bool> isSeen)
    {
        var added = new List<UnitSpec>();
        var elites = casualties.Count(c => c.UnitClass.Tier >= 2);
        if (elites <= 0)
            return added;

        // Un nœud « relève » peut préciser un domaine (ex. « mort T2/T3 → recrute un Lancier ») ; à défaut,
        // un tier 1 déjà vu au hasard. Chaque nœud produit elites × son Amount recrues.
        foreach (var e in ActiveEffects.Where(x => x.Kind == CommandEffectKind.EliteDeathRecruit))
            for (var i = 0; i < elites * e.Amount && !IsReserveFull; i++)
            {
                var recruit = RecruitFor(e, rng, isSeen);
                _roster.Add(recruit);
                added.Add(recruit);
            }
        return added;
    }

    /// <summary>Ajoute l'unité choisie à l'inventaire et lance le placement du combat suivant.</summary>
    public void Recruit(UnitSpec choice)
    {
        if (Phase != RunPhase.Recruitment)
            return;

        _roster.Add(new UnitSpec(choice.Domaine, choice.UnitClass));
        _draft.Clear();
        CombatNumber++;
        GrantPhaseRerollIfNewPhase();
        Phase = RunPhase.Placement;
    }

    /// <summary>
    /// Passe le recrutement SANS rien recruter et enchaîne le combat suivant. Cas d'une mission dont le
    /// draft est vide (ex. mission spéciale réussie sans avoir tué de garde) : la récompense était ailleurs
    /// (paysans ralliés en combat), il n'y a personne à drafter, mais la run doit avancer sans se bloquer.
    /// </summary>
    public void SkipRecruitment()
    {
        if (Phase != RunPhase.Recruitment)
            return;

        _draft.Clear();
        CombatNumber++;
        GrantPhaseRerollIfNewPhase();
        Phase = RunPhase.Placement;
    }

    /// <summary>
    /// Combat spécial terminé SANS draft : retire les pertes (permadeath) et passe à l'écran post-combat
    /// (<see cref="RunPhase.Recruitment"/>) où la SCÈNE affiche sa propre récompense (ex. « protéger » :
    /// tous les paysans sauvés). Aucune carte de draft n'est construite ; la scène ajoute les pions et
    /// avance via <see cref="AddUnit"/> + <see cref="SkipRecruitment"/>.
    /// </summary>
    public void CompleteSpecialNoDraft(IEnumerable<UnitSpec> casualties)
    {
        var dead = new HashSet<UnitSpec>(casualties);
        _roster.RemoveAll(u => !u.Essential && dead.Contains(u));
        CommandPoints += PointsPerMission;
        _draft.Clear();
        Phase = RunPhase.Recruitment;
    }

    public void Defeat() => Phase = RunPhase.Defeat;

    // ─── FUSION ────────────────────────────────────────────────────────────────────────────────
    // Pendant le PLACEMENT, fusionner FusionSize exemplaires d'une MÊME classe (même domaine + même
    // UnitClass) en 1 unité évoluée, choisie parmi les 2 évolutions de l'arbre. La fusion mute le
    // roster EN MÉMOIRE ; elle n'est PAS resauvegardée ici. Comme la progression n'est persistée
    // qu'au début de chaque phase de placement (côté scène), quitter avant de lancer le combat
    // annule la fusion (on revient au début du placement) ; lancer le combat la verrouille (elle
    // sera sauvegardée au placement du combat suivant). Permadeath : l'unité évoluée morte = les 3
    // exemplaires perdus. Les meneurs (commandant/boss, essentiels) ne fusionnent jamais. Une unité
    // déjà au sommet de son arbre (feuille) ne peut pas fusionner — l'arbre étant récursif, un futur
    // tier 3 réactiverait automatiquement la fusion une fois les évolutions ajoutées au JSON.

    /// <summary>
    /// Nombre d'exemplaires d'une même classe requis pour fusionner la classe de <paramref name="spec"/>.
    /// Base 3, RAMENÉ au minimum de 2 par un nœud « Amalgame »
    /// (<see cref="CommandEffectKind.FusionSizeReduction"/>) — GLOBAL, ou restreint à un domaine (ex. le Bastion :
    /// uniquement le domaine de la Tour). La réduction ne s'applique QU'AUX fusions de tier 1 vers tier 2
    /// (classe de base) : fusionner un tier 2 en tier 3 demande toujours 3 pions, sinon les hautes évolutions
    /// deviennent trop faciles à obtenir.
    /// </summary>
    public int FusionSizeFor(UnitSpec spec) =>
        System.Math.Max(2, BaseFusionSize - (spec.UnitClass.Tier > 1 ? 0 : ActiveEffects
            .Where(e => e.Kind == CommandEffectKind.FusionSizeReduction && (e.Domaine is null || e.Domaine == spec.Domaine))
            .Sum(e => e.Amount)));
    private const int BaseFusionSize = 3;

    /// <summary>Bonus de réduction du trait « Rempart » apporté par l'arbre (nœud « Rempart renforcé »). 0 = aucun.</summary>
    public int RempartBonus => TotalOf(CommandEffectKind.RempartBonus);

    /// <summary>Bonus (points de %) de chance du trait « Esquive » apporté par l'arbre (nœud « Esquive renforcée »). 0 = aucun.</summary>
    public int EsquiveBonusPercent => TotalOf(CommandEffectKind.EsquiveBonus);

    /// <summary>Bonus de dégâts du trait « Tueur de géants » apporté par l'arbre (nœud « Tueur de géant renforcé »). 0 = aucun.</summary>
    public int TueurDeGeantsBonus => TotalOf(CommandEffectKind.TueurDeGeantsBonus);

    /// <summary>Bonus de puissance par allié du trait « Formation » apporté par l'arbre (nœud « Formation renforcée »). 0 = aucun.</summary>
    public int FormationBonus => TotalOf(CommandEffectKind.FormationBonus);

    /// <summary>
    /// Deux gabarits sont de la MÊME classe (donc fusionnables ensemble) s'ils partagent domaine et
    /// classe. Source unique de la règle d'« identité » (réutilisée par l'UI réserve/plateau).
    /// </summary>
    public static bool SameClass(UnitSpec a, UnitSpec b) =>
        a.Domaine == b.Domaine && a.UnitClass == b.UnitClass;

    /// <summary>Nombre d'exemplaires non-essentiels de la classe de <paramref name="spec"/> dans le roster.</summary>
    public int CountFusable(UnitSpec spec) =>
        _roster.Count(u => !u.Essential && SameClass(u, spec));

    /// <summary>
    /// Vrai si <paramref name="spec"/> peut amorcer une fusion : en placement, non essentiel, classe
    /// non-feuille (évolutions disponibles) et assez d'exemplaires en roster (cf. <see cref="FusionSizeFor"/>).
    /// </summary>
    public bool CanFuse(UnitSpec spec) =>
        Phase == RunPhase.Placement
        && !spec.Essential
        && !spec.UnitClass.IsLeaf
        && spec.UnitClass.Tier < MaxUnitTier   // mode démo : coupe la fusion qui dépasserait le plafond (T2→T3)
        && CountFusable(spec) >= FusionSizeFor(spec);

    /// <summary>Les évolutions proposées au choix pour fusionner <paramref name="spec"/> (vide si impossible).</summary>
    public IReadOnlyList<UnitClass> FusionOptions(UnitSpec spec) =>
        CanFuse(spec) ? spec.UnitClass.Evolutions : System.Array.Empty<UnitClass>();

    /// <summary>
    /// Réalise la fusion : retire le nombre requis d'exemplaires de la classe de <paramref name="spec"/>
    /// (cf. <see cref="FusionSizeFor"/>) et ajoute 1 unité de la classe <paramref name="evolution"/> choisie.
    /// Renvoie le nouveau gabarit, ou <c>null</c> si la fusion est invalide (mauvaise phase, classe
    /// feuille/essentielle, pas assez d'exemplaires, ou évolution étrangère à l'arbre de la classe).
    /// </summary>
    public UnitSpec? Fuse(UnitSpec spec, UnitClass evolution)
    {
        if (!CanFuse(spec))
            return null;
        // Retire le nombre requis d'exemplaires (n'importe lesquels : ils sont identiques).
        var group = _roster.Where(u => !u.Essential && SameClass(u, spec)).Take(FusionSizeFor(spec)).ToList();
        return Fuse(group, evolution);
    }

    /// <summary>
    /// Variante EXPLICITE : fusionne précisément le nombre requis de gabarits donnés (instances réellement
    /// présentes au roster, de même classe non-feuille/non-essentielle ; cf. <see cref="FusionSizeFor"/>). Le
    /// caller choisit donc quelles instances sont consommées — indispensable côté scène, où roster, réserve et
    /// pièces posées partagent les mêmes instances <see cref="UnitSpec"/> : retirer les bonnes évite de
    /// désynchroniser la vue. Renvoie le nouveau gabarit (ajouté au roster), ou <c>null</c> si le groupe est invalide.
    /// </summary>
    public UnitSpec? Fuse(IReadOnlyList<UnitSpec> group, UnitClass evolution)
    {
        if (group.Count == 0)
            return null;
        var size = FusionSizeFor(group[0]);   // taille requise pour la classe fusionnée (domaine + tier)
        // Autorisée au PLACEMENT (drag-stack habituel) ET au RECRUTEMENT (faire de la place sous le plafond
        // de réserve en fusionnant, cf. écrans draft/récompense).
        if (Phase is not (RunPhase.Placement or RunPhase.Recruitment) || group.Count != size)
            return null;

        var first = group[0];
        if (first.Essential || first.UnitClass.IsLeaf || !first.UnitClass.Evolutions.Contains(evolution))
            return null;
        if (evolution.Tier > MaxUnitTier)   // mode démo : jamais d'unité au-dessus du plafond de tier
            return null;
        if (group.Distinct().Count() != size)                             // instances DISTINCTES
            return null;
        if (group.Any(u => !SameClass(u, first) || !_roster.Contains(u)))  // même classe + réellement au roster
            return null;

        foreach (var u in group)
        {
            if (u.Equipment is { } e)   // fusion : les équipements des 3 pions reviennent à l'inventaire
            {
                _equipment.Add(e);
                u.Equipment = null;
            }
            _roster.Remove(u);
        }

        var fused = new UnitSpec(first.Domaine, evolution);   // l'unité évoluée sort nue
        _roster.Add(fused);
        // Source de points PROPRE au commandant de départ : chaque fusion en rapporte (0 pour un commandant
        // dont la source de gain sera autre). Le bonus « recrue de fusion » (FusionRecruits), lui, est
        // appliqué par l'appelant : c'est lui qui sait quelles unités sont déjà découvertes.
        CommandPoints += CommanderDef.FusionPoints;
        return fused;
    }

    /// <summary>
    /// Recrutement = les <see cref="DraftSize"/> DERNIERS ennemis vaincus (dans l'ordre de leur mort),
    /// ou moins s'il y en a eu moins. Doublons conservés (reflète les pièces réellement abattues).
    /// Les gabarits sont posés tels quels ; ils s'affichent et se recrutent côté joueur (bleu).
    /// </summary>
    private void BuildDraft(IReadOnlyList<UnitSpec> defeatedEnemies)
    {
        _draft.Clear();
        var start = Math.Max(0, defeatedEnemies.Count - DraftSize);
        for (var i = start; i < defeatedEnemies.Count; i++)
            _draft.Add(defeatedEnemies[i]);
    }

    /// <summary>Mélange en place (Fisher-Yates) avec le RNG déterministe du combat.</summary>
    private static void Shuffle(List<UnitSpec> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Mélange en place une liste avec le RNG DÉTERMINISTE du combat (stable d'une session à l'autre,
    /// donc même tirage à la reprise d'une sauvegarde). Sert ex. à placer la vague ennemie sur des
    /// cases tirées au hasard parmi celles proposées par la map. <paramref name="salt"/> par défaut 2
    /// (≠ terrain=0, vague=1).
    /// </summary>
    public void ShuffleForCombat<T>(IList<T> list, int salt = 2)
    {
        var rng = CombatRng(salt);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ─── BOSS ─────────────────────────────────────────────────────────────────────────────────────
    // Chaque run tire 3 boss DISTINCTS (un par phase), déterministe via la graine (rejoué à l'identique
    // après « Continuer »). Le boss d'une phase combat avec le PROFIL (stats + traits) de CETTE phase.
    // Repli sur répétition si moins de boss éligibles que de phases. Cf. Bosses.AssignForRun.
    private IReadOnlyList<BossDef>? _bossAssignment;
    private IReadOnlyList<BossDef> BossAssignment =>
        _bossAssignment ??= Bosses.AssignForRun(Seed, PhaseCount, _unlockedCommanders);

    /// <summary>
    /// Ids des commandants DÉJÀ débloqués (méta-progression du profil), injectés par la scène au démarrage.
    /// La dernière phase priorise un boss dont le commandant n'est PAS ici (cf. <see cref="Bosses.AssignForRun"/>).
    /// </summary>
    private IReadOnlySet<string> _unlockedCommanders = new HashSet<string>();

    /// <summary>
    /// Renseigne les commandants déjà débloqués (profil global). À appeler AVANT le premier combat de boss :
    /// réinitialise le tirage mis en cache pour qu'il tienne compte de la priorité de déblocage.
    /// </summary>
    public void SetUnlockedCommanders(IReadOnlySet<string> ids)
    {
        _unlockedCommanders = ids ?? new HashSet<string>();
        _bossAssignment = null;
    }

    /// <summary>Boss (identité + profils par phase) assigné à la <paramref name="phase"/> (1..<see cref="PhaseCount"/>) de CETTE run.</summary>
    public BossDef BossOfPhase(int phase) => BossAssignment[phase - 1];

    /// <summary>Gabarit essentiel du boss d'une phase : son domaine de déplacement + le profil (stats/traits) de la phase.</summary>
    private UnitSpec BossSpecFor(int phase)
    {
        var boss = BossOfPhase(phase);
        return new UnitSpec(boss.Movement, boss.ProfileFor(phase), essential: true);
    }

    /// <summary>
    /// RNG DÉTERMINISTE pour le combat courant, dérivé de (<see cref="Seed"/>, <see cref="CombatNumber"/>,
    /// <paramref name="salt"/>) — stable d'une session à l'autre (pas de <c>HashCode.Combine</c> qui
    /// varie par process). <paramref name="salt"/> sépare terrain (0) et vague ennemie (1).
    /// </summary>
    private Random CombatRng(int salt) =>
        new(unchecked(Seed * 6151 + CombatNumber * 1031 + salt));

    /// <summary>Convertit une définition COMMANDE en gabarit essentiel (mouvement = son domaine).</summary>
    private static UnitSpec ToSpec(CommandeDef def) =>
        new(def.Movement, def.BaseClass, essential: true);
}
