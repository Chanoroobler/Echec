using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Campaign;

/// <summary>
/// Forme sérialisable d'une <see cref="Run"/> : numéro de combat + inventaire. Sauvegardée pendant
/// la phase de placement (entre les combats), jamais en plein combat. Volontairement minimale et
/// stable : chaque unité est décrite par son domaine, l'asset de sa classe et le drapeau essentiel,
/// ce qui permet de reconstruire la <see cref="UnitClass"/> exacte même si l'arbre de classes évolue.
/// </summary>
public sealed class RunSave
{
    /// <summary>
    /// Version du format. v10 = commandant BRUTE : pertes cumulées de la run (<see cref="AllyDeaths"/>, qui
    /// alimente ses PV max et sa puissance) et CHOIX faits sur les nœuds d'arbre à options
    /// (<see cref="NodeChoices"/>, les évolutions du Paysan). Une sauvegarde v9 ou antérieure reste LISIBLE :
    /// compteur absent → 0, choix absents → aucun nœud à options acheté.
    /// v9 = map IMPOSÉE par l'éditeur de sauvegarde (<see cref="ForcedMap"/>), outil de
    /// test. Une sauvegarde v8 ou antérieure reste LISIBLE : champ absent → null → tirage habituel.
    /// v8 = « Continue sans moi » (arbre du DUO) : le legs d'un meneur tombé est persisté
    /// (<see cref="InheritedLeaderPower"/> / <see cref="InheritedLeaderHp"/>), le tombé ayant quitté le roster.
    /// Une sauvegarde v7 ou antérieure reste LISIBLE : legs absent → 0, les deux meneurs sont encore là.
    /// v7 = le commandant DUO : le SECOND meneur est marqué dans le roster
    /// (<see cref="UnitSpecSave.Companion"/>) et ses PV max ramassés sur le terrain sont persistés
    /// (<see cref="LeaderBonusHp"/>). Une sauvegarde v6 ou antérieure reste LISIBLE : sans commandant duo,
    /// les deux champs valent leur défaut et rien ne change.
    /// v6 = la nouveauté IA figée de la run (<see cref="AiFreshTier2"/> /
    /// <see cref="AiFreshTier3"/>) est persistée ; une sauvegarde v5 ou antérieure reste LISIBLE (nouveauté
    /// absente → null → retirée au 1er combat du tier, la reprise reste jouable).
    /// v5 = le chronomètre de la run (<see cref="RunStatsSave.PlayTime"/>) est persisté ;
    /// une sauvegarde v4 reste LISIBLE (chronomètre absent → run reprise à 0 s).
    /// v4 = le récap de run (<see cref="Stats"/>) est persisté (compteurs de fin de run).
    /// v3 = le commandant choisi (<see cref="CommanderId"/>) et la difficulté (<see cref="Difficulty"/>) sont
    /// persistés ; ces sauvegardes restent LISIBLES (récap absent → compteur neuf).
    /// v2 = campagne en 3 phases de 6 missions (<see cref="Run.TotalCombats"/> valait alors 18) : ces
    /// sauvegardes restent LISIBLES — sans id, le commandant est retrouvé par l'asset de sa classe, et la
    /// difficulté absente vaut Normal. La phase 3 ayant depuis PERDU son escarmouche d'avant-boss (17
    /// combats), un CombatNumber de 13 à 17 y désigne une mission décalée d'un cran, et 18 sort de la
    /// grille : la sauvegarde est alors ignorée (cf. <see cref="IsUsable"/>). Migration acceptée telle quelle.
    /// v1 = ancienne boucle plate de 6 combats : LISIBLES aussi (leur
    /// <see cref="CombatNumber"/> 1..6 tombe dans la grille et pointe désormais vers la nouvelle —
    /// combat 6 devient le boss de la PHASE 1, non plus le boss final ; migration acceptée telle quelle).
    /// En revanche un <see cref="CombatNumber"/> hors [1..<see cref="Run.TotalCombats"/>] est à ignorer
    /// (cf. <see cref="IsUsable"/>).
    /// </summary>
    public int Version { get; set; } = 10;

