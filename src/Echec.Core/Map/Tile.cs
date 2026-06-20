namespace Echec.Core.Map;

/// <summary>
/// Donnée d'une case : son terrain (et, plus tard, occupant, hauteur, etc.).
/// </summary>
public readonly record struct Tile(TerrainType Terrain);
