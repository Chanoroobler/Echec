using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Pvp;

/// <summary>Stats MULTI d'un commandant (sans arbre de compétences), cf. pvp.json « commanders ».</summary>
public sealed record PvpCommanderStats(int Hp, int Damage, int MoveRange, int AttackRange, IReadOnlyList<string> Traits);

/// <summary>
/// Stats d'un pion PROPRES AU MULTI (pvp.json « unitStats ») : chaque valeur renseignée remplace celle de units.json,
/// en multi seulement (le solo ne bouge pas). Null = inchangé.
/// </summary>
public sealed record PvpUnitStats(int? Hp, int? Damage, int? MoveRange, int? AttackRange, IReadOnlyList<string>? Traits);

/// <summary>Un rang (Bronze, Fer…) : cote de départ, nombre de divisions et étendue en points de cote.</summary>
public sealed record PvpRankDef(string Id, int From, int Divisions, int Span);

/// <summary>
/// Intelligence du bot d'un rang (pvp.json « bots ») : plus le rang monte, plus il calcule loin, moins il se trompe.
/// Le NOMBRE de pions ne change jamais : toutes les armées des bots remplissent les mêmes budgets.
/// </summary>
public sealed record PvpBotDef
{
    /// <summary>Probabilité de jouer le coup qu'il juge le meilleur (sinon un des suivants).</summary>
    public double Accuracy { get; init; } = 1.0;

    /// <summary>Attaque dès qu'un de ses pions le peut (plancher de tous les rangs : jamais complètement bête).</summary>
    public bool ForceAttack { get; init; }

    /// <summary>Avec <see cref="ForceAttack"/> : probabilité de choisir la MEILLEURE attaque (une mise à mort plutôt
    /// qu'une simple blessure) ; sinon une attaque au hasard.</summary>
    public double KillChance { get; init; } = 1.0;

    /// <summary>Prudence : poids du danger de la case d'arrivée (0 = fonce sans regarder, 1 = pèse l'échange).</summary>
    public double Caution { get; init; } = 1.0;

    /// <summary>Imprudence : part des coups où il fonce SANS regarder le danger (prudence nulle). Réglage progressif
    /// des rangs bas : ignorer le danger tout le temps est une falaise, une fois sur deux une pente.</summary>
    public double Recklessness { get; init; }

    /// <summary>Profondeur de calcul : 0 = un coup à la fois ; 1 = prévoit la réponse adverse ; 2 = et sa propre
    /// réponse ; etc. (recherche alpha-bêta sur des copies de la partie, cf. <see cref="PvpForker"/>).</summary>
    public int Depth { get; init; }

    /// <summary>Coups examinés à la racine, et à chaque niveau plus profond.</summary>
    public int Breadth { get; init; } = 8;
    public int ReplyBreadth { get; init; } = 6;

    /// <summary>Temps de réflexion maximal EN JEU (secondes) : au-delà, le bot joue le meilleur coup de la dernière
    /// profondeur terminée. Les mesures de l'outil, elles, vont toujours jusqu'au bout.</summary>
    public double ThinkSeconds { get; init; } = 2.5;

    /// <summary>Temps de « réflexion » AFFICHÉ (secondes) avant de jouer, tiré au hasard entre les deux à chaque coup :
    /// le bot a l'air d'un joueur. Le vrai calcul se fait pendant ce temps (il ne l'allonge pas, sauf s'il dure plus).</summary>
    public double MinDelaySeconds { get; init; }
    public double MaxDelaySeconds { get; init; }

    /// <summary>Réflexion affichée quand le coup est ÉVIDENT (seul possible, commandant adverse à prendre, prise qui
    /// sauve le sien, cf. <see cref="PvpBotAi.Obvious"/>) : bien plus courte que la normale.</summary>
    public double ObviousMinDelaySeconds { get; init; } = 0.5;
    public double ObviousMaxDelaySeconds { get; init; } = 1.2;

