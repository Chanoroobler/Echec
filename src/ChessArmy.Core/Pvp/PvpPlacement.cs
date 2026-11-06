using System.Linq;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Pvp;

/// <summary>Issue d'un geste de placement sur un build multi.</summary>
public enum PlacementResult
{
    /// <summary>Le build a changé.</summary>
    Ok,
    /// <summary>Rien à faire (même case, même élément) : le build est inchangé.</summary>
    Unchanged,
    /// <summary>Case du commandant : fixe, rien ne s'y pose ni n'en part.</summary>
    KingCell,
    /// <summary>Case hors des deux lignes du joueur.</summary>
    OutOfZone,
    /// <summary>Le geste ferait dépasser le budget.</summary>
    OverBudget,
    /// <summary>Équipement posé sur une case sans pion.</summary>
    NoUnit,
    /// <summary>Le commandant ne porte pas d'équipement.</summary>
    CommanderNoEquipment,
    /// <summary>Classe ou équipement inconnu.</summary>
    Unknown,
    /// <summary>Déjà autant d'exemplaires de cet équipement que permis dans l'armée.</summary>
    TooManyCopies,
}

/// <summary>
/// Gestes du build multi (écran Armée), hors de toute interface : poser, déplacer, échanger, retirer un pion ;
/// équiper, déplacer, échanger, retirer un équipement. Règles communes :
///  • la case du commandant est fixe (rien ne s'y pose, le commandant ne porte rien) ;
///  • l'équipement SUIT son pion (déplacement, échange) ; un pion remplacé depuis la réserve perd le sien ;
///  • un geste qui ferait dépasser le budget est refusé. Un geste qui ne coûte rien de plus passe toujours,
///    même sur un build déjà trop cher (on doit pouvoir le corriger).
/// Les cases sont en repère de build : colonne 0..7, ligne <see cref="PvpBuild.BackLine"/> ou <see cref="PvpBuild.FrontLine"/>.
/// </summary>
public static class PvpPlacement
{
    public static bool IsPlayerCell(int column, int line) =>
        column >= 0 && column < PvpBuild.Columns && line is PvpBuild.BackLine or PvpBuild.FrontLine;

    /// <summary>Points d'ARMÉE restants (négatif si le build dépasse).</summary>
    public static int Remaining(PvpBuild build) => PvpConfig.Current.Budget - PvpCatalog.Cost(build);

    /// <summary>Points d'ÉQUIPEMENT restants (budget séparé ; négatif si le build dépasse).</summary>
    public static int RemainingEquipment(PvpBuild build) =>
        PvpConfig.Current.EquipmentBudget - PvpCatalog.EquipmentCost(build);

    public static int UnitCost(string asset) =>
        PvpCatalog.FindClass(asset) is { } c ? PvpConfig.Current.CostOf(c.Class) : 0;

    public static int EquipmentCost(string? id) =>
        id != null && Equipments.ById(id) is { } e ? PvpConfig.Current.CostOf(e) : 0;

    /// <summary>Le surcoût <paramref name="extra"/> tient-il dans les points restants ? (un geste gratuit passe toujours)</summary>
    private static bool Affordable(int extra, int remaining) => extra <= 0 || extra <= remaining;

    private static PlacementResult CheckCell(int column, int line) =>
        !IsPlayerCell(column, line) ? PlacementResult.OutOfZone
        : PvpBuild.IsKingCell(column, line) ? PlacementResult.KingCell
        : PlacementResult.Ok;

    // ── Pions ────────────────────────────────────────────────────────────────────

    /// <summary>Pion de la RÉSERVE posé sur une case : libre, ou occupée (il remplace l'occupant, dont
    /// l'équipement est retiré). Refusé si le budget est dépassé.</summary>
    public static PlacementResult PlaceUnit(PvpBuild build, int column, int line, string asset)
    {
        if (CheckCell(column, line) is not PlacementResult.Ok and var bad)
            return bad;
        if (PvpCatalog.FindClass(asset) == null)
            return PlacementResult.Unknown;
        var old = build.SlotAt(column, line);
        if (old is { EquipmentId: null } && old.ClassAsset == asset)
            return PlacementResult.Unchanged;
        var extra = UnitCost(asset) - (old == null ? 0 : UnitCost(old.ClassAsset));   // budget d'armée seul
        if (!Affordable(extra, Remaining(build)))
            return PlacementResult.OverBudget;
        build.Set(column, line, asset);
        return PlacementResult.Ok;
    }

