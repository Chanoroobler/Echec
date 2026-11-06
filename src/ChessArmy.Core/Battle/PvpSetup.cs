using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Map;
using ChessArmy.Core.Pvp;

namespace ChessArmy.Core.Battle;

/// <summary>
/// Mise en place et règles d'une partie PvP sur un plateau 8×8.
///
/// Chaque client joue « en bas » en <see cref="Faction.Player"/>. L'hôte voit le repère COMMUN ; l'invité a le
/// plateau RETOURNÉ (lignes inversées, camps échangés) et son <see cref="Match"/> est créé avec
/// <c>mirrorRows</c> : le moteur parcourt alors le plateau dans l'ordre du repère commun, si bien que les deux
/// clients résolvent chaque coup à l'identique. Un coup reçu est retourné par <see cref="Mirror(in BattleAction)"/>
/// avant d'être appliqué, et l'empreinte se compare dans le repère de l'hôte (cf. <see cref="Match.ComputeStateHash(bool)"/>).
/// </summary>
public static class PvpSetup
{
    public const int Size = 8;

    /// <summary>Plateau d'un client. <paramref name="mirrored"/> = invité (plateau retourné) ;
    /// <paramref name="localStarts"/> = le joueur local joue le premier coup (tirage au sort).</summary>
    public static Match CreateMatch(bool mirrored, bool localStarts, int seed, Battlefield? terrain = null,
        IEnumerable<Cell>? cover = null) =>
        new(Size, Size, terrain, cover, rng: new SeededRandom(seed),
            firstTurn: localStarts ? Faction.Player : Faction.Enemy, mirrorRows: mirrored, pvpRules: true);

    /// <summary>Tirage au sort du premier joueur, dérivé de la graine de l'hôte (les deux clients le connaissent).</summary>
    public static bool HostStarts(int seed) => (seed & 0x10) == 0;

    /// <summary>
    /// Pose une armée : celle du joueur local sur les deux lignes du bas, celle de l'adversaire sur les deux du
    /// haut (même colonne : les deux commandants se font face). Les pions inconnus sont ignorés.
    /// </summary>
    public static void PlaceArmy(Match match, PvpBuild build, Faction faction)
    {
        var back = faction == Faction.Player ? Size - 1 : 0;
        var front = faction == Faction.Player ? Size - 2 : 1;
        if (PvpCatalog.SpawnCommander(build.CommanderId, faction) is { } king)
            match.Place(new Cell(PvpBuild.KingColumn, back), king);
        if (PvpCatalog.SpawnCompanion(build.CommanderId, faction) is { } companion)
            match.Place(new Cell(PvpBuild.CompanionColumn, back), companion);   // DUO : Basile, à côté du commandant
        foreach (var slot in build.Slots)
        {
            if (build.IsReserved(slot.Column, slot.Line) || slot.Column is < 0 or >= Size)
                continue;
            var cell = new Cell(slot.Column, slot.Line == PvpBuild.BackLine ? back : front);
            if (match.UnitAt(cell) == null && PvpCatalog.Spawn(slot, faction) is { } unit)
                match.Place(cell, unit);
        }
    }

    /// <summary>
    /// Règles de FIN DE COUP du PvP, appliquées à l'identique par la scène, l'adversaire et le rejeu :
    /// quand la main passe à un camp, ses porteurs de « Séisme » frappent (règle symétrique de celle de la
    /// campagne), puis la partie est déclarée nulle si la position est morte. Renvoie les frappes de Séisme.
    /// </summary>
    public static IReadOnlyList<(Cell Cell, int Damage)> AfterAction(Match match, Faction actor)
    {
        IReadOnlyList<(Cell Cell, int Damage)> hits = System.Array.Empty<(Cell, int)>();
        if (!match.IsOver && match.CurrentTurn != actor)
            hits = match.ApplySeismes(match.CurrentTurn);
        if (!match.IsOver && match.IsDeadPosition())
            match.EndInDraw(MatchEndReason.DeadPosition);
        return hits;
    }

    /// <summary>Résout les effets en chaîne (réaction en chaîne, balle rebondissante) que la scène déroule
    /// pendant ses animations : le rejeu les vide d'un coup.</summary>
    public static void ResolvePending(Match match)
    {
        while (match.ResolveNextChainLink() != null) { }
        while (match.ResolveNextBounce() != null) { }
    }

    /// <summary>
    /// Rejoue une partie à partir de sa graine, des deux armées et du journal des coups (dans le repère local),
    /// exactement comme la scène l'a jouée. Sert à REVALIDER une partie contre un bot avant d'accorder points et
    /// cote : une valeur modifiée en mémoire pendant le combat donne un autre résultat.
    /// </summary>
    public static Match Replay(int seed, bool localStarts, Battlefield? terrain, IEnumerable<Cell>? cover,
        PvpBuild local, PvpBuild opponent, IEnumerable<BattleAction> log)
    {
        var match = CreateMatch(mirrored: false, localStarts, seed, terrain, cover);
        PlaceArmy(match, local, Faction.Player);
        PlaceArmy(match, opponent, Faction.Enemy);
        foreach (var action in log)
        {
            if (match.IsOver)
                break;
            var actor = match.CurrentTurn;
            if (match.Apply(action) == MoveKind.Invalid)
                break;
            AfterAction(match, actor);
            ResolvePending(match);
        }
        return match;
    }

