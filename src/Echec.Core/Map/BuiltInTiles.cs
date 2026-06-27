namespace Echec.Core.Map;

/// <summary>
/// Tuiles « historiques » (ancien terrain aléatoire) exposées en <see cref="TileDef"/>, pour que le
/// code existant (génération, plateau plat, tutoriel) garde un comportement identique après le
/// passage au catalogue. Mêmes règles qu'avant : l'herbe est libre, l'eau borne le déplacement
/// (le tir passe), la montagne borne déplacement ET tir.
/// </summary>
public static class BuiltInTiles
{
    public static readonly TileDef Grass = new("grass", BlocksMove: false, BlocksFire: false);
    public static readonly TileDef Water = new("water", BlocksMove: true, BlocksFire: false);
    public static readonly TileDef Mountain = new("mountain", BlocksMove: true, BlocksFire: true);
}