    /// <summary>Le bot équipe ses pions (à partir d'Argent) : équipements TIRÉS AU HASARD à chaque partie dans le
    /// budget d'équipement, pour que la même armée ne revienne pas toujours pareille. Sinon, aucun équipement.</summary>
    public bool Equipment { get; init; } = true;

    /// <summary>Style de jeu du bot nommé de la partie (null = neutre), cf. <see cref="PvpBotStyle"/>.</summary>
    public PvpBotStyle? Style { get; init; }

    /// <summary>Avec <see cref="Equipment"/> : chaque objet tiré va au pion qui en profite le plus (Or, Master),
    /// au lieu d'un pion au hasard (cf. <see cref="PvpBotEquipment"/>). Mesuré : sans effet sur la force, il
    /// sert à ce que le bot ait l'air d'un vrai joueur (armure devant, portée aux tireurs).</summary>
    public bool SmartEquipment { get; init; }

    /// <summary>Poids du coût multi MESURÉ des pièces (traits compris) dans l'évaluation de CE niveau ; null = celui
    /// de « botWeights » (0). Donné à partir d'Or III : les rangs bas gardent leur lecture « aux stats ».</summary>
    public double? PieceCost { get; init; }

    /// <summary>L'armée telle que ce bot la joue pour une partie : équipements tirés au hasard, ou retirés.</summary>
    public PvpBuild ArmyFor(PvpBuild army, System.Random rng) =>
        Equipment ? PvpBotEquipment.Equip(army, rng, SmartEquipment) : army.WithoutEquipment();

    /// <summary>Armées que ce bot peut jouer (par défaut : la réserve commune « botArmies »), sans équipement :
    /// ils sont tirés à chaque partie (<see cref="ArmyFor"/>).</summary>
    public IReadOnlyList<PvpBuild> Armies { get; init; } = System.Array.Empty<PvpBuild>();

    public static PvpBotDef Default { get; } = new() { Accuracy = 0.8, ForceAttack = true, KillChance = 0.8 };
}

/// <summary>
/// Barème du mode multijoueur (Assets/Config/pvp.json) : budget, coûts, coffres, gains, temps, rangs, stats
/// des commandants et bots. Valeurs par défaut codées si le fichier manque. État statique, comme les autres
/// catalogues du Core (cf. <see cref="Domaines"/>).
/// </summary>
public sealed class PvpConfig
{
    public static PvpConfig Current { get; private set; } = new();

    public static void Load(PvpConfig config) => Current = config;

    /// <summary>Budget d'ARMÉE (commandant + pions, sans les équipements) : le coût de l'armée de DÉPART (elle remplit tout le budget, cf. outil
    /// d'équilibrage tools/ChessArmy.Balance qui le recalcule).</summary>
    public int Budget { get; init; } = 15;

    /// <summary>Budget SÉPARÉ des équipements : les points d'équipement ne se dépensent qu'en équipements et
    /// les points d'armée qu'en pions et commandant (l'un ne déborde jamais sur l'autre).</summary>
    public int EquipmentBudget { get; init; } = 10;

    /// <summary>Exemplaires d'un MÊME équipement dans une armée, au plus (« maxSameEquipment »).</summary>
    public int MaxSameEquipment { get; init; } = 2;

    /// <summary>
    /// Coût de chaque CLASSE (par asset), mesuré par l'outil d'équilibrage : en multi, le tier ne fixe pas le prix,
    /// seule la force réelle compte. Une classe absente coûte <see cref="DefaultCost"/>.
    /// </summary>
    public IReadOnlyDictionary<string, int> UnitCost { get; init; } = new Dictionary<string, int>();

