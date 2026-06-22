using System;
using System.Collections.Generic;

namespace Echec.Core.Map;

/// <summary>
/// Génère un <see cref="Battlefield"/> d'herbe parsemé d'obstacles (eau / montagne) dans la
/// zone NEUTRE du milieu, hors des rangées de déploiement de chaque camp, et SYMÉTRIQUES
/// haut/bas pour rester équitable (chaque camp affronte le même terrain miroir).
/// </summary>
public static class TerrainGenerator
{
    /// <param name="deployRows">Nombre de rangées de déploiement à chaque bord (laissées en herbe).</param>
    /// <param name="obstaclePairs">Nombre de paires d'obstacles (chaque paire = une tuile + son miroir).</param>
    public static Battlefield Generate(int width, int height, Random rng,
        int deployRows = 2, int obstaclePairs = 3)
    {
        var field = Battlefield.CreateFlat(width, height);

        var top = deployRows;                    // première rangée neutre
        var bottom = height - 1 - deployRows;    // dernière rangée neutre
        if (top > bottom)
            return field;                        // pas de zone neutre (petit plateau)

        // On tire dans la MOITIÉ HAUTE de la zone neutre [top .. center] puis on miroir vers le bas
        // (row → height-1-row) → disposition symétrique.
        var center = (top + bottom) / 2;
        var taken = new HashSet<Cell>();

        var placed = 0;
        var attempts = 0;
        while (placed < obstaclePairs && attempts++ < obstaclePairs * 20)
        {
            var cell = new Cell(rng.Next(width), top + rng.Next(center - top + 1));
            var mirror = new Cell(cell.Column, height - 1 - cell.Row);
            if (!taken.Add(cell))
                continue;                        // déjà occupée : on retire
            taken.Add(mirror);

            var terrain = rng.Next(2) == 0 ? TerrainType.Mountain : TerrainType.Water;
            field[cell] = new Tile(terrain);
            field[mirror] = new Tile(terrain);
            placed++;
        }

        return field;
    }
}