    public int CombatNumber { get; set; } = 1;

    /// <summary>
    /// Vrai si la sauvegarde est exploitable sur la grille de campagne actuelle : <see cref="CombatNumber"/>
    /// dans [1..<see cref="Run.TotalCombats"/>]. Hors bornes (données corrompues ou format futur
    /// incompatible) → slot traité comme vide au chargement.
    /// </summary>
    public bool IsUsable => CombatNumber >= 1 && CombatNumber <= Run.TotalCombats;

    /// <summary>Graine de la run : rejoue EXACTEMENT la même vague/terrain au combat repris.</summary>
    public int Seed { get; set; }

    /// <summary>Première campagne du joueur (déblocage ennemi plus doux) — conservé à la reprise.</summary>
    public bool FirstRun { get; set; }

    public List<UnitSpecSave> Roster { get; set; } = new();

    /// <summary>Équipements possédés mais NON équipés (ids du catalogue). Ceux équipés vivent sur leur unité.</summary>
    public List<string> Inventory { get; set; } = new();

    /// <summary>« Pitié » légendaire/rare accumulée (bonus de chance au prochain coffre). Absent (vieux save) → 0.</summary>
    public int LegendaryPity { get; set; }
    public int RarePity { get; set; }

    /// <summary>Points de commandement non dépensés. Absent (vieux save) → 0.</summary>
    public int CommandPoints { get; set; }

    /// <summary>Relances disponibles (1/phase cumulables + équipements cassés). Absent (vieux save) → 0.</summary>
    public int Rerolls { get; set; }

    /// <summary>
    /// Nœuds de l'arbre de commandement achetés (ids). Un id absent de l'arbre courant (JSON modifié depuis
    /// la sauvegarde) est ignoré au chargement — sans rembourser ses points.
    /// </summary>
    public List<string> CommandNodes { get; set; } = new();

    /// <summary>
    /// Id du commandant choisi à la création (<see cref="CommandeDef.Id"/>). Absent (sauvegarde v2 ou
    /// antérieure) → retrouvé par l'asset de la classe du commandant dans le roster.
    /// </summary>
    public string? CommanderId { get; set; }

    /// <summary>Difficulté choisie à la création, figée pour la run. Absente (vieux save) → Normal.</summary>
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;

    /// <summary>Récap CUMULÉ de la run (dégâts par classe, tués, perdus, déblocages…). Absent (save v3 ou
    /// antérieur) → compteur neuf à la reprise.</summary>
    public RunStatsSave? Stats { get; set; }

    /// <summary>
    /// Nouveauté IA figée pour la run : classes NON découvertes que l'IA peut aligner en plus des découvertes
    /// (cf. <see cref="Run.AiFreshTier2"/>). <c>null</c> = pas encore tirée pour ce tier (sera tirée au 1er
    /// combat qui l'aligne) ; absente d'une sauvegarde v5 ou antérieure → null → même comportement.
    /// </summary>
    public List<string>? AiFreshTier2 { get; set; }
    public List<string>? AiFreshTier3 { get; set; }

    /// <summary>« Renaissance ultime » (arbre du Marchand) DÉJÀ consommée : elle ne sert qu'une fois par
    /// partie. Absent (vieux save) → false.</summary>
    public bool UltimateReviveUsed { get; set; }

    /// <summary>
    /// COMMANDANT DUO : PV max GAGNÉS sur le terrain par les deux meneurs (trousses de soin utilisées +
    /// objets de sacoche ramassés, cf. <see cref="Run.LeaderBonusHp"/>). Cumulé sur la run, donc persisté.
    /// Absent (vieux save, ou tout autre commandant) → 0.
    /// </summary>
    public int LeaderBonusHp { get; set; }

