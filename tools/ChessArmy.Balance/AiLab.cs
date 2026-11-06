using System.Diagnostics;
using System.Text;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;
using ChessArmy.Core.Pvp;

/// <summary>Laboratoire de l'IA des bots multi : armées des bots, entraînement par auto-jeu, échelle des rangs.</summary>
static class AiLab
{
    // ── Armées des bots ──────────────────────────────────────────────────────────

    /// <summary>
    /// Compose <paramref name="count"/> armées DIFFÉRENTES pour les bots, chacune remplissant EXACTEMENT le budget
    /// d'armée (commandant compris) et celui d'équipement : tous les commandants à tour de rôle, 2 à 5 sortes de
    /// pièces tirées au hasard, posées avec du bon sens (corps à corps devant, tireurs derrière, depuis le centre).
    /// </summary>
    public static List<PvpBuild> ComposeBotArmies(int count, int seed)
    {
        var config = PvpConfig.Current;
        var rng = new Random(seed);
        var units = PvpCatalog.AllUnitClasses().Select(u => (u.Class.Asset, Cost: config.CostOf(u.Class), u.Class.AttackRange)).ToList();
        var equipment = PvpCatalog.AllEquipment().Select(e => (e.Id, Cost: config.CostOf(e))).ToList();
        var commanders = config.CommanderCost.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var result = new List<PvpBuild>();
        var seen = new HashSet<string>();
        for (var attempt = 0; result.Count < count && attempt < count * 400; attempt++)
        {
            var commander = commanders[result.Count % commanders.Count];
            var build = new PvpBuild { CommanderId = commander };
            var points = config.Budget - config.CommanderCostOf(commander);
            var cells = 2 * PvpBuild.Columns - 1 - (build.HasCompanion ? 1 : 0);
            var kinds = units.OrderBy(_ => rng.Next()).Take(rng.Next(2, 6)).ToList();
            if (Fill(kinds.Select(k => k.Cost).ToList(), points, cells, rng) is not { } picks)
                continue;
            var pieces = picks.Select(i => kinds[i]).ToList();
            var items = FillEquipment(equipment, config.EquipmentBudget, pieces.Count, rng);
            if (items == null)
                continue;
            Place(build, pieces.Select(p => (p.Asset, p.AttackRange)).ToList(), items, rng);
            var key = commander + "|" + string.Join(",", build.Slots.Select(s => s.ClassAsset + "+" + s.EquipmentId).OrderBy(s => s));
            if (PvpCatalog.Problem(build) != null || PvpCatalog.Cost(build) != config.Budget
                || PvpCatalog.EquipmentCost(build) != config.EquipmentBudget || !seen.Add(key))
                continue;
            result.Add(build);
        }
        return result;
    }

    /// <summary>
    /// Compose <paramref name="count"/> armées « débutant » DIFFÉRENTES (bots Bronze et Fer) : pions de BASE (la classe
    /// de base de chaque domaine) et AU PLUS UNE pièce d'une autre classe, tirée au hasard. Chacune remplit EXACTEMENT
    /// le budget d'armée, sans équipement ; tous les commandants à tour de rôle, posées comme les autres.
    /// </summary>
    public static List<PvpBuild> ComposeBeginnerArmies(int count, int seed)
    {
        var config = PvpConfig.Current;
        var rng = new Random(seed);
        var baseAssets = Domaines.All.Select(d => d.BaseClass.Asset).ToHashSet();
        var all = PvpCatalog.AllUnitClasses().Select(u => (u.Class.Asset, Cost: config.CostOf(u.Class), u.Class.AttackRange)).ToList();
        var bases = all.Where(u => baseAssets.Contains(u.Asset)).ToList();
        var others = all.Where(u => !baseAssets.Contains(u.Asset)).ToList();
        var commanders = config.CommanderCost.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var result = new List<PvpBuild>();
        var seen = new HashSet<string>();
        for (var attempt = 0; result.Count < count && attempt < count * 400; attempt++)
        {
            var commander = commanders[result.Count % commanders.Count];
            var build = new PvpBuild { CommanderId = commander };
            var points = config.Budget - config.CommanderCostOf(commander);
            var cells = 2 * PvpBuild.Columns - 1 - (build.HasCompanion ? 1 : 0);
            var pieces = new List<(string Asset, int Cost, int AttackRange)>();
            if (rng.Next(4) > 0)   // trois armées sur quatre ont leur pièce différente
            {
                var special = others[rng.Next(others.Count)];
                pieces.Add(special);
                points -= special.Cost;
                cells--;
            }
            var kinds = bases.OrderBy(_ => rng.Next()).Take(rng.Next(2, bases.Count + 1)).ToList();
            if (points <= 0 || Fill(kinds.Select(k => k.Cost).ToList(), points, cells, rng) is not { } picks)
                continue;
            pieces.AddRange(picks.Select(i => kinds[i]));
            Place(build, pieces.Select(p => (p.Asset, p.AttackRange)).ToList(), new List<string>(), rng);
            var key = commander + "|" + string.Join(",", build.Slots.Select(s => s.ClassAsset).OrderBy(s => s));
            if (PvpCatalog.Problem(build) != null || PvpCatalog.Cost(build) != config.Budget || !seen.Add(key))
                continue;
            result.Add(build);
        }
        return result;
    }

