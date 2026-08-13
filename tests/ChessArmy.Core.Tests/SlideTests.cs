using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// « Glace » : une unité qui s'arrête sur une tuile glissante (<c>glisse</c>) dérape d'une case dans sa
/// direction d'arrivée, en chaîne sur les tuiles glissantes, jusqu'à un obstacle, un pion ou le bord.
/// </summary>
public class SlideTests
{
    private static readonly TileDef Ice = new("ice", BlocksMove: false, BlocksFire: false, Slides: true);

    private static Battlefield Field(int w, int h, params (Cell Cell, TileDef Tile)[] overrides)
    {
        var field = Battlefield.CreateFlat(w, h);
        foreach (var (cell, tile) in overrides)
            field[cell] = new Tile(tile);
        return field;
    }

    // Unité de troupe contrôlable : domaine « Tour » (glissé orthogonal) par défaut, portée de déplacement 3.
    private static Unit Slider(Faction f, Domaine d = Domaine.Tour, int move = 3, string[]? traits = null) =>
        new(d, f, new UnitClass("S", "s", tier: 1, maxHp: 10, damage: 5, moveRange: move, attackRange: 1, traits: traits));

    [Fact]
    public void MoveOntoIce_SlidesOneTile_InTravelDirection()
    {
        var field = Field(8, 8, (new Cell(3, 5), Ice));
        var match = new Match(8, 8, field);
        var from = new Cell(3, 7);
        match.Place(from, Slider(Faction.Player));

        var kind = match.TryMove(from, new Cell(3, 5));   // avance de 2 vers le nord, atterrit sur la glace

        Assert.Equal(MoveKind.Moved, kind);
        Assert.Null(match.UnitAt(new Cell(3, 5)));         // a glissé au-delà de la case d'arrivée
        Assert.NotNull(match.UnitAt(new Cell(3, 4)));      // repos une case plus loin (même direction)
        Assert.NotNull(match.LastSlide);
        Assert.Equal(new Cell(3, 5), match.LastSlide![0]);  // départ = case d'arrêt sur la glace
        Assert.Equal(new Cell(3, 4), match.LastSlide![^1]); // repos
    }

    [Fact]
    public void Slide_Chains_AcrossConsecutiveIceTiles()
    {
        var field = Field(8, 8, (new Cell(3, 5), Ice), (new Cell(3, 4), Ice));
        var match = new Match(8, 8, field);
        var from = new Cell(3, 7);
        match.Place(from, Slider(Faction.Player));

        match.TryMove(from, new Cell(3, 5));

        Assert.NotNull(match.UnitAt(new Cell(3, 3)));      // (3,5) glace → (3,4) glace → repos sur (3,3)
        Assert.Equal(3, match.LastSlide!.Count);
        Assert.Equal(new Cell(3, 3), match.LastSlide![^1]);
    }

    [Fact]
    public void Slide_StopsBeforeBlockingTile_NoSlide()
    {
        var field = Field(8, 8, (new Cell(3, 5), Ice), (new Cell(3, 4), BuiltInTiles.Mountain));
        var match = new Match(8, 8, field);
        var from = new Cell(3, 7);
        match.Place(from, Slider(Faction.Player));

        match.TryMove(from, new Cell(3, 5));

        Assert.NotNull(match.UnitAt(new Cell(3, 5)));      // obstacle juste après : reste sur la glace
        Assert.Null(match.LastSlide);
    }

    [Fact]
    public void Slide_StopsBeforeOccupiedTile_NoSlide()
    {
        var field = Field(8, 8, (new Cell(3, 5), Ice));
        var match = new Match(8, 8, field);
        var from = new Cell(3, 7);
        match.Place(from, Slider(Faction.Player));
        match.Place(new Cell(3, 4), Slider(Faction.Enemy));   // pion juste après la glace

        match.TryMove(from, new Cell(3, 5));

        Assert.NotNull(match.UnitAt(new Cell(3, 5)));      // un pion barre la route : reste sur la glace
        Assert.NotNull(match.UnitAt(new Cell(3, 4)));      // le pion n'a pas bougé
        Assert.Null(match.LastSlide);
    }