    /// <summary>Coût de chaque ÉQUIPEMENT (par id), mesuré de même. Absent : <see cref="DefaultCost"/>.</summary>
    public IReadOnlyDictionary<string, int> EquipmentCost { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// Coût de chaque COMMANDANT (par id), mesuré de même : le plus faible coûte 0, chacun coûte son écart de force
    /// avec lui. Un commandant fort laisse moins de points pour l'armée. Absent : 0.
    /// </summary>
    public IReadOnlyDictionary<string, int> CommanderCost { get; init; } = new Dictionary<string, int>();

    /// <summary>Stats de pions propres au multi (par asset), qui priment sur units.json en multi seulement.</summary>
    public IReadOnlyDictionary<string, PvpUnitStats> UnitStats { get => _unitStats; init => _unitStats = value; }
    private IReadOnlyDictionary<string, PvpUnitStats> _unitStats = new Dictionary<string, PvpUnitStats>();

    /// <summary>Copie du barème avec d'autres stats de pions (outil d'équilibrage : essais successifs).</summary>
    public PvpConfig WithUnitStats(IReadOnlyDictionary<string, PvpUnitStats> stats)
    {
        var copy = (PvpConfig)MemberwiseClone();
        copy._unitStats = stats;
        return copy;
    }

    /// <summary>
    /// Bonus de stats d'équipement propres au multi (pvp.json « equipmentStats ») : par id, stat → montant, qui
    /// remplace le bonus de cette stat d'equipment.json en multi seulement (traits inchangés). Sert à affaiblir un
    /// objet trop fort pour le plafond de coût sans toucher au solo.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> EquipmentStats
    {
        get => _equipmentStats;
        init => _equipmentStats = value;
    }
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> _equipmentStats =
        new Dictionary<string, IReadOnlyDictionary<string, int>>();

    /// <summary>Copie du barème avec d'autres stats d'équipement multi (tests, essais de l'outil).</summary>
    public PvpConfig WithEquipmentStats(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> stats)
    {
        var copy = (PvpConfig)MemberwiseClone();
        copy._equipmentStats = stats;
        return copy;
    }

    /// <summary>Équipements RETIRÉS du multi (par id) : ni posables dans une armée, ni dans les coffres multi. Le solo
    /// les garde.</summary>
    public IReadOnlyCollection<string> ExcludedEquipment { get; init; } = new HashSet<string>();

    public const int DefaultCost = 1;
    public int UnitChestCost { get; init; } = 60;
    public int EquipmentChestCost { get; init; } = 40;
    public int StartPoints { get; init; } = 100;
    public int PointsPerKill { get; init; } = 5;
    public int PointsForWin { get; init; } = 30;
    public int PointsForDraw { get; init; } = 5;
    /// <summary>Part des points gagnée en ENTRAÎNEMENT (bot de rang choisi, sans cote) : 0.5 = moitié.</summary>
    public float TrainingPointsFactor { get; init; } = 0.5f;
    /// <summary>Part des points gagnée en DÉFI entre amis (0 = aucun point).</summary>
    public float FriendlyPointsFactor { get; init; }
    public float ReserveSeconds { get; init; } = 600;
    public float MoveSeconds { get; init; } = 60;
    public float ReconnectSeconds { get; init; } = 60;
    /// <summary>Sous cette réserve, chaque tour joué fait s'effondrer une tuile derrière le joueur
    /// (cf. <see cref="Battle.PvpSetup.CollapseAfterTurn"/>).</summary>
    public float CollapseBelowSeconds { get; init; } = 240;
    public float BuildChoiceSeconds { get; init; } = 30;
    /// <summary>Après le pile ou face : temps pour étudier l'armée adverse (pendules gelées). Fini plus tôt si
    /// les deux joueurs sont prêts.</summary>
    public float AnalysisSeconds { get; init; } = 60;
    /// <summary>Balance du moral : chaque pion tombé la fait pencher de son prix vers l'adversaire ; à ce seuil,
    /// victoire (cf. <see cref="Battle.Match.Morale"/>). 0 = pas de moral.</summary>
    public int MoraleToWin { get; init; } = 15;
    /// <summary>Moral perdu à chaque fin de tour par un camp réduit à son commandant (fuite sans fin du roi isolé).</summary>
    public int LoneCommanderMorale { get; init; } = 1;
    public float BotAfterSeconds { get; init; } = 10;
    /// <summary>Délai avant le bot quand d'AUTRES joueurs sont sur le jeu (un humain a des chances d'arriver).</summary>
    public float BotAfterBusySeconds { get; init; } = 40;

    /// <summary>Délai avant le bot : <see cref="BotAfterBusySeconds"/> si d'autres joueurs sont en ligne, sinon
    /// <see cref="BotAfterSeconds"/>.</summary>
    public float BotDelay(bool othersOnline) => othersOnline ? BotAfterBusySeconds : BotAfterSeconds;

    public int StartRating { get; init; } = 950;
    public int EloK { get; init; } = 50;
    /// <summary>Plafond de la cote (Elo, bonus de série compris) : on ne monte jamais au-dessus.</summary>
    public int MaxRating { get; init; } = 3000;
    /// <summary>Série de victoires classées : à partir de la <see cref="WinStreakFrom"/>e victoire d'affilée, chaque
    /// victoire rapporte <see cref="WinStreakBonus"/> de cote en plus de l'Elo.</summary>
    public int WinStreakFrom { get; init; } = 3;
    public int WinStreakBonus { get; init; } = 15;

    public IReadOnlyList<PvpRankDef> Ranks { get; init; } = new[]
    {
        new PvpRankDef("bronze", 0, 3, 300), new PvpRankDef("fer", 1100, 3, 300),
        new PvpRankDef("argent", 1400, 3, 300), new PvpRankDef("or", 1700, 3, 300),
        new PvpRankDef("master", 2000, 1, 0),
    };

    /// <summary>Armée de DÉPART (premier build de tout nouveau profil, et référence de l'équilibrage) : pions T1 qui
    /// remplissent le budget, commandant compris. Composée par l'outil d'équilibrage. Null = échiquier classique.</summary>
    public PvpBuild? StartingArmy { get; init; }

    /// <summary>Armée du bot TUTO (« tutorialArmy ») : petite, pour que le joueur gagne en voyant chaque règle.</summary>
    public PvpBuild? TutorialArmy { get; init; }

    public IReadOnlyList<string> StartingCommanders { get; init; } =
        new[] { "commandant", "Commandant_lancier", "Commandant_cavalier" };

    public IReadOnlyDictionary<string, PvpCommanderStats> Commanders { get; init; } =
        new Dictionary<string, PvpCommanderStats>();

    public IReadOnlyDictionary<string, PvpBotDef> Bots { get; init; } = new Dictionary<string, PvpBotDef>();

    /// <summary>Réserve COMMUNE des armées des bots (« botArmies », composée par l'outil d'équilibrage) : chacune
    /// remplit le budget d'armée et ne dépasse pas celui d'équipement.</summary>
    public IReadOnlyList<PvpBuild> BotArmies { get; init; } = System.Array.Empty<PvpBuild>();

    /// <summary>Poids de l'évaluation des bots qui calculent (« botWeights », ENTRAÎNÉS par auto-jeu, cf. outil).</summary>
    public PvpAiWeights BotWeights { get; init; } = new();

    /// <summary>Styles de jeu des bots nommés (« botStyles ») : chaque nom joue toujours le même style.</summary>
    public IReadOnlyList<PvpBotStyle> BotStyles { get; init; } = System.Array.Empty<PvpBotStyle>();

    public int CostOf(UnitClass cls) => UnitCost.TryGetValue(cls.Asset, out var c) ? c : DefaultCost;

    public int CostOf(Equipment equipment) => EquipmentCost.TryGetValue(equipment.Id, out var c) ? c : DefaultCost;

    public int CommanderCostOf(string commanderId) => CommanderCost.TryGetValue(commanderId, out var c) ? c : 0;

    // ── Lecture du JSON ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PvpConfig FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, Options) ?? new Dto();
        var defaults = new PvpConfig();
        return new PvpConfig
        {
            Budget = dto.Budget ?? defaults.Budget,
            EquipmentBudget = dto.EquipmentBudget ?? defaults.EquipmentBudget,
            MaxSameEquipment = dto.MaxSameEquipment ?? defaults.MaxSameEquipment,
            UnitCost = dto.UnitCost ?? defaults.UnitCost,
            EquipmentCost = dto.EquipmentCost ?? defaults.EquipmentCost,
            CommanderCost = dto.CommanderCost ?? defaults.CommanderCost,
            ExcludedEquipment = dto.ExcludedEquipment?.ToHashSet() ?? defaults.ExcludedEquipment,
            UnitStats = dto.UnitStats?.ToDictionary(kv => kv.Key, kv => new PvpUnitStats(kv.Value.Hp, kv.Value.Damage,
                kv.Value.MoveRange, kv.Value.AttackRange, kv.Value.Traits)) ?? defaults.UnitStats,
            EquipmentStats = dto.EquipmentStats?.ToDictionary(kv => kv.Key,
                kv => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(kv.Value, StringComparer.OrdinalIgnoreCase))
                ?? defaults.EquipmentStats,
            UnitChestCost = dto.ChestCost?.GetValueOrDefault("units") ?? defaults.UnitChestCost,
            EquipmentChestCost = dto.ChestCost?.GetValueOrDefault("equipment") ?? defaults.EquipmentChestCost,
            StartPoints = dto.StartPoints ?? defaults.StartPoints,
            PointsPerKill = dto.PointsPerKill ?? defaults.PointsPerKill,
            PointsForWin = dto.PointsForWin ?? defaults.PointsForWin,
            PointsForDraw = dto.PointsForDraw ?? defaults.PointsForDraw,
            TrainingPointsFactor = dto.TrainingPointsFactor ?? defaults.TrainingPointsFactor,
            FriendlyPointsFactor = dto.FriendlyPointsFactor ?? defaults.FriendlyPointsFactor,
            ReserveSeconds = dto.ReserveSeconds ?? defaults.ReserveSeconds,
            MoveSeconds = dto.MoveSeconds ?? defaults.MoveSeconds,
            ReconnectSeconds = dto.ReconnectSeconds ?? defaults.ReconnectSeconds,
            CollapseBelowSeconds = dto.CollapseBelowSeconds ?? defaults.CollapseBelowSeconds,
            BuildChoiceSeconds = dto.BuildChoiceSeconds ?? defaults.BuildChoiceSeconds,
            AnalysisSeconds = dto.AnalysisSeconds ?? defaults.AnalysisSeconds,
            MoraleToWin = dto.MoraleToWin ?? defaults.MoraleToWin,
            LoneCommanderMorale = dto.LoneCommanderMorale ?? defaults.LoneCommanderMorale,
            BotAfterSeconds = dto.BotAfterSeconds ?? defaults.BotAfterSeconds,
            BotAfterBusySeconds = dto.BotAfterBusySeconds ?? defaults.BotAfterBusySeconds,
            StartRating = dto.StartRating ?? defaults.StartRating,
            EloK = dto.EloK ?? defaults.EloK,
            MaxRating = dto.MaxRating ?? defaults.MaxRating,
            WinStreakFrom = dto.WinStreakFrom ?? defaults.WinStreakFrom,
            WinStreakBonus = dto.WinStreakBonus ?? defaults.WinStreakBonus,
            Ranks = dto.Ranks?.Select(r => new PvpRankDef(r.Id ?? "", r.From, System.Math.Max(1, r.Divisions), r.Span)).ToList()
                    ?? defaults.Ranks,
            StartingCommanders = dto.StartingCommanders ?? defaults.StartingCommanders,
            StartingArmy = dto.StartingArmy?.ToBuild() ?? defaults.StartingArmy,
            TutorialArmy = dto.TutorialArmy?.ToBuild() ?? defaults.TutorialArmy,
            Commanders = dto.Commanders?.ToDictionary(kv => kv.Key,
                kv => new PvpCommanderStats(kv.Value.Hp, kv.Value.Damage, kv.Value.MoveRange, kv.Value.AttackRange,
                    (IReadOnlyList<string>?)kv.Value.Traits ?? System.Array.Empty<string>())) ?? defaults.Commanders,
            BotArmies = dto.BotArmies?.Select(a => a.ToBuild()).ToList() ?? defaults.BotArmies,
            BotWeights = dto.BotWeights is { } w ? PvpAiWeights.FromDictionary(w) : defaults.BotWeights,
            BotStyles = dto.BotStyles?.Select(kv => new PvpBotStyle
            {
                Id = kv.Key, Advance = kv.Value.Advance, Caution = kv.Value.Caution, Aggression = kv.Value.Aggression,
                Guard = kv.Value.Guard, Names = (IReadOnlyList<string>?)kv.Value.Names ?? System.Array.Empty<string>(),
            }).ToList() ?? defaults.BotStyles,
            Bots = dto.Bots?.ToDictionary(kv => kv.Key, kv => new PvpBotDef
            {
                Accuracy = kv.Value.Accuracy, ForceAttack = kv.Value.ForceAttack, KillChance = kv.Value.KillChance,
                Caution = kv.Value.Caution, Recklessness = kv.Value.Recklessness, Depth = kv.Value.Depth, Breadth = kv.Value.Breadth,
                ReplyBreadth = kv.Value.ReplyBreadth, ThinkSeconds = kv.Value.ThinkSeconds,
                MinDelaySeconds = kv.Value.MinDelaySeconds, MaxDelaySeconds = kv.Value.MaxDelaySeconds,
                ObviousMinDelaySeconds = kv.Value.ObviousMinDelaySeconds, ObviousMaxDelaySeconds = kv.Value.ObviousMaxDelaySeconds,
                Equipment = kv.Value.Equipment, SmartEquipment = kv.Value.SmartEquipment, PieceCost = kv.Value.PieceCost,
                Armies = ((kv.Value.Armies is { Count: > 0 } own ? own
                           : kv.Value.BeginnerArmies ? dto.BotArmiesBeginner : dto.BotArmies) ?? new List<ArmyDto>())
                    .Select(a => a.ToBuild().WithoutEquipment()).ToList(),
            }) ?? defaults.Bots,
        };
    }

