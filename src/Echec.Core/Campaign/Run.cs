using System;
using System.Collections.Generic;
using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Command;
using Echec.Core.Equip;
using Echec.Core.Map;

namespace Echec.Core.Campaign;

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
    /// boss de la phase 2 ; remettre <c>= PhaseCount</c> (3) pour la campagne COMPLÈTE. Toute la machinerie des
    /// 3 phases (vagues, boss, coffres) reste EN PLACE : seule la fin de run est avancée.
    /// </summary>
    public const int EndAtPhase = 2;

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

    public Run(int? seed = null, bool firstRun = false)
    {
        Seed = seed ?? new Random().Next();
        FirstRun = firstRun;
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

    /// <summary>Arbre de commandement du commandant courant.</summary>
    public CommandTree Tree => CommandTrees.For(CommanderDef);

    /// <summary>Points de commandement disponibles (non dépensés).</summary>
    public int CommandPoints { get; private set; }

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
        CommandBuffs.From(ActiveEffects, spec.Essential, DistinctPairs);

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

    /// <summary>(Re)démarre une campagne : commandant + 2 soldats, combat 1, arbre de commandement vierge.</summary>
    public void Reset()
    {
        CommanderDef = Commandes.Commander;
        _roster.Clear();
        _roster.Add(ToSpec(CommanderDef));
        _roster.Add(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
        _roster.Add(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
        _draft.Clear();
        _equipment.Clear();
        _unlocked.Clear();
        CommandPoints = 0;
        CombatNumber = 1;
        _rerolls = 1;               // 1 relance offerte à l'ouverture de la phase 1
        LegendaryPity = 0;
        RarePity = 0;
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
        int commandPoints = 0, IReadOnlyList<string>? unlockedNodes = null, int rerolls = 0)
    {
        var run = new Run(seed, firstRun);
        run._roster.Clear();
        run._roster.AddRange(roster);
        run.CommanderDef = ResolveCommander(roster);
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
    /// Retrouve la définition du commandant sauvegardé par l'asset de sa classe (repli : le commandant de
    /// départ). Le roster ne conserve que des <see cref="UnitSpec"/> ; les plafonds et l'arbre vivent, eux,
    /// sur la <see cref="CommandeDef"/>.
    /// </summary>
    private static CommandeDef ResolveCommander(IReadOnlyList<UnitSpec> roster)
    {
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
    private static readonly int[] LegendaryChanceByPhase = { 2, 5, 10 };
    private static readonly int[] RareChanceByPhase = { 15, 25, 40 };
    /// <summary>Bonus de « pitié » ajouté par coffre qui ne donne pas de légendaire (cf. <see cref="LegendaryPity"/>).</summary>
    private const int LegendaryPityStep = 1;
    /// <summary>Bonus de « pitié » ajouté par coffre qui ne donne pas de rare (cf. <see cref="RarePity"/>).</summary>
    private const int RarePityStep = 2;

    /// <summary>Nombre d'exemplaires d'un même équipement à partir duquel il devient RARE au coffre (anti-doublon).</summary>
    private const int DuplicateThreshold = 2;

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

    /// <summary>
    /// Vague ennemie du combat courant (le placement est assuré par la scène). L'effectif et la
    /// composition en TIERS viennent de la table maître <see cref="CampaignPlan"/>, indexée par
    /// (<see cref="PhaseIndex"/>, <see cref="MissionInPhase"/>) — TOUJOURS exacts et déterministes. Pour
    /// chaque tier requis : on tire un domaine dans le pool débloqué (<see cref="UnlockedDomaines"/>),
    /// puis une <see cref="UnitClass"/> de CE tier (<see cref="ClassesAtTier"/>). Aux tiers 2-3, si
    /// <paramref name="isSeen"/> est fourni (méta-progression), on PRIVILÉGIE AU MAXIMUM les unités déjà
    /// découvertes (cf. <see cref="PickEnemy"/>). Sur une mission boss, le pion <see cref="BossDef"/>
    /// est ajouté EN TÊTE. RNG déterministe (<see cref="CombatRng"/>) : « Continuer » rejoue la même vague
    /// tant que la découverte n'a pas changé (l'effectif et les tiers, eux, ne bougent jamais).
    /// </summary>
    public List<UnitSpec> BuildEnemyWave(Func<string, bool>? isSeen = null)
    {
        var rng = CombatRng(1);   // RNG déterministe propre à la vague de CE combat (reprise = même vague)
        var wave = new List<UnitSpec>();

        var pool = UnlockedDomaines();
        var counts = new Dictionary<UnitClass, int>();   // pour éviter au max plus de 2 fois la même classe
        foreach (var tier in CampaignPlan.For(PhaseIndex, MissionInPhase).Tiers)
            wave.Add(PickEnemy(rng, pool, tier, isSeen, counts));
        Shuffle(wave, rng);   // position aléatoire des types dans la vague (déterministe)

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
        var tiers = fixedTiers is { Count: > 0 } ? fixedTiers : CampaignPlan.For(PhaseIndex, MissionInPhase).Tiers;
        for (var k = 0; k < count; k++)
            wave.Add(PickEnemy(rng, pool, tiers[k % tiers.Count], isSeen, counts));
        Shuffle(wave, rng);
        return wave;
    }

    /// <summary>Nombre MAX d'exemplaires d'une même classe qu'on cherche à ne pas dépasser dans une vague.</summary>
    private const int MaxSameUnit = 2;

    /// <summary>
    /// Tire un ennemi de tier <paramref name="tier"/> parmi le pool débloqué. Aux tiers 2-3 AVEC
    /// méta-progression (<paramref name="isSeen"/>), on PRIVILÉGIE AU MAXIMUM les classes déjà découvertes.
    /// Dans tous les cas on ÉVITE AU MAXIMUM les doublons : on tire d'abord parmi les classes (préférées) qui
    /// n'ont pas encore <see cref="MaxSameUnit"/> exemplaires dans <paramref name="counts"/> ; si TOUTES sont
    /// saturées (pool trop petit), on autorise un exemplaire de plus (le tirage ne bloque jamais). Renvoie une
    /// classe du bon tier dans tous les cas (effectif/tiers de la table préservés).
    /// </summary>
    private static UnitSpec PickEnemy(Random rng, IReadOnlyList<Domaine> pool, int tier,
        Func<string, bool>? isSeen, Dictionary<UnitClass, int> counts)
    {
        var all = new List<(Domaine Domaine, UnitClass Class)>();
        var seen = new List<(Domaine Domaine, UnitClass Class)>();
        foreach (var domaine in pool)
            foreach (var cls in ClassesAtTier(domaine, tier))
            {
                all.Add((domaine, cls));
                if (tier >= 2 && isSeen != null && isSeen(cls.Asset))
                    seen.Add((domaine, cls));
            }

        // Sous-ensemble PRÉFÉRÉ : les découverts (méta) s'il y en a, sinon tout. On évite les doublons À
        // L'INTÉRIEUR de ce sous-ensemble (fallback sur les préférés, jamais sur « tout », pour ne pas casser
        // la priorité méta ni la contrainte de domaine du 1er combat).
        var preferred = seen.Count > 0 ? seen : all;
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
    /// <paramref name="casualties"/>), fait arriver en réserve <see cref="EliteDeathRecruits"/> pion(s) tier 1
    /// aléatoire(s) DÉJÀ VU(S), dans la limite du plafond de réserve. Sans effet si le nœud n'est pas acheté.
    /// À appeler APRÈS <see cref="CompleteCombat"/> / <see cref="CompleteSpecialNoDraft"/> (les pertes retirées
    /// ont libéré la place). Renvoie les recrues ajoutées (pour un éventuel retour visuel).
    /// </summary>
    public IReadOnlyList<UnitSpec> GrantEliteDeathReplacements(
        IEnumerable<UnitSpec> casualties, Random rng, Func<string, bool> isSeen)
    {
        var added = new List<UnitSpec>();
        var perDeath = EliteDeathRecruits;
        if (perDeath <= 0)
            return added;

        var elites = casualties.Count(c => c.UnitClass.Tier >= 2);
        for (var i = 0; i < elites * perDeath && !IsReserveFull; i++)
        {
            var recruit = RollSeenTier1(rng, isSeen);
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

    /// <summary>Nombre d'exemplaires d'une même classe requis pour fusionner.</summary>
    public const int FusionSize = 3;

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
    /// non-feuille (évolutions disponibles) et au moins <see cref="FusionSize"/> exemplaires en roster.
    /// </summary>
    public bool CanFuse(UnitSpec spec) =>
        Phase == RunPhase.Placement
        && !spec.Essential
        && !spec.UnitClass.IsLeaf
        && CountFusable(spec) >= FusionSize;

    /// <summary>Les évolutions proposées au choix pour fusionner <paramref name="spec"/> (vide si impossible).</summary>
    public IReadOnlyList<UnitClass> FusionOptions(UnitSpec spec) =>
        CanFuse(spec) ? spec.UnitClass.Evolutions : System.Array.Empty<UnitClass>();

    /// <summary>
    /// Réalise la fusion : retire <see cref="FusionSize"/> exemplaires de la classe de
    /// <paramref name="spec"/> et ajoute 1 unité de la classe <paramref name="evolution"/> choisie.
    /// Renvoie le nouveau gabarit, ou <c>null</c> si la fusion est invalide (mauvaise phase, classe
    /// feuille/essentielle, pas assez d'exemplaires, ou évolution étrangère à l'arbre de la classe).
    /// </summary>
    public UnitSpec? Fuse(UnitSpec spec, UnitClass evolution)
    {
        if (!CanFuse(spec))
            return null;
        // Retire FusionSize exemplaires (n'importe lesquels : ils sont identiques).
        var group = _roster.Where(u => !u.Essential && SameClass(u, spec)).Take(FusionSize).ToList();
        return Fuse(group, evolution);
    }

    /// <summary>
    /// Variante EXPLICITE : fusionne précisément les <see cref="FusionSize"/> gabarits donnés (instances
    /// réellement présentes au roster, de même classe non-feuille/non-essentielle). Le caller choisit donc
    /// quelles instances sont consommées — indispensable côté scène, où roster, réserve et pièces posées
    /// partagent les mêmes instances <see cref="UnitSpec"/> : retirer les bonnes évite de désynchroniser
    /// la vue. Renvoie le nouveau gabarit (ajouté au roster), ou <c>null</c> si le groupe est invalide.
    /// </summary>
    public UnitSpec? Fuse(IReadOnlyList<UnitSpec> group, UnitClass evolution)
    {
        // Autorisée au PLACEMENT (drag-stack habituel) ET au RECRUTEMENT (faire de la place sous le plafond
        // de réserve en fusionnant, cf. écrans draft/récompense).
        if (Phase is not (RunPhase.Placement or RunPhase.Recruitment) || group.Count != FusionSize)
            return null;

        var first = group[0];
        if (first.Essential || first.UnitClass.IsLeaf || !first.UnitClass.Evolutions.Contains(evolution))
            return null;
        if (group.Distinct().Count() != FusionSize)                        // FusionSize instances DISTINCTES
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
    private IReadOnlyList<BossDef> BossAssignment => _bossAssignment ??= Bosses.AssignForRun(Seed, PhaseCount);

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