    /// <summary>
    /// COMMANDANT DUO : puissance et PV max LÉGUÉS au meneur survivant par celui qui est tombé, nœud
    /// « Continue sans moi » (cf. <see cref="Run.InheritedLeaderPower"/>). Le tombé ayant quitté le
    /// <see cref="Roster"/>, ces deux valeurs sont tout ce qu'il reste de lui : sans elles, la reprise
    /// rendrait le survivant à ses stats d'avant. Absent (vieux save, ou tout autre commandant) → 0.
    /// </summary>
    public int InheritedLeaderPower { get; set; }

    /// <summary>PV max légués au meneur survivant (cf. <see cref="InheritedLeaderPower"/>).</summary>
    public int InheritedLeaderHp { get; set; }

    /// <summary>
    /// OUTIL DE TEST : map imposée à tous les combats de la run (cf. <see cref="Run.ForcedMapName"/>), posée
    /// par l'éditeur de sauvegarde. Absent/null — le cas de toute partie normale — : tirage habituel.
    /// </summary>
    public string? ForcedMap { get; set; }

    /// <summary>
    /// BRUTE : nombre d'unités alliées tombées depuis le début de la run (cf. <see cref="Run.AllyDeaths"/>).
    /// C'est LUI qui porte ses deux bonus de branche (PV max et puissance), d'où la persistance. Absent
    /// (v9 ou antérieure) → 0.
    /// </summary>
    public int AllyDeaths { get; set; }

    /// <summary>
    /// Choix retenus sur les nœuds d'arbre à OPTIONS : id du nœud → asset choisi (cf.
    /// <see cref="Run.NodeChoices"/>). Absent (v9 ou antérieure) → aucun.
    /// </summary>
    public Dictionary<string, string>? NodeChoices { get; set; }

    /// <summary>Nombre d'unités de l'inventaire (résumé léger pour l'écran de slots).</summary>
    public int UnitCount => Roster.Count;

    /// <summary>
    /// Temps de jeu cumulé de la run, en secondes (résumé léger pour l'écran de slots : évite de
    /// reconstruire une <see cref="Run"/> juste pour l'afficher). 0 si récap absent (sauvegarde v4 ou
    /// antérieure).
    /// </summary>
    public double PlayTimeSeconds => Stats?.PlayTime ?? 0;

    /// <summary>Capture l'état persistant d'une run en cours.</summary>
    public static RunSave From(Run run)
    {
        var save = new RunSave
        {
            CombatNumber = run.CombatNumber, Seed = run.Seed, FirstRun = run.FirstRun,
            LegendaryPity = run.LegendaryPity, RarePity = run.RarePity,
            CommandPoints = run.CommandPoints, CommandNodes = run.UnlockedNodes.ToList(),
            Rerolls = run.Rerolls,
            CommanderId = run.CommanderDef.Id, Difficulty = run.Difficulty,
            Stats = RunStatsSave.From(run.Stats),
            AiFreshTier2 = run.AiFreshTier2?.ToList(),
            AiFreshTier3 = run.AiFreshTier3?.ToList(),
            UltimateReviveUsed = run.UltimateReviveUsed,
            LeaderBonusHp = run.LeaderBonusHp,
            InheritedLeaderPower = run.InheritedLeaderPower,
            InheritedLeaderHp = run.InheritedLeaderHp,
            ForcedMap = run.ForcedMapName,
            AllyDeaths = run.AllyDeaths,
            NodeChoices = run.NodeChoices.Count == 0 ? null : new Dictionary<string, string>(run.NodeChoices),
        };
        foreach (var spec in run.Roster)
            save.Roster.Add(UnitSpecSave.From(spec));
        foreach (var equipment in run.EquipmentInventory)
            save.Inventory.Add(equipment.Id);
        return save;
    }

