using System.Collections.Generic;
using System.Linq;

namespace Echec.Core.Battle;

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
        string? unlocksCommander = null)
    {
        Name = name;
        Asset = asset;
        Movement = movement;
        Profiles = profiles;
        UnlocksCommander = unlocksCommander;
    }

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