    [Fact]
    public void Slide_StopsAtBoardEdge_NoSlide()
    {
        var field = Field(8, 8, (new Cell(3, 0), Ice));
        var match = new Match(8, 8, field);
        var from = new Cell(3, 3);
        match.Place(from, Slider(Faction.Player));

        match.TryMove(from, new Cell(3, 0));               // atterrit sur la glace du bord haut

        Assert.NotNull(match.UnitAt(new Cell(3, 0)));      // le bord borne le dérapage : reste sur place
        Assert.Null(match.LastSlide);
    }

    [Fact]
    public void FlyingUnit_DoesNotSlide()
    {
        var field = Field(8, 8, (new Cell(3, 5), Ice));
        var match = new Match(8, 8, field);
        var from = new Cell(3, 7);
        match.Place(from, Slider(Faction.Player, traits: new[] { Trait.Vol }));  // survole la glace

        match.TryMove(from, new Cell(3, 5));

        Assert.NotNull(match.UnitAt(new Cell(3, 5)));      // un volant ne glisse pas
        Assert.Null(match.LastSlide);
    }

    [Fact]
    public void MeleeKillAdvanceOntoIce_Slides()
    {
        // L'attaquant tue une victime posée sur la glace et AVANCE dessus → il dérape (décision « toute arrivée »).
        var field = Field(8, 8, (new Cell(3, 6), Ice));
        var match = new Match(8, 8, field);
        var attacker = new Cell(3, 7);
        var victim = new Cell(3, 6);
        match.Place(attacker, Slider(Faction.Player));                                   // dégâts 5
        match.Place(victim, new Unit(Domaine.Tour, Faction.Enemy,
            new UnitClass("V", "v", tier: 1, maxHp: 3, damage: 1, moveRange: 1, attackRange: 1)));  // meurt en 1 coup

        match.TryAttack(attacker, victim);

        Assert.Null(match.UnitAt(new Cell(3, 6)));         // a tué, avancé sur la glace, puis dérapé au-delà
        Assert.NotNull(match.UnitAt(new Cell(3, 5)));      // repos une case plus loin
        Assert.Equal(new Cell(3, 5), match.LastSlide![^1]);
    }

    [Fact]
    public void ReculePushOntoIce_VictimSlides()
    {
        // Un attaquant « Recule » pousse la victime d'une case ; si elle atterrit sur de la glace, elle continue
        // de glisser dans la direction du recul (mêmes règles que la glissade normale).
        var field = Field(8, 8, (new Cell(3, 5), Ice));
        var match = new Match(8, 8, field);
        var attacker = new Cell(3, 7);
        var victim = new Cell(3, 6);
        match.Place(attacker, new Unit(Domaine.Tour, Faction.Player,
            new UnitClass("R", "r", tier: 1, maxHp: 20, damage: 1, moveRange: 1, attackRange: 1,
                traits: new[] { Trait.Recule })));
        match.Place(victim, new Unit(Domaine.Tour, Faction.Enemy,
            new UnitClass("V", "v", tier: 1, maxHp: 20, damage: 1, moveRange: 1, attackRange: 1)));

        match.TryAttack(attacker, victim);

        // Recule pousse (3,6) -> (3,5) [glace] -> glisse jusqu'à (3,4).
        Assert.Null(match.UnitAt(new Cell(3, 6)));
        Assert.Null(match.UnitAt(new Cell(3, 5)));
        Assert.NotNull(match.UnitAt(new Cell(3, 4)));
        Assert.Equal(new Cell(3, 4), match.LastRecule!.Value.To);   // case d'arrivée du recul = repos après glissade
    }

    [Fact]
    public void KnightJumpOntoIce_SlidesDiagonally()
    {
        // Le cavalier saute en L (3,7) → (4,5). Direction d'arrivée = signe (1,-2) = (1,-1) : glisse en DIAGONALE.
        var field = Field(8, 8, (new Cell(4, 5), Ice));
        var match = new Match(8, 8, field);
        var from = new Cell(3, 7);
        match.Place(from, Units.Of(Domaine.Cavalier, Faction.Player));

        var kind = match.TryMove(from, new Cell(4, 5));

        Assert.Equal(MoveKind.Moved, kind);
        Assert.Null(match.UnitAt(new Cell(4, 5)));         // a dérapé en diagonale
        Assert.NotNull(match.UnitAt(new Cell(5, 4)));      // repos une case en diagonale (haut-droite)
        Assert.Equal(new Cell(5, 4), match.LastSlide![^1]);
    }
}