    /// <summary>Reconstruit une run jouable à partir de la sauvegarde (équipements résolus par id, ignorés si inconnus).</summary>
    public Run ToRun()
    {
        var roster = Roster.Select(s => s.ToSpec()).ToList();
        var inventory = Inventory
            .Select(Equipments.ById)
            .Where(e => e != null)
            .Select(e => e!)
            .ToList();
        return Run.Restore(roster, CombatNumber, Seed, FirstRun, inventory, LegendaryPity, RarePity,
            CommandPoints, CommandNodes, Rerolls, CommanderId, Difficulty, Stats?.ToStats(),
            AiFreshTier2, AiFreshTier3, UltimateReviveUsed, LeaderBonusHp,
            InheritedLeaderPower, InheritedLeaderHp, ForcedMap, AllyDeaths, NodeChoices);
    }
}

/// <summary>Forme sérialisable du récap cumulé d'une run (<see cref="RunStats"/>) — persistée dans le slot.</summary>
public sealed class RunStatsSave
{
    /// <summary>Dégâts infligés par NOM de classe.</summary>
    public Dictionary<string, int> Damage { get; set; } = new();

    public int Kills { get; set; }
    public int Lost { get; set; }
    public int Fusions { get; set; }
    public int Paysans { get; set; }
    public int Equipment { get; set; }

    /// <summary>Chronomètre de la run, en secondes. Absent (save v4 ou antérieur) → 0.</summary>
    public double PlayTime { get; set; }

    public List<string> UnlockedCommanders { get; set; } = new();
    public List<string> DiscoveredClasses { get; set; } = new();
    public List<string> DiscoveredEquipment { get; set; } = new();

    public static RunStatsSave From(RunStats s) => new()
    {
        Damage = new Dictionary<string, int>(s.DamageByClass),
        Kills = s.TotalKills, Lost = s.UnitsLost, Fusions = s.Fusions,
        Paysans = s.PaysansSaved, Equipment = s.EquipmentFound,
        PlayTime = s.PlayTimeSeconds,
        UnlockedCommanders = s.UnlockedCommanders.ToList(),
        DiscoveredClasses = s.DiscoveredClasses.ToList(),
        DiscoveredEquipment = s.DiscoveredEquipment.ToList(),
    };

    public RunStats ToStats()
    {
        var s = new RunStats();
        // Arguments NOMMÉS : la signature est longue et homogène (des entiers à la suite), un décalage
        // silencieux y passerait la compilation.
        s.Restore(damage: Damage, totalKills: Kills, unitsLost: Lost, fusions: Fusions,
            paysansSaved: Paysans, equipmentFound: Equipment,
            unlockedCommanders: UnlockedCommanders, discoveredClasses: DiscoveredClasses,
            discoveredEquipment: DiscoveredEquipment, playTimeSeconds: PlayTime);
        return s;
    }
}

/// <summary>Forme sérialisable d'un <see cref="UnitSpec"/> (un emplacement d'inventaire).</summary>
public sealed class UnitSpecSave
{
    public Domaine Domaine { get; set; }

    /// <summary>Asset de la classe (identifiant stable dans l'arbre du domaine).</summary>
    public string Class { get; set; } = "";

    public bool Essential { get; set; }

    /// <summary>
    /// SECOND meneur d'un commandant DUO (cf. <see cref="UnitSpec.Companion"/>) : essentiel comme lui, mais
    /// distingué pour que l'arbre vise le bon. Absent (vieux save, commandant solo) → false.
    /// </summary>
    public bool Companion { get; set; }

    /// <summary>
    /// HÉRITÉ (sauvegardes mono-slot) : id de l'UNIQUE équipement porté. Plus jamais écrit — relu seulement si
    /// <see cref="EquipmentIds"/> est absent. Cf. <see cref="ToSpec"/>.
    /// </summary>
    public string? Equipment { get; set; }

