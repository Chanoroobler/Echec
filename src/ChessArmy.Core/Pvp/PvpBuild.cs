using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Pvp;

/// <summary>
/// Un pion d'un build : sa case (colonne 0..7, ligne <see cref="PvpBuild.BackLine"/> ou
/// <see cref="PvpBuild.FrontLine"/>), sa classe (asset) et son équipement éventuel (id).
/// </summary>
public sealed record PvpBuildSlot(int Column, int Line, string ClassAsset, string? EquipmentId);

/// <summary>
/// Armée composée par le joueur pour le multi : un commandant (case fixe, il coûte selon sa force) et des pions posés sur ses
/// deux lignes, chacun éventuellement équipé. Le coût total doit tenir dans le budget (cf. <see cref="PvpCatalog"/>).
/// </summary>
public sealed class PvpBuild
{
    public const int Columns = 8;
    public const int BackLine = 0;   // ligne du fond (celle du commandant)
    public const int FrontLine = 1;  // ligne de devant
    public const int KingColumn = 4; // case FIXE du commandant sur la ligne du fond

    public string CommanderId { get; set; } = "commandant";
    public List<PvpBuildSlot> Slots { get; } = new();

    public static bool IsKingCell(int column, int line) => line == BackLine && column == KingColumn;

    /// <summary>Case FIXE du compagnon d'un commandant DUO (Basile) : sur la ligne du fond, à gauche du commandant.</summary>
    public const int CompanionColumn = KingColumn - 1;

    /// <summary>Le commandant de ce build arrive avec un compagnon (DUO + Basile).</summary>
    public bool HasCompanion => PvpCatalog.CompanionClass(CommanderId) != null;

    /// <summary>Case occupée d'office : celle du commandant, et celle de son compagnon s'il en a un. Aucun pion n'y va.</summary>
    public bool IsReserved(int column, int line) =>
        IsKingCell(column, line) || (line == BackLine && column == CompanionColumn && HasCompanion);

    public PvpBuildSlot? SlotAt(int column, int line) =>
        Slots.FirstOrDefault(s => s.Column == column && s.Line == line);

    /// <summary>Pose (ou remplace) un pion sur une case libre de commandant. Faux sur la case du commandant.</summary>
    public bool Set(int column, int line, string classAsset, string? equipmentId = null)
    {
        if (IsReserved(column, line) || column < 0 || column >= Columns || line is not (BackLine or FrontLine))
            return false;
        Slots.RemoveAll(s => s.Column == column && s.Line == line);
        Slots.Add(new PvpBuildSlot(column, line, classAsset, equipmentId));
        return true;
    }

    public void Remove(int column, int line) => Slots.RemoveAll(s => s.Column == column && s.Line == line);

    public PvpBuild Clone()
    {
        var copy = new PvpBuild { CommanderId = CommanderId };
        copy.Slots.AddRange(Slots);
        return copy;
    }

    /// <summary>Même armée, mêmes cases, sans aucun équipement (bots des rangs Bronze et Fer).</summary>
    public PvpBuild WithoutEquipment()
    {
        var copy = new PvpBuild { CommanderId = CommanderId };
        foreach (var slot in Slots)
            copy.Slots.Add(slot with { EquipmentId = null });
        return copy;
    }

    // ── Sérialisation (sauvegarde + réseau) ──────────────────────────────────────

    private sealed class Dto
    {
        public string? C { get; set; }
        public List<SlotDto>? S { get; set; }
    }

    private sealed class SlotDto
    {
        public int X { get; set; }
        public int L { get; set; }
        public string? U { get; set; }
        public string? E { get; set; }
    }

    public string ToJson() => JsonSerializer.Serialize(new Dto
    {
        C = CommanderId,
        S = Slots.Select(s => new SlotDto { X = s.Column, L = s.Line, U = s.ClassAsset, E = s.EquipmentId }).ToList(),
    });

    /// <summary>Relit un build ; les cases hors grille ou celle du commandant sont ignorées. Null si illisible.</summary>
    public static PvpBuild? FromJson(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json);
            if (dto == null)
                return null;
            var build = new PvpBuild { CommanderId = dto.C ?? "commandant" };
            foreach (var s in dto.S ?? new List<SlotDto>())
                if (s.U != null)
                    build.Set(s.X, s.L, s.U, s.E);
            return build;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Ce que le multi sait faire des classes, commandants et équipements du jeu : retrouver une classe par son
/// asset, instancier le commandant avec ses stats MULTI, chiffrer et valider un build.
/// </summary>
public static class PvpCatalog
{
    /// <summary>Toutes les classes de pions des 4 domaines (tous tiers), hors commandants, avec leurs stats MULTI
    /// (cf. <see cref="PvpConfig.UnitStats"/>).</summary>
    public static IEnumerable<(Domaine Domaine, UnitClass Class)> AllUnitClasses()
    {
        foreach (var def in Domaines.All)
            foreach (var cls in Flatten(def.BaseClass))
                yield return (def.Id, WithMultiStats(cls));
    }