    private sealed class Dto
    {
        public int? Budget { get; set; }
        public int? EquipmentBudget { get; set; }
        public int? MaxSameEquipment { get; set; }
        public Dictionary<string, int>? UnitCost { get; set; }
        public Dictionary<string, int>? EquipmentCost { get; set; }
        public Dictionary<string, int>? CommanderCost { get; set; }
        public Dictionary<string, UnitStatsDto>? UnitStats { get; set; }
        public Dictionary<string, Dictionary<string, int>>? EquipmentStats { get; set; }
        public List<string>? ExcludedEquipment { get; set; }
        public Dictionary<string, int>? ChestCost { get; set; }
        public int? StartPoints { get; set; }
        public int? PointsPerKill { get; set; }
        public int? PointsForWin { get; set; }
        public int? PointsForDraw { get; set; }
        public float? TrainingPointsFactor { get; set; }
        public float? FriendlyPointsFactor { get; set; }
        public float? ReserveSeconds { get; set; }
        public float? MoveSeconds { get; set; }
        public float? ReconnectSeconds { get; set; }
        public float? CollapseBelowSeconds { get; set; }
        public float? BuildChoiceSeconds { get; set; }
        public float? AnalysisSeconds { get; set; }
        public int? MoraleToWin { get; set; }
        public int? LoneCommanderMorale { get; set; }
        public float? BotAfterSeconds { get; set; }
        public float? BotAfterBusySeconds { get; set; }
        public int? StartRating { get; set; }
        public int? EloK { get; set; }
        public int? MaxRating { get; set; }
        public int? WinStreakFrom { get; set; }
        public int? WinStreakBonus { get; set; }
        public List<RankDto>? Ranks { get; set; }
        public List<string>? StartingCommanders { get; set; }
        public ArmyDto? StartingArmy { get; set; }
        public ArmyDto? TutorialArmy { get; set; }
        public Dictionary<string, CommanderDto>? Commanders { get; set; }
        public Dictionary<string, BotDto>? Bots { get; set; }
        public List<ArmyDto>? BotArmies { get; set; }
        public List<ArmyDto>? BotArmiesBeginner { get; set; }
        public Dictionary<string, double>? BotWeights { get; set; }
        public Dictionary<string, StyleDto>? BotStyles { get; set; }
    }

