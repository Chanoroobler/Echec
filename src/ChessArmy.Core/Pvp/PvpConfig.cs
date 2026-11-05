using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Pvp;

/// <summary>Stats MULTI d'un commandant (sans arbre de compétences), cf. pvp.json « commanders ».</summary>
public sealed record PvpCommanderStats(int Hp, int Damage, int MoveRange, int AttackRange, IReadOnlyList<string> Traits);

/// <summary>Un rang (Bronze, Fer…) : cote de départ, nombre de divisions et étendue en points de cote.</summary>
public sealed record PvpRankDef(string Id, int From, int Divisions, int Span);

/// <summary>Niveau de bot d'un rang : précision, IA tactique, et ses armées écrites à la main.</summary>
public sealed record PvpBotDef(double Accuracy, bool Tactical, IReadOnlyList<PvpBuild> Armies);

/// <summary>
/// Barème du mode multijoueur (Assets/Config/pvp.json) : budget, coûts, coffres, gains, temps, rangs, stats
/// des commandants et bots. Valeurs par défaut codées si le fichier manque. État statique, comme les autres
/// catalogues du Core (cf. <see cref="Domaines"/>).
/// </summary>
public sealed class PvpConfig
{
    public static PvpConfig Current { get; private set; } = new();

    public static void Load(PvpConfig config) => Current = config;

    public int Budget { get; init; } = 100;
    public IReadOnlyDictionary<int, int> UnitCost { get; init; } = new Dictionary<int, int> { [1] = 4, [2] = 8, [3] = 13 };
    public IReadOnlyDictionary<EquipmentRarity, int> EquipmentCost { get; init; } =
        new Dictionary<EquipmentRarity, int> { [EquipmentRarity.Common] = 3, [EquipmentRarity.Rare] = 6, [EquipmentRarity.Legendary] = 10 };
    public int UnitChestCost { get; init; } = 60;
    public int EquipmentChestCost { get; init; } = 40;
    public int StartPoints { get; init; } = 100;
    public int PointsPerKill { get; init; } = 2;
    public int PointsForWin { get; init; } = 15;
    public int PointsForDraw { get; init; } = 5;
    public float ReserveSeconds { get; init; } = 600;
    public float MoveSeconds { get; init; } = 60;
    public float ReconnectSeconds { get; init; } = 60;
    public float BuildChoiceSeconds { get; init; } = 30;
    public float BotAfterSeconds { get; init; } = 40;
    public int StartRating { get; init; } = 950;
    public int EloK { get; init; } = 32;

    public IReadOnlyList<PvpRankDef> Ranks { get; init; } = new[]
    {
        new PvpRankDef("bronze", 0, 3, 300), new PvpRankDef("fer", 1100, 3, 300),
        new PvpRankDef("argent", 1400, 3, 300), new PvpRankDef("or", 1700, 3, 300),
        new PvpRankDef("master", 2000, 1, 0),
    };

    public IReadOnlyList<string> StartingCommanders { get; init; } =
        new[] { "commandant", "Commandant_lancier", "Commandant_cavalier" };

    public IReadOnlyDictionary<string, PvpCommanderStats> Commanders { get; init; } =
        new Dictionary<string, PvpCommanderStats>();

    public IReadOnlyDictionary<string, PvpBotDef> Bots { get; init; } = new Dictionary<string, PvpBotDef>();

    /// <summary>Coût d'un pion selon son tier (tiers inconnus : coût du tier connu le plus haut).</summary>
    public int CostOf(UnitClass cls) =>
        UnitCost.TryGetValue(cls.Tier, out var c) ? c : UnitCost.Values.DefaultIfEmpty(0).Max();

    public int CostOf(Equipment equipment) =>
        EquipmentCost.TryGetValue(equipment.Rarity, out var c) ? c : 0;

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
            UnitCost = dto.UnitCost?.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value) ?? defaults.UnitCost,
            EquipmentCost = dto.EquipmentCost?.ToDictionary(
                kv => System.Enum.Parse<EquipmentRarity>(kv.Key, ignoreCase: true), kv => kv.Value) ?? defaults.EquipmentCost,
            UnitChestCost = dto.ChestCost?.GetValueOrDefault("units") ?? defaults.UnitChestCost,
            EquipmentChestCost = dto.ChestCost?.GetValueOrDefault("equipment") ?? defaults.EquipmentChestCost,
            StartPoints = dto.StartPoints ?? defaults.StartPoints,
            PointsPerKill = dto.PointsPerKill ?? defaults.PointsPerKill,
            PointsForWin = dto.PointsForWin ?? defaults.PointsForWin,
            PointsForDraw = dto.PointsForDraw ?? defaults.PointsForDraw,
            ReserveSeconds = dto.ReserveSeconds ?? defaults.ReserveSeconds,
            MoveSeconds = dto.MoveSeconds ?? defaults.MoveSeconds,
            ReconnectSeconds = dto.ReconnectSeconds ?? defaults.ReconnectSeconds,
            BuildChoiceSeconds = dto.BuildChoiceSeconds ?? defaults.BuildChoiceSeconds,
            BotAfterSeconds = dto.BotAfterSeconds ?? defaults.BotAfterSeconds,
            StartRating = dto.StartRating ?? defaults.StartRating,
            EloK = dto.EloK ?? defaults.EloK,
            Ranks = dto.Ranks?.Select(r => new PvpRankDef(r.Id ?? "", r.From, System.Math.Max(1, r.Divisions), r.Span)).ToList()
                    ?? defaults.Ranks,
            StartingCommanders = dto.StartingCommanders ?? defaults.StartingCommanders,
            Commanders = dto.Commanders?.ToDictionary(kv => kv.Key,
                kv => new PvpCommanderStats(kv.Value.Hp, kv.Value.Damage, kv.Value.MoveRange, kv.Value.AttackRange,
                    (IReadOnlyList<string>?)kv.Value.Traits ?? System.Array.Empty<string>())) ?? defaults.Commanders,
            Bots = dto.Bots?.ToDictionary(kv => kv.Key, kv => new PvpBotDef(kv.Value.Accuracy, kv.Value.Tactical,
                (kv.Value.Armies ?? new List<ArmyDto>()).Select(a => a.ToBuild()).ToList())) ?? defaults.Bots,
        };
    }

    private sealed class Dto
    {
        public int? Budget { get; set; }
        public Dictionary<string, int>? UnitCost { get; set; }
        public Dictionary<string, int>? EquipmentCost { get; set; }
        public Dictionary<string, int>? ChestCost { get; set; }
        public int? StartPoints { get; set; }
        public int? PointsPerKill { get; set; }
        public int? PointsForWin { get; set; }
        public int? PointsForDraw { get; set; }
        public float? ReserveSeconds { get; set; }
        public float? MoveSeconds { get; set; }
        public float? ReconnectSeconds { get; set; }
        public float? BuildChoiceSeconds { get; set; }
        public float? BotAfterSeconds { get; set; }
        public int? StartRating { get; set; }
        public int? EloK { get; set; }
        public List<RankDto>? Ranks { get; set; }
        public List<string>? StartingCommanders { get; set; }
        public Dictionary<string, CommanderDto>? Commanders { get; set; }
        public Dictionary<string, BotDto>? Bots { get; set; }
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

    private sealed class BotDto
    {
        public double Accuracy { get; set; } = 1.0;
        public bool Tactical { get; set; }
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