    private static readonly object s_statsLock = new();
    private static PvpConfig? s_statsFor;
    private static Dictionary<string, UnitClass> s_statsCache = new();

    /// <summary>
    /// La classe avec ses stats propres au multi, si pvp.json en définit (le solo garde celles de units.json).
    /// Mise en cache par configuration : une même classe n'est construite qu'une fois.
    /// </summary>
    private static UnitClass WithMultiStats(UnitClass cls)
    {
        var config = PvpConfig.Current;
        if (!config.UnitStats.TryGetValue(cls.Asset, out var s))
            return cls;
        lock (s_statsLock)
        {
            if (!ReferenceEquals(s_statsFor, config))
            {
                s_statsCache = new Dictionary<string, UnitClass>();
                s_statsFor = config;
            }
            if (!s_statsCache.TryGetValue(cls.Asset, out var multi))
            {
                multi = new UnitClass(cls.Name, cls.Asset, cls.Tier, s.Hp ?? cls.MaxHp, s.Damage ?? cls.Damage,
                    s.MoveRange ?? cls.MoveRange, s.AttackRange ?? cls.AttackRange, cls.PiercesAllies, cls.MinAttackRange,
                    s.Traits ?? cls.Traits, cls.AttackDomaine);
                s_statsCache[cls.Asset] = multi;
            }
            return multi;
        }
    }

    private static IEnumerable<UnitClass> Flatten(UnitClass cls)
    {
        yield return cls;
        foreach (var child in cls.Evolutions)
            foreach (var c in Flatten(child))
                yield return c;
    }

    public static (Domaine Domaine, UnitClass Class)? FindClass(string asset)
    {
        foreach (var entry in AllUnitClasses())
            if (entry.Class.Asset == asset)
                return entry;
        return null;
    }

    /// <summary>Commandants jouables en multi (ceux du solo, sans compagnon).</summary>
    public static IEnumerable<CommandeDef> Commanders() =>
        Commandes.All.Where(c => c.Role == CommandeRole.Commander);

    /// <summary>Équipements utilisables en multi (hors objets de sacoche du DUO), avec leurs stats MULTI.</summary>
    public static IEnumerable<Equipment> AllEquipment() => Equipments.All.Where(IsAllowed).Select(WithMultiStats);

    /// <summary>L'équipement <paramref name="id"/> avec ses stats MULTI (pvp.json « equipmentStats »). Null si inconnu.
    /// Toute création ou affichage d'équipement en multi passe par ici.</summary>
    public static Equipment? FindEquipment(string? id) =>
        id != null && Equipments.ById(id) is { } e ? WithMultiStats(e) : null;

    private static PvpConfig? s_equipFor;
    private static Dictionary<string, Equipment> s_equipCache = new();

    private static Equipment WithMultiStats(Equipment e)
    {
        var config = PvpConfig.Current;
        if (!config.EquipmentStats.TryGetValue(e.Id, out var stats))
            return e;
        lock (s_statsLock)
        {
            if (!ReferenceEquals(s_equipFor, config))
            {
                s_equipCache = new Dictionary<string, Equipment>();
                s_equipFor = config;
            }
            if (!s_equipCache.TryGetValue(e.Id, out var multi))
            {
                var effects = e.Effects.Select(fx => !fx.IsTrait && stats.TryGetValue(fx.Stat.ToString(), out var amount)
                    ? EquipEffect.OfStat(fx.Stat, amount) : fx).ToList();
                multi = Equipment.Of(e.Id, e.Name, e.Rarity, effects, e.Icon, e.EnemyAllowed, e.Demo, e.Satchel);
                s_equipCache[e.Id] = multi;
            }
            return multi;
        }
    }

    /// <summary>Équipement utilisable en multi : ni objet de sacoche, ni retiré du multi (pvp.json « excludedEquipment »).</summary>
    public static bool IsAllowed(Equipment e) => !e.Satchel && !PvpConfig.Current.ExcludedEquipment.Contains(e.Id);

    /// <summary>Le commandant avec ses stats MULTI (pvp.json), à défaut ses stats de solo. Null si inconnu.</summary>
    public static (Domaine Domaine, UnitClass Class)? CommanderClass(string commanderId)
    {
        if (Commandes.ById(commanderId) is not { Role: CommandeRole.Commander } def)
            return null;
        var b = def.BaseClass;
        if (!PvpConfig.Current.Commanders.TryGetValue(def.Id, out var s))
            return (def.Movement, b);
        return (def.Movement, new UnitClass(b.Name, b.Asset, b.Tier, s.Hp, s.Damage, s.MoveRange, s.AttackRange,
            b.PiercesAllies, b.MinAttackRange, s.Traits, b.AttackDomaine));
    }

