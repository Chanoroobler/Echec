using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;

namespace ChessArmy.Core.Pvp;

/// <summary>
/// Adversaire de secours du multi (aucun joueur trouvé), réglé par rang (<see cref="PvpBotDef"/>). Les rangs bas
/// jouent un coup à la fois ; les rangs hauts CALCULENT (alpha-bêta sur des copies de la partie), ce qui peut
/// prendre quelques centaines de millisecondes : le calcul tourne alors en tâche de fond et
/// <see cref="TryGetAction"/> répond « pas encore » en attendant (la scène réessaie à la frame suivante).
/// Joue toujours le camp <see cref="Faction.Enemy"/>.
/// </summary>
public sealed class PvpBotController : IBattleController
{
    private readonly PvpBotDef _def;
    private readonly PvpAiWeights _weights;
    private readonly Random _rng;
    private readonly PvpForker? _forker;
    private readonly IReadOnlyList<BattleAction>? _log;
    private readonly Random _delayRng;
    private Task<BattleAction>? _thinking;
    private BattleAction? _pending;      // coup choisi, joué quand le temps de réflexion affiché est écoulé
    private long _readyAt;
    private long _slowReadyAt;           // fin de la réflexion normale (si le coup joué n'est pas l'évident)
    private BattleAction? _obvious;      // coup évident de la position, s'il y en a un
    private int _position = -2;          // longueur du journal quand la réflexion a commencé

    /// <param name="forker">Copies de la partie (sans elles, le bot ne calcule pas : un coup à la fois).</param>
    /// <param name="log">Journal des coups de la partie, tenu par la scène (lu au début de chaque réflexion).</param>
    public PvpBotController(PvpBotDef level, int seed, PvpForker? forker = null, IReadOnlyList<BattleAction>? log = null,
        PvpAiWeights? weights = null)
    {
        _def = level;
        _weights = weights ?? PvpConfig.Current.BotWeights;
        _rng = new Random(seed);
        _delayRng = new Random(seed ^ 0x2545F491);   // à part : le délai ne change pas le choix des coups
        _forker = forker;
        _log = log;
    }

    /// <summary>Pendule basse du bot : son tour a commencé sous le seuil, donc la case <see cref="Match.NextCollapse"/>
    /// derrière lui tombera après son coup avec ce qui s'y trouve. Tenu à jour par la scène (seule à voir la pendule).</summary>
    public bool CollapseArmed { get; set; }

    public bool TryGetAction(Match match, out BattleAction action)
    {
        // Un coup préparé pour une autre position (la partie a bougé entre-temps) est oublié.
        var position = _log?.Count ?? -1;
        if ((_thinking != null || _pending != null) && position != _position)
            Reset();

        if (_thinking == null && _pending == null)
        {
            _position = position;
            // Temps de « réflexion » affiché, tiré à chaque coup : le bot a l'air d'un joueur. Coup évident (seul
            // possible, commandant adverse à prendre, prise qui sauve le sien) : bien plus court, s'il le joue.
            var delay = Draw(_def.MinDelaySeconds, _def.MaxDelaySeconds);
            var quick = Draw(_def.ObviousMinDelaySeconds, _def.ObviousMaxDelaySeconds);
            _obvious = PvpBotAi.Obvious(match, Faction.Enemy, _delayRng, out var certain,
                CollapseArmed ? match.NextCollapse(Faction.Enemy) : null);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            _slowReadyAt = now + (long)(delay * System.Diagnostics.Stopwatch.Frequency);
            _readyAt = _obvious != null ? now + (long)(Math.Min(quick, delay) * System.Diagnostics.Stopwatch.Frequency) : _slowReadyAt;
            var doomed = CollapseArmed ? match.NextCollapse(Faction.Enemy) : null;   // tombe après ce coup
            var collapse = CollapseArmed;
            if (certain && _obvious is { } sure)
                _pending = sure;   // rien à calculer
            else if (_def.Depth <= 0 || _forker == null || _log == null)
                _pending = PvpBotAi.Choose(match, Faction.Enemy, _def, _weights, _rng, doomed) ?? BattleAction.Pass;
            else
            {
                var log = new List<BattleAction>(_log);   // instantané : la scène ne joue rien pendant la réflexion
                var forker = _forker;
                var rng = new Random(_rng.Next());   // propre à cette réflexion : une réflexion abandonnée ne gêne pas la suivante
                var budget = Math.Max(_def.ThinkSeconds, delay);   // il calcule vraiment pendant sa « réflexion »
                _thinking = Task.Run(() => PvpBotAi.Search(forker, log, Faction.Enemy, _def, _weights, rng, budget, collapse)
                                           ?? BattleAction.Pass);
            }
        }

        if (_thinking is { IsCompleted: true } done)
        {
            _pending = done.IsCompletedSuccessfully ? done.Result : BattleAction.Pass;
            _thinking = null;
        }
        if (_pending is { } chosen && _obvious is { } obvious && chosen != obvious)
        {
            _readyAt = _slowReadyAt;   // il a préféré un autre coup que l'évident : réflexion normale
            _obvious = null;
        }
        if (_pending is not { } ready || System.Diagnostics.Stopwatch.GetTimestamp() < _readyAt)
        {
            action = default;
            return false;   // encore en train de « réfléchir »
        }
        action = ready;
        Reset();
        return true;
    }

