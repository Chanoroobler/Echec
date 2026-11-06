using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;

namespace ChessArmy.Core.Pvp;

/// <summary>
/// Tests d'ÉQUILIBRAGE du multi : la force d'une pièce (ou d'un équipement) est MESURÉE en faisant s'affronter des
/// bots tactiques, jamais déduite de son tier. Méthode : <c>count</c> exemplaires du candidat contre n pièces tirées
/// de l'ARMÉE DE DÉPART (mélange de Soldats, Lanciers, Cavaliers, Mages : une seule référence fausserait tout, un
/// Duelliste écrase les Soldats mais pas les tireurs), sur un plateau 8×8 dégagé, sans commandant (victoire à
/// l'élimination) ; on cherche le n où le candidat gagne une fois sur deux. Sa valeur = n / count, en pièces de
/// départ. Un équipement vaut la valeur d'un Soldat équipé moins celle d'un Soldat nu.
/// </summary>
public static class PvpBalance
{
    /// <summary>Fabrique d'une pièce pour un camp (chaque partie a ses propres instances).</summary>
    public delegate Unit Piece(Faction faction);

    private const int MaxPieces = 16;   // deux lignes de 8

    /// <summary>
    /// Une partie bot contre bot : <paramref name="a"/> (camp Player, en bas) contre <paramref name="b"/> (en haut),
    /// placés au hasard sur leurs deux lignes. Score de A : 1 victoire, 0 défaite ; au-delà de
    /// <paramref name="maxPlies"/> coups, sa part du matériel restant (une partie qui s'éternise n'est pas une nulle sèche).
    /// </summary>
    public static double PlayGame(IReadOnlyList<Piece> a, IReadOnlyList<Piece> b, int seed, bool aStarts, int maxPlies = 160,
        List<string>? trace = null)
    {
        var rng = new Random(seed);
        var match = new Match(PvpSetup.Size, PvpSetup.Size, rng: new Random(seed),
            firstTurn: aStarts ? Faction.Player : Faction.Enemy, pvpRules: true);
        Place(match, a, Faction.Player, rng);
        Place(match, b, Faction.Enemy, rng);

        for (var ply = 0; ply < maxPlies && !match.IsOver; ply++)
        {
            var actor = match.CurrentTurn;
            var action = PvpBotAi.Choose(match, actor, 1.0, rng) ?? BattleAction.Pass;
            var kind = match.Apply(action);
            trace?.Add($"{ply,3} {actor,-6} {action} -> {kind}");
            if (kind == MoveKind.Invalid)
                match.Apply(BattleAction.Pass);
            if (!match.IsOver && match.CurrentTurn != actor)
                match.ApplySeismes(match.CurrentTurn);
            PvpSetup.ResolvePending(match);
        }

        if (match.Winner == Faction.Player) return 1;
        if (match.Winner == Faction.Enemy) return 0;
        var mine = PvpBotAi.Material(match, Faction.Player);
        var theirs = PvpBotAi.Material(match, Faction.Enemy);
        return mine + theirs <= 0 ? 0.5 : mine / (mine + theirs);
    }

    // ── Vraies batailles (armées complètes) ──────────────────────────────────────

    /// <summary>
    /// Une VRAIE bataille bot contre bot entre deux armées complètes (commandant compris : sa chute fait perdre),
    /// avec les règles du multi. Score de A : 1 victoire, 0 défaite, 0,5 nulle ; au-delà de
    /// <paramref name="maxPlies"/> coups, sa part du matériel restant.
    /// </summary>
    public static double PlayBattle(PvpBuild a, PvpBuild b, int seed, bool aStarts, int maxPlies = 300,
        List<string>? trace = null)
    {
        var rng = new Random(seed);
        var match = PvpSetup.CreateMatch(mirrored: false, aStarts, seed);
        PvpSetup.PlaceArmy(match, a, Faction.Player);
        PvpSetup.PlaceArmy(match, b, Faction.Enemy);
        for (var ply = 0; ply < maxPlies && !match.IsOver; ply++)
        {
            var actor = match.CurrentTurn;
            var action = PvpBotAi.Choose(match, actor, 1.0, rng) ?? BattleAction.Pass;
            var kind = match.Apply(action);
            trace?.Add($"{ply,3} {actor,-6} {action.Kind,-7} {action.From.Column},{action.From.Row} > {action.Target.Column},{action.Target.Row}  {kind}");
            if (kind == MoveKind.Invalid)
                match.Apply(BattleAction.Pass);
            if (!match.IsOver && match.CurrentTurn != actor)
                match.ApplySeismes(match.CurrentTurn);
            PvpSetup.ResolvePending(match);
        }
        trace?.Add($"Fin : vainqueur {match.Winner}, nulle {match.IsDraw}");
        if (match.IsDraw) return 0.5;
        if (match.Winner == Faction.Player) return 1;
        if (match.Winner == Faction.Enemy) return 0;
        var mine = PvpBotAi.Material(match, Faction.Player);
        var theirs = PvpBotAi.Material(match, Faction.Enemy);
        return mine + theirs <= 0 ? 0.5 : mine / (mine + theirs);
    }