    /// <summary>Coût d'ARMÉE d'un build : commandant et pions (les équipements ont leur propre budget, cf.
    /// <see cref="EquipmentCost(PvpBuild)"/>). Éléments inconnus : coût par défaut.</summary>
    public static int Cost(PvpBuild build)
    {
        var config = PvpConfig.Current;
        var total = config.CommanderCostOf(build.CommanderId);
        foreach (var slot in build.Slots)
            if (FindClass(slot.ClassAsset) is { } cls)
                total += config.CostOf(cls.Class);
        return total;
    }

    /// <summary>Même armée, équipements TIRÉS AU HASARD dans le budget, posés au hasard (cf. <see cref="PvpBotEquipment"/>).</summary>
    public static PvpBuild RandomEquipment(PvpBuild army, System.Random rng) => PvpBotEquipment.Equip(army, rng, smart: false);

    /// <summary>Coût des ÉQUIPEMENTS d'un build, à comparer à <see cref="PvpConfig.EquipmentBudget"/>.</summary>
    public static int EquipmentCost(PvpBuild build)
    {
        var config = PvpConfig.Current;
        var total = 0;
        foreach (var slot in build.Slots)
            if (FindEquipment(slot.EquipmentId) is { } eq)
                total += config.CostOf(eq);
        return total;
    }

    /// <summary>
    /// Raison pour laquelle un build n'est pas jouable (null = jouable) : budget dépassé, classe ou commandant
    /// inconnu, équipement inconnu. <paramref name="owns"/> (facultatif) vérifie en plus que tout est débloqué.
    /// </summary>
    public static string? Problem(PvpBuild build, System.Func<string, bool>? owns = null)
    {
        if (CommanderClass(build.CommanderId) == null)
            return "commandant inconnu";
        if (owns != null && !owns(build.CommanderId))
            return "commandant non débloqué";
        foreach (var slot in build.Slots)
        {
            if (build.IsReserved(slot.Column, slot.Line))
                return "case réservée au commandant ou à son compagnon";
            if (FindClass(slot.ClassAsset) == null)
                return $"pion inconnu ({slot.ClassAsset})";
            if (owns != null && !owns(slot.ClassAsset))
                return $"pion non débloqué ({slot.ClassAsset})";
            if (slot.EquipmentId != null)
            {
                if (FindEquipment(slot.EquipmentId) is not { } item || !IsAllowed(item))
                    return $"équipement inconnu ({slot.EquipmentId})";
                if (owns != null && !owns(slot.EquipmentId))
                    return $"équipement non débloqué ({slot.EquipmentId})";
            }
        }
        var copies = build.Slots.Where(s => s.EquipmentId != null).GroupBy(s => s.EquipmentId).FirstOrDefault(g => g.Count() > PvpConfig.Current.MaxSameEquipment);
        if (copies != null)
            return $"plus de {PvpConfig.Current.MaxSameEquipment} exemplaires de {copies.Key}";
        if (Cost(build) > PvpConfig.Current.Budget)
            return "budget d'armée dépassé";
        return EquipmentCost(build) > PvpConfig.Current.EquipmentBudget ? "budget d'équipement dépassé" : null;
    }

    /// <summary>Instancie le pion d'un emplacement (classe + équipement). Null si la classe est inconnue.</summary>
    public static Unit? Spawn(PvpBuildSlot slot, Faction faction)
    {
        if (FindClass(slot.ClassAsset) is not { } cls)
            return null;
        var equipment = FindEquipment(slot.EquipmentId) is { } eq && IsAllowed(eq)
            ? new[] { eq }
            : null;
        return new Unit(cls.Domaine, faction, cls.Class, equipment);
    }

    public static Unit? SpawnCommander(string commanderId, Faction faction) =>
        CommanderClass(commanderId) is { } c
            ? new Unit(c.Domaine, faction, c.Class) { IsEssential = true }
            : null;

    /// <summary>
    /// Compagnon d'un commandant (Basile pour le DUO) avec ses stats MULTI (pvp.json « commanders », sinon celles du
    /// solo), sans pouvoir. Null si le commandant n'en a pas.
    /// </summary>
    public static (Domaine Domaine, UnitClass Class)? CompanionClass(string commanderId)
    {
        if (Commandes.ById(commanderId) is not { Role: CommandeRole.Commander, CompanionId: { } companionId }
            || Commandes.CompanionById(companionId) is not { } def)
            return null;
        var b = def.BaseClass;
        if (!PvpConfig.Current.Commanders.TryGetValue(def.Id, out var s))
            return (def.Movement, b);
        return (def.Movement, new UnitClass(b.Name, b.Asset, b.Tier, s.Hp, s.Damage, s.MoveRange, s.AttackRange,
            b.PiercesAllies, b.MinAttackRange, s.Traits, b.AttackDomaine));
    }

    /// <summary>Le compagnon est ESSENTIEL comme le commandant : la mort de l'un OU de l'autre fait perdre.</summary>
    public static Unit? SpawnCompanion(string commanderId, Faction faction) =>
        CompanionClass(commanderId) is { } c
            ? new Unit(c.Domaine, faction, c.Class) { IsEssential = true, IsCompanion = true }
            : null;
}
