using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Echec.Core.Campaign;

/// <summary>
/// Réglage d'UNE mission : taille du plateau (<see cref="MapSize"/>) + composition en TIERS des ennemis
/// (<see cref="Tiers"/> : sa LONGUEUR = nombre d'ennemis, chaque VALEUR = le tier d'un ennemi, 1..3).
/// </summary>
public sealed record MissionSetup(int MapSize, IReadOnlyList<int> Tiers);

/// <summary>
/// Plan de campagne : pour chaque (phase, mission) — <c>[phase-1][mission-1]</c> — la taille de map et la
/// composition ennemie. SOURCE DE VÉRITÉ du nombre d'ennemis, de leurs tiers et de la taille du plateau.
/// Chargé depuis <c>Assets/Config/campaign.json</c> ; à défaut, <see cref="Defaults"/> (repli codé, qui DOIT
/// refléter le JSON livré). Réglage « facile » : éditer le JSON suffit, aucun code à toucher.
///
/// Selon le type de mission (cf. <see cref="Run"/>) :
/// <list type="bullet">
///   <item>ESCARMOUCHE : effectif = longueur de <see cref="MissionSetup.Tiers"/> ; taille = <see cref="MissionSetup.MapSize"/>.</item>
///   <item>SPÉCIALE / BOSS : la liste de tiers sert de GABARIT (cyclée) ; l'effectif vient des cases de spawn
///   de la map dessinée, et <see cref="MissionSetup.MapSize"/> ne sert que de repli « terrain aléatoire ».</item>
/// </list>
/// </summary>
public static class CampaignPlan
{
    /// <summary>Nombre de phases et de missions par phase attendus (aligné sur <see cref="Run.PhaseCount"/> / <see cref="Run.MissionsPerPhase"/>).</summary>
    public const int Phases = 3;
    public const int MissionsPerPhase = 6;

    private static IReadOnlyList<IReadOnlyList<MissionSetup>> _table = Defaults();

    /// <summary>Remplace le plan (depuis le JSON). Ignoré si la table est incomplète (garde le repli).</summary>
    public static void Load(IReadOnlyList<IReadOnlyList<MissionSetup>> table)
    {
        if (IsComplete(table))
            _table = table;
    }

    /// <summary>
    /// Réglage de la mission (<paramref name="phaseIndex"/> 1..3, <paramref name="missionInPhase"/> 1..6).
    /// Borné pour ne jamais lever, même hors plage (repli sur la mission valide la plus proche).
    /// </summary>
    public static MissionSetup For(int phaseIndex, int missionInPhase)
    {
        var row = _table[Math.Clamp(phaseIndex, 1, _table.Count) - 1];
        return row[Math.Clamp(missionInPhase, 1, row.Count) - 1];
    }

    /// <summary>Parse le contenu de <c>campaign.json</c> en table (phase → missions). Lève si mal formé.</summary>
    public static IReadOnlyList<IReadOnlyList<MissionSetup>> FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<CampaignDto>(json, JsonOpts)
                  ?? throw new FormatException("campaign.json vide ou illisible.");
        if (dto.Phases is null || dto.Phases.Count == 0)
            throw new FormatException("campaign.json ne contient aucune phase.");

        var table = dto.Phases
            .Select(p => (IReadOnlyList<MissionSetup>)(p.Missions ?? new()).Select(ToSetup).ToList())
            .ToList();