    private sealed class UnitStatsDto
    {
        public int? Hp { get; set; }
        public int? Damage { get; set; }
        public int? MoveRange { get; set; }
        public int? AttackRange { get; set; }
        public List<string>? Traits { get; set; }
    }

    private sealed class RankDto
    {
        public string? Id { get; set; }
        public int From { get; set; }
        public int Divisions { get; set; } = 1;
        public int Span { get; set; }
    }

    private sealed class CommanderDto
    {
        public int Hp { get; set; }
        public int Damage { get; set; }
        public int MoveRange { get; set; }
        public int AttackRange { get; set; }
        public List<string>? Traits { get; set; }
    }

    private sealed class StyleDto
    {
        public double Advance { get; set; } = 1;
        public double Caution { get; set; } = 1;
        public double Aggression { get; set; } = 1;
        public double Guard { get; set; }
        public List<string>? Names { get; set; }
    }

    private sealed class BotDto
    {
        public double Accuracy { get; set; } = 1.0;
        public bool ForceAttack { get; set; }
        public double KillChance { get; set; } = 1.0;
        public double Caution { get; set; } = 1.0;
        public double Recklessness { get; set; }
        public int Depth { get; set; }
        public int Breadth { get; set; } = 8;
        public int ReplyBreadth { get; set; } = 6;
        public double ThinkSeconds { get; set; } = 2.5;
        public double MinDelaySeconds { get; set; }
        public double MaxDelaySeconds { get; set; }
        public double ObviousMinDelaySeconds { get; set; } = 0.5;
        public double ObviousMaxDelaySeconds { get; set; } = 1.2;
        public bool Equipment { get; set; } = true;
        public bool SmartEquipment { get; set; }
        public double? PieceCost { get; set; }
        /// <summary>Joue la réserve « botArmiesBeginner » (Bronze, Fer : pions de base) au lieu de « botArmies ».</summary>
        public bool BeginnerArmies { get; set; }
        public List<ArmyDto>? Armies { get; set; }
    }

    private sealed class ArmyDto
    {
        public string? Commander { get; set; }
        public List<string>? Back { get; set; }
        public List<string>? Front { get; set; }

        public PvpBuild ToBuild()
        {
            var build = new PvpBuild { CommanderId = Commander ?? "commandant" };
            AddLine(Back, PvpBuild.BackLine, build);
            AddLine(Front, PvpBuild.FrontLine, build);
            return build;
        }

        private static void AddLine(List<string>? line, int lineIndex, PvpBuild build)
        {
            if (line == null)
                return;
            for (var c = 0; c < line.Count && c < PvpBuild.Columns; c++)
            {
                var token = line[c].Trim();
                if (token.Length == 0 || token == "*")
                    continue;
                var plus = token.IndexOf('+');
                build.Slots.Add(new PvpBuildSlot(c, lineIndex,
                    plus < 0 ? token : token[..plus], plus < 0 ? null : token[(plus + 1)..]));
            }
        }
    }
}
