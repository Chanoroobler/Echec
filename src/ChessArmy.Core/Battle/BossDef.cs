using System.Collections.Generic;
using System.Linq;

namespace ChessArmy.Core.Battle;

/// <summary>
/// Définition d'un BOSS : une IDENTITÉ (nom, asset/sprite, domaine de déplacement) portant un PROFIL de
/// stats+traits PAR PHASE de campagne. Un même boss peut ainsi être plus fort — et gagner des traits — selon
/// la phase où il tombe. Chargé depuis units.json (champ <c>phases</c>), repli codé dans <see cref="Bosses"/>.
///
/// Le tirage des boss d'une run (un boss distinct par phase, déterministe) vit dans <see cref="Bosses"/> ;
/// une fois assigné à une phase, le boss est instancié via <see cref="ProfileFor"/> (cf. Campaign.Run).
/// </summary>
public sealed class BossDef
{
    public BossDef(string name, string asset, Domaine movement, IReadOnlyDictionary<int, UnitClass> profiles,
        string? unlocksCommander = null, IReadOnlyList<string>? equipmentPool = null,
        IReadOnlyDictionary<int, int>? equipmentCounts = null,
        string? companionId = null, string? requiresCommander = null,
        IReadOnlyDictionary<int, UnitClass>? companionProfiles = null)
    {
        _companionProfiles = companionProfiles;
        Name = name;
        Asset = asset;
        Movement = movement;
        Profiles = profiles;
        UnlocksCommander = unlocksCommander;
        EquipmentPool = equipmentPool ?? System.Array.Empty<string>();
        _equipmentCounts = equipmentCounts;
        CompanionId = companionId;
        RequiresCommander = requiresCommander;
    }

    private readonly IReadOnlyDictionary<int, int>? _equipmentCounts;

    public string Name { get; }

    /// <summary>Asset/sprite du boss (PNG <c>Assets/Units/&lt;asset&gt;_*.png</c>) — constant sur toutes les phases.</summary>
    public string Asset { get; }

    /// <summary>Domaine (parmi les 5) dont le boss emprunte le motif de déplacement — constant sur toutes les phases.</summary>
    public Domaine Movement { get; }

    /// <summary>Profil (classe : stats + traits) par phase (1..3). Au moins une entrée (garanti au chargement).</summary>
    public IReadOnlyDictionary<int, UnitClass> Profiles { get; }

    /// <summary>
    /// Id du COMMANDANT (cf. <see cref="CommandeDef.Id"/>) débloqué en battant ce boss en dernière phase.
    /// Null = ce boss ne débloque aucun commandant (ex. la Brute). Cf. la méta-progression du profil.
    /// </summary>
    public string? UnlocksCommander { get; }

    /// <summary>
    /// Id d'une <see cref="CommandeDef"/> de rôle <see cref="CommandeRole.Companion"/> qui ACCOMPAGNE ce boss :
    /// elle REMPLACE l'escorte du plus haut tier au lieu de s'y ajouter, pour que l'effectif reste celui des
    /// cases de spawn dessinées (cf. <c>Run.SubstituteBossCompanion</c>). Le second n'est PAS essentiel :
    /// l'abattre ne gagne pas le combat, seul le boss décide. Null = boss seul, le cas général.
    /// </summary>
    public string? CompanionId { get; }

    private readonly IReadOnlyDictionary<int, UnitClass>? _companionProfiles;

    /// <summary>
    /// Stats + traits du SECOND (<see cref="CompanionId"/>) pour cette <paramref name="phase"/>, réglables
    /// phase par phase comme celles du boss : sans ça il combattrait avec la fiche du second JOUABLE, qu'on ne
    /// peut pas équilibrer pour une rencontre de boss sans déséquilibrer le commandant du joueur.
    /// <c>null</c> = aucun profil déclaré, l'appelant retombe sur la classe de base du second.
    ///
    /// Seuls les CHIFFRES et les traits sont lus ici : le nom et le sprite viennent de la
    /// <see cref="CommandeDef"/> du second (cf. <c>Run.SubstituteBossCompanion</c>), ceux portés par ces
    /// <see cref="UnitClass"/> ne sont que des porte-valeurs. Repli sur la phase déclarée la plus proche,
    /// même règle que <see cref="ProfileFor"/>.
    /// </summary>
    public UnitClass? CompanionProfileFor(int phase)
    {
        if (_companionProfiles is not { Count: > 0 } profiles)
            return null;
        if (profiles.TryGetValue(phase, out var exact))
            return exact;
        var keys = profiles.Keys.OrderBy(k => k).ToList();
        var lower = keys.Where(k => k <= phase).ToList();
        return profiles[lower.Count > 0 ? lower[^1] : keys[0]];
    }

    /// <summary>
    /// Id d'un COMMANDANT (cf. <see cref="CommandeDef.Id"/>) que le joueur doit avoir DÉBLOQUÉ pour que ce
    /// boss entre dans le tirage (cf. <see cref="Bosses.AssignForRun"/>). Sert aux boss « miroir » d'un
    /// commandant jouable : les affronter avant de les connaître n'aurait pas de sens. Null = toujours
    /// tirable, le cas général.
    /// </summary>
    public string? RequiresCommander { get; }

    /// <summary>
    /// Ids d'équipement (equipment.json) où ce boss pioche ce qu'il porte, sans doublon. Vide = boss nu, quelle
    /// que soit la phase. Pool CHOISI À LA MAIN dans units.json : ni la rareté ni <c>enemyAllowed</c> ne le
    /// filtrent — un boss porte ce que le designer lui a mis, pas le butin ordinaire des ennemis de passage.
    /// </summary>
    public IReadOnlyList<string> EquipmentPool { get; }

    /// <summary>
    /// Nombre d'équipements portés à cette <paramref name="phase"/>, tirés dans <see cref="EquipmentPool"/>.
    /// 0 (boss nu) si la phase ne le déclare pas ou si le pool est vide. Borné par la taille du pool : on ne
    /// peut pas porter deux fois le même objet.
    /// </summary>
    public int EquipmentCountFor(int phase) =>
        EquipmentPool.Count == 0 || _equipmentCounts is null || !_equipmentCounts.TryGetValue(phase, out var n)
            ? 0
            : System.Math.Clamp(n, 0, EquipmentPool.Count);

    /// <summary>Vrai si ce boss DÉCLARE un profil pour cette phase (= éligible à y être tiré).</summary>
    public bool SupportsPhase(int phase) => Profiles.ContainsKey(phase);

    /// <summary>
    /// Profil (stats + traits) à utiliser pour cette <paramref name="phase"/>. Le profil exact s'il existe,
    /// sinon celui de la phase déclarée la plus proche (la plus haute ≤ demandée, à défaut la plus basse) —
    /// ainsi un boss reste utilisable même sur une phase qu'il ne déclare pas (repli du tirage).
    /// </summary>
    public UnitClass ProfileFor(int phase)
    {
        if (Profiles.TryGetValue(phase, out var exact))
            return exact;
        var keys = Profiles.Keys.OrderBy(k => k).ToList();
        var lower = keys.Where(k => k <= phase).ToList();
        return Profiles[lower.Count > 0 ? lower[^1] : keys[0]];
    }
}