    /// <summary>
    /// Ids des équipements portés (collés au pion), dans l'ordre de pose. Le commandant peut en porter depuis
    /// l'arbre du MARCHAND. Absent (vieux save) → repli sur <see cref="Equipment"/>.
    /// </summary>
    public List<string>? EquipmentIds { get; set; }

    /// <summary>Total d'ennemis tués À VIE par ce pion. Absent (vieux save) → 0. Voir <see cref="UnitSpec.Kills"/>.</summary>
    public int Kills { get; set; }

    /// <summary>
    /// Paliers du trait « Survivant » (cf. <see cref="UnitSpec.SurvivantStacks"/>). Absent (vieux save) → 0.
    /// </summary>
    public int Survivant { get; set; }

    /// <summary>
    /// Porte les bonus « commandant » de l'arbre (cf. <see cref="UnitSpec.CommanderBody"/>). Absent (vieux
    /// save) → déduit de <see cref="Essential"/>, ce qui restitue exactement l'ancien comportement.
    /// </summary>
    public bool? CommanderBody { get; set; }

    public static UnitSpecSave From(UnitSpec spec) => new()
    {
        Domaine = spec.Domaine,
        Class = spec.UnitClass.Asset,
        Essential = spec.Essential,
        Companion = spec.Companion,
        CommanderBody = spec.CommanderBody,
        EquipmentIds = spec.Equipments.Select(e => e.Id).ToList(),
        Kills = spec.Kills,
        Survivant = spec.SurvivantStacks,
    };

    public UnitSpec ToSpec()
    {
        UnitSpec spec;
        // L'ASSET décide de la reconstruction, pas le drapeau « essentiel » : depuis « Révolte » (arbre de la
        // Brute), un commandant peut avoir cessé d'être essentiel et un simple Paysan l'être devenu.
        var commande = Commandes.All.FirstOrDefault(c => c.BaseClass.Asset == Class);
        if (commande == null && Essential && ExclusiveClasses.Find(Class) == null
            && FindClass(Domaines.Of(Domaine).BaseClass, Class) == null)
            commande = Commandes.Commander;   // asset de commandant disparu du JSON : repli historique

        if (commande != null)
        {
            var companion = Companion || commande.Role == CommandeRole.Companion;
            spec = new UnitSpec(commande.Movement, commande.BaseClass, essential: true, companion) { Kills = Kills };
        }
        else if (ExclusiveClasses.Find(Class) is { } exclusive)
        {
            // Pion EXCLUSIF (le Paysan de la Brute), éventuellement déjà évolué par un nœud d'arbre.
            spec = new UnitSpec(ExclusiveClasses.DomaineOf(Class) ?? Domaine, exclusive) { Kills = Kills };
        }
        else
        {
            // Classe quelconque de l'arbre du domaine (base ou évolution), repli sur la classe de base.
            var cls = FindClass(Domaines.Of(Domaine).BaseClass, Class) ?? Domaines.Of(Domaine).BaseClass;
            spec = new UnitSpec(Domaine, cls) { Kills = Kills };
        }

        spec.SurvivantStacks = Survivant;
        spec.SetRole(Essential, CommanderBody ?? (Essential && !Companion));

        // Multi-slot depuis le Marchand ; une sauvegarde plus ancienne n'a que le champ mono-slot.
        var ids = EquipmentIds ?? (Equipment is { } legacy ? new List<string> { legacy } : new List<string>());
        foreach (var id in ids)
            if (Equipments.ById(id) is { } item)   // équipement inconnu (catalogue modifié) → ignoré
                spec.AddEquipment(item);
        return spec;
    }

    /// <summary>Recherche en profondeur la classe d'asset donné dans un arbre de classes.</summary>
    private static UnitClass? FindClass(UnitClass root, string asset)
    {
        if (root.Asset == asset)
            return root;
        foreach (var evolution in root.Evolutions)
            if (FindClass(evolution, asset) is { } found)
                return found;
        return null;
    }
}