    private void Reset()
    {
        _thinking = null;
        _pending = null;
        _obvious = null;
        _position = -2;
    }

    private double Draw(double min, double max) => min + _delayRng.NextDouble() * Math.Max(0, max - min);

    /// <summary>Le bot accepte une proposition d'égalité seulement s'il est nettement en retard au matériel.</summary>
    public static bool AcceptsDraw(Match match) =>
        PvpBotAi.Material(match, Faction.Enemy) < PvpBotAi.Material(match, Faction.Player) * 0.8;
}

/// <summary>
/// Cerveau des bots multi, pour un camp donné :
///  • <see cref="ScoreActions"/> : note RAPIDE de chaque coup possible (gain de l'attaque, danger de la case
///    d'arrivée, menace sur les commandants, avance) ; sert de tri aux autres et de jeu aux rangs bas ;
///  • <see cref="Evaluate"/> : note d'une POSITION (matériel, commandants menacés, pièces exposées, initiative,
///    avance, mobilité, soutien), dont les poids sont ENTRAÎNÉS par auto-jeu (<see cref="PvpAiWeights"/>) ;
///  • <see cref="Search"/> : alpha-bêta sur des copies de la partie, aux rangs hauts.
/// La valeur d'une pièce vient de ses STATS, jamais de son coût (l'équilibrage en dérive les coûts).
/// </summary>
public static class PvpBotAi
{
    private const double KingValue = 100;
    private const double WinScore = 1_000_000;

    /// <summary>Copies de partie faites par la recherche (mesures de l'outil).</summary>
    public static long NodeCount;

    /// <summary>Valeur d'une pièce pour l'heuristique rapide : PV restants et puissance ; le commandant vaut la partie.</summary>
    public static double Value(Unit unit) =>
        unit.IsEssential ? KingValue : 2 + unit.Hp * 0.3 + unit.Damage * 0.4;

    public static double Material(Match match, Faction side)
    {
        var total = 0.0;
        foreach (var (_, u) in match.Units())
            if (u.Faction == side && !u.IsEssential)
                total += Value(u);
        return total;
    }

    // ── Choix d'un coup ──────────────────────────────────────────────────────────

    /// <summary>Ancien bot tactique (un coup à la fois, prudence pleine) : sert aux mesures d'équilibrage.</summary>
    public static BattleAction? Choose(Match match, Faction side, double accuracy, Random rng) =>
        Choose(match, side, new PvpBotDef { Accuracy = accuracy }, null, rng);

    /// <summary>
    /// Coup d'un bot qui ne calcule pas (profondeur 0) : le mieux noté par <see cref="ScoreActions"/>, avec sa part
    /// d'erreur. S'il « attaque toujours » et qu'une attaque existe, il attaque : la meilleure (souvent une mise à
    /// mort) avec la probabilité <see cref="PvpBotDef.KillChance"/>, sinon une attaque au hasard.
    /// </summary>
    public static BattleAction? Choose(Match match, Faction side, PvpBotDef def, PvpAiWeights? weights, Random rng,
        Cell? doomed = null)
    {
        var caution = def.Recklessness > 0 && rng.NextDouble() < def.Recklessness ? 0 : def.Caution;   // coup imprudent
        var scored = ScoreActions(match, side, caution, rng, def.Style, doomed);
        if (scored.Count == 0)
            return null;
        if (def.ForceAttack && !MustRescue(match, side, doomed) && FirstAttack(scored) is var first && first >= 0)
        {
            if (rng.NextDouble() < def.KillChance)
                return scored[first].Action;
            var attacks = 0;
            foreach (var s in scored)
                if (s.Action.Kind == BattleActionKind.Attack) attacks++;
            var pick = rng.Next(attacks);
            foreach (var s in scored)
                if (s.Action.Kind == BattleActionKind.Attack && pick-- == 0)
                    return s.Action;
        }
        return scored[Blunder(def.Accuracy, scored.Count, rng)].Action;
    }

    /// <summary>
    /// Coup ÉVIDENT de <paramref name="side"/>, s'il y en a un (le bot le joue alors plus vite) : le seul coup
    /// possible ou la mise à mort du commandant adverse (<paramref name="certain"/> : rien à calculer), ou la prise
    /// qui supprime une menace sur son propre commandant. Null sinon.
    /// </summary>
    public static BattleAction? Obvious(Match match, Faction side, Random rng, out bool certain, Cell? doomed = null)
    {
        certain = false;
        if (match.IsOver || match.CurrentTurn != side)
            return null;
        var scored = ScoreActions(match, side, 1.0, rng, null, doomed);
        if (scored.Count <= 1)
        {
            certain = true;
            return scored.Count == 0 ? BattleAction.Pass : scored[0].Action;
        }
        var (score, action) = scored[0];
        if (action.Kind != BattleActionKind.Attack)
            return null;
        certain = score >= CommanderKillBonus;
        return score >= SavingKillBonus ? action : null;
    }