    /// <summary>
    /// Raison pour laquelle une map <see cref="CombatType.Multi"/> n'est PAS jouable (null = jouable) : taille
    /// différente de <see cref="Size"/>×<see cref="Size"/>, ou case infranchissable sur les deux lignes de départ
    /// d'une armée (les pions y sont posés d'office).
    /// </summary>
    public static string? Problem(MapData map)
    {
        if (map.Width != Size || map.Height != Size)
            return $"doit faire {Size}×{Size}";
        for (var c = 0; c < Size; c++)
            foreach (var r in ArmyRows)
                if (map.TileAt(new Cell(c, r)).BlocksMove)
                    return $"case infranchissable sur une ligne d'armée ({c},{r})";
        return null;
    }

    private static readonly int[] ArmyRows = { 0, 1, Size - 2, Size - 1 };

    /// <summary>
    /// Vrai si la map est SYMÉTRIQUE haut/bas pour le JEU : chaque case et sa case miroir ont les mêmes règles
    /// (blocage, tir, glisse, portée, trait prêté, buisson). L'invité joue alors la map telle quelle, avec le
    /// même rendu que l'hôte. Sinon il la joue retournée (règles justes, mais les tuiles orientées, falaises,
    /// escaliers, peuvent paraître à l'envers).
    /// </summary>
    public static bool IsMirrorSymmetric(MapData map)
    {
        var bushes = map.Objects.Where(o => o.Kind == MapObjectKind.Bush).Select(o => o.Cell).ToHashSet();
        for (var c = 0; c < map.Width; c++)
            for (var r = 0; r < map.Height; r++)
            {
                var cell = new Cell(c, r);
                var twin = new Cell(c, map.Height - 1 - r);
                var a = map.TileAt(cell);
                var b = map.TileAt(twin);
                if (a.BlocksMove != b.BlocksMove || a.BlocksFire != b.BlocksFire || a.Slides != b.Slides
                    || a.RangeBonus != b.RangeBonus || a.Trait != b.Trait
                    || bushes.Contains(cell) != bushes.Contains(twin))
                    return false;
            }
        return true;
    }

    /// <summary>
    /// Terrain local d'un client (et ses buissons, qui donnent du couvert) : la map telle quelle pour l'hôte, ou
    /// pour l'invité si elle est symétrique ; retournée sinon, pour que chaque case garde ses règles.
    /// </summary>
    public static Battlefield BuildBattlefield(MapData map, bool isHost, out List<Cell> bushes)
    {
        var flip = !isHost && !IsMirrorSymmetric(map);
        var battlefield = new Battlefield(map.Width, map.Height);
        foreach (var cell in battlefield.Cells())
            battlefield[cell] = new Tile(map.TileAt(flip ? Mirror(cell) : cell));
        bushes = map.Objects.Where(o => o.Kind == MapObjectKind.Bush)
            .Select(o => flip ? Mirror(o.Cell) : o.Cell).ToList();
        return battlefield;
    }

    /// <summary>
    /// Map d'une partie : tirée parmi les maps Multi jouables (hors brouillons) par la GRAINE de l'hôte, dans un
    /// ordre stable (par nom). Les deux clients ayant la même build, ils tombent sur la même. Null = aucune map :
    /// échiquier d'herbe nu.
    /// </summary>
    public static MapData? PickMap(IEnumerable<MapData> maps, int seed)
    {
        var pool = maps.Where(m => m.Type == CombatType.Multi && !m.IsDraft && Problem(m) is null)
            .OrderBy(m => m.Name, System.StringComparer.Ordinal).ToList();
        return pool.Count == 0 ? null : pool[(seed & int.MaxValue) % pool.Count];
    }

    public static Cell Mirror(Cell cell) => new(cell.Column, Size - 1 - cell.Row);

    /// <summary>Coup de l'adversaire (dans SON repère) traduit dans le repère local. Les fins de partie
    /// (abandon) changent aussi de camp : « Player » chez l'un est « Enemy » chez l'autre.</summary>
    public static BattleAction Mirror(in BattleAction action) => action.Kind switch
    {
        BattleActionKind.Resign => BattleAction.Resign(((Faction)action.From.Column).Opponent()),
        BattleActionKind.AgreeDraw or BattleActionKind.Pass => action,
        _ => new(action.Kind, Mirror(action.From), Mirror(action.Target)),
    };
}
