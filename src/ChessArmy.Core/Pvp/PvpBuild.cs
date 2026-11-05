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
/// Armée composée par le joueur pour le multi : un commandant (gratuit, case fixe) et des pions posés sur ses
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

    public PvpBuildSlot? SlotAt(int column, int line) =>
        Slots.FirstOrDefault(s => s.Column == column && s.Line == line);

    /// <summary>Pose (ou remplace) un pion sur une case libre de commandant. Faux sur la case du commandant.</summary>
    public bool Set(int column, int line, string classAsset, string? equipmentId = null)
    {
        if (IsKingCell(column, line) || column < 0 || column >= Columns || line is not (BackLine or FrontLine))
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
    /// <summary>Toutes les classes de pions des 4 domaines (tous tiers), hors commandants.</summary>
    public static IEnumerable<(Domaine Domaine, UnitClass Class)> AllUnitClasses()
    {
        foreach (var def in Domaines.All)
            foreach (var cls in Flatten(def.BaseClass))
                yield return (def.Id, cls);
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

    /// <summary>Équipements utilisables en multi (hors objets de sacoche du DUO).</summary>
    public static IEnumerable<Equipment> AllEquipment() => Equipments.All.Where(e => !e.Satchel);

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

    /// <summary>Coût total d'un build (pions + équipements ; le commandant est gratuit). Éléments inconnus : 0.</summary>
    public static int Cost(PvpBuild build)
    {
        var config = PvpConfig.Current;
        var total = 0;
        foreach (var slot in build.Slots)
        {
            if (FindClass(slot.ClassAsset) is { } cls)
                total += config.CostOf(cls.Class);
            if (slot.EquipmentId != null && Equipments.ById(slot.EquipmentId) is { } eq)
                total += config.CostOf(eq);
        }
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
            if (FindClass(slot.ClassAsset) == null)
                return $"pion inconnu ({slot.ClassAsset})";
            if (owns != null && !owns(slot.ClassAsset))
                return $"pion non débloqué ({slot.ClassAsset})";
            if (slot.EquipmentId != null)
            {
                if (Equipments.ById(slot.EquipmentId) is not { Satchel: false })
                    return $"équipement inconnu ({slot.EquipmentId})";
                if (owns != null && !owns(slot.EquipmentId))
                    return $"équipement non débloqué ({slot.EquipmentId})";
            }
        }
        return Cost(build) > PvpConfig.Current.Budget ? "budget dépassé" : null;
    }

    /// <summary>Instancie le pion d'un emplacement (classe + équipement). Null si la classe est inconnue.</summary>
    public static Unit? Spawn(PvpBuildSlot slot, Faction faction)
    {
        if (FindClass(slot.ClassAsset) is not { } cls)
            return null;
        var equipment = slot.EquipmentId != null && Equipments.ById(slot.EquipmentId) is { Satchel: false } eq
            ? new[] { eq }
            : null;
        return new Unit(cls.Domaine, faction, cls.Class, equipment);
    }

    public static Unit? SpawnCommander(string commanderId, Faction faction) =>
        CommanderClass(commanderId) is { } c
            ? new Unit(c.Domaine, faction, c.Class) { IsEssential = true }
            : null;
}