    private static int FirstAttack(List<(double Score, BattleAction Action)> scored)
    {
        for (var i = 0; i < scored.Count; i++)
            if (scored[i].Action.Kind == BattleActionKind.Attack)
                return i;
        return -1;
    }

    /// <summary>Rang du coup joué : 0 (le meilleur) avec la probabilité <paramref name="accuracy"/>, sinon un des
    /// deux suivants.</summary>
    private static int Blunder(double accuracy, int count, Random rng)
    {
        var index = 0;
        while (index < 2 && index + 1 < count && rng.NextDouble() > accuracy)
            index++;
        return index;
    }

    /// <summary>
    /// Coup d'un bot qui CALCULE : alpha-bêta à <see cref="PvpBotDef.Depth"/> + 1 demi-coups sur des copies de la
    /// partie (<paramref name="log"/> = coups déjà joués), en n'examinant à chaque niveau que les coups les mieux
    /// notés par l'heuristique rapide. Les mises à mort et l'attaque obligatoire restent prioritaires comme aux
    /// rangs bas ; l'imprécision fait parfois jouer le deuxième meilleur coup calculé.
    /// </summary>
    public static BattleAction? Search(PvpForker forker, IReadOnlyList<BattleAction> log, Faction side, PvpBotDef def,
        PvpAiWeights weights, Random rng, double maxSeconds = 0, bool collapse = false)
    {
        weights = def.Style?.Apply(weights) ?? weights;   // style du bot nommé
        if (def.PieceCost is { } pieceCost)
            weights = weights.With(PvpAiWeights.PieceCost, pieceCost);   // valeur mesurée des pièces (Or III et plus)
        var root = forker.Fork(log, log.Count);
        if (root is null || root.IsOver || root.CurrentTurn != side)
            return null;
        var candidates = Candidates(root, side, def.Breadth, def, rng, def.Style, collapse ? root.NextCollapse(side) : null);
        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];

