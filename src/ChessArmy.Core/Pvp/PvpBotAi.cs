using System;
using System.Collections.Generic;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;

namespace ChessArmy.Core.Pvp;

/// <summary>
/// Adversaire de secours du multi (aucun joueur trouvé). Deux niveaux d'intelligence selon le rang :
///  • de base : l'IA de la campagne (<see cref="EnemyAi"/>), plus ou moins précise ;
///  • tactique : évalue chaque coup possible (valeur des pièces, mise à mort, danger de la case d'arrivée,
///    sécurité de son commandant, menace sur le commandant adverse) et joue le meilleur, avec une part d'erreur
///    réglée par la précision.
/// Joue toujours le camp <see cref="Faction.Enemy"/>.
/// </summary>
public sealed class PvpBotController : IBattleController
{
    private readonly double _accuracy;
    private readonly bool _tactical;
    private readonly Random _rng;

    public PvpBotController(PvpBotDef level, int seed)
    {
        _accuracy = Math.Clamp(level.Accuracy, 0, 1);
        _tactical = level.Tactical;
        _rng = new Random(seed);
    }

    public bool TryGetAction(Match match, out BattleAction action)
    {
        if (!_tactical)
        {
            action = EnemyAi.ChooseAction(match, Array.Empty<Cell>(), _rng, _accuracy) is { } a
                ? (a.IsAttack ? BattleAction.Attack(a.From, a.To) : BattleAction.Move(a.From, a.To))
                : BattleAction.Pass;
            return true;
        }
        action = PvpBotAi.Choose(match, _accuracy, _rng) ?? BattleAction.Pass;
        return true;
    }

    /// <summary>Le bot accepte une proposition d'égalité seulement s'il est nettement en retard au matériel.</summary>
    public static bool AcceptsDraw(Match match) =>
        PvpBotAi.Material(match, Faction.Enemy) < PvpBotAi.Material(match, Faction.Player) * 0.8;
}

/// <summary>Évaluation tactique d'un coup pour le camp <see cref="Faction.Enemy"/> (cf. <see cref="PvpBotController"/>).</summary>
public static class PvpBotAi
{
    private const double KingValue = 100;

    /// <summary>Valeur d'une pièce : son coût en multi, plus ses PV restants ; le commandant vaut la partie.</summary>
    public static double Value(Unit unit) =>
        unit.IsEssential ? KingValue : PvpConfig.Current.CostOf(unit.Class) * 3 + unit.Hp * 0.25;

    public static double Material(Match match, Faction side)
    {
        var total = 0.0;
        foreach (var (_, u) in match.Units())
            if (u.Faction == side && !u.IsEssential)
                total += Value(u);
        return total;
    }

    public static BattleAction? Choose(Match match, double accuracy, Random rng)
    {
        if (match.IsOver || match.CurrentTurn != Faction.Enemy)
            return null;

        // Cases sous le feu du joueur, et position des deux commandants.
        var danger = new HashSet<Cell>();
        var buffer = new List<Cell>();
        Cell? myKing = null, theirKing = null;
        foreach (var (cell, u) in match.Units())
        {
            if (u.Faction == Faction.Player)
            {
                match.ThreatenedCells(cell, buffer);
                danger.UnionWith(buffer);
                if (u.IsEssential) theirKing = cell;
            }
            else if (u.IsEssential)
                myKing = cell;
        }
        var kingInDanger = myKing is { } k && danger.Contains(k);

        var scored = new List<(double Score, BattleAction Action)>();
        var bots = new List<(Cell Cell, Unit Unit)>();
        foreach (var entry in match.Units())
            if (entry.Unit.Faction == Faction.Enemy)
                bots.Add(entry);

        foreach (var (from, unit) in bots)
        {
            var value = Value(unit);

            // Attaques.
            foreach (var target in match.AttackTargets(from))
            {
                if (match.UnitAt(target) is not { } victim)
                    continue;
                var damage = match.PreviewDamage(from, target);
                var kill = damage >= victim.Hp;
                var score = damage * 0.5 + (kill ? 20 + Value(victim) : Value(victim) * damage / Math.Max(1.0, victim.MaxHp));
                if (victim.IsEssential && kill)
                    score += 10_000;   // mise à mort du commandant : partie gagnée
                if (kingInDanger && myKing is { } kc)
                {
                    match.ThreatenedCells(target, buffer);
                    if (buffer.Contains(kc) && kill)
                        score += 300;   // élimine la menace qui pèse sur son commandant
                }
                scored.Add((score, BattleAction.Attack(from, target)));
            }

            // Déplacements.
            foreach (var to in match.LegalMoves(from))
            {
                var score = 0.0;
                var exposedNow = danger.Contains(from);
                var exposedThen = danger.Contains(to);
                if (exposedThen)
                    score -= value * (unit.IsEssential ? 8 : 0.9);
                else if (exposedNow)
                    score += value * (unit.IsEssential ? 6 : 0.7);   // se met à l'abri

                var targets = match.TargetsAfterMove(from, to);
                foreach (var t in targets)
                    if (match.UnitAt(t) is { } enemy)
                        score += enemy.IsEssential ? 40 : Value(enemy) * 0.15;

                // Avance vers le commandant adverse (prudemment pour le sien).
                if (!unit.IsEssential && theirKing is { } tk)
                    score += (Distance(from, tk) - Distance(to, tk)) * 1.5;
                scored.Add((score + rng.NextDouble() * 0.01, BattleAction.Move(from, to)));
            }
        }

        if (scored.Count == 0)
            return null;
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        // Imprécision : de temps en temps, le bot rate le meilleur coup et joue un des suivants.
        var index = 0;
        while (index + 1 < scored.Count && rng.NextDouble() > accuracy)
            index++;
        return scored[Math.Min(index, Math.Min(2, scored.Count - 1))].Action;
    }

    private static int Distance(Cell a, Cell b) =>
        Math.Max(Math.Abs(a.Column - b.Column), Math.Abs(a.Row - b.Row));
}
