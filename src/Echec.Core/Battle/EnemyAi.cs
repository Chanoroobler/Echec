using System;
using System.Collections.Generic;
using System.Linq;
using Echec.Core.Map;

namespace Echec.Core.Battle;

/// <summary>Action choisie par l'IA : un déplacement ou une attaque.</summary>
public readonly record struct AiAction(Cell From, Cell To, bool IsAttack);

/// <summary>
/// IA du camp ennemi (un tour = UNE action). Priorités :
///   1. ATTAQUER une cible à portée (mortelle d'abord, sinon quelconque) ;
///   2. sinon SE METTRE À PORTÉE : un déplacement qui amène l'unité à pouvoir attaquer un joueur,
///      sans atterrir sur une case où elle se ferait tuer (« sans mourir ») ;
///   3. sinon faire avancer un PION ALÉATOIRE vers le joueur le plus proche.
///
/// Le tirage aléatoire (mise à portée comme avancée) évite de toujours pousser la même pièce :
/// l'armée progresse en se renouvelant au lieu d'envoyer un éclaireur solitaire.
/// </summary>
public static class EnemyAi
{
    private static readonly Random SharedRng = new();

    public static AiAction? ChooseAction(Match match) => ChooseAction(match, SharedRng);

    /// <param name="rng">Source d'aléa (injectable pour des tests déterministes).</param>
    public static AiAction? ChooseAction(Match match, Random rng)
    {
        if (match.IsOver || match.CurrentTurn != Faction.Enemy)
            return null;

        var enemies = match.Units().Where(x => x.Unit.Faction == Faction.Enemy).ToList();
        var players = match.Units().Where(x => x.Unit.Faction == Faction.Player).ToList();
        if (players.Count == 0)
            return null;

        var playerCells = players.Select(p => p.Cell).ToList();

        // 1. Attaques. On garde la première mortelle et la première quelconque rencontrées.
        AiAction? bestKill = null, bestAttack = null;

        // 2. Coups qui amènent à PORTÉE D'ATTAQUE sans mourir (départage aléatoire entre eux).
        var engage = new List<AiAction>();

        // 3. Avancées : un pion ALÉATOIRE parmi ceux qui peuvent se rapprocher (on retient, par
        //    unité, son meilleur coup ; puis on choisit l'unité au hasard).
        var advanceByUnit = new List<AiAction>();
        var anyLegalMove = new List<AiAction>();   // repli anti-blocage (le tour doit passer)

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

            var currentDistance = playerCells.Min(p => Chebyshev(from, p));
            AiAction? bestAdvanceForUnit = null;
            var bestAdvanceDistance = currentDistance;   // strictement mieux = se rapprocher

            foreach (var to in match.LegalMoves(from))
            {
                anyLegalMove.Add(new AiAction(from, to, IsAttack: false));

                // Mise à portée : depuis « to », l'unité aurait au moins une cible, et « to » n'est pas
                // une case où un joueur la tuerait.
                if (match.TargetsAfterMove(from, to).Count > 0 && !WouldBeKilledAt(match, to, unit, players))
                    engage.Add(new AiAction(from, to, IsAttack: false));

                var distance = playerCells.Min(p => Chebyshev(to, p));
                if (distance < bestAdvanceDistance)
                {
                    bestAdvanceDistance = distance;
                    bestAdvanceForUnit = new AiAction(from, to, IsAttack: false);
                }
            }

            if (bestAdvanceForUnit is { } adv)
                advanceByUnit.Add(adv);
        }

        if (bestKill is { } kill) return kill;
        if (bestAttack is { } attack) return attack;
        if (engage.Count > 0) return engage[rng.Next(engage.Count)];
        if (advanceByUnit.Count > 0) return advanceByUnit[rng.Next(advanceByUnit.Count)];
        if (anyLegalMove.Count > 0) return anyLegalMove[rng.Next(anyLegalMove.Count)];
        return null;
    }

    /// <summary>
    /// Vrai si un joueur menace déjà <paramref name="cell"/> d'une attaque qui TUERAIT
    /// <paramref name="unit"/> (dégâts ≥ PV). Approximation : menace depuis les positions
    /// actuelles des joueurs (sans anticiper leurs propres déplacements).
    /// </summary>
    private static bool WouldBeKilledAt(Match match, Cell cell, Unit unit, List<(Cell Cell, Unit Unit)> players)
    {
        foreach (var (pc, pu) in players)
        {
            if (pu.Damage < unit.Hp)
                continue;   // ne pourrait pas le tuer d'un coup
            if (match.ThreatenedCells(pc).Contains(cell))
                return true;
        }
        return false;
    }

    private static int Chebyshev(Cell a, Cell b) =>
        Math.Max(Math.Abs(a.Column - b.Column), Math.Abs(a.Row - b.Row));
}
