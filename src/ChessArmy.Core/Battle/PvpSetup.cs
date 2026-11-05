using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Campaign;
using ChessArmy.Core.Map;

namespace ChessArmy.Core.Battle;

/// <summary>
/// Mise en place du PvP (POC) : un vrai échiquier 8×8, la même armée des deux côtés.
///
/// Chaque client joue « en bas » en <see cref="Faction.Player"/> : l'invité a donc un plateau RETOURNÉ (lignes
/// inversées, camps échangés). Les deux armées étant identiques et symétriques par rapport à l'axe horizontal,
/// les deux clients construisent EXACTEMENT le même plateau local ; seul change qui joue en premier (l'hôte).
/// Un coup reçu est retourné par <see cref="Mirror(in BattleAction)"/> avant d'être appliqué, et l'empreinte
/// se compare dans le repère de l'hôte (cf. <see cref="Match.ComputeStateHash(bool)"/>).
/// </summary>
public static class PvpSetup
{
    public const int Size = 8;

    /// <summary>Plateau d'un client : <paramref name="isHost"/> joue le premier coup. <paramref name="terrain"/> et
    /// <paramref name="cover"/> (buissons) viennent de <see cref="BuildBattlefield"/> ; null = herbe nue.</summary>
    public static Match CreateMatch(bool isHost, int seed, Battlefield? terrain = null, IEnumerable<Cell>? cover = null) =>
        new(Size, Size, terrain, cover, rng: new System.Random(seed),
            firstTurn: isHost ? Faction.Player : Faction.Enemy);

    /// <summary>
    /// Rangée arrière, de gauche à droite (colonnes 0 à 7), vue du joueur local : Lancier, Cavalier, Mage,
    /// Archer (case de la dame), Commandant (le roi : essentiel, sa chute décide la partie), Mage, Cavalier, Lancier.
    /// Devant : 8 Soldats.
    /// </summary>
    public static UnitSpec BackRowSpec(int column)
    {
        switch (column)
        {
            case 0: case 7: return new UnitSpec(Domaine.Tour, Domaines.Tour.BaseClass);
            case 1: case 6: return new UnitSpec(Domaine.Cavalier, Domaines.Cavalier.BaseClass);
            case 2: case 5: return new UnitSpec(Domaine.Fou, Domaines.Fou.BaseClass);
            case 3:
                var archer = Domaines.Dame.BaseClass.Evolutions.FirstOrDefault(c => c.Name == "Archer")
                             ?? Domaines.Dame.BaseClass;
                return new UnitSpec(Domaine.Dame, archer);
            default:
                var king = Commandes.Commander;
                return new UnitSpec(king.Movement, king.BaseClass, essential: true);
        }
    }

    public static UnitSpec PawnSpec() => new(Domaine.Dame, Domaines.Dame.BaseClass);

    /// <summary>Pose les deux armées : le joueur local sur les deux dernières lignes, l'adversaire en haut.</summary>
    public static void PlaceArmies(Match match)
    {
        for (var c = 0; c < Size; c++)
        {
            match.Place(new Cell(c, Size - 1), BackRowSpec(c).Spawn(Faction.Player));
            match.Place(new Cell(c, Size - 2), PawnSpec().Spawn(Faction.Player));
            match.Place(new Cell(c, 0), BackRowSpec(c).Spawn(Faction.Enemy));
            match.Place(new Cell(c, 1), PawnSpec().Spawn(Faction.Enemy));
        }
    }

    /// <summary>
    /// Raison pour laquelle une map <see cref="CombatType.Multi"/> n'est PAS jouable (null = jouable) : taille
    /// différente de <see cref="Size"/>×<see cref="Size"/>, ou case infranchissable sur les deux lignes de départ
    /// d'une armée (les 16 pions y sont posés d'office).
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

    /// <summary>Coup de l'adversaire (dans SON repère) traduit dans le repère local.</summary>
    public static BattleAction Mirror(in BattleAction action) =>
        new(action.Kind, Mirror(action.From), Mirror(action.Target));
}
