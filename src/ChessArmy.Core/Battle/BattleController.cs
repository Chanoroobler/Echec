using System.Collections.Generic;
using ChessArmy.Core.Map;

namespace ChessArmy.Core.Battle;

/// <summary>
/// Ce qui décide des coups d'un camp en combat. Aujourd'hui l'IA (<see cref="AiBattleController"/>) joue le
/// camp ennemi ; demain un adversaire DISTANT (coups reçus du réseau) ou un second joueur local pourra le
/// remplacer sans toucher à la scène, qui ne fait que demander « ton coup est-il prêt ? » chaque frame.
/// </summary>
public interface IBattleController
{
    /// <summary>
    /// Vrai si un coup est prêt (<paramref name="action"/>, éventuellement <see cref="BattleAction.Pass"/>) ;
    /// faux s'il faut encore attendre (coup réseau pas encore arrivé). Appelé seulement quand c'est le tour
    /// du camp contrôlé et que la partie n'est pas finie.
    /// </summary>
    bool TryGetAction(Match match, out BattleAction action);
}

/// <summary>Camp joué par <see cref="EnemyAi"/> : son coup est toujours prêt (ou un passe s'il n'a rien d'utile).</summary>
public sealed class AiBattleController : IBattleController
{
    private readonly System.Func<IReadOnlyCollection<Cell>> _paysanCells;
    private readonly double _accuracy;

    /// <param name="paysanCells">Cases des paysans encore en jeu, relues à chaque coup (missions spéciales).</param>
    /// <param name="accuracy">Précision de l'IA (cf. <see cref="EnemyAi.ChooseAction(Match, IReadOnlyCollection{Cell}, double)"/>).</param>
    public AiBattleController(System.Func<IReadOnlyCollection<Cell>> paysanCells, double accuracy)
    {
        _paysanCells = paysanCells;
        _accuracy = accuracy;
    }

    public bool TryGetAction(Match match, out BattleAction action)
    {
        action = EnemyAi.ChooseAction(match, _paysanCells(), _accuracy) is { } a
            ? (a.IsAttack ? BattleAction.Attack(a.From, a.To) : BattleAction.Move(a.From, a.To))
            : BattleAction.Pass;
        return true;
    }
}