    /// <summary>Multiensemble des sortes (indices) qui fait EXACTEMENT <paramref name="points"/>, chacune au moins
    /// une fois, au plus <paramref name="cells"/> pièces. Null si introuvable.</summary>
    private static List<int>? Fill(List<int> costs, int points, int cells, Random rng)
    {
        for (var tryIndex = 0; tryIndex < 200; tryIndex++)
        {
            var picks = Enumerable.Range(0, costs.Count).ToList();
            var left = points - costs.Sum();
            if (left < 0)
                return null;
            while (left > 0 && picks.Count < cells)
            {
                var affordable = Enumerable.Range(0, costs.Count).Where(i => costs[i] <= left).ToList();
                if (affordable.Count == 0)
                    break;
                var i = affordable[rng.Next(affordable.Count)];
                picks.Add(i);
                left -= costs[i];
            }
            if (left == 0 && picks.Count <= cells)
                return picks;
        }
        return null;
    }

    /// <summary>Équipements qui font EXACTEMENT le budget d'équipement, au plus un par pièce.</summary>
    private static List<string>? FillEquipment(List<(string Id, int Cost)> equipment, int budget, int pieces, Random rng)
    {
        for (var tryIndex = 0; tryIndex < 200; tryIndex++)
        {
            var items = new List<string>();
            var left = budget;
            while (left > 0 && items.Count < pieces)
            {
                var affordable = equipment.Where(e => e.Cost <= left).ToList();
                if (affordable.Count == 0)
                    break;
                var e = affordable[rng.Next(affordable.Count)];
                items.Add(e.Id);
                left -= e.Cost;
            }
            if (left == 0)
                return items;
        }
        return null;
    }

