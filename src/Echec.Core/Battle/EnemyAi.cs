using System;
using System.Collections.Generic;
using System.Linq;
using Echec.Core.Map;

namespace Echec.Core.Battle;

/// <summary>
/// IA gloutonne simple pour le camp ennemi : si une attaque mortelle est possible
/// elle la joue, sinon une attaque quelconque, sinon elle avance l'unité qui peut
/// le plus se rapprocher d'une unité joueur. Choix déterministe (premier trouvé).
/// </summary>
public static class EnemyAi
{
    public static (Cell From, Cell To)? ChooseMove(Match match)
    {
        if (match.IsOver || match.CurrentTurn != Faction.Enemy)
            return null;

        var enemies = match.Units().Where(x => x.Unit.Faction == Faction.Enemy).ToList();
        var players = match.Units().Where(x => x.Unit.Faction == Faction.Player)
            .Select(x => x.Cell).ToList();
        if (players.Count == 0)
            return null;

        (Cell, Cell)? bestKill = null;
        (Cell, Cell)? bestAttack = null;
        (Cell, Cell)? bestAdvance = null;
        var bestAdvanceDistance = int.MaxValue;

        foreach (var (from, unit) in enemies)
        {
            foreach (var to in match.LegalMoves(from))
            {
                var target = match.UnitAt(to);
                if (target != null && target.Faction == Faction.Player)
                {
                    if (unit.Damage >= target.Hp)
                        bestKill ??= (from, to);
                    else
                        bestAttack ??= (from, to);
                }
                else
                {
                    var distance = players.Min(p => Chebyshev(to, p));
                    if (distance < bestAdvanceDistance)
                    {
                        bestAdvanceDistance = distance;
                        bestAdvance = (from, to);
                    }
                }
            }
        }

        return bestKill ?? bestAttack ?? bestAdvance;
    }

    private static int Chebyshev(Cell a, Cell b) =>
        Math.Max(Math.Abs(a.Column - b.Column), Math.Abs(a.Row - b.Row));
}
