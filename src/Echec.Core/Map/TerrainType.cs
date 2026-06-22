namespace Echec.Core.Map;

/// <summary>
/// Type de terrain d'une tuile. <see cref="Grass"/> est traversable ; <see cref="Water"/> et
/// <see cref="Mountain"/> sont des obstacles (voir <see cref="Terrain"/> pour les règles exactes).
/// </summary>
public enum TerrainType
{
    Grass,
    Water,     // bloque le DÉPLACEMENT ; le tir passe au-dessus
    Mountain,  // bloque le déplacement ET le tir
}

/// <summary>Règles de blocage par type de terrain (domaine pur, sans rendu).</summary>
public static class Terrain
{
    /// <summary>Vrai si une unité ne peut ni s'arrêter ni passer sur cette tuile (eau, montagne).</summary>
    public static bool BlocksMovement(this TerrainType terrain) =>
        terrain is TerrainType.Water or TerrainType.Mountain;

    /// <summary>Vrai si la tuile arrête une ligne de tir (montagne seule ; l'eau laisse passer).</summary>
    public static bool BlocksLineOfFire(this TerrainType terrain) =>
        terrain is TerrainType.Mountain;
}