    /// <summary>Corps à corps sur la ligne de devant, tireurs (portée 2+) derrière, chacun depuis le centre ; le
    /// trop-plein passe sur l'autre ligne. Les équipements vont à des pièces tirées au hasard.</summary>
    private static void Place(PvpBuild build, List<(string Asset, int Range)> pieces, List<string> items, Random rng)
    {
        int[] frontOrder = { 3, 4, 2, 5, 1, 6, 0, 7 };
        int[] backOrder = { 3, 5, 2, 6, 1, 7, 0 };
        var front = new Queue<int>(frontOrder);
        var back = new Queue<int>(backOrder.Where(c => !build.IsReserved(c, PvpBuild.BackLine)));
        var ordered = pieces.OrderBy(p => p.Range >= 2 ? 1 : 0).ThenBy(_ => rng.Next()).ToList();
        var shuffledItems = items.OrderBy(_ => rng.Next()).ToList();
        var equipAt = Enumerable.Range(0, ordered.Count).OrderBy(_ => rng.Next()).Take(shuffledItems.Count).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (asset, range) = ordered[i];
            var wantBack = range >= 2;
            var queue = wantBack ? (back.Count > 0 ? back : front) : (front.Count > 0 ? front : back);
            var line = queue == back ? PvpBuild.BackLine : PvpBuild.FrontLine;
            var k = equipAt.IndexOf(i);
            build.Set(queue.Dequeue(), line, asset, k >= 0 ? shuffledItems[k] : null);
        }
    }

    // ── Parties bot contre bot ───────────────────────────────────────────────────

    /// <summary>Temps unitaire des briques de la recherche sur des positions de milieu de partie, puis d'un coup de chaque niveau qui calcule.</summary>
    public static void Bench(int plies, int seed)
    {
        var config = PvpConfig.Current;
        var armies = config.BotArmies;
        var fer = config.Bots["fer-1"];
        double fork = 0, score = 0, eval = 0, startMs = 0; long n = 0;
        var searchMs = new Dictionary<string, (double Ms, int Count)>();
        for (var g = 0; g < 8; g++)
        {
            var rng = new Random(seed + g);
            var forker = new PvpForker(seed + g, true, null, null, armies[g % armies.Count].WithoutEquipment(), armies[(g * 7 + 3) % armies.Count].WithoutEquipment());
            var match = forker.Start();
            var log = new List<BattleAction>();
            for (var p = 0; p < plies && !match.IsOver; p++)
            {
                var a = PvpBotAi.Choose(match, match.CurrentTurn, fer, config.BotWeights, rng) ?? BattleAction.Pass;
                if (!PvpForker.Step(match, a)) { a = BattleAction.Pass; PvpForker.Step(match, a); }
                log.Add(a);
            }
            if (match.IsOver) continue;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 200; i++) forker.Fork(log, log.Count);
            fork += sw.Elapsed.TotalMilliseconds / 200;
            sw.Restart();
            for (var i = 0; i < 200; i++) forker.Start();
            startMs += sw.Elapsed.TotalMilliseconds / 200;
            sw.Restart();
            for (var i = 0; i < 200; i++) PvpBotAi.ScoreActions(match, match.CurrentTurn, 1, rng);
            score += sw.Elapsed.TotalMilliseconds / 200;
            sw.Restart();
            for (var i = 0; i < 200; i++) PvpBotAi.Evaluate(match, match.CurrentTurn, config.BotWeights);
            eval += sw.Elapsed.TotalMilliseconds / 200;
            n++;
            foreach (var key in new[] { "argent-1", "or-1", "master", "master+1", "master+2" })
            {
                sw.Restart();
                var def = key.Contains('+') ? config.Bots["master"] with { Depth = config.Bots["master"].Depth + (key[^1] - '0') } : config.Bots[key];
                PvpBotAi.Search(forker, log, match.CurrentTurn, def, config.BotWeights, new Random(1));
                var t = searchMs.GetValueOrDefault(key);
                searchMs[key] = (t.Ms + sw.Elapsed.TotalMilliseconds, t.Count + 1);
            }
        }
        Console.WriteLine($"{n} positions après {plies} coups : copie {fork / n * 1000:0} µs (dont partie neuve {startMs / n * 1000:0} µs), tri des coups {score / n * 1000:0} µs, évaluation {eval / n * 1000:0} µs");
        foreach (var (key, t) in searchMs)
            Console.WriteLine($"  {key,-9} {t.Ms / t.Count,8:0} ms par coup (tous les cœurs)");
        Console.WriteLine($"  nœuds : {PvpBotAi.NodeCount}");
    }

    /// <summary>Un camp d'une partie simulée : son niveau d'intelligence et ses poids d'évaluation.</summary>
    public sealed record Brain(string Name, PvpBotDef Def, PvpAiWeights Weights);

    /// <summary>Statistiques de temps de réflexion (ms par coup, par niveau).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double Ms, long Moves)> Timing = new();

    /// <summary>
    /// Une partie complète entre deux bots, armée <paramref name="a"/> (camp Player) contre <paramref name="b"/>,
    /// jouée exactement comme en jeu (copies de la partie pour ceux qui calculent). Score de A : 1 victoire,
    /// 0 défaite, 0,5 nulle ; au-delà de <paramref name="maxPlies"/>, sa part du matériel.
    /// </summary>
    public static double Play(PvpBuild a, PvpBuild b, Brain brainA, Brain brainB, int seed, bool aStarts, int maxPlies = 240)
    {
        var draw = new Random(seed * 31 + 3);   // équipements tirés à chaque partie (Argent+), sans pour Bronze et Fer
        a = brainA.Def.ArmyFor(a, draw);
        b = brainB.Def.ArmyFor(b, draw);
        var forker = new PvpForker(seed, aStarts, null, null, a, b);
        var match = forker.Start();
        var log = new List<BattleAction>();
        var rngA = new Random(seed * 31 + 1);
        var rngB = new Random(seed * 31 + 2);
        var clock = new Stopwatch();
        for (var ply = 0; ply < maxPlies && !match.IsOver; ply++)
        {
            var actor = match.CurrentTurn;
            var brain = actor == Faction.Player ? brainA : brainB;
            var rng = actor == Faction.Player ? rngA : rngB;
            clock.Restart();
            var action = (brain.Def.Depth > 0
                ? PvpBotAi.Search(forker, log, actor, brain.Def, brain.Weights, rng)
                : PvpBotAi.Choose(match, actor, brain.Def, brain.Weights, rng)) ?? BattleAction.Pass;
            Timing.AddOrUpdate(brain.Name, (clock.Elapsed.TotalMilliseconds, 1),
                (_, t) => (t.Ms + clock.Elapsed.TotalMilliseconds, t.Moves + 1));
            if (!PvpForker.Step(match, action))
            {
                action = BattleAction.Pass;
                PvpForker.Step(match, action);
            }
            log.Add(action);
            if (ply % 8 == 7 && !match.IsOver && match.IsDeadPosition())
                match.EndInDraw();
        }
        if (match.IsDraw) return 0.5;
        if (match.Winner == Faction.Player) return 1;
        if (match.Winner == Faction.Enemy) return 0;
        var mine = PvpBotAi.Material(match, Faction.Player);
        var theirs = PvpBotAi.Material(match, Faction.Enemy);
        return mine + theirs <= 0 ? 0.5 : mine / (mine + theirs);
    }

    /// <summary>
    /// Score moyen de <paramref name="x"/> contre <paramref name="y"/> sur <paramref name="games"/> parties : les
    /// armées sont tirées dans la réserve des bots et chaque tirage est joué QUATRE fois (chaque cerveau avec
    /// chaque armée, chacun commençant), pour que ni l'armée ni le premier coup ne faussent la mesure.
    /// </summary>
    public static double Duel(Brain x, Brain y, IReadOnlyList<PvpBuild> armies, int games, int seed)
    {
        PvpBotAi.Parallel = false;   // les parties tournent déjà en parallèle : un cœur par partie
        // Chaque niveau joue SES armées (celles de son rang, sinon la réserve donnée). Réserves différentes (Fer contre
        // Argent) : l'armée fait partie de la force mesurée, chacun garde la sienne au lieu de les échanger.
        var poolX = x.Def.Armies.Count > 0 ? x.Def.Armies : armies;
        var poolY = y.Def.Armies.Count > 0 ? y.Def.Armies : armies;
        var samePool = PoolKey(poolX) == PoolKey(poolY);
        var rounds = Math.Max(1, games / 4);
        var scores = new double[rounds * 4];
        Parallel.For(0, rounds * 4, i =>
        {
            var round = i / 4;
            var variant = i % 4;
            var draw = new Random(seed * 7919 + round * 104729);
            var armyP = poolX[draw.Next(poolX.Count)];
            var armyQ = poolY[draw.Next(poolY.Count)];
            var gameSeed = draw.Next();
            if (!samePool && variant >= 2)   // pas d'échange possible : un second tirage plutôt que la même partie
            {
                armyP = poolX[draw.Next(poolX.Count)];
                armyQ = poolY[draw.Next(poolY.Count)];
                gameSeed = draw.Next();
            }
            var xHasP = variant < 2 || !samePool;
            var starts = variant % 2 == 0;
            var (a, b) = xHasP ? (armyP, armyQ) : (armyQ, armyP);
            scores[i] = Play(a, b, x, y, gameSeed, starts);   // x joue toujours le camp Player
        });
        return scores.Average();
    }

    private static string PoolKey(IReadOnlyList<PvpBuild> pool) => string.Join(";", pool.Select(a => a.CommanderId + "|"
        + string.Join(",", a.Slots.Select(s => $"{s.Column}{s.Line}{s.ClassAsset}"))));

    /// <summary>Échelle des rangs : chaque rang contre le précédent (et le plus haut contre le plus bas).</summary>
    public static void Ladder(int games, int seed, string from = "")
    {
        var config = PvpConfig.Current;
        var ranks = PvpRanks.BotKeys().Where(config.Bots.ContainsKey).ToList();   // un niveau par division
        if (from.Length > 0 && ranks.IndexOf(from) > 0)
            ranks = ranks.Skip(ranks.IndexOf(from)).ToList();   // --from : seulement le haut de l'échelle
        var brains = ranks.Select(r => new Brain(r, config.Bots[r], config.BotWeights)).ToList();
        var clock = Stopwatch.StartNew();
        Console.WriteLine($"Échelle des bots : {games} parties par duel, chaque niveau avec ses armées.");
        for (var i = 1; i < brains.Count; i++)
        {
            var score = Duel(brains[i], brains[i - 1], config.BotArmies, games, seed + i);
            Console.WriteLine($"  {brains[i].Name,-8} contre {brains[i - 1].Name,-8} : {score * 100,5:0.0} %   ({clock.Elapsed:mm\\:ss})");
        }
        if (brains.Count > 2)
        {
            var top = Duel(brains[^1], brains[0], config.BotArmies, games, seed + 99);
            Console.WriteLine($"  {brains[^1].Name,-8} contre {brains[0].Name,-8} : {top * 100,5:0.0} %");
        }
        Console.WriteLine("Temps de réflexion moyen :");
        foreach (var b in brains)
            if (Timing.TryGetValue(b.Name, out var t) && t.Moves > 0)
                Console.WriteLine($"  {b.Name,-8} {t.Ms / t.Moves,7:0.0} ms par coup");
    }

    // ── Entraînement par auto-jeu ────────────────────────────────────────────────

    /// <summary>
    /// Entraîne les poids d'évaluation (« botWeights ») par AUTO-JEU : à chaque génération, un challenger aux poids
    /// mutés (bruit log-normal) affronte le champion avec le même cerveau ; il ne le remplace que s'il gagne
    /// nettement, et sur une seconde série de parties neuves (pour ne pas garder un coup de chance).
    /// Renvoie le champion final.
    /// </summary>
    public static PvpAiWeights Train(PvpBotDef level, int generations, int games, double sigma, int seed)
    {
        var armies = PvpConfig.Current.BotArmies;
        var champion = PvpConfig.Current.BotWeights;
        var rng = new Random(seed);
        var clock = Stopwatch.StartNew();
        Console.WriteLine($"Entraînement : {generations} générations, {games} parties par duel, profondeur {level.Depth}.");
        Print("départ", champion);
        for (var gen = 1; gen <= generations; gen++)
        {
            var values = champion.ToArray();
            // Une mutation touche quelques poids seulement : on sait mieux lequel a fait la différence.
            var touched = 0;
            for (var i = 0; i < values.Length; i++)
                if (rng.NextDouble() < 0.4)
                {
                    values[i] *= Math.Exp(sigma * Gaussian(rng));
                    touched++;
                }
            if (touched == 0)
            {
                var i = rng.Next(values.Length);
                values[i] *= Math.Exp(sigma * Gaussian(rng));
            }
            var challenger = new PvpAiWeights(values);
            var first = Duel(new Brain("challenger", level, challenger), new Brain("champion", level, champion), armies,
                games, seed + gen * 1000);
            var verdict = $"gen {gen,3} : {first * 100,5:0.0} %";
            if (first > 0.56)
            {
                var second = Duel(new Brain("challenger", level, challenger), new Brain("champion", level, champion), armies,
                    games, seed + gen * 1000 + 500);
                verdict += $" puis {second * 100,5:0.0} %";
                if ((first + second) / 2 > 0.555)
                {
                    champion = challenger;
                    verdict += "  -> NOUVEAU CHAMPION";
                }
            }
            Console.WriteLine($"{verdict}   ({clock.Elapsed:mm\\:ss})");
            if (verdict.EndsWith("CHAMPION"))
                Print("champion", champion);
        }
        return champion;
    }

    /// <summary>
    /// Entraînement SPSA (bien plus efficace que le challenger unique quand chaque mesure est bruitée) : à chaque
    /// itération, TOUS les poids sont poussés au hasard de ±δ (en échelle logarithmique) ; le cerveau « plus »
    /// affronte le cerveau « moins », et on avance d'un pas vers le gagnant, proportionnel à son avance. Le pas
    /// décroît au fil des itérations. À la fin, le résultat doit battre les poids de départ sur des parties neuves,
    /// sinon on garde les poids de départ.
    /// </summary>
    public static PvpAiWeights TrainSpsa(PvpBotDef level, int iterations, int games, double delta, double step, int seed)
    {
        var armies = PvpConfig.Current.BotArmies;
        var start = PvpConfig.Current.BotWeights;
        var theta = start.ToArray().Select(v => Math.Log(v)).ToArray();
        var rng = new Random(seed);
        var clock = Stopwatch.StartNew();
        Console.WriteLine($"SPSA : {iterations} itérations, {games} parties chacune, profondeur {level.Depth}, δ {delta}, pas {step}.");
        Print("départ", start);
        for (var it = 1; it <= iterations; it++)
        {
            var a = step / Math.Pow(1 + it / 10.0, 0.602) * Math.Pow(1 + 1 / 10.0, 0.602);
            var signs = theta.Select(_ => rng.Next(2) == 0 ? -1.0 : 1.0).ToArray();
            var plus = new PvpAiWeights(theta.Select((t, i) => Math.Exp(t + delta * signs[i])).ToArray());
            var minus = new PvpAiWeights(theta.Select((t, i) => Math.Exp(t - delta * signs[i])).ToArray());
            var score = Duel(new Brain("plus", level, plus), new Brain("moins", level, minus), armies, games, seed + it * 977);
            var advantage = (score - 0.5) * 2;   // -1..1
            for (var i = 0; i < theta.Length; i++)
                theta[i] += a * advantage / (2 * delta) * signs[i];   // estimation du gradient
            Console.WriteLine($"it {it,3} : plus {score * 100,5:0.0} %   ({clock.Elapsed:mm\\:ss})");
            if (it % 10 == 0)
                Print($"poids it {it}", new PvpAiWeights(theta.Select(t => Math.Exp(t)).ToArray()));
        }
        var trained = new PvpAiWeights(theta.Select(t => Math.Exp(t)).ToArray());
        Print("final", trained);
        var check = Duel(new Brain("entraîné", level, trained), new Brain("départ", level, start), armies, games * 3, seed + 424242);
        Console.WriteLine($"Vérification, entraîné contre départ : {check * 100:0.0} % sur {games * 3} parties neuves.");
        if (check > 0.53)
            return trained;
        Console.WriteLine("Pas d'amélioration nette : on garde les poids de départ.");
        return start;
    }

    private static void Print(string label, PvpAiWeights w) =>
        Console.WriteLine($"  {label} : " + string.Join("  ",
            PvpAiWeights.Names.Select((n, i) => $"{n} {w[i]:0.###}")));

    private static double Gaussian(Random rng) =>
        Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    /// <summary>Écrit « botWeights » dans pvp.json, juste avant les bots.</summary>
    public static string WriteWeights(string text, PvpAiWeights weights)
    {
        var body = "{ " + string.Join(", ", PvpAiWeights.Names.Select((n, i) =>
            $"\"{n}\": {weights[i].ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}")) + " }";
        var at = text.IndexOf("\"botWeights\"", StringComparison.Ordinal);
        if (at < 0)
        {
            var anchor = text.IndexOf("  // Bots de secours", StringComparison.Ordinal);
            return text[..anchor]
                   + "  // Poids de l'évaluation des bots qui calculent, ENTRAÎNÉS par auto-jeu (outil : --ai-train).\n"
                   + "  \"botWeights\": " + body + ",\n\n" + text[anchor..];
        }
        var open = text.IndexOf('{', at);
        var close = text.IndexOf('}', open);
        return text[..open] + body + text[(close + 1)..];
    }

    /// <summary>Écrit « botArmies » (ou une autre réserve, <paramref name="key"/>) dans pvp.json (une armée par
    /// entrée), juste avant les bots.</summary>
    public static string WriteBotArmies(string text, List<PvpBuild> armies, string key = "botArmies", string? comment = null)
    {
        static string Cell(PvpBuild b, int column, int line) =>
            PvpBuild.IsKingCell(column, line) ? "*"
            : b.SlotAt(column, line) is { } s ? s.ClassAsset + (s.EquipmentId != null ? "+" + s.EquipmentId : "") : "";
        static string Arr(PvpBuild b, int line) =>
            "[ " + string.Join(", ", Enumerable.Range(0, PvpBuild.Columns).Select(c => $"\"{Cell(b, c, line)}\"")) + " ]";
        var sb = new StringBuilder("[\n");
        for (var i = 0; i < armies.Count; i++)
        {
            var a = armies[i];
            sb.Append("    { \"commander\": \"").Append(a.CommanderId).Append("\",\n      \"back\":  ")
              .Append(Arr(a, PvpBuild.BackLine)).Append(",\n      \"front\": ").Append(Arr(a, PvpBuild.FrontLine))
              .Append(" }").Append(i + 1 < armies.Count ? ",\n" : "\n");
        }
        sb.Append("  ]");
        var at = text.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (at < 0)
        {
            var anchor = text.IndexOf("  // Bots de secours", StringComparison.Ordinal);
            return text[..anchor]
                   + (comment ?? "  // Armées des bots, composées par l'outil (--bot-armies). Chacune remplit exactement le budget d'armée.\n")
                   + $"  \"{key}\": " + sb + ",\n\n" + text[anchor..];
        }
        var open = text.IndexOf('[', at);
        var depth = 0;
        var close = open;
        for (; close < text.Length; close++)
        {
            if (text[close] == '[') depth++;
            else if (text[close] == ']' && --depth == 0) break;
        }
        return text[..open] + sb + text[(close + 1)..];
    }
}