    /// <summary>Pion POSÉ déplacé vers une autre case : son équipement le suit ; case occupée = échange (les
    /// équipements s'échangent avec les pions). Ne change jamais le coût.</summary>
    public static PlacementResult MoveUnit(PvpBuild build, int fromColumn, int fromLine, int toColumn, int toLine)
    {
        if (CheckCell(fromColumn, fromLine) is not PlacementResult.Ok and var badFrom)
            return badFrom;
        if (CheckCell(toColumn, toLine) is not PlacementResult.Ok and var badTo)
            return badTo;
        if (build.SlotAt(fromColumn, fromLine) is not { } moving)
            return PlacementResult.NoUnit;
        if (fromColumn == toColumn && fromLine == toLine)
            return PlacementResult.Unchanged;
        var other = build.SlotAt(toColumn, toLine);
        build.Set(toColumn, toLine, moving.ClassAsset, moving.EquipmentId);
        if (other != null)
            build.Set(fromColumn, fromLine, other.ClassAsset, other.EquipmentId);
        else
            build.Remove(fromColumn, fromLine);
        return PlacementResult.Ok;
    }

    /// <summary>Pion retiré (avec son équipement).</summary>
    public static PlacementResult RemoveUnit(PvpBuild build, int column, int line)
    {
        if (CheckCell(column, line) is not PlacementResult.Ok and var bad)
            return bad;
        if (build.SlotAt(column, line) == null)
            return PlacementResult.Unchanged;
        build.Remove(column, line);
        return PlacementResult.Ok;
    }

    // ── Équipements ──────────────────────────────────────────────────────────────

    /// <summary>Équipement de la RÉSERVE posé sur un pion : un équipement déjà porté est remplacé. Refusé sur le
    /// commandant, sur une case vide, ou si le budget est dépassé.</summary>
    public static PlacementResult Equip(PvpBuild build, int column, int line, string equipmentId)
    {
        if (!IsPlayerCell(column, line))
            return PlacementResult.OutOfZone;
        if (PvpBuild.IsKingCell(column, line))
            return PlacementResult.CommanderNoEquipment;
        if (Equipments.ById(equipmentId) is not { Satchel: false })
            return PlacementResult.Unknown;
        if (build.SlotAt(column, line) is not { } slot)
            return PlacementResult.NoUnit;
        if (slot.EquipmentId == equipmentId)
            return PlacementResult.Unchanged;
        if (build.Slots.Count(s => s.EquipmentId == equipmentId && !(s.Column == column && s.Line == line))
            >= PvpConfig.Current.MaxSameEquipment)
            return PlacementResult.TooManyCopies;
        if (!Affordable(EquipmentCost(equipmentId) - EquipmentCost(slot.EquipmentId), RemainingEquipment(build)))
            return PlacementResult.OverBudget;
        build.Set(column, line, slot.ClassAsset, equipmentId);
        return PlacementResult.Ok;
    }

    /// <summary>Équipement PORTÉ déplacé vers un autre pion ; si celui-ci était équipé, les deux s'échangent.
    /// Ne change jamais le coût.</summary>
    public static PlacementResult MoveEquipment(PvpBuild build, int fromColumn, int fromLine, int toColumn, int toLine)
    {
        if (!IsPlayerCell(toColumn, toLine))
            return PlacementResult.OutOfZone;
        if (PvpBuild.IsKingCell(toColumn, toLine))
            return PlacementResult.CommanderNoEquipment;
        if (build.SlotAt(fromColumn, fromLine) is not { EquipmentId: { } moving } from)
            return PlacementResult.NoUnit;
        if (build.SlotAt(toColumn, toLine) is not { } to)
            return PlacementResult.NoUnit;
        if (fromColumn == toColumn && fromLine == toLine)
            return PlacementResult.Unchanged;
        build.Set(toColumn, toLine, to.ClassAsset, moving);
        build.Set(fromColumn, fromLine, from.ClassAsset, to.EquipmentId);
        return PlacementResult.Ok;
    }

    /// <summary>Équipement retiré du pion qui le porte.</summary>
    public static PlacementResult RemoveEquipment(PvpBuild build, int column, int line)
    {
        if (build.SlotAt(column, line) is not { EquipmentId: not null } slot)
            return PlacementResult.Unchanged;
        build.Set(column, line, slot.ClassAsset, null);
        return PlacementResult.Ok;
    }
}