    /// <summary>
    /// Armée tirée au hasard qui DÉPENSE tout le budget (à une unité près) : commandant au hasard, pièces
    /// abordables tirées au hasard, posées au hasard sur les deux lignes, chacune équipée avec la probabilité
    /// <paramref name="equipChance"/> si le budget le permet.
    /// </summary>
    public static PvpBuild RandomArmy(Random rng, int budget, IReadOnlyList<(string Id, int Cost)> commanders,
        IReadOnlyList<(string Asset, int Cost)> units, IReadOnlyList<(string Id, int Cost)> equipment, double equipChance)
    {
        var commander = commanders[rng.Next(commanders.Count)];   // il se paie sur le budget, comme le reste
        var build = new PvpBuild { CommanderId = commander.Id };
        var cells = Enumerable.Range(0, 2 * PvpBuild.Columns)
            .Select(i => (Column: i % PvpBuild.Columns, Line: i / PvpBuild.Columns))
            .Where(c => !build.IsReserved(c.Column, c.Line))
            .OrderBy(_ => rng.Next()).ToList();
        var left = Math.Max(0, budget - commander.Cost);
        foreach (var (column, line) in cells)
        {
            var affordable = units.Where(u => u.Cost <= left).ToList();
            if (affordable.Count == 0)
                break;
            var unit = affordable[rng.Next(affordable.Count)];
            left -= unit.Cost;
            string? equip = null;
            if (rng.NextDouble() < equipChance)
            {
                var items = equipment.Where(e => e.Cost <= left).ToList();
                if (items.Count > 0)
                {
                    var item = items[rng.Next(items.Count)];
                    equip = item.Id;
                    left -= item.Cost;
                }
            }
            build.Set(column, line, unit.Asset, equip);
        }
        return build;
    }

    /// <summary>
    /// Régression logistique (descente de gradient, pénalité L2) : trouve les poids w tels que
    /// P(A gagne) = sigmoïde(w · x) colle aux résultats. Chaque échantillon x = différence de composition
    /// (A − B) ; y = score de A (0..1, nulles et parts de matériel comprises).
    /// </summary>
    public static double[] FitLogistic(IReadOnlyList<(double[] X, double Y)> samples, int features, double l2 = 1e-3,
        int iterations = 3000, double rate = 0.5)
    {
        var w = new double[features];
        var grad = new double[features];
        for (var it = 0; it < iterations; it++)
        {
            Array.Clear(grad);
            foreach (var (x, y) in samples)
            {
                var z = 0.0;
                for (var i = 0; i < features; i++) z += w[i] * x[i];
                var err = 1.0 / (1.0 + Math.Exp(-z)) - y;
                for (var i = 0; i < features; i++) grad[i] += err * x[i];
            }
            for (var i = 0; i < features; i++)
                w[i] -= rate * (grad[i] / samples.Count + l2 * w[i]);
        }
        return w;
    }

    private static void Place(Match match, IReadOnlyList<Piece> army, Faction faction, Random rng)
    {
        var rows = faction == Faction.Player ? new[] { PvpSetup.Size - 1, PvpSetup.Size - 2 } : new[] { 0, 1 };
        var cells = rows.SelectMany(r => Enumerable.Range(0, PvpSetup.Size).Select(c => new Cell(c, r)))
            .OrderBy(_ => rng.Next()).ToList();
        for (var i = 0; i < army.Count && i < cells.Count; i++)
            match.Place(cells[i], army[i](faction));
    }

    /// <summary>Score moyen de <paramref name="count"/> candidats contre <paramref name="soldiers"/> références
    /// (moitié des parties en commençant, moitié en second, pour annuler l'avantage du premier coup).</summary>
    public static double WinRate(Piece candidate, int count, IReadOnlyList<Piece> pool, int opponents, int games, int seed)
    {
        var a = Enumerable.Repeat(candidate, count).ToList();
        var scores = new double[games];
        Parallel.For(0, games, g =>
        {
            // Adversaires tirés de la réserve de référence (l'armée de départ), mélangée à chaque partie.
            var gameSeed = seed * 7919 + g * 104729 + opponents;
            var draw = new Random(gameSeed ^ 0x5bd1e995);
            var shuffled = pool.OrderBy(_ => draw.Next()).ToList();
            var b = Enumerable.Range(0, opponents).Select(i => shuffled[i % shuffled.Count]).ToList();
            scores[g] = PlayGame(a, b, gameSeed, aStarts: g % 2 == 0);
        });
        return scores.Average();
    }

    /// <summary>
    /// Valeur du candidat en Soldats : recherche dichotomique du nombre de références qui le tient en échec
    /// (score 0,5), puis interpolation linéaire entre les deux effectifs qui encadrent ce point.
    /// </summary>
    public static double MeasureValue(Piece candidate, IReadOnlyList<Piece> pool, int count = 2, int games = 40, int seed = 1)
    {
        var measured = new Dictionary<int, double> { [0] = 1.0 };
        double Rate(int n) => measured.TryGetValue(n, out var w) ? w : measured[n] = WinRate(candidate, count, pool, n, games, seed);

        int lo = 0, hi = MaxPieces;
        if (Rate(hi) > 0.5)
            return (double)MaxPieces / count;   // plus fort que toute une armée de référence : valeur plafonnée
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (Rate(mid) > 0.5) lo = mid;
            else hi = mid;
        }
        var wLo = Rate(lo);
        var wHi = Rate(hi);
        var crossing = wLo - wHi < 1e-9 ? hi : lo + (wLo - 0.5) / (wLo - wHi);
        return crossing / count;
    }

    /// <summary>Pièce d'une classe donnée (avec un équipement éventuel).</summary>
    public static Piece Of(Domaine domaine, UnitClass cls, Equip.Equipment? equipment = null) =>
        faction => new Unit(domaine, faction, cls, equipment == null ? null : new[] { equipment });
}