        if (!IsComplete(table))
            throw new FormatException(
                $"campaign.json : attendu {Phases} phases de {MissionsPerPhase} missions, avec mapSize > 0 et une liste 'enemies' non vide (tiers 1..3).");
        return table;
    }

    private static MissionSetup ToSetup(MissionDto m)
    {
        if (m.MapSize <= 0)
            throw new FormatException($"campaign.json : mapSize invalide ({m.MapSize}).");
        if (m.Enemies is null || m.Enemies.Count == 0)
            throw new FormatException("campaign.json : liste 'enemies' absente ou vide.");
        if (m.Enemies.Any(t => t is < 1 or > 3))
            throw new FormatException("campaign.json : tier d'ennemi hors 1..3.");
        return new MissionSetup(m.MapSize, m.Enemies);
    }

    private static bool IsComplete(IReadOnlyList<IReadOnlyList<MissionSetup>> t) =>
        t.Count == Phases
        && t.All(r => r.Count == MissionsPerPhase
                      && r.All(s => s.MapSize > 0 && s.Tiers.Count > 0 && s.Tiers.All(v => v is >= 1 and <= 3)));

    // Repli codé — DOIT rester aligné sur Assets/Config/campaign.json. Reprend l'ancienne table WaveTiers
    // (effectif + tiers) et l'ancienne logique MapSizeFor (ph.1 = 6, sauf missions 4-5 = 7 ; ph.2 = 7 ; ph.3 = 8).
    public static IReadOnlyList<IReadOnlyList<MissionSetup>> Defaults() => new IReadOnlyList<MissionSetup>[]
    {
        new MissionSetup[] // Phase 1 — apprentissage (T1, le T2 arrive au combat 4)
        {
            new(6, new[] { 1, 1 }),                             // m1 Escarmouche : 2× T1
            new(6, new[] { 1, 1, 1 }),                          // m2 Escarmouche : 3× T1
            new(6, new[] { 1, 1, 1, 1 }),                       // m3 Escarmouche : 4× T1
            new(7, new[] { 1, 1, 1, 1, 1, 2 }),                 // m4 Spéciale    : gabarit T1/T2 (effectif = spawns)
            new(7, new[] { 1, 1, 1, 1, 1, 2, 2 }),              // m5 Escarmouche : 5× T1 + 2× T2
            new(6, new[] { 1, 1, 1, 1, 1, 1, 1, 2, 2 }),        // m6 Boss        : + 7× T1 + 2× T2
        },
        new MissionSetup[] // Phase 2 — montée en puissance (T2 dominant)
        {
            new(7, new[] { 1, 1, 1, 1, 2, 2, 2 }),              // m1 : 4× T1 + 3× T2
            new(7, new[] { 1, 1, 1, 1, 2, 2, 2, 2 }),           // m2 : 4× T1 + 4× T2
            new(7, new[] { 1, 1, 1, 2, 2, 2, 2, 2, 2 }),        // m3 Spéciale : 3× T1 + 6× T2
            new(7, new[] { 1, 1, 1, 2, 2, 2, 2, 2, 2 }),        // m4 : 3× T1 + 6× T2
            new(7, new[] { 1, 1, 2, 2, 2, 2, 2, 2, 2, 2 }),     // m5 : 2× T1 + 8× T2
            new(7, new[] { 1, 1, 1, 2, 2, 2, 2, 2, 2, 2 }),     // m6 Boss : + 3× T1 + 7× T2
        },
        new MissionSetup[] // Phase 3 — fin de run (T2 → T3)
        {
            new(8, new[] { 2, 2, 2, 2, 2, 3, 3, 3 }),           // m1 : 5× T2 + 3× T3
            new(8, new[] { 2, 2, 2, 2, 2, 3, 3, 3, 3 }),        // m2 : 5× T2 + 4× T3
            new(8, new[] { 2, 2, 2, 2, 3, 3, 3, 3, 3, 3 }),     // m3 Spéciale : 4× T2 + 6× T3
            new(8, new[] { 2, 2, 2, 2, 3, 3, 3, 3, 3, 3 }),     // m4 : 4× T2 + 6× T3
            new(8, new[] { 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3 }),  // m5 : 3× T2 + 8× T3
            new(8, new[] { 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3 }), // m6 BossFinal : + 4× T2 + 8× T3
        },
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class CampaignDto
    {
        public List<PhaseDto>? Phases { get; set; }
    }

    private sealed class PhaseDto
    {
        public List<MissionDto>? Missions { get; set; }
    }

    private sealed class MissionDto
    {
        public int MapSize { get; set; }
        public List<int>? Enemies { get; set; }
    }
}
