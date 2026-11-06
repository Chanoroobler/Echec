using System;
using System.Collections.Generic;
using System.Linq;

namespace ChessArmy.Core.Pvp;

/// <summary>Une ligne du classement multi : joueur, cote, bilan et la formation avec laquelle il a joué sa dernière
/// partie classée (null si elle ne se relit plus : pièce retirée, barème changé…).</summary>
public sealed record PvpLeaderboardEntry(ulong PlayerId, string Name, int GlobalRank, int Rating, int Wins, int Losses,
    int Draws, PvpBuild? Build);

/// <summary>
/// Classement multi stocké chez Steam : la COTE est le score, le reste tient dans les « détails » de l'entrée
/// (64 entiers au plus, <c>k_cLeaderboardDetailsMax</c> du SDK) : version, bilan V/D/N, commandant, puis deux entiers
/// par pion (case + classe, équipement). Les identifiants (classe, équipement, commandant) sont stockés par une
/// EMPREINTE stable de leur nom (jamais par leur rang dans un catalogue, qui bouge) et relus en cherchant
/// l'empreinte dans le catalogue courant.
/// </summary>
public static class PvpLeaderboard
{
    public const int MaxDetails = 64;
    private const int Version = 1;
    private const int Header = 5;   // version, victoires, défaites, nulles, commandant

    /// <summary>Détails d'une entrée : bilan du profil et formation jouée.</summary>
    public static int[] Encode(int wins, int losses, int draws, PvpBuild build)
    {
        var details = new List<int>(Header + 2 * build.Slots.Count)
        {
            Version, wins, losses, draws, Hash(build.CommanderId),
        };
        foreach (var slot in build.Slots)
        {
            // Case (3 bits de colonne + 1 de ligne) dans l'octet bas, empreinte de la classe sur les 24 bits hauts.
            details.Add((Hash24(slot.ClassAsset) << 8) | (slot.Column & 7) | ((slot.Line & 1) << 3));
            details.Add(slot.EquipmentId is { } e ? Hash(e) : 0);
        }
        return details.Take(MaxDetails).ToArray();
    }

    /// <summary>Relit des détails : bilan toujours, formation seulement si elle est encore jouable telle quelle
    /// (toutes ses pièces connues, <see cref="PvpCatalog.Problem"/> nul).</summary>
    public static (int Wins, int Losses, int Draws, PvpBuild? Build) Decode(IReadOnlyList<int> details)
    {
        if (details.Count < Header || details[0] != Version)
            return (0, 0, 0, null);
        var wins = Math.Max(0, details[1]);
        var losses = Math.Max(0, details[2]);
        var draws = Math.Max(0, details[3]);
        var catalog = Catalog();
        if (!catalog.Commanders.TryGetValue(details[4], out var commander))
            return (wins, losses, draws, null);
        var build = new PvpBuild { CommanderId = commander };
        for (var i = Header; i + 1 < details.Count; i += 2)
        {
            var packed = details[i];
            if (!catalog.Classes.TryGetValue((packed >> 8) & 0xFFFFFF, out var cls))
                return (wins, losses, draws, null);
            string? equipment = null;
            if (details[i + 1] != 0 && !catalog.Equipment.TryGetValue(details[i + 1], out equipment))
                return (wins, losses, draws, null);
            if (!build.Set(packed & 7, (packed >> 3) & 1, cls, equipment))
                return (wins, losses, draws, null);
        }
        return (wins, losses, draws, PvpCatalog.Problem(build) == null ? build : null);
    }

    /// <summary>
    /// Armée d'un bot de rang <paramref name="botKey"/> : EN PRIORITÉ la formation exacte d'un vrai joueur du même
    /// rang (hors <paramref name="exceptPlayer"/>, le joueur local) ; s'il y en a peu, une chance de prendre une
    /// armée habituelle du bot, pour ne pas toujours croiser la même formation. Chance de copie : 15 % par
    /// formation disponible, plafonnée à 75 %. Null = garder une armée habituelle.
    /// </summary>
    public static PvpBuild? PickPlayerFormation(IEnumerable<PvpLeaderboardEntry> entries, string botKey,
        ulong exceptPlayer, Random rng)
    {
        var candidates = entries
            .Where(e => e.PlayerId != exceptPlayer && e.Build != null && PvpRanks.BotKey(e.Rating) == botKey)
            .Select(e => e.Build!)
            .ToList();
        if (candidates.Count == 0)
            return null;
        var chance = Math.Min(0.75, 0.15 * candidates.Count);
        return rng.NextDouble() < chance ? candidates[rng.Next(candidates.Count)].Clone() : null;
    }

    // ── Empreintes ───────────────────────────────────────────────────────────────

    private sealed record Lookup(Dictionary<int, string> Commanders, Dictionary<int, string> Classes,
        Dictionary<int, string> Equipment);

    private static Lookup? s_lookup;
    private static PvpConfig? s_lookupFor;

    private static Lookup Catalog()
    {
        if (s_lookup != null && ReferenceEquals(s_lookupFor, PvpConfig.Current))
            return s_lookup;
        var lookup = new Lookup(new(), new(), new());
        foreach (var c in PvpCatalog.Commanders())
            lookup.Commanders[Hash(c.Id)] = c.Id;
        foreach (var (_, cls) in PvpCatalog.AllUnitClasses())
            lookup.Classes[Hash24(cls.Asset)] = cls.Asset;
        foreach (var e in PvpCatalog.AllEquipment())
            lookup.Equipment[Hash(e.Id)] = e.Id;
        s_lookupFor = PvpConfig.Current;
        return s_lookup = lookup;
    }

    /// <summary>FNV-1a 32 bits du nom (stable d'une version du jeu à l'autre, jamais 0).</summary>
    public static int Hash(string id)
    {
        var h = 2166136261u;
        foreach (var ch in id)
        {
            h ^= ch;
            h *= 16777619u;
        }
        return h == 0 ? 1 : unchecked((int)h);
    }

    public static int Hash24(string id) => (int)((uint)Hash(id) & 0xFFFFFF);
}
