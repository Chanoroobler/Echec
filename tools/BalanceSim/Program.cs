using System;
using System.Collections.Generic;
using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Campaign;
using Echec.Core.Map;

namespace Echec.Tools.BalanceSim;

// ─────────────────────────────────────────────────────────────────────────────
//  Harnais d'equilibrage — auto-joue des combats en rejouant le VRAI code du jeu
//  (EnemyAi, Match, Domaines/Bosses/CampaignPlan) et mesure ce qui fait mal :
//    • taux de victoire du bot de reference
//    • unites perdues par combat
//    • DISTRIBUTION DES PICS = nb d'unites tuees par UNE seule action ennemie
//    • one-shots depuis PV pleins
//
//  IMPORTANT sur la lecture des chiffres :
//    - Le TAUX DE VICTOIRE est relatif au bot de reference codé ici (pas un
//      humain). Il sert d'ETALON pour COMPARER des reglages (baseline vs preset
//      facile), pas de verite absolue sur la difficulte ressentie.
//    - Les PICS et les ONE-SHOTS sont, eux, intrinseques au design (ils dependent
//      des degats/portees/traits ennemis, pas de l'habileté du joueur) : c'est la
//      metrique la plus fiable pour juger la frustration.
//
//  Usage :
//    dotnet run --project tools/BalanceSim -- [options]
//  Options :
//    --runs N        combats simules par mission (defaut 400)
//    --enemy-dmg F   multiplicateur de degats ennemis (defaut 1.0 ; preset facile ~0.85)
//    --blunder F     proba qu'un tour ennemi "rate" (joue un simple deplacement) (defaut 0)
//    --player N      taille de l'armee joueur deployee (defaut = Deployments du commandant)
//    --seed N        graine de base (defaut 12345)
//    --preset-easy   raccourci : --enemy-dmg 0.85 --blunder 0.25
//
//  Exemples :
//    dotnet run --project tools/BalanceSim                      # baseline (le jeu tel quel)
//    dotnet run --project tools/BalanceSim -- --preset-easy      # apercu du mode facile
//    dotnet run --project tools/BalanceSim -- --enemy-dmg 0.8 --blunder 0.3
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    private static readonly Domaine[] AllDomaines = { Domaine.Dame, Domaine.Tour, Domaine.Cavalier, Domaine.Fou };

    private sealed class Config
    {
        public int Runs = 400;
        public double EnemyDamage = 1.0;
        public double Blunder = 0.0;
        public int? PlayerArmy = null;
        public int Seed = 12345;
    }

    private static int Main(string[] args)
    {
        var cfg = ParseArgs(args);
        if (cfg == null) return 1;

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
        Console.WriteLine("  HARNAIS D'EQUILIBRAGE — Echec");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Reglages : runs/mission={cfg.Runs}  degats ennemis x{cfg.EnemyDamage:0.00}  "
                          + $"maladresse IA={cfg.Blunder:0%}  graine={cfg.Seed}");
        var deploy = cfg.PlayerArmy ?? Commandes.Commander.Deployments;
        Console.WriteLine($"  Armee joueur deployee : {deploy} unites (commandant inclus)");
        Console.WriteLine();

        // Les missions-cles a mesurer (phase, mission). On couvre debut/milieu/fin + boss.
        var checkpoints = new (int Phase, int Mission, string Label)[]
        {
            (1, 2, "Ph1 m2  escarmouche (debut)"),
            (1, 5, "Ph1 m5  escarmouche"),
            (1, 6, "Ph1 m6  BOSS"),
            (2, 3, "Ph2 m3  speciale"),
            (2, 5, "Ph2 m5  escarmouche"),
            (2, 6, "Ph2 m6  BOSS"),
            (3, 5, "Ph3 m5  escarmouche"),
            (3, 6, "Ph3 m6  BOSS FINAL"),
        };

        Console.WriteLine($"  {"Mission",-30}{"Victoires",10}{"Pertes/combat",15}{"Tours",8}{"Pic>=2",9}{"1-shot/combat",14}");
        Console.WriteLine("  " + new string('─', 84));

        var overall = new Aggregate();
        foreach (var (phase, mission, label) in checkpoints)
        {
            var agg = new Aggregate();
            for (var i = 0; i < cfg.Runs; i++)
            {
                var rng = new Random(unchecked(cfg.Seed + phase * 100003 + mission * 1009 + i));
                SimulateCombat(phase, mission, deploy, cfg, rng, agg);
            }
            overall.Absorb(agg);

            Console.WriteLine($"  {label,-30}{agg.WinRate,9:0%}{agg.AvgLosses,15:0.00}{agg.AvgTurns,8:0}"
                              + $"{agg.SpikeRate,8:0%}{agg.OneShotsPerCombat,14:0.00}");
        }

        Console.WriteLine("  " + new string('─', 84));
        Console.WriteLine($"  {"GLOBAL",-30}{overall.WinRate,9:0%}{overall.AvgLosses,15:0.00}{overall.AvgTurns,8:0}"
                          + $"{overall.SpikeRate,8:0%}{overall.OneShotsPerCombat,14:0.00}");
        Console.WriteLine();

        // Distribution des pics (nb d'unites tuees par UNE action ennemie).
        Console.WriteLine("  DISTRIBUTION DES PICS — nb d'unites joueur tuees par UNE SEULE action ennemie");
        var totalActions = overall.KillsPerAction.Values.Sum();
        foreach (var k in overall.KillsPerAction.Keys.OrderBy(x => x))
        {
            if (k == 0) continue; // on ne montre que les actions qui tuent
            var n = overall.KillsPerAction[k];
            var pct = totalActions > 0 ? 100.0 * n / totalActions : 0;
            var bar = new string('█', (int)Math.Round(pct * 1.2));
            Console.WriteLine($"    {k} unite(s) d'un coup : {pct,5:0.0}% des actions ennemies  {bar}");
        }
        Console.WriteLine();

        // Ventilation MESUREE par motif d'attaque : repond a "quel poids donner au deplacement".
        // Un motif qui pese peu ici est facile a eviter en pratique (la geometrie est deja resolue par le moteur).
        Console.WriteLine("  D'OU VIENNENT LES DEGATS — par MOTIF d'attaque de l'ennemi (geometrie reelle)");
        var totalKills = overall.KillsByPattern.Values.Sum();
        var totalOneShots = overall.OneShotsByPattern.Values.Sum();
        var patternLabel = new Dictionary<Domaine, string>
        {
            [Domaine.Dame] = "Dame (8 dir.)", [Domaine.Tour] = "Tour (orthog.)",
            [Domaine.Fou] = "Fou (diagonale)", [Domaine.Cavalier] = "Cavalier (saut)",
        };
        foreach (var d in AllDomaines)
        {
            var kills = overall.KillsByPattern.GetValueOrDefault(d);
            var os = overall.OneShotsByPattern.GetValueOrDefault(d);
            var kp = totalKills > 0 ? 100.0 * kills / totalKills : 0;
            var op = totalOneShots > 0 ? 100.0 * os / totalOneShots : 0;
            Console.WriteLine($"    {patternLabel[d],-18} {kp,5:0.0}% des kills   {op,5:0.0}% des one-shots depuis PV pleins");
        }
        Console.WriteLine("    (Un motif a faible % ici — typiquement Fou/diagonale — est celui que le");
        Console.WriteLine("     positionnement evite le mieux : sa menace brute est surestimee.)");
        Console.WriteLine();
        Console.WriteLine("  Legende :");
        Console.WriteLine("    Victoires      = part des combats gagnes par le bot de reference (etalon, pas un humain)");
        Console.WriteLine("    Pertes/combat  = unites joueur perdues en moyenne par combat");
        Console.WriteLine("    Pic>=2         = part des combats ou AU MOINS une action ennemie a tue >=2 unites d'un coup");
        Console.WriteLine("    1-shot/combat  = unites tuees depuis PV PLEINS en une action (le coup 'injuste')");
        Console.WriteLine();
        return 0;
    }

    // ── Un combat complet : place les armees, joue jusqu'a la fin, agrege les metriques ──
    private static void SimulateCombat(int phase, int mission, int deploy, Config cfg, Random rng, Aggregate agg)
    {
        var setup = CampaignPlan.For(phase, mission);
        var enemies = BuildEnemyWave(phase, mission, cfg, rng);
        var players = BuildPlayerArmy(phase, deploy, rng);

        var width = Math.Max(setup.MapSize, 8);
        var rowsNeeded = (int)Math.Ceiling(Math.Max(enemies.Count, players.Count) / (double)width);
        var height = Math.Max(setup.MapSize + 2, rowsNeeded * 2 + 3);

        var match = new Match(width, height, rng: new Random(rng.Next()));
        PlaceRows(match, enemies, topDown: true, width, height);
        PlaceRows(match, players, topDown: false, width, height);

        var stats = new CombatStats();
        var maxTurns = 400;
        var turn = 0;
        while (!match.IsOver && turn < maxTurns)
        {
            turn++;
            if (match.CurrentTurn == Faction.Player)
            {
                if (!PlayerTurn(match, rng))
                    break; // le joueur ne peut plus rien faire : on arrete (timeout)
            }
            else
            {
                EnemyTurn(match, cfg, rng, stats);
            }
        }

        var playerAlive = match.Units().Count(x => x.Unit.Faction == Faction.Player);
        var won = match.Winner == Faction.Player;
        var losses = deploy - playerAlive;

        agg.Combats++;
        if (won) agg.Wins++;
        agg.TotalLosses += losses;
        agg.TotalTurns += turn;
        agg.TotalOneShots += stats.OneShotsFromFull;
        if (stats.MaxKillsInOneAction >= 2) agg.CombatsWithSpike++;
        foreach (var (k, n) in stats.KillsPerAction)
            agg.KillsPerAction[k] = agg.KillsPerAction.GetValueOrDefault(k) + n;
        foreach (var (d, n) in stats.KillsByPattern)
            agg.KillsByPattern[d] = agg.KillsByPattern.GetValueOrDefault(d) + n;
        foreach (var (d, n) in stats.OneShotsByPattern)
            agg.OneShotsByPattern[d] = agg.OneShotsByPattern.GetValueOrDefault(d) + n;
    }

    // ── Tour ennemi : on rejoue EnemyAi, on applique l'action, on MESURE les degats collateraux ──
    private static void EnemyTurn(Match match, Config cfg, Random rng, CombatStats stats)
    {
        // Etat des unites joueur AVANT l'action (refs + PV) pour detecter morts et one-shots.
        var before = match.Units()
            .Where(x => x.Unit.Faction == Faction.Player)
            .Select(x => x.Unit)
            .ToDictionary(u => u, u => u.Hp);

        var action = EnemyAi.ChooseAction(match, Array.Empty<Cell>(), rng);
        if (action is not { } a)
        {
            match.PassTurn();
            return;
        }

        // Maladresse : avec proba Blunder, un tour d'attaque est "rate" (remplace par un simple deplacement).
        if (a.IsAttack && cfg.Blunder > 0 && rng.NextDouble() < cfg.Blunder)
        {
            if (TryRandomEnemyMove(match, rng)) return;
            // sinon : aucun deplacement possible, on applique quand meme l'attaque.
        }

        // Motif d'attaque de l'unite qui agit, CAPTURE AVANT l'action (l'attaquant peut ensuite prendre
        // la place de sa victime). Sert a attribuer les degats a Dame/Fou/Tour/Cavalier.
        var attackerDomaine = match.UnitAt(a.From)?.Domaine;

        if (a.IsAttack) match.TryAttack(a.From, a.To);
        else match.TryMove(a.From, a.To);

        // Combien d'unites joueur sont mortes DE CETTE action ? (l'action est unique mais splash/orage/
        // transpercement peuvent en tuer plusieurs -> c'est exactement la mesure du "pic".)
        var stillAlive = match.Units()
            .Where(x => x.Unit.Faction == Faction.Player)
            .Select(x => x.Unit).ToHashSet();
        var killed = 0;
        var oneShotsFromFull = 0;
        foreach (var (unit, hpBefore) in before)
        {
            if (stillAlive.Contains(unit)) continue;
            killed++;
            if (hpBefore >= unit.MaxHp) oneShotsFromFull++; // etait a PV pleins avant l'action
        }
        stats.KillsPerAction[killed] = stats.KillsPerAction.GetValueOrDefault(killed) + 1;
        stats.OneShotsFromFull += oneShotsFromFull;
        if (killed > stats.MaxKillsInOneAction) stats.MaxKillsInOneAction = killed;
        if (attackerDomaine is { } dom && killed > 0)
        {
            stats.KillsByPattern[dom] = stats.KillsByPattern.GetValueOrDefault(dom) + killed;
            stats.OneShotsByPattern[dom] = stats.OneShotsByPattern.GetValueOrDefault(dom) + oneShotsFromFull;
        }
    }

    // ── Bot joueur "competent glouton" : UNE action par tour (comme le jeu). Priorites :
    //    1. tuer l'ennemi le plus dangereux a portee
    //    2. soigner un allie sous 50 %
    //    3. attaquer pour un max de degats (en preferant la distance = sans riposte)
    //    4. se deplacer vers l'ennemi le plus proche SANS entrer dans une case mortelle (kite sinon)
    private static bool PlayerTurn(Match match, Random rng)
    {
        var players = match.Units().Where(x => x.Unit.Faction == Faction.Player).ToList();
        var enemies = match.Units().Where(x => x.Unit.Faction == Faction.Enemy).ToList();
        if (players.Count == 0 || enemies.Count == 0) return false;

        // 1. KILL — priorise l'ennemi le plus dangereux (degats les plus eleves).
        (Cell From, Cell To, int Threat)? bestKill = null;
        foreach (var (from, unit) in players)
            foreach (var tgt in match.AttackTargets(from))
            {
                var victim = match.UnitAt(tgt)!;
                if (match.PreviewDamage(from, tgt) >= victim.Hp)
                {
                    var threat = victim.Damage + (victim.IsEssential ? 1000 : 0); // le boss d'abord
                    if (bestKill is null || threat > bestKill.Value.Threat)
                        bestKill = (from, tgt, threat);
                }
            }
        if (bestKill is { } k) return match.TryAttack(k.From, k.To) != MoveKind.Invalid;

        // 2. HEAL — soigne l'allie le plus bas sous 50 % s'il existe un soigneur a portee.
        (Cell From, Cell To, double Ratio)? bestHeal = null;
        foreach (var (from, unit) in players)
        {
            if (!unit.HasTrait(Trait.Soin) && !unit.HasTrait(Trait.SoinParfait)) continue;
            foreach (var tgt in match.HealTargets(from))
            {
                var ally = match.UnitAt(tgt)!;
                var ratio = ally.Hp / (double)ally.MaxHp;
                if (ratio < 0.5 && (bestHeal is null || ratio < bestHeal.Value.Ratio))
                    bestHeal = (from, tgt, ratio);
            }
        }
        if (bestHeal is { } h) return match.TryHeal(h.From, h.To) != MoveKind.Invalid;

        // 3. ATTAQUE — max de degats, en preferant tirer a distance (evite la riposte de melee).
        (Cell From, Cell To, int Score)? bestAttack = null;
        foreach (var (from, unit) in players)
            foreach (var tgt in match.AttackTargets(from))
            {
                var dist = Chebyshev(from, tgt);
                var score = match.PreviewDamage(from, tgt) * 10 + (dist >= 2 ? 5 : 0);
                if (bestAttack is null || score > bestAttack.Value.Score)
                    bestAttack = (from, tgt, score);
            }
        if (bestAttack is { } at) return match.TryAttack(at.From, at.To) != MoveKind.Invalid;

        // 4. DEPLACEMENT — se rapprocher de l'ennemi le plus proche sans marcher dans une case mortelle.
        var lethal = LethalCells(match, enemies);
        (Cell From, Cell To, int Dist)? bestMove = null;
        (Cell From, Cell To, int Dist)? bestSafeMove = null;
        foreach (var (from, unit) in players)
        {
            foreach (var to in match.LegalMoves(from))
            {
                var dist = enemies.Min(e => Chebyshev(to, e.Cell));
                if (bestMove is null || dist < bestMove.Value.Dist)
                    bestMove = (from, to, dist);
                if (!lethal.Contains(to) && (bestSafeMove is null || dist < bestSafeMove.Value.Dist))
                    bestSafeMove = (from, to, dist);
            }
        }
        var move = bestSafeMove ?? bestMove;
        if (move is { } m) return match.TryMove(m.From, m.To) != MoveKind.Invalid;

        return false; // aucune action possible
    }

    /// <summary>Cases ou un ennemi pourrait TUER (degats >= PV) une unite qui s'y arreterait.</summary>
    private static HashSet<Cell> LethalCells(Match match, List<(Cell Cell, Unit Unit)> enemies)
    {
        var set = new HashSet<Cell>();
        foreach (var (cell, e) in enemies)
            foreach (var t in match.ThreatenedCells(cell))
                set.Add(t); // approximation : menace depuis la position actuelle (comme WouldBeKilledAt de l'IA)
        return set;
    }

    private static bool TryRandomEnemyMove(Match match, Random rng)
    {
        var moves = new List<(Cell From, Cell To)>();
        foreach (var (from, unit) in match.Units().Where(x => x.Unit.Faction == Faction.Enemy))
            foreach (var to in match.LegalMoves(from))
                moves.Add((from, to));
        if (moves.Count == 0) return false;
        var pick = moves[rng.Next(moves.Count)];
        return match.TryMove(pick.From, pick.To) != MoveKind.Invalid;
    }

    // ── Construction des armees ──────────────────────────────────────────────

    private static List<Unit> BuildEnemyWave(int phase, int mission, Config cfg, Random rng)
    {
        var setup = CampaignPlan.For(phase, mission);
        var isBoss = mission == 6;
        var wave = new List<Unit>();

        if (isBoss)
        {
            var bossDef = Bosses.All[0];
            var profile = ScaleDamage(bossDef.ProfileFor(phase), cfg.EnemyDamage);
            wave.Add(new Unit(bossDef.Movement, Faction.Enemy, profile) { IsEssential = true, AiKind = AiKind.Normal });
        }

        foreach (var tier in setup.Tiers)
        {
            var domaine = AllDomaines[rng.Next(AllDomaines.Length)];
            var pool = Run.ClassesAtTier(domaine, tier);
            if (pool.Count == 0) continue;
            var cls = ScaleDamage(pool[rng.Next(pool.Count)], cfg.EnemyDamage);
            wave.Add(new Unit(domaine, Faction.Enemy, cls) { AiKind = AiKind.Normal });
        }
        return wave;
    }

    private static List<Unit> BuildPlayerArmy(int phase, int deploy, Random rng)
    {
        // Le commandant + (deploy-1) troupes, calees en tier sur la phase (mix melee/distance/domaines).
        var army = new List<Unit>();
        var cdef = Commandes.Commander;
        army.Add(new Unit(cdef.Movement, Faction.Player, cdef.BaseClass) { IsEssential = true });

        // Distribution de tiers de l'armee joueur, par phase (approx. ce qu'un joueur a recrute).
        int[] tiers = phase switch
        {
            1 => new[] { 1, 1, 1, 1, 1, 1 },
            2 => new[] { 2, 2, 1, 2, 1, 2 },
            _ => new[] { 3, 2, 3, 2, 3, 2 },
        };
        for (var i = 0; i < deploy - 1; i++)
        {
            var tier = tiers[i % tiers.Length];
            var domaine = AllDomaines[i % AllDomaines.Length];
            var pool = Run.ClassesAtTier(domaine, tier);
            if (pool.Count == 0) pool = Run.ClassesAtTier(domaine, 1);
            var cls = pool[rng.Next(pool.Count)];
            army.Add(new Unit(domaine, Faction.Player, cls));
        }
        return army;
    }

    /// <summary>Clone une classe en multipliant SES DEGATS (preview du bouton "degats ennemis" du preset facile).</summary>
    private static UnitClass ScaleDamage(UnitClass c, double mul)
    {
        if (Math.Abs(mul - 1.0) < 1e-9) return c;
        var dmg = Math.Max(1, (int)Math.Round(c.Damage * mul));
        return new UnitClass(c.Name, c.Asset, c.Tier, c.MaxHp, dmg, c.MoveRange, c.AttackRange,
            c.PiercesAllies, c.MinAttackRange, c.Traits, c.AttackDomaine);
    }

    // ── Placement : ennemis en haut, joueurs en bas, remplissage par rangees ──
    private static void PlaceRows(Match match, List<Unit> units, bool topDown, int width, int height)
    {
        var idx = 0;
        var row = topDown ? 0 : height - 1;
        var step = topDown ? 1 : -1;
        while (idx < units.Count && row >= 0 && row < height)
        {
            for (var col = 0; col < width && idx < units.Count; col++)
            {
                var cell = new Cell(col, row);
                if (match.UnitAt(cell) == null)
                    match.Place(cell, units[idx++]);
            }
            row += step * 2; // laisse une rangee vide entre les lignes (respiration)
        }
    }

    private static int Chebyshev(Cell a, Cell b) =>
        Math.Max(Math.Abs(a.Column - b.Column), Math.Abs(a.Row - b.Row));

    // ── Agregats & config ────────────────────────────────────────────────────

    private sealed class CombatStats
    {
        public readonly Dictionary<int, int> KillsPerAction = new();
        // Ventilation par MOTIF d'attaque de l'ennemi (Dame/Fou/Tour/Cavalier) : rend la geometrie MESUREE.
        public readonly Dictionary<Domaine, int> KillsByPattern = new();
        public readonly Dictionary<Domaine, int> OneShotsByPattern = new();
        public int OneShotsFromFull;
        public int MaxKillsInOneAction;
    }

    private sealed class Aggregate
    {
        public int Combats, Wins, TotalLosses, TotalTurns, TotalOneShots, CombatsWithSpike;
        public readonly Dictionary<int, int> KillsPerAction = new();
        public readonly Dictionary<Domaine, int> KillsByPattern = new();
        public readonly Dictionary<Domaine, int> OneShotsByPattern = new();

        public double WinRate => Combats == 0 ? 0 : (double)Wins / Combats;
        public double AvgLosses => Combats == 0 ? 0 : (double)TotalLosses / Combats;
        public double AvgTurns => Combats == 0 ? 0 : (double)TotalTurns / Combats;
        public double SpikeRate => Combats == 0 ? 0 : (double)CombatsWithSpike / Combats;
        public double OneShotsPerCombat => Combats == 0 ? 0 : (double)TotalOneShots / Combats;

        public void Absorb(Aggregate o)
        {
            Combats += o.Combats; Wins += o.Wins; TotalLosses += o.TotalLosses;
            TotalTurns += o.TotalTurns; TotalOneShots += o.TotalOneShots; CombatsWithSpike += o.CombatsWithSpike;
            foreach (var (k, n) in o.KillsPerAction)
                KillsPerAction[k] = KillsPerAction.GetValueOrDefault(k) + n;
            foreach (var (d, n) in o.KillsByPattern)
                KillsByPattern[d] = KillsByPattern.GetValueOrDefault(d) + n;
            foreach (var (d, n) in o.OneShotsByPattern)
                OneShotsByPattern[d] = OneShotsByPattern.GetValueOrDefault(d) + n;
        }
    }

    private static Config? ParseArgs(string[] args)
    {
        var cfg = new Config();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--runs": cfg.Runs = int.Parse(args[++i]); break;
                case "--enemy-dmg": cfg.EnemyDamage = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--blunder": cfg.Blunder = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--player": cfg.PlayerArmy = int.Parse(args[++i]); break;
                case "--seed": cfg.Seed = int.Parse(args[++i]); break;
                case "--preset-easy": cfg.EnemyDamage = 0.85; cfg.Blunder = 0.25; break;
                case "-h": case "--help":
                    Console.WriteLine("Voir l'entete de Program.cs pour les options.");
                    return null;
                default:
                    Console.WriteLine($"Option inconnue : {args[i]}");
                    return null;
            }
        }
        return cfg;
    }
}
