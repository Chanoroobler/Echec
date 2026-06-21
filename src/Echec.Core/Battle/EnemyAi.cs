using System;
using System.Collections.Generic;
using System.Linq;
using Echec.Core.Map;

namespace Echec.Core.Battle;

/// <summary>Action choisie par l'IA : un déplacement ou une attaque.</summary>
public readonly record struct AiAction(Cell From, Cell To, bool IsAttack);

/// <summary>
/// IA gloutonne simple (camp ennemi). Priorités : attaque mortelle &gt; attaque
/// quelconque &gt; avancer l'unité qui se rapproche le plus d'une unité joueur.
/// Choix déterministe (premier trouvé).
/// </summary>
public static class EnemyAi
{
    public static AiAction? ChooseAction(Match match)
    {
        if (match.IsOver || match.CurrentTurn != Faction.Enemy)
            return null;

        var enemies = match.Units().Where(x => x.Unit.Faction == Faction.Enemy).ToList();
        var players = match.Units().Where(x => x.Unit.Faction == Faction.Player)
            .Select(x => x.Cell).ToList();
        if (players.Count == 0)
            return null;

        AiAction? bestKill = null, bestAttack = null, bestAdvance = null;
        var bestAdvanceDistance = int.MaxValue;

        foreach (var (from, unit) in enemies)
        {
            foreach (var target in match.AttackTargets(from))
            {
                var victim = match.UnitAt(target)!;
                if (unit.Damage >= victim.Hp)
                    bestKill ??= new AiAction(from, target, IsAttack: true);
                else
                    bestAttack ??= new AiAction(from, target, IsAttack: true);
            }

            foreach (var to in match.LegalMoves(from))
            {
                var distance = players.Min(p => Chebyshev(to, p));
                if (distance < bestAdvanceDistance)
                {
                    bestAdvanceDistance = distance;
                    bestAdvance = new AiAction(from, to, IsAttack: false);
                }
            }
        }

        return bestKill ?? bestAttack ?? bestAdvance;
    }

    private static int Chebyshev(Cell a, Cell b) =>
        Math.Max(Math.Abs(a.Column - b.Column), Math.Abs(a.Row - b.Row));
}