        // Sans limite de temps : directement à la profondeur voulue (mesures de l'outil, reproductibles). Avec
        // une limite (en jeu) : approfondissement progressif, on garde la dernière profondeur TERMINÉE.
        var deadline = maxSeconds > 0
            ? System.Diagnostics.Stopwatch.GetTimestamp() + (long)(maxSeconds * System.Diagnostics.Stopwatch.Frequency)
            : long.MaxValue;
        List<(double Score, BattleAction Action)>? best = null;
        var table = new SearchTable();
        for (var depth = maxSeconds > 0 ? 0 : def.Depth; depth <= def.Depth; depth++)
        {
            var results = SearchRoot(root, candidates, depth, side, def, weights, rng, deadline, table, collapse);
            if (results == null)
                break;   // temps écoulé pendant cette profondeur : on garde la précédente
            best = results;
            // Le meilleur coup d'abord à la profondeur suivante : l'alpha-bêta coupe bien plus.
            candidates = results.ConvertAll(r => r.Action);
        }
        if (best == null)
            return candidates[0];
        return best[Blunder(def.Accuracy, best.Count, rng)].Action;
    }

    /// <summary>
    /// Une profondeur de la racine. Le premier coup (le meilleur de la profondeur précédente) est calculé seul ; il
    /// fixe la fenêtre des autres, calculés EN PARALLÈLE sur les cœurs (chacun son aléa, tiré d'avance : le résultat
    /// ne dépend pas de l'ordre d'arrivée). On garde une fenêtre ouverte sous le meilleur pour classer aussi le
    /// deuxième (imprécision).
    /// </summary>
    private static List<(double Score, BattleAction Action)>? SearchRoot(Match root, List<BattleAction> candidates,
        int depth, Faction side, PvpBotDef def, PvpAiWeights weights, Random rng, long deadline, SearchTable table,
        bool collapse)
    {
        var count = candidates.Count;
        var values = new double[count];
        var seeds = new int[count];
        for (var i = 0; i < count; i++)
            seeds[i] = rng.Next();

        double Run(int i, double alpha)
        {
            var child = Child(root, candidates[i]);
            if (collapse && child != null)
                PvpSetup.CollapseAfterTurn(child, side, 0f);   // pendule basse : sa case tombe après le coup, comme en jeu
            return child is null ? double.NegativeInfinity
                : AlphaBeta(child, depth, alpha, double.PositiveInfinity, side, def, weights, new Random(seeds[i]), deadline, table);
        }

        values[0] = Run(0, double.NegativeInfinity);
        var window = values[0] - 1;
        if (Parallel && count > 2)
            System.Threading.Tasks.Parallel.For(1, count, i => values[i] = Run(i, window));
        else
            for (var i = 1; i < count; i++)
                values[i] = Run(i, window);
        if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
            return null;

        var results = new List<(double Score, BattleAction Action)>(count);
        for (var i = 0; i < count; i++)
            results.Add((values[i] + rng.NextDouble() * 1e-6, candidates[i]));
        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

    /// <summary>Calcul de la racine sur plusieurs cœurs (désactivable pour les mesures en parallèle de l'outil).</summary>
    public static bool Parallel { get; set; } = true;

    /// <summary>Copie de <paramref name="match"/> après <paramref name="action"/> ; null si le coup est invalide.</summary>
    private static Match? Child(Match match, BattleAction action)
    {
        System.Threading.Interlocked.Increment(ref NodeCount);
        var child = match.Clone();
        return PvpForker.Step(child, action) ? child : null;
    }

    private static double AlphaBeta(Match match, int depth, double alpha, double beta, Faction me, PvpBotDef def,
        PvpAiWeights weights, Random rng, long deadline, SearchTable table)
    {
        if (depth <= 0 || match.IsOver || System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
            return Evaluate(match, me, weights);

        // Position déjà calculée (atteinte par un autre ordre de coups, ou à la profondeur précédente) : sa valeur
        // si elle suffit, sinon au moins son meilleur coup, essayé en premier.
        var key = match.ComputeStateHash();
        var known = table.Get(key);
        if (known is { } k && k.Depth >= depth)
        {
            if (k.Bound == SearchTable.Exact) return k.Value;
            if (k.Bound == SearchTable.Lower && k.Value >= beta) return k.Value;
            if (k.Bound == SearchTable.Upper && k.Value <= alpha) return k.Value;
        }

        var mover = match.CurrentTurn;
        var moves = Candidates(match, mover, def.ReplyBreadth, null, rng, mover == me ? def.Style : null);
        if (moves.Count == 0)
            return Evaluate(match, me, weights);
        if (known?.Best is { } hint && moves.IndexOf(hint) is var at && at > 0)
        {
            moves.RemoveAt(at);
            moves.Insert(0, hint);
        }

        var alpha0 = alpha;
        var beta0 = beta;
        var maximizing = mover == me;
        var best = maximizing ? double.NegativeInfinity : double.PositiveInfinity;
        BattleAction? bestMove = null;
        foreach (var action in moves)
        {
            var child = Child(match, action);
            if (child == null)
                continue;
            var value = AlphaBeta(child, depth - 1, alpha, beta, me, def, weights, rng, deadline, table);
            if (maximizing ? value > best : value < best)
            {
                best = value;
                bestMove = action;
            }
            if (maximizing) alpha = Math.Max(alpha, value);
            else beta = Math.Min(beta, value);
            if (beta <= alpha)
                break;
        }
        if (double.IsInfinity(best))
            return Evaluate(match, me, weights);
        if (System.Diagnostics.Stopwatch.GetTimestamp() <= deadline)   // un calcul interrompu ne s'apprend pas
            table.Put(key, depth, best, best <= alpha0 ? SearchTable.Upper : best >= beta0 ? SearchTable.Lower : SearchTable.Exact, bestMove);
        return best;
    }

    /// <summary>
    /// Mémoire des positions déjà calculées pendant UNE réflexion (table de transposition), partagée par les cœurs :
    /// valeur, profondeur, nature de la valeur (exacte ou borne d'une coupure alpha-bêta) et meilleur coup.
    /// </summary>
    private sealed class SearchTable
    {
        public const byte Exact = 0, Lower = 1, Upper = 2;

        public readonly record struct Entry(int Depth, double Value, byte Bound, BattleAction? Best);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, Entry> _entries = new();

        public Entry? Get(ulong key) => _entries.TryGetValue(key, out var e) ? e : null;

        public void Put(ulong key, int depth, double value, byte bound, BattleAction? best)
        {
            if (_entries.Count > 400_000)
                return;   // garde-fou mémoire (jamais atteint aux profondeurs des bots)
            _entries.AddOrUpdate(key, new Entry(depth, value, bound, best),
                (_, old) => depth >= old.Depth ? new Entry(depth, value, bound, best ?? old.Best) : old);
        }
    }

    /// <summary>
    /// Coups examinés par la recherche : les <paramref name="breadth"/> mieux notés, plus TOUTES les mises à mort
    /// (jamais élaguées). À la racine d'un bot qui « attaque toujours », seules les attaques restent s'il y en a.
    /// </summary>
    private static List<BattleAction> Candidates(Match match, Faction side, int breadth, PvpBotDef? root, Random rng,
        PvpBotStyle? style = null, Cell? doomed = null)
    {
        var scored = ScoreActions(match, side, 1.0, rng, style, doomed);
        var result = new List<BattleAction>(breadth + 4);
        var onlyAttacks = root is { ForceAttack: true } && !MustRescue(match, side, doomed) && FirstAttack(scored) >= 0;
        for (var i = 0; i < scored.Count; i++)
        {
            var (score, action) = scored[i];
            if (onlyAttacks && action.Kind != BattleActionKind.Attack)
                continue;
            var kill = action.Kind == BattleActionKind.Attack && score >= KillThreshold;
            if (result.Count < breadth || kill)
                result.Add(action);
        }
        return result;
    }

    // ── Heuristique rapide ───────────────────────────────────────────────────────

    private const double KillBonus = 20;
    private const double CommanderKillBonus = 10_000;
    private const double SavingKillBonus = 300;
    private const double CollapseLossBonus = 100;      // laisser une pièce sur la case qui tombe (pendule basse)
    private const double KillThreshold = KillBonus;   // une attaque notée au moins autant tue sa cible
    private const int GuardRadius = 2;                 // style « garde » : distance au commandant jugée sûre
    private const double KingReachBonus = 40;          // coup qui met le commandant adverse à portée
    // Réglés par mesure (tools/ChessArmy.Balance --stall) : sans eux, un tiers des parties de bots n'en finissait pas.
    private const double KingHitBonus = 30;            // frapper le commandant sans le tuer (sinon se replacer passait avant)
    private const double UndoPenalty = 25;             // défaire son propre dernier déplacement (aller-retour sans fin)

    /// <summary>
    /// Note RAPIDE de chaque coup possible de <paramref name="side"/> (qui doit avoir la main), du meilleur au moins
    /// bon : gain de l'attaque (mise à mort surtout), danger de la case d'arrivée pesé par
    /// <paramref name="caution"/>, menaces sur les deux commandants, avance vers le commandant adverse.
    /// </summary>
    public static List<(double Score, BattleAction Action)> ScoreActions(Match match, Faction side, double caution,
        Random rng, PvpBotStyle? style = null, Cell? doomed = null)
    {
        var scored = ScoreActionsCore(match, side, caution, rng, style);
        if (doomed is { } d)
            PenalizeCollapse(match, side, d, scored);
        return scored;
    }

    /// <summary>
    /// Pendule basse : la case <paramref name="doomed"/> tombera après ce coup, avec ce qui s'y tient. Y laisser une
    /// de ses pièces ou y en poser une coûte cette pièce (la partie pour le commandant) ; l'en sortir rapporte autant.
    /// </summary>
    /// <summary>Une pièce de <paramref name="side"/> se tient sur la case qui va tomber : l'attaque obligatoire
    /// cède, la sortir de là passe avant.</summary>
    private static bool MustRescue(Match match, Faction side, Cell? doomed) =>
        doomed is { } d && match.UnitAt(d) is { } u && u.Faction == side;

    private static void PenalizeCollapse(Match match, Faction side, Cell doomed,
        List<(double Score, BattleAction Action)> scored)
    {
        // Une pièce perdue POUR RIEN : plus cher que tout coup de placement ou prise ordinaire, moins que sauver
        // son commandant (SavingKillBonus) ou prendre celui d'en face.
        static double Cost(Unit u) => u.IsEssential ? CommanderKillBonus : CollapseLossBonus + Value(u);
        var stuck = match.UnitAt(doomed) is { } u && u.Faction == side ? Cost(u) : 0.0;
        for (var i = 0; i < scored.Count; i++)
        {
            var (score, action) = scored[i];
            if (action.Kind == BattleActionKind.Move && action.From == doomed)
                continue;                                     // la pièce menacée s'en va : sauvée
            score -= stuck;                                   // elle reste dessus : perdue
            if (action.Kind == BattleActionKind.Move && action.Target == doomed && match.UnitAt(action.From) is { } mover)
                score -= Cost(mover);                         // s'y poser : perdue aussi
            scored[i] = (score, action);
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
    }

    private static List<(double Score, BattleAction Action)> ScoreActionsCore(Match match, Faction side, double caution,
        Random rng, PvpBotStyle? style)
    {
        var scored = new List<(double Score, BattleAction Action)>();
        if (match.IsOver || match.CurrentTurn != side)
            return scored;
        style ??= PvpBotStyle.Neutral;
        caution *= style.Caution;
        var opponent = side.Opponent();

        // Dégâts que l'adversaire peut porter sur chaque case, et position des commandants (et des ennemis).
        var danger = new HashSet<Cell>();
        var incoming = new Dictionary<Cell, int>();
        var buffer = new List<Cell>();
        var enemies = new List<Cell>();
        Cell? myKing = null, theirKing = null;
        foreach (var (cell, u) in match.Units())
        {
            if (u.Faction == opponent)
            {
                match.ThreatenedCells(cell, buffer);
                danger.UnionWith(buffer);
                foreach (var c in buffer)
                    incoming[c] = incoming.GetValueOrDefault(c) + u.Damage;
                enemies.Add(cell);
                if (u.IsEssential) theirKing = cell;
            }
            else if (u.IsEssential)
                myKing = cell;
        }
        var kingInDanger = myKing is { } k && danger.Contains(k);

        var mine = new List<(Cell Cell, Unit Unit)>();
        foreach (var entry in match.Units())
            if (entry.Unit.Faction == side)
                mine.Add(entry);

        // En avance au matériel, nos pièces échangent sans frilosité : se laisser toucher pour approcher le
        // commandant adverse vaut mieux que tourner autour de lui jusqu'à la pendule.
        var myMaterial = Material(match, side);
        var theirMaterial = Material(match, opponent);
        var lead = myMaterial <= 0 ? 1.0 : Math.Clamp(theirMaterial / myMaterial, 0.25, 1.0);
        var lastMove = match.LastMoveOf(side);
        var kingHunts = theirMaterial <= 0 && myKing is { } mk && theirKing is { } ok2
            && match.UnitAt(mk) is { } me && match.UnitAt(ok2) is { } him
            && (myMaterial > 0 || HitsToKill(me, him) < HitsToKill(him, me));

        foreach (var (from, unit) in mine)
        {
            var value = Value(unit);

            // Attaques.
            foreach (var target in match.AttackTargets(from))
            {
                if (match.UnitAt(target) is not { } victim)
                    continue;
                var damage = match.PreviewDamage(from, target);
                var kill = damage >= victim.Hp;
                var score = damage * 0.5 + (kill ? KillBonus + Value(victim) : Value(victim) * damage / Math.Max(1.0, victim.MaxHp));
                if (victim.IsEssential && kill)
                    score += CommanderKillBonus;   // mise à mort du commandant : partie gagnée
                else if (victim.IsEssential)
                    score += KingHitBonus;
                if (kingInDanger && myKing is { } kc && kill)
                {
                    match.ThreatenedCells(target, buffer);
                    if (buffer.Contains(kc))
                        score += SavingKillBonus;   // élimine la menace qui pèse sur son commandant
                }
                scored.Add((score, BattleAction.Attack(from, target)));
            }

            // Soins (pièces qui en ont le trait).
            foreach (var target in match.HealTargets(from))
                if (match.UnitAt(target) is { } ally)
                    scored.Add((Math.Min(match.PreviewHeal(from), ally.MaxHp - ally.Hp) * 0.6, BattleAction.Heal(from, target)));

            // Déplacements.
            foreach (var to in match.LegalMoves(from))
            {
                // L'ÉCHANGE : ce qu'on risque sur la case d'arrivée (part de nos PV que l'adversaire peut y
                // enlever) contre ce qu'on pourra frapper depuis elle. Un commandant ne prend presque aucun risque.
                var riskNow = Math.Min(1.0, incoming.GetValueOrDefault(from) / (double)Math.Max(1, unit.Hp));
                var riskThen = Math.Min(1.0, incoming.GetValueOrDefault(to) / (double)Math.Max(1, unit.Hp));
                var cautionFactor = (unit.IsEssential ? 8.0 : 0.6 * lead) * caution;
                var score = (riskNow - riskThen) * value * cautionFactor;

                // Défaire son propre dernier coup (A→B puis B→A) : sans mémoire, le bot tourne en rond à l'infini.
                // Un commandant menacé fuit quand même par là : son risque pèse bien plus que la pénalité.
                if (lastMove is { } lm && lm.To == from && lm.From == to)
                    score -= UndoPenalty;

                // Le commandant ne va PAS chercher de cible (sauf à la chasse) : il frappe ce qui vient à lui. Mesuré
                // (--stall, duels miroir) : le commandant qui partait au contact se faisait encercler, et la mesure
                // des coûts pénalisait les commandants mobiles à tort.
                if (!unit.IsEssential || kingHunts)
                {
                    var best = 0.0;
                    foreach (var t in match.TargetsAfterMove(from, to))
                        if (match.UnitAt(t) is { } enemy)
                            best = Math.Max(best, enemy.IsEssential ? KingReachBonus
                                : Value(enemy) * Math.Min(1.0, unit.Damage / (double)Math.Max(1, enemy.Hp)));
                    score += best * 0.8 * style.Aggression;
                }

                // Avance vers le commandant adverse, ou vers l'ennemi le plus proche s'il n'y en a pas
                // (prudemment pour son propre commandant) : sans ça, deux bots prudents tourneraient en rond.
                if (!unit.IsEssential)
                {
                    var goal = theirKing ?? Nearest(from, enemies);
                    if (goal is { } g)
                        score += (Distance(from, g) - Distance(to, g)) * 2.0 * style.Advance;
                    // Style « garde » : les pièces éloignées reviennent vers leur commandant.
                    if (style.Guard > 0 && myKing is { } home && Distance(from, home) > GuardRadius)
                        score += (Distance(from, home) - Distance(to, home)) * 2.0 * style.Guard;
                }
                // Le commandant part à l'assaut quand l'adversaire n'a plus que le sien et qu'il gagne le duel
                // (ou qu'il a encore des pièces pour l'appuyer) : sinon deux commandants se regardent jusqu'à la pendule.
                else if (kingHunts && theirKing is { } prey2)
                    score += (Distance(from, prey2) - Distance(to, prey2)) * 2.0 * style.Advance;
                scored.Add((score + rng.NextDouble() * 0.01, BattleAction.Move(from, to)));
            }
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored;
    }

    // ── Évaluation d'une position ────────────────────────────────────────────────

    /// <summary>
    /// Note d'une position du point de vue de <paramref name="me"/> (positive = avantage). Partie finie : ±victoire.
    /// Si le camp qui a la main peut tuer le commandant adverse, la partie est jouée. Sinon : matériel (pièces
    /// pesées par leurs PV, puissance, portée, déplacement), pression sur chaque commandant, meilleure prise du camp
    /// qui a la main (initiative), pièces exposées de l'autre camp, avance vers le commandant adverse, mobilité,
    /// soutien entre voisins.
    /// </summary>
    public static double Evaluate(Match match, Faction me, PvpAiWeights w)
    {
        if (match.IsOver)
            return match.IsDraw || match.Winner is null ? 0 : match.Winner == me ? WinScore : -WinScore;

        var mover = match.CurrentTurn;
        var units = new List<(Cell Cell, Unit Unit)>(32);
        foreach (var entry in match.Units())
            units.Add(entry);

        // Dégâts que chaque pièce peut subir au prochain coup de l'adversaire, et meilleure prise de chaque camp.
        var hits = new Dictionary<Cell, int>();
        var bestGain = new double[2];
        var mobility = new int[2];
        Cell? playerKing = null, enemyKing = null;
        var buffer = new List<Cell>();
        foreach (var (cell, u) in units)
        {
            if (u.IsEssential)
            {
                if (u.Faction == Faction.Player) playerKing = cell;
                else enemyKing = cell;
            }
            match.AttackTargets(cell, buffer);
            foreach (var t in buffer)
            {
                if (match.UnitAt(t) is not { } victim || victim.Faction == u.Faction)
                    continue;
                var dmg = match.PreviewDamage(cell, t);
                hits[t] = hits.GetValueOrDefault(t) + dmg;
                var gain = victim.IsEssential ? (dmg >= victim.Hp ? WinScore / 2 : w[PvpAiWeights.KingDanger] * dmg / Math.Max(1.0, victim.Hp))
                    : Loss(victim, dmg, w);
                var side = (int)u.Faction;
                if (gain > bestGain[side]) bestGain[side] = gain;
            }
            match.LegalMoves(cell, buffer);
            mobility[(int)u.Faction] += buffer.Count;
        }

        // Le camp qui a la main tue le commandant adverse : partie jouée.
        if (bestGain[(int)mover] >= WinScore / 2)
            return mover == me ? WinScore / 2 : -WinScore / 2;

        var score = 0.0;
        foreach (var (cell, u) in units)
        {
            var sign = u.Faction == me ? 1.0 : -1.0;
            var damage = hits.GetValueOrDefault(cell);
            if (u.IsEssential)
            {
                score -= sign * w[PvpAiWeights.KingDanger] * Math.Min(1.0, damage / Math.Max(1.0, u.Hp));
                continue;
            }
            score += sign * PieceValue(u, w);
            if (u.Faction != mover && damage > 0)
                score -= sign * w[PvpAiWeights.Hanging] * Loss(u, damage, w);

            var goal = u.Faction == Faction.Player ? enemyKing : playerKing;
            if (goal is { } g)
                score += sign * w[PvpAiWeights.Advance] * (PvpSetup.Size - 1 - Distance(cell, g));

            var friends = 0;
            for (var dc = -1; dc <= 1; dc++)
                for (var dr = -1; dr <= 1; dr++)
                    if ((dc != 0 || dr != 0) && match.UnitAt(new Cell(cell.Column + dc, cell.Row + dr)) is { } n
                        && n.Faction == u.Faction)
                        friends++;
            score += sign * w[PvpAiWeights.Support] * Math.Min(friends, 3);

            // Style « garde » (ses propres pièces seulement) : rester près de son commandant.
            if (sign > 0 && w[PvpAiWeights.Guard] != 0 && (u.Faction == Faction.Player ? playerKing : enemyKing) is { } home)
                score += w[PvpAiWeights.Guard] * Math.Max(0, GuardRadius + 1 - Distance(cell, home));
        }

        var meIndex = (int)me;
        var themIndex = (int)me.Opponent();
        score += w[PvpAiWeights.Mobility] * (mobility[meIndex] - mobility[themIndex]);
        score += (mover == me ? 1 : -1) * w[PvpAiWeights.Tempo] * bestGain[(int)mover];
        return score;
    }

    /// <summary>Valeur d'une pièce (hors commandant) pour l'évaluation : poids entraînés.</summary>
    /// <summary>Valeur d'une pièce pour l'évaluation : ses stats (PV restants : une pièce blessée vaut moins) plus son
    /// coût multi MESURÉ en batailles réelles (« unitCost »), seul à refléter ses TRAITS (Duelliste, Interception…) :
    /// sans lui, un Maître d'armes « valait » moins qu'un Mage. L'outil d'équilibrage mesure ces coûts avec des bots
    /// sans recherche, qui n'utilisent pas cette évaluation : pas de boucle.</summary>
    private static double PieceValue(Unit u, PvpAiWeights w) =>
        w[PvpAiWeights.PieceBase] + w[PvpAiWeights.PieceHp] * u.Hp + w[PvpAiWeights.PieceDamage] * u.Damage
        + w[PvpAiWeights.PieceRange] * u.AttackRange + w[PvpAiWeights.PieceMove] * u.MoveRange
        + (w[PvpAiWeights.PieceCost] == 0 ? 0 : w[PvpAiWeights.PieceCost] * PvpConfig.Current.CostOf(u.Class));

    /// <summary>Perte de valeur d'une pièce qui encaisse <paramref name="damage"/> : toute sa valeur si elle meurt.</summary>
    private static double Loss(Unit u, int damage, PvpAiWeights w) =>
        damage >= u.Hp ? PieceValue(u, w) : w[PvpAiWeights.PieceHp] * damage;

    private static int HitsToKill(Unit attacker, Unit victim) =>
        attacker.Damage <= 0 ? int.MaxValue : (victim.Hp + attacker.Damage - 1) / attacker.Damage;

    private static Cell? Nearest(Cell from, List<Cell> cells)
    {
        Cell? best = null;
        var bestDistance = int.MaxValue;
        foreach (var c in cells)
        {
            var d = Distance(from, c);
            if (d < bestDistance) { bestDistance = d; best = c; }
        }
        return best;
    }

    private static int Distance(Cell a, Cell b) =>
        Math.Max(Math.Abs(a.Column - b.Column), Math.Abs(a.Row - b.Row));
}

/// <summary>
/// Poids de l'évaluation des bots qui calculent (pvp.json « botWeights »). Ils ne s'écrivent pas à la main :
/// l'outil d'équilibrage les ENTRAÎNE par auto-jeu (un challenger aux poids mutés affronte le champion ; il le
/// remplace s'il gagne nettement), cf. tools/ChessArmy.Balance --ai-train.
/// </summary>
public sealed class PvpAiWeights
{
    public const int PieceBase = 0, PieceHp = 1, PieceDamage = 2, PieceRange = 3, PieceMove = 4, Tempo = 5, Hanging = 6,
        KingDanger = 7, Advance = 8, Mobility = 9, Support = 10, Guard = 11, PieceCost = 12;

    public static readonly string[] Names =
    {
        "pieceBase", "pieceHp", "pieceDamage", "pieceRange", "pieceMove", "tempo", "hanging", "kingDanger", "advance",
        "mobility", "support", "guard", "pieceCost",
    };

    /// <summary>Poids de départ (avant entraînement), à l'échelle de l'heuristique rapide. « pieceCost » (coût multi
    /// MESURÉ de la classe, traits compris) vaut 0 par défaut : un ancien jeu de poids garde son comportement.</summary>
    public static readonly double[] Defaults = { 2.0, 0.3, 0.4, 0.5, 0.3, 0.8, 0.3, 30.0, 0.15, 0.03, 0.2, 0.0, 0.0 };

    private readonly double[] _values;

    public PvpAiWeights() => _values = (double[])Defaults.Clone();

    public PvpAiWeights(double[] values)
    {
        _values = (double[])Defaults.Clone();
        Array.Copy(values, _values, Math.Min(values.Length, _values.Length));
    }

    public int Count => _values.Length;

    public double this[int index] => _values[index];

    public double[] ToArray() => (double[])_values.Clone();

    /// <summary>Copie avec un seul poids changé.</summary>
    public PvpAiWeights With(int index, double value)
    {
        var values = ToArray();
        values[index] = value;
        return new PvpAiWeights(values);
    }

    public static PvpAiWeights FromDictionary(IReadOnlyDictionary<string, double> values)
    {
        var array = (double[])Defaults.Clone();
        for (var i = 0; i < Names.Length; i++)
            if (values.TryGetValue(Names[i], out var v))
                array[i] = v;
        return new PvpAiWeights(array);
    }

    public Dictionary<string, double> ToDictionary()
    {
        var result = new Dictionary<string, double>();
        for (var i = 0; i < Names.Length; i++)
            result[Names[i]] = _values[i];
        return result;
    }
}

/// <summary>
/// Style de jeu d'un bot nommé (pvp.json « botStyles ») : il change sa FAÇON de jouer, pas sa force (chaque style
/// est mesuré contre le style neutre au même niveau). Facteurs sur l'heuristique rapide et l'évaluation :
/// avance vers l'adversaire, prudence (danger des cases, pièces exposées), agressivité (recherche des cibles),
/// garde (ses pièces restent près de son commandant).
/// </summary>
public sealed record PvpBotStyle
{
    public string Id { get; init; } = "neutre";
    public double Advance { get; init; } = 1;
    public double Caution { get; init; } = 1;
    public double Aggression { get; init; } = 1;
    public double Guard { get; init; }

    /// <summary>Noms des bots qui jouent ce style.</summary>
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();

    public static PvpBotStyle Neutral { get; } = new();

    /// <summary>Poids d'évaluation de ce style, à partir des poids entraînés.</summary>
    public PvpAiWeights Apply(PvpAiWeights weights)
    {
        if (this == Neutral)
            return weights;
        var values = weights.ToArray();
        values[PvpAiWeights.Guard] += Guard * 0.5;
        return new PvpAiWeights(values);
    }
}
