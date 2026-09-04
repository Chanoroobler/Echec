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
    /// Version du format. v6 = la nouveauté IA figée de la run (<see cref="AiFreshTier2"/> /
    /// <see cref="AiFreshTier3"/>) est persistée ; une sauvegarde v5 ou antérieure reste LISIBLE (nouveauté
    /// absente → null → retirée au 1er combat du tier, la reprise reste jouable).
    /// v5 = le chronomètre de la run (<see cref="RunStatsSave.PlayTime"/>) est persisté ;
    /// une sauvegarde v4 reste LISIBLE (chronomètre absent → run reprise à 0 s).
    /// v4 = le récap de run (<see cref="Stats"/>) est persisté (compteurs de fin de run).
    /// v3 = le commandant choisi (<see cref="CommanderId"/>) et la difficulté (<see cref="Difficulty"/>) sont
    /// persistés ; ces sauvegardes restent LISIBLES (récap absent → compteur neuf).
    /// v2 = campagne en 3 phases de 6 missions (<see cref="Run.TotalCombats"/> = 18) : ces sauvegardes
    /// restent LISIBLES — sans id, le commandant est retrouvé par l'asset de sa classe, et la difficulté
    /// absente vaut Normal.
    /// v1 = ancienne boucle plate de 6 combats : LISIBLES aussi (leur
    /// <see cref="CombatNumber"/> 1..6 tombe dans 1..18 et pointe désormais vers la nouvelle grille —
    /// combat 6 devient le boss de la PHASE 1, non plus le boss final ; migration acceptée telle quelle).
    /// En revanche un <see cref="CombatNumber"/> hors [1..18] est impossible sous ce format → à ignorer
    /// (cf. <see cref="IsUsable"/>).
    /// </summary>
    public int Version { get; set; } = 6;

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
            AiFreshTier2, AiFreshTier3, UltimateReviveUsed);
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

    public static UnitSpecSave From(UnitSpec spec) => new()
    {
        Domaine = spec.Domaine,
        Class = spec.UnitClass.Asset,
        Essential = spec.Essential,
        EquipmentIds = spec.Equipments.Select(e => e.Id).ToList(),
        Kills = spec.Kills,
    };

    public UnitSpec ToSpec()
    {
        UnitSpec spec;
        if (Essential)
        {
            // Unité COMMANDE (commandant) : retrouvée par asset dans le registre, repli sur le commandant.
            var def = Commandes.All.FirstOrDefault(c => c.BaseClass.Asset == Class) ?? Commandes.Commander;
            spec = new UnitSpec(def.Movement, def.BaseClass, essential: true) { Kills = Kills };
        }
        else
        {
            // Classe quelconque de l'arbre du domaine (base ou évolution), repli sur la classe de base.
            var cls = FindClass(Domaines.Of(Domaine).BaseClass, Class) ?? Domaines.Of(Domaine).BaseClass;
            spec = new UnitSpec(Domaine, cls) { Kills = Kills };
        }

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
