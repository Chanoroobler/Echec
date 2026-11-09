using System.Diagnostics;
using System.Globalization;
using System.Text;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Battle.Config;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Pvp;

// Équilibrage du multijoueur : mesure chaque classe et chaque équipement par simulation (bots contre bots), puis
// réécrit "unitCost", "equipmentCost" et "budget" dans pvp.json. Le coût = valeur mesurée en Soldats, arrondie
// (minimum 1). Le budget = coût de l'armée de départ, qui remplit donc toujours tout le budget.
//
//   dotnet run --project tools/ChessArmy.Balance -c Release -- [--games 40] [--units-only] [--dry-run]

var games = ArgInt("--games", 40);
var unitsOnly = args.Contains("--units-only");
var dryRun = args.Contains("--dry-run");

var config = FindConfigDir();
var unitsJson = File.ReadAllText(Path.Combine(config, "units.json"));
Domaines.Load(DomaineCatalog.FromJson(unitsJson));
Commandes.Load(DomaineCatalog.CommandesFromJson(unitsJson));
Equipments.Load(EquipmentCatalog.FromJson(File.ReadAllText(Path.Combine(config, "equipment.json"))));
var pvpPath = Path.Combine(config, "pvp.json");
PvpConfig.Load(PvpConfig.FromJson(File.ReadAllText(pvpPath)));

var soldierEntry = PvpCatalog.FindClass(Domaines.Dame.BaseClass.Asset)!.Value;
var soldier = PvpBalance.Of(soldierEntry.Domaine, soldierEntry.Class);
var clock = Stopwatch.StartNew();

if (args.Contains("--trace"))
{
    // Diagnostic : une partie 2 Soldats contre 3 Soldats, coup par coup.
    var trace = new List<string>();
    var score = PvpBalance.PlayGame(new[] { soldier, soldier }, new[] { soldier, soldier, soldier }, 1, true, trace: trace);
    trace.ForEach(Console.WriteLine);
    Console.WriteLine($"Score : {score}");
    return;
}

if (args.Contains("--battle-trace"))
{
    // Diagnostic : une vraie bataille entre deux armées de départ, coup par coup.
    var start = PvpProfile.DefaultBuild();
    var log = new List<string>();
    var result = PvpBalance.PlayBattle(start, start, 7, true, trace: log);
    log.ForEach(Console.WriteLine);
    Console.WriteLine($"Score : {result}  ({log.Count} coups)");
    return;
}

if (args.Contains("--dump-fields"))
{
    foreach (var t in new[] { typeof(Match), typeof(Unit), typeof(ChessArmy.Core.Command.CommandBuffs) })
        foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            Console.WriteLine($"{t.Name}.{fi.Name} : {fi.FieldType.Name}{(fi.IsInitOnly ? " ro" : "")}");
    return;
}

if (args.Contains("--ai-bench"))
{
    // Où passe le temps de calcul des bots : copie de la partie, tri des coups, évaluation (positions de milieu de partie).
    AiLab.Bench(ArgInt("--plies", 40), ArgInt("--seed", 1));
    return;
}

if (args.Contains("--ai-ladder"))
{
    // Échelle des bots : chaque rang contre le précédent, avec la réserve d'armées des bots.
    AiLab.Ladder(ArgInt("--games", 200), ArgInt("--seed", 1), ArgString("--from"));
    return;
}

if (args.Contains("--ai-pair"))
{
    // Duel de deux réglages écrits en ligne : --a "forceAttack=1,killChance=0.3,caution=0,depth=0,accuracy=0.5" --b "...".
    // Poids d'évaluation retouchés par camp : --wa "pieceCost=0" / --wb "pieceCost=1.5" (sinon ceux de pvp.json).
    var x = new AiLab.Brain("A", ParseBot(ArgString("--a")), Weights(ArgString("--wa")));
    var y = new AiLab.Brain("B", ParseBot(ArgString("--b")), Weights(ArgString("--wb")));
    var score = AiLab.Duel(x, y, PvpConfig.Current.BotArmies, ArgInt("--games", 200), ArgInt("--seed", 1));
    Console.WriteLine($"A contre B : {score * 100:0.0} %");
    return;
}

static PvpAiWeights Weights(string spec)
{
    var values = PvpConfig.Current.BotWeights.ToDictionary();
    foreach (var part in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = part.Split('=');
        values[kv[0]] = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
    }
    return PvpAiWeights.FromDictionary(values);
}

static PvpBotDef ParseBot(string spec)
{
    // « or-2 » : le niveau livré ; « or-2:guard=1,advance=0.5 » : ce niveau avec des retouches ; sinon tout en ligne.
    var colon = spec.IndexOf(':');
    var head = colon < 0 ? spec : spec[..colon];
    var def = PvpConfig.Current.Bots.TryGetValue(head, out var shipped) ? shipped : null;
    if (def != null)
        spec = colon < 0 ? "" : spec[(colon + 1)..];
    def ??= new PvpBotDef();
    foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = part.Split('=');
        var v = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
        def = kv[0] switch
        {
            "forceAttack" => def with { ForceAttack = v != 0 },
            "killChance" => def with { KillChance = v },
            "caution" => def with { Caution = v },
            "reckless" => def with { Recklessness = v },
            "depth" => def with { Depth = (int)v },
            "breadth" => def with { Breadth = (int)v },
            "replyBreadth" => def with { ReplyBreadth = (int)v },
            "accuracy" => def with { Accuracy = v },
            "equipment" => def with { Equipment = v != 0 },
            "smart" => def with { SmartEquipment = v != 0 },
            "advance" => def with { Style = (def.Style ?? new PvpBotStyle()) with { Id = "test", Advance = v } },
            "cautionStyle" => def with { Style = (def.Style ?? new PvpBotStyle()) with { Id = "test", Caution = v } },
            "aggression" => def with { Style = (def.Style ?? new PvpBotStyle()) with { Id = "test", Aggression = v } },
            "guard" => def with { Style = (def.Style ?? new PvpBotStyle()) with { Id = "test", Guard = v } },
            _ => throw new ArgumentException(kv[0]),
        };
    }
    return def;
}

if (args.Contains("--ai-spsa"))
{
    // Entraînement SPSA des poids d'évaluation (auto-jeu, cerveau de profondeur --depth).
    var level = new PvpBotDef { Depth = ArgInt("--depth", 1), Breadth = 6, ReplyBreadth = 5 };
    var trained = AiLab.TrainSpsa(level, ArgInt("--iterations", 60), ArgInt("--games", 128),
        ArgInt("--delta-pct", 25) / 100.0, ArgInt("--step-pct", 15) / 100.0, ArgInt("--seed", 11));
    if (!dryRun)
        File.WriteAllText(pvpPath, AiLab.WriteWeights(File.ReadAllText(pvpPath), trained));
    return;
}

if (args.Contains("--ai-train"))
{
    // Entraînement des poids d'évaluation par auto-jeu, avec un cerveau qui calcule (profondeur --depth).
    var level = new PvpBotDef { Depth = ArgInt("--depth", 1), Breadth = 6, ReplyBreadth = 5 };
    var sigma = ArgInt("--sigma-pct", 30) / 100.0;
    var trained = AiLab.Train(level, ArgInt("--generations", 40), ArgInt("--games", 128), sigma, ArgInt("--seed", 7));
    if (!dryRun)
        File.WriteAllText(pvpPath, AiLab.WriteWeights(File.ReadAllText(pvpPath), trained));
    return;
}

if (args.Contains("--bot-armies"))
{
    // Armées des bots : une réserve commune, chacune remplit exactement les deux budgets.
    // Sans équipement : le jeu le retire au chargement et en tire un au hasard à chaque partie (cf. PvpConfig).
    var armies = AiLab.ComposeBotArmies(ArgInt("--count", 40), ArgInt("--seed", 2026))
        .Select(a => a.WithoutEquipment()).ToList();
    foreach (var a in armies)
        Console.WriteLine($"  {a.CommanderId,-20} {PvpCatalog.Cost(a)}+{PvpCatalog.EquipmentCost(a)}  "
                          + string.Join(" ", a.Slots.Select(s => s.ClassAsset + (s.EquipmentId != null ? "+" + s.EquipmentId : ""))));
    Console.WriteLine($"{armies.Count} armées.");
    if (!dryRun)
        File.WriteAllText(pvpPath, AiLab.WriteBotArmies(File.ReadAllText(pvpPath), armies));
    return;
}

if (args.Contains("--beginner-armies"))
{
    // Armées des bots Bronze et Fer : pions de base et au plus une pièce différente, sans équipement.
    var armies = AiLab.ComposeBeginnerArmies(ArgInt("--count", 20), ArgInt("--seed", 2026));
    foreach (var a in armies)
        Console.WriteLine($"  {a.CommanderId,-20} {PvpCatalog.Cost(a)}  " + string.Join(" ", a.Slots.Select(s => s.ClassAsset)));
    Console.WriteLine($"{armies.Count} armées débutant.");
    if (!dryRun)
        File.WriteAllText(pvpPath, AiLab.WriteBotArmies(File.ReadAllText(pvpPath), armies, "botArmiesBeginner",
            "  // Armées des bots BRONZE et FER (« beginnerArmies » des bots), composées par l'outil (--beginner-armies) :\n"
            + "  // pions de BASE et au plus UNE pièce différente, pour qu'un nouveau joueur ne soit pas perdu. Budget plein, sans équipement.\n"));
    return;
}

if (args.Contains("--chase-trace"))
{
    // Diagnostic : UNE poursuite (commandant seul --commander contre commandant + 4 pièces), plateau à chaque coup.
    var rng = new Random(17 + ArgInt("--seed", 0) * 104729);
    var units = PvpCatalog.AllUnitClasses().Select(c => (c.Class.Asset, PvpConfig.Current.UnitCost.GetValueOrDefault(c.Class.Asset, PvpConfig.DefaultCost)))
        .Where(u => u.Item2 <= 6).ToList();
    var strong = PvpBalance.RandomArmy(rng, 24, new[] { ("commandant", 0) }, units, new List<(string, int)>(), 0);
    while (strong.Slots.Count > 4)
        strong.Remove(strong.Slots[^1].Column, strong.Slots[^1].Line);
    var lone = new PvpBuild { CommanderId = ArgString("--commander") is { Length: > 0 } c ? c : "Commandant_cavalier" };
    var match = PvpSetup.CreateMatch(mirrored: false, true, rng.Next());
    PvpSetup.PlaceArmy(match, strong, Faction.Player);
    PvpSetup.PlaceArmy(match, lone, Faction.Enemy);
    var r = new Random(1);
    for (var ply = 0; ply < ArgInt("--plies", 60) && !match.IsOver; ply++)
    {
        var actor = match.CurrentTurn;
        var top = PvpBotAi.ScoreActions(match, actor, 1.0, r);
        var action = PvpBotAi.Choose(match, actor, 1.0, r) ?? BattleAction.Pass;
        Console.WriteLine($"{ply,3} {actor,-6} {action.Kind,-6} {action.From.Column},{action.From.Row}>{action.Target.Column},{action.Target.Row}   top: "
            + string.Join(" | ", top.Take(4).Select(t => $"{t.Action.Kind} {t.Action.From.Column},{t.Action.From.Row}>{t.Action.Target.Column},{t.Action.Target.Row} {t.Score:0.0}")));
        if (match.Apply(action) == MoveKind.Invalid) match.Apply(BattleAction.Pass);
        if (!match.IsOver && match.CurrentTurn != actor) match.ApplySeismes(match.CurrentTurn);
        PvpSetup.ResolvePending(match);
        if (ply % 2 == 1 || match.IsOver)
            for (var row = 0; row < PvpSetup.Size; row++)
            {
                var sb = new StringBuilder("      ");
                for (var col = 0; col < PvpSetup.Size; col++)
                    sb.Append(match.UnitAt(new ChessArmy.Core.Map.Cell(col, row)) is { } u
                        ? (u.IsEssential ? (u.Faction == Faction.Player ? 'K' : 'k') : (u.Faction == Faction.Player ? char.ToUpper(u.Class.Asset[0]) : char.ToLower(u.Class.Asset[0])))
                        : '.');
                Console.WriteLine(sb + $"   {(row == 0 ? string.Join(" ", match.Units().Select(x => $"{x.Unit.Class.Asset}:{x.Unit.Hp}")) : "")}");
            }
    }
    Console.WriteLine($"Fin : vainqueur {match.Winner}");
    return;
}

if (args.Contains("--stall"))
{
    // Diagnostic : parties qui s'éternisent, par commandant (aucune écriture dans pvp.json).
    StallReport(ArgInt("--battles", 4000), ArgInt("--chases", 1000), ArgInt("--max-plies", 300), ArgInt("--chase-plies", 120));
    return;
}

if (args.Contains("--tune-stats"))
{
    TuneStats();
    return;
}

if (!args.Contains("--skirmish"))
{
    RunBattles();
    return;
}

// Référence : les pièces de l'ARMÉE DE DÉPART, dans leurs proportions (une Mage pour quatre Soldats…).
var pool = PvpProfile.DefaultBuild().Slots
    .Select(s => PvpCatalog.FindClass(s.ClassAsset)!.Value)
    .Select(c => PvpBalance.Of(c.Domaine, c.Class)).ToList();
var startingAssets = PvpProfile.DefaultBuild().Slots.Select(s => s.ClassAsset).ToList();

Console.WriteLine($"Équilibrage : {games} parties par mesure, référence = armée de départ ({pool.Count} pièces).");
Console.WriteLine();

var raw = new Dictionary<string, double>();
foreach (var (domaine, cls) in PvpCatalog.AllUnitClasses())
{
    raw[cls.Asset] = PvpBalance.MeasureValue(PvpBalance.Of(domaine, cls), pool, games: games);
    Console.WriteLine($"  {cls.Name,-26} {raw[cls.Asset],5:0.00}   ({clock.Elapsed:mm\\:ss})");
}

// Unité de mesure : une pièce MOYENNE de l'armée de départ vaut 1 (l'armée de départ vaut donc ~son effectif).
var scale = startingAssets.Average(a => raw[a]);
var unitValues = raw.ToDictionary(kv => kv.Key, kv => kv.Value / scale);
Console.WriteLine();
Console.WriteLine($"  Échelle : 1 point = {scale:0.00} (pièce moyenne de l'armée de départ)");
foreach (var (asset, value) in unitValues.OrderBy(kv => kv.Value))
    Console.WriteLine($"  {asset,-26} {value,5:0.00}  ->  {UnitCost(value)} pt");

var equipmentValues = new Dictionary<string, double>();
if (!unitsOnly)
{
    Console.WriteLine();
    var bare = raw[soldierEntry.Class.Asset];
    foreach (var eq in PvpCatalog.AllEquipment())
    {
        var equipped = PvpBalance.MeasureValue(PvpBalance.Of(soldierEntry.Domaine, soldierEntry.Class, eq), pool, games: games);
        var value = (equipped - bare) / scale;
        equipmentValues[eq.Id] = value;
        Console.WriteLine($"  {eq.Id,-26} {value,5:+0.00;-0.00}  ->  {EquipCost(value)} pt   ({clock.Elapsed:mm\\:ss})");
    }
}

var unitCosts = unitValues.ToDictionary(kv => kv.Key, kv => UnitCost(kv.Value));
var equipmentCosts = unitsOnly
    ? PvpConfig.Current.EquipmentCost.ToDictionary(kv => kv.Key, kv => kv.Value)
    : equipmentValues.ToDictionary(kv => kv.Key, kv => EquipCost(kv.Value));
var budget = PvpProfile.DefaultBuild().Slots.Sum(s => unitCosts.GetValueOrDefault(s.ClassAsset, PvpConfig.DefaultCost));

Console.WriteLine();
Console.WriteLine($"Budget (armée de départ) : {budget} points.   Durée : {clock.Elapsed:mm\\:ss}");
if (dryRun)
    return;

var text = File.ReadAllText(pvpPath);
text = ReplaceNumber(text, "budget", budget);
text = ReplaceObject(text, "unitCost", unitCosts, unitValues);
text = ReplaceObject(text, "equipmentCost", equipmentCosts, equipmentValues);
File.WriteAllText(pvpPath, text, new UTF8Encoding(false));
Console.WriteLine($"pvp.json mis à jour : {pvpPath}");
return;

// ── Vraies batailles ─────────────────────────────────────────────────────────
//
// Des milliers de batailles entre armées COMPLÈTES tirées au hasard (budget dépensé en entier, pièces, équipements
// et commandant au hasard), jouées par deux bots tactiques avec toutes les règles (commandant à protéger…). Une
// régression logistique attribue ensuite à chaque pièce, équipement et commandant sa contribution à la victoire :
// P(A gagne) = sigmoïde(Σ valeurs de A − Σ valeurs de B + avantage du premier coup). Les valeurs sont converties en
// points (l'armée de départ vaut le budget visé), puis une nouvelle passe rejoue avec ces coûts pour affiner.
void RunBattles()
{
    var budget = ArgInt("--budget", 35);       // budget FIXE d'une armée (commandant compris)
    var minCost = ArgInt("--min-cost", 3);     // coût de la pièce la plus faible : fixe l'échelle
    var battles = ArgInt("--battles", 20000);
    var passes = ArgInt("--passes", 2);
    var equipChance = ArgInt("--equip-pct", 35) / 100.0;   // part des pions équipés dans les armées tirées
    // --equipment-only : pions et commandants gardent leurs coûts ; seuls les équipements sont mesurés et réécrits.
    var equipmentOnly = args.Contains("--equipment-only");
    // Les équipements ont leur PROPRE budget en jeu : leur échelle se compresse pour que le plus fort coûte au plus
    // --max-equipment points (les simulations, elles, les paient sur le budget d'armée, à l'échelle des pions).
    var maxEquipment = ArgInt("--max-equipment", 5);

    var classes = PvpCatalog.AllUnitClasses().Select(c => c.Class.Asset).ToList();
    var equipment = PvpCatalog.AllEquipment().Select(e => e.Id).ToList();
    var commanders = PvpCatalog.Commanders().Select(c => c.Id).ToList();
    var featureCount = classes.Count + equipment.Count + commanders.Count + 1;   // + avantage du premier coup
    var startCommander = PvpConfig.Current.StartingCommanders.FirstOrDefault() ?? "commandant";

    // Coûts de départ : ceux de pvp.json, remis à l'échelle « pièce la plus faible = minCost ».
    var currentMin = Math.Max(1, classes.Min(a => PvpConfig.Current.UnitCost.GetValueOrDefault(a, 1)));
    var ratio = minCost / (double)currentMin;
    var unitCost = classes.ToDictionary(a => a, a => Math.Max(minCost, (int)Math.Round(PvpConfig.Current.UnitCost.GetValueOrDefault(a, 1) * ratio)));
    var equipCost = equipment.ToDictionary(e => e, e => Math.Max(1, (int)Math.Round(PvpConfig.Current.EquipmentCost.GetValueOrDefault(e, 1) * ratio)));
    var commanderCost = commanders.ToDictionary(c => c, c => (int)Math.Round(PvpConfig.Current.CommanderCostOf(c) * ratio));
    double[] weights = Array.Empty<double>();
    double scale = 1;
    int CommanderIndex(string c) => classes.Count + equipment.Count + commanders.IndexOf(c);

    Console.WriteLine($"Vraies batailles : {passes} passe(s) de {battles} batailles, budget {budget}, pièce la plus faible = {minCost} pts.");
    for (var pass = 1; pass <= passes; pass++)
    {
        var units = classes.Select(a => (a, unitCost[a])).ToList();
        var items = equipment.Select(e => (e, equipCost[e])).ToList();
        var cmds = commanders.Select(c => (c, commanderCost[c])).ToList();
        var samples = new (double[] X, double Y)[battles];
        var done = 0;
        Parallel.For(0, battles, i =>
        {
            var rng = new Random(pass * 1_000_003 + i * 7919);
            // Budget VARIABLE (2/3 à 4/3 du budget) : si toutes les armées dépensaient exactement la même somme,
            // la régression ne pourrait pas séparer la valeur d'une pièce de son coût (valeurs à une constante près).
            var a = PvpBalance.RandomArmy(rng, rng.Next(budget * 2 / 3, budget * 4 / 3 + 1), cmds, units, items, equipChance);
            var b = PvpBalance.RandomArmy(rng, rng.Next(budget * 2 / 3, budget * 4 / 3 + 1), cmds, units, items, equipChance);
            var aStarts = i % 2 == 0;
            var y = PvpBalance.PlayBattle(a, b, rng.Next(), aStarts);
            var x = new double[featureCount];
            AddArmy(x, a, +1);
            AddArmy(x, b, -1);
            x[^1] = aStarts ? 1 : -1;
            samples[i] = (x, y);
            var n = Interlocked.Increment(ref done);
            if (n % 10000 == 0)
                Console.WriteLine($"  passe {pass} : {n}/{battles} batailles   ({clock.Elapsed:mm\\:ss})");
        });

        weights = PvpBalance.FitLogistic(samples, featureCount);
        // Échelle : la pièce la plus FAIBLE coûte minCost points (moins de pions sur le plateau qu'à 1 ou 2 points).
        var weakestUnit = classes.Min(a => weights[classes.IndexOf(a)]);
        scale = weakestUnit > 0 ? minCost / weakestUnit : 1;
        foreach (var a in classes.Where(_ => !equipmentOnly))
            unitCost[a] = Math.Max(minCost, (int)Math.Round(weights[classes.IndexOf(a)] * scale, MidpointRounding.AwayFromZero));
        foreach (var e in equipment)
            equipCost[e] = Math.Max(1, (int)Math.Round(weights[classes.Count + equipment.IndexOf(e)] * scale, MidpointRounding.AwayFromZero));
        // Commandants : leur force ne se mesure que LES UNS PAR RAPPORT AUX AUTRES (une armée en a toujours un). Le plus
        // faible coûte 0, chacun coûte son écart avec lui.
        var weakest = commanders.Min(c => weights[CommanderIndex(c)]);
        foreach (var c in commanders.Where(_ => !equipmentOnly))
            commanderCost[c] = (int)Math.Round((weights[CommanderIndex(c)] - weakest) * scale, MidpointRounding.AwayFromZero);
        var draws = samples.Count(s => Math.Abs(s.Y - 0.5) < 1e-9);
        Console.WriteLine($"  passe {pass} terminée : {draws} nulles sur {battles}, avantage du premier coup {weights[^1] * scale:+0.0;-0.0} pt");
    }

    void AddArmy(double[] x, PvpBuild army, int sign)
    {
        foreach (var slot in army.Slots)
        {
            x[classes.IndexOf(slot.ClassAsset)] += sign;
            if (slot.EquipmentId != null)
                x[classes.Count + equipment.IndexOf(slot.EquipmentId)] += sign;
        }
        x[classes.Count + equipment.Count + commanders.IndexOf(army.CommanderId)] += sign;
    }

    Console.WriteLine();
    Console.WriteLine($"PIÈCES (valeur en points, la plus faible = {minCost})");
    foreach (var a in classes.OrderBy(a => weights[classes.IndexOf(a)]))
        Console.WriteLine($"  {PvpCatalog.FindClass(a)!.Value.Class.Name,-26} {weights[classes.IndexOf(a)] * scale,6:0.0}  ->  {unitCost[a]} pt");
    Console.WriteLine();
    Console.WriteLine("COMMANDANTS (le plus faible coûte 0, chacun coûte son écart avec lui)");
    foreach (var c in commanders.OrderBy(c => commanderCost[c]))
        Console.WriteLine($"  {c,-26} {commanderCost[c],6} pt");
    Console.WriteLine();
    Console.WriteLine("ÉQUIPEMENTS");
    foreach (var e in equipment.OrderBy(e => weights[classes.Count + equipment.IndexOf(e)]))
        Console.WriteLine($"  {e,-26} {weights[classes.Count + equipment.IndexOf(e)] * scale,6:+0.0;-0.0}  ->  {equipCost[e]} pt");

    var start = ComposeStartingArmy(startCommander, budget - commanderCost[startCommander], unitCost);
    var startCost = start.Slots.Sum(s => unitCost[s.ClassAsset]) + commanderCost[startCommander];
    Console.WriteLine();
    Console.WriteLine($"Armée de départ : {PvpCatalog.FindClass(Domaines.Dame.BaseClass.Asset)!.Value.Class.Name} et autres T1, " +
                      $"{start.Slots.Count} pions, {startCost} / {budget} points (commandant {startCommander} : {commanderCost[startCommander]}).");
    Console.WriteLine($"  fond   : {string.Join(" ", Line(start, PvpBuild.BackLine))}");
    Console.WriteLine($"  devant : {string.Join(" ", Line(start, PvpBuild.FrontLine))}");
    Console.WriteLine($"Durée : {clock.Elapsed:mm\\:ss}");
    if (dryRun)
        return;
    var measuredUnits = classes.ToDictionary(a => a, a => weights[classes.IndexOf(a)] * scale);
    // Équipements : valeur mesurée (en points de pion) ramenée à l'échelle du budget d'équipement, le plus fort à
    // maxEquipment. Le « mesuré » écrit en commentaire est déjà à cette échelle.
    var rawItems = equipment.ToDictionary(e => e, e => weights[classes.Count + equipment.IndexOf(e)] * scale);
    // Échelle fixée par le DEUXIÈME plus fort : un seul objet hors norme ne doit pas écraser tous les autres à 1 point.
    // Ce qui dépasse le plafond est plafonné et signalé : on l'affaiblit en multi (pvp.json « equipmentStats »).
    var ranked = rawItems.Values.OrderByDescending(v => v).ToList();
    var squeeze = Math.Min(1.0, maxEquipment / Math.Max(1e-9, ranked.Count > 1 ? ranked[1] : ranked[0]));
    var measuredItems = rawItems.ToDictionary(kv => kv.Key, kv => kv.Value * squeeze);
    var shippedItems = measuredItems.ToDictionary(kv => kv.Key,
        kv => Math.Clamp((int)Math.Round(kv.Value, MidpointRounding.AwayFromZero), 1, maxEquipment));
    Console.WriteLine($"Équipements : échelle × {squeeze:0.00} (le deuxième plus fort à {maxEquipment} pt).");
    foreach (var (id, v) in measuredItems.Where(kv => kv.Value >= maxEquipment + 0.5).OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  TROP FORT : {id} vaut {v:0.0} pt, plafonné à {maxEquipment} : à affaiblir (equipmentStats).");
    var text = File.ReadAllText(pvpPath);
    text = ReplaceObject(text, "equipmentCost", shippedItems, measuredItems);
    if (equipmentOnly)
    {
        File.WriteAllText(pvpPath, text, new UTF8Encoding(false));
        Console.WriteLine($"pvp.json mis à jour (équipements seulement) : {pvpPath}");
        return;
    }
    text = ReplaceNumber(text, "budget", budget);
    text = ReplaceObject(text, "unitCost", unitCost, measuredUnits);
    var weakestValue = commanders.Min(c => weights[CommanderIndex(c)]);
    text = ReplaceObject(text, "commanderCost", commanderCost,
        commanders.ToDictionary(c => c, c => (weights[CommanderIndex(c)] - weakestValue) * scale));
    text = ReplaceStartingArmy(text, start);
    File.WriteAllText(pvpPath, text, new UTF8Encoding(false));
    Console.WriteLine($"pvp.json mis à jour : {pvpPath}");
}

// Armée de départ : uniquement des pions T1 (les seuls débloqués au départ), disposés comme aux échecs (Soldats
// devant, en partant du centre ; Lanciers, Cavaliers, Mages au fond), la plus nombreuse possible qui tombe PILE sur
// le budget (sinon au plus près en dessous), en gardant autant que possible chaque type de pièce.
static PvpBuild ComposeStartingArmy(string commander, int points, Dictionary<string, int> cost)
{
    var soldier = Domaines.Dame.BaseClass.Asset;
    var lancer = Domaines.Tour.BaseClass.Asset;
    var rider = Domaines.Cavalier.BaseClass.Asset;
    var mage = Domaines.Fou.BaseClass.Asset;
    (int S, int L, int C, int M) best = (0, 0, 0, 0);
    var bestKey = (-1, -1, -1, -1);
    for (var s = 0; s <= 9; s++)
        for (var l = 0; l <= 2; l++)
            for (var c = 0; c <= 2; c++)
                for (var m = 0; m <= 2; m++)
                {
                    var total = s * cost[soldier] + l * cost[lancer] + c * cost[rider] + m * cost[mage];
                    if (total > points)
                        continue;
                    // Priorité : budget pile, puis le plus de points dépensés, puis la variété, puis le nombre de pièces.
                    var variety = (l > 0 ? 1 : 0) + (c > 0 ? 1 : 0) + (m > 0 ? 1 : 0) + Math.Min(l, Math.Min(c, m));
                    var key = (total == points ? 1 : 0, total, variety, s + l + c + m);
                    if (key.CompareTo(bestKey) > 0) { bestKey = key; best = (s, l, c, m); }
                }

    var build = new PvpBuild { CommanderId = commander };
    int[] frontOrder = { 3, 4, 2, 5, 1, 6, 0, 7 };   // centrés sur le milieu du plateau
    for (var i = 0; i < Math.Min(best.S, 8); i++)
        build.Set(frontOrder[i], PvpBuild.FrontLine, soldier);
    // Pièces du fond posées du centre vers les bords, en alternant autour du roi (colonne 4) : armée centrée.
    int[] backOrder = { 3, 5, 2, 6, 1, 7, 0 };
    var next = 0;
    if (best.S > 8)
        build.Set(backOrder[next++], PvpBuild.BackLine, soldier);
    for (var i = 0; i < best.M; i++) build.Set(backOrder[next++], PvpBuild.BackLine, mage);
    for (var i = 0; i < best.L; i++) build.Set(backOrder[next++], PvpBuild.BackLine, lancer);
    for (var i = 0; i < best.C; i++) build.Set(backOrder[next++], PvpBuild.BackLine, rider);
    return build;
}

static IEnumerable<string> Line(PvpBuild build, int line) =>
    Enumerable.Range(0, PvpBuild.Columns).Select(c =>
        PvpBuild.IsKingCell(c, line) ? "*" : build.SlotAt(c, line)?.ClassAsset ?? "");

// Écrit « startingArmy » (même format que les armées des bots), avant « startingCommanders » s'il n'existe pas encore.
static string ReplaceStartingArmy(string text, PvpBuild army)
{
    static string Arr(IEnumerable<string> cells) => "[ " + string.Join(", ", cells.Select(c => $"\"{c}\"")) + " ]";
    var block = "{ \"commander\": \"" + army.CommanderId + "\",\n    \"back\":  " + Arr(Line(army, PvpBuild.BackLine))
                + ",\n    \"front\": " + Arr(Line(army, PvpBuild.FrontLine)) + " }";
    var at = text.IndexOf("\"startingArmy\"", StringComparison.Ordinal);
    if (at < 0)
    {
        var anchor = text.IndexOf("  \"startingCommanders\"", StringComparison.Ordinal);
        return anchor < 0 ? text : text[..anchor] + "  // Armée de DÉPART (écrite par l'outil d'équilibrage) : premier build de tout nouveau profil.\n"
                                   + "  \"startingArmy\": " + block + ",\n" + text[anchor..];
    }
    var open = text.IndexOf('{', at);
    var depth = 0;
    var close = open;
    for (; close < text.Length; close++)
    {
        if (text[close] == '{') depth++;
        else if (text[close] == '}' && --depth == 0) break;
    }
    return text[..open] + block + text[(close + 1)..];
}

// ── Ajustement des STATS (multi) ─────────────────────────────────────────────
//
// Ramène toutes les pièces dans une fourchette de coûts (3 à 8 par défaut) en modifiant leurs PV et dégâts MULTI
// (pvp.json « unitStats » ; le solo ne bouge pas). L'ordre de force est conservé : la mesure de départ est COMPRESSÉE
// linéairement (la plus faible reste au minimum, la plus forte vise le maximum). Puis, tour après tour : on mesure
// chaque pièce en vraies batailles, on corrige PV et dégâts du facteur √(cible / mesure), on remesure.
void TuneStats()
{
    var minCost = ArgInt("--min-cost", 3);
    var maxCost = ArgInt("--max-cost", 8);
    var budget = ArgInt("--budget", 35);
    var battles = ArgInt("--battles", 30000);
    var rounds = ArgInt("--rounds", 4);

    var classes = PvpCatalog.AllUnitClasses().ToList();
    var assets = classes.Select(c => c.Class.Asset).ToList();
    // Stats de travail : celles du multi actuel (overrides compris).
    var hp = classes.ToDictionary(c => c.Class.Asset, c => c.Class.MaxHp);
    var dmg = classes.ToDictionary(c => c.Class.Asset, c => c.Class.Damage);
    var baseConfig = PvpConfig.Current;
    PvpConfig.Load(baseConfig.WithUnitStats(new Dictionary<string, PvpUnitStats>()));
    var soloHp = PvpCatalog.AllUnitClasses().ToDictionary(c => c.Class.Asset, c => c.Class.MaxHp);
    var soloDmg = PvpCatalog.AllUnitClasses().ToDictionary(c => c.Class.Asset, c => c.Class.Damage);
    PvpConfig.Load(baseConfig);
    Dictionary<string, double>? target = null;

    // Recherche par DICHOTOMIE sur un facteur de force par pièce (PV et dégâts = stats du SOLO × facteur) : la valeur
    // d'une pièce saute aux seuils de dégâts (tuer en un coup ou non), une correction directe ferait le yoyo. On
    // encadre le bon facteur entre « trop fort » et « trop faible », on resserre, et on garde à la fin le réglage
    // MESURÉ le plus proche de la cible. Le Soldat (étalon) ne bouge jamais.
    var soldier = Domaines.Dame.BaseClass.Asset;
    var factor = assets.ToDictionary(a => a, _ => 1.0);
    var lo = new Dictionary<string, double>();   // facteur connu « trop faible »
    var hi = new Dictionary<string, double>();   // facteur connu « trop fort »
    var best = new Dictionary<string, (int Hp, int Dmg, double Value, double Gap)>();
    int HpOf(string a) => Math.Max((int)Math.Ceiling(soloHp[a] * 0.4), (int)Math.Round(soloHp[a] * factor[a]));
    int DmgOf(string a) => Math.Max((int)Math.Ceiling(soloDmg[a] * 0.4), (int)Math.Round(soloDmg[a] * factor[a]));

    for (var round = 1; round <= rounds; round++)
    {
        foreach (var a in assets)
        {
            hp[a] = HpOf(a);
            dmg[a] = DmgOf(a);
        }
        // Applique les stats de travail (en mémoire), en gardant déplacement / portée / traits éventuels du multi.
        var stats = assets.ToDictionary(a => a, a =>
        {
            var previous = baseConfig.UnitStats.GetValueOrDefault(a);
            return new PvpUnitStats(hp[a], dmg[a], previous?.MoveRange, previous?.AttackRange, previous?.Traits);
        });
        PvpConfig.Load(baseConfig.WithUnitStats(stats));
        var values = MeasureUnits(budget, minCost, battles, round);

        target ??= Compress(values, minCost, maxCost);
        Console.WriteLine($"Tour {round} : mesure ({clock.Elapsed:mm\\:ss})");
        foreach (var a in assets.OrderBy(a => target[a]))
            Console.WriteLine($"  {PvpCatalog.FindClass(a)!.Value.Class.Name,-22} PV {hp[a],3}  dégâts {dmg[a],3}   mesuré {values[a],5:0.0}   cible {target[a],4:0.0}");

        foreach (var a in assets)
        {
            var gap = Math.Abs(values[a] - target[a]);
            if (!best.TryGetValue(a, out var b) || gap < b.Gap)
                best[a] = (hp[a], dmg[a], values[a], gap);
            if (a == soldier || gap < 0.35 || values[a] <= 0)
                continue;
            if (values[a] > target[a]) hi[a] = factor[a];
            else lo[a] = factor[a];
            factor[a] = lo.ContainsKey(a) && hi.ContainsKey(a)
                ? (lo[a] + hi[a]) / 2
                : Math.Clamp(factor[a] * Math.Sqrt(target[a] / values[a]), 0.4, 1.6);
        }
    }

    // Réglage retenu pour chaque pièce : le plus proche de sa cible parmi tous ceux qui ont été mesurés.
    foreach (var a in assets)
    {
        hp[a] = best[a].Hp;
        dmg[a] = best[a].Dmg;
    }
    Console.WriteLine();
    Console.WriteLine("RETENU (meilleur réglage mesuré)");
    foreach (var a in assets.OrderBy(a => target![a]))
        Console.WriteLine($"  {PvpCatalog.FindClass(a)!.Value.Class.Name,-22} PV {hp[a],3}  dégâts {dmg[a],3}   mesuré {best[a].Value,5:0.0}   cible {target![a],4:0.0}");

    if (dryRun)
        return;
    // Écrit les stats multi de toutes les pièces qui diffèrent de units.json.
    var solo = PvpCatalog.AllUnitClasses().ToDictionary(c => c.Class.Asset, c => c.Class);
    PvpConfig.Load(baseConfig.WithUnitStats(new Dictionary<string, PvpUnitStats>()));
    var soloStats = PvpCatalog.AllUnitClasses().ToDictionary(c => c.Class.Asset, c => (c.Class.MaxHp, c.Class.Damage));
    var lines = assets.Where(a => soloStats[a] != (hp[a], dmg[a]))
        .OrderBy(a => a, StringComparer.Ordinal)
        .Select(a => $"    \"{a}\": {{ \"hp\": {hp[a]}, \"damage\": {dmg[a]} }}   // solo : {soloStats[a].MaxHp} / {soloStats[a].Damage}")
        .ToList();
    var block = "{\n" + string.Join(",\n", lines.Select((l, i) => l)) + "\n  }";
    // Les virgules doivent précéder les commentaires de fin de ligne.
    block = "{\n" + string.Join("\n", lines.Select((l, i) =>
    {
        var cut = l.IndexOf("   //", StringComparison.Ordinal);
        return i + 1 < lines.Count ? l[..cut] + "," + l[cut..] : l;
    })) + "\n  }";
    var text = File.ReadAllText(pvpPath);
    var at = text.IndexOf("\"unitStats\"", StringComparison.Ordinal);
    var open = text.IndexOf('{', at);
    var depth = 0;
    var close = open;
    for (; close < text.Length; close++)
    {
        if (text[close] == '{') depth++;
        else if (text[close] == '}' && --depth == 0) break;
    }
    File.WriteAllText(pvpPath, text[..open] + block + text[(close + 1)..], new UTF8Encoding(false));
    Console.WriteLine($"Stats multi écrites dans {pvpPath} ({lines.Count} pièces). Relancez ensuite l'équilibrage pour les coûts.");
}

// Cibles : la mesure compressée linéairement dans [min, max] (la plus faible au min, la plus forte au max).
static Dictionary<string, double> Compress(Dictionary<string, double> values, int min, int max)
{
    // Ancrée sur le minimum (le Soldat, étalon) : une pièce mesurée au minimum ou en dessous y reste.
    var hi = values.Values.Max();
    return values.ToDictionary(kv => kv.Key, kv => hi <= min || kv.Value <= min ? Math.Max(min, kv.Value)
        : min + (kv.Value - min) * (max - min) / (hi - min));
}

// Une passe de vraies batailles ; valeur de chaque pièce à l'échelle « pièce la plus faible = minCost ».
Dictionary<string, double> MeasureUnits(int budget, int minCost, int battles, int seed)
{
    var classes = PvpCatalog.AllUnitClasses().Select(c => c.Class.Asset).ToList();
    var equipment = PvpCatalog.AllEquipment().Select(e => e.Id).ToList();
    var commanders = PvpCatalog.Commanders().Select(c => c.Id).ToList();
    var featureCount = classes.Count + equipment.Count + commanders.Count + 1;
    var units = classes.Select(a => (a, Math.Max(minCost, PvpConfig.Current.UnitCost.GetValueOrDefault(a, minCost)))).ToList();
    var items = equipment.Select(e => (e, PvpConfig.Current.EquipmentCost.GetValueOrDefault(e, 1))).ToList();
    var cmds = commanders.Select(c => (c, PvpConfig.Current.CommanderCostOf(c))).ToList();
    var samples = new (double[] X, double Y)[battles];
    Parallel.For(0, battles, i =>
    {
        var rng = new Random(seed * 1_000_003 + i * 7919);
        var a = PvpBalance.RandomArmy(rng, rng.Next(budget * 2 / 3, budget * 4 / 3 + 1), cmds, units, items, 0.35);
        var b = PvpBalance.RandomArmy(rng, rng.Next(budget * 2 / 3, budget * 4 / 3 + 1), cmds, units, items, 0.35);
        var aStarts = i % 2 == 0;
        var y = PvpBalance.PlayBattle(a, b, rng.Next(), aStarts);
        var x = new double[featureCount];
        void Add(PvpBuild army, int sign)
        {
            foreach (var slot in army.Slots)
            {
                x[classes.IndexOf(slot.ClassAsset)] += sign;
                if (slot.EquipmentId != null)
                    x[classes.Count + equipment.IndexOf(slot.EquipmentId)] += sign;
            }
            x[classes.Count + equipment.Count + commanders.IndexOf(army.CommanderId)] += sign;
        }
        Add(a, +1);
        Add(b, -1);
        x[^1] = aStarts ? 1 : -1;
        samples[i] = (x, y);
    });
    var w = PvpBalance.FitLogistic(samples, featureCount);
    // Étalon FIXE : le Soldat vaut minCost (ses stats ne bougent jamais) ; sinon la cible dériverait d'un tour à l'autre.
    var anchor = w[classes.IndexOf(Domaines.Dame.BaseClass.Asset)];
    var scale = anchor > 0 ? minCost / anchor : 1;
    return classes.ToDictionary(a => a, a => w[classes.IndexOf(a)] * scale);
}

// ── Outils ───────────────────────────────────────────────────────────────────

static int UnitCost(double value) => Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));
static int EquipCost(double value) => Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));

string ArgString(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
}

int ArgInt(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

static string FindConfigDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
        dir = dir.Parent;
    return dir == null
        ? throw new DirectoryNotFoundException("Racine du dépôt introuvable (src/ChessArmy.Game).")
        : Path.Combine(dir.FullName, "src", "ChessArmy.Game", "Assets", "Config");
}

static string ReplaceNumber(string text, string key, int value)
{
    var at = text.IndexOf($"\"{key}\"", StringComparison.Ordinal);
    if (at < 0) return text;
    var colon = text.IndexOf(':', at);
    var end = colon + 1;
    while (end < text.Length && (char.IsWhiteSpace(text[end]) || char.IsDigit(text[end]))) end++;
    return text[..(colon + 1)] + " " + value + text[end..].TrimStart(' ');
}

// Remplace l'objet JSON de la clé par un objet trié, une entrée par ligne, avec la valeur mesurée en commentaire.
static string ReplaceObject(string text, string key, Dictionary<string, int> costs, Dictionary<string, double> measured)
{
    var sb = new StringBuilder("{\n");
    var ordered = costs.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
    for (var i = 0; i < ordered.Count; i++)
    {
        var (id, cost) = (ordered[i].Key, ordered[i].Value);
        var note = measured.TryGetValue(id, out var v) ? $"   // mesuré : {v.ToString("0.00", CultureInfo.InvariantCulture)}" : "";
        sb.Append($"    \"{id}\": {cost}{(i + 1 < ordered.Count ? "," : "")}{note}\n");
    }
    sb.Append("  }");

    var at = text.IndexOf($"\"{key}\"", StringComparison.Ordinal);
    if (at < 0) return text;
    var open = text.IndexOf('{', at);
    var depth = 0;
    var close = open;
    for (; close < text.Length; close++)
    {
        if (text[close] == '{') depth++;
        else if (text[close] == '}' && --depth == 0) break;
    }
    return text[..open] + sb + text[(close + 1)..];
}

// ── Parties qui s'éternisent (diagnostic, --stall) ───────────────────────────
//
// 1) Batailles complètes au même budget, commandants tirés au hasard : par commandant, part des parties qui
//    atteignent la limite de coups (personne n'a tué l'autre commandant), nulles, victoires, durée moyenne.
// 2) Poursuite : le commandant SEUL contre le commandant de départ + 4 pièces au hasard. Part des poursuites
//    où le camp fort tue le commandant seul avant --chase-plies coups.
void StallReport(int battles, int chases, int maxPlies, int chasePlies)
{
    var budget = PvpConfig.Current.Budget;
    var commanders = PvpCatalog.Commanders().Select(c => c.Id).ToList();
    var units = PvpCatalog.AllUnitClasses().Select(c => (c.Class.Asset, PvpConfig.Current.UnitCost.GetValueOrDefault(c.Class.Asset, PvpConfig.DefaultCost))).ToList();
    var items = PvpCatalog.AllEquipment().Select(e => (e.Id, PvpConfig.Current.EquipmentCost.GetValueOrDefault(e.Id, 1))).ToList();
    var noItems = new List<(string, int)>();

    // (commandant, gagne, perd, nulle, limite, coups, coups du commandant, coups joués par ce camp)
    var stats = commanders.ToDictionary(c => c, _ => new long[8]);
    var gate = new object();
    var limitKinds = new long[3];   // à la limite : commandants seuls, un camp sans pièce, pièces des deux côtés
    Console.WriteLine($"Batailles : {battles}, budget {budget}, limite {maxPlies} coups.");
    Parallel.For(0, battles, i =>
    {
        var rng = new Random(91 + i * 7919);
        var ca = commanders[rng.Next(commanders.Count)];
        var cb = commanders[rng.Next(commanders.Count)];
        var a = PvpBalance.RandomArmy(rng, budget, new[] { (ca, PvpConfig.Current.CommanderCostOf(ca)) }, units, items, 0.35);
        var b = PvpBalance.RandomArmy(rng, budget, new[] { (cb, PvpConfig.Current.CommanderCostOf(cb)) }, units, items, 0.35);
        var (winner, draw, plies, cmdMoves, left) = PlayCounted(a, b, rng.Next(), i % 2 == 0, maxPlies);
        lock (gate)
        {
            Record(stats[ca], Faction.Player, winner, draw, plies, cmdMoves[0], maxPlies);
            Record(stats[cb], Faction.Enemy, winner, draw, plies, cmdMoves[1], maxPlies);
            if (winner == null && !draw)
                limitKinds[left[0] == 0 && left[1] == 0 ? 0 : left[0] == 0 || left[1] == 0 ? 1 : 2]++;
        }
    });

    static void Record(long[] s, Faction me, Faction? winner, bool draw, int plies, int cmdMoves, int maxPlies)
    {
        if (draw) s[3]++;
        else if (winner == me) s[1]++;
        else if (winner != null) s[2]++;
        else s[4]++;
        s[0]++;
        s[5] += plies;
        s[6] += cmdMoves;
        s[7] += (plies + 1) / 2;
    }

    Console.WriteLine();
    Console.WriteLine($"  {"Commandant",-22} {"parties",7} {"gagne",6} {"perd",6} {"nulle",6} {"limite",7} {"coups moy.",10} {"coups du cdt",12}");
    foreach (var c in commanders.OrderByDescending(c => stats[c][4] / (double)Math.Max(1, stats[c][0])))
    {
        var s = stats[c];
        double P(long v) => 100.0 * v / Math.Max(1, s[0]);
        Console.WriteLine($"  {c,-22} {s[0],7} {P(s[1]),5:0}% {P(s[2]),5:0}% {P(s[3]),5:0}% {P(s[4]),6:0.0}% {s[5] / (double)Math.Max(1, s[0]),10:0} {100.0 * s[6] / Math.Max(1, s[7]),11:0}%");
    }
    var limitTotal = Math.Max(1, limitKinds.Sum());
    Console.WriteLine($"  À la limite ({limitKinds.Sum()} parties) : commandants seuls {100.0 * limitKinds[0] / limitTotal:0}%, "
                      + $"un camp sans pièce {100.0 * limitKinds[1] / limitTotal:0}%, pièces des deux côtés {100.0 * limitKinds[2] / limitTotal:0}%");

    Console.WriteLine();
    Console.WriteLine($"Poursuite : commandant SEUL contre {PvpConfig.Current.StartingCommanders.FirstOrDefault() ?? "commandant"} + 4 pièces, {chases} par commandant, limite {chasePlies} coups.");
    var hunter = PvpConfig.Current.StartingCommanders.FirstOrDefault() ?? "commandant";
    foreach (var c in commanders)
    {
        long caught = 0, totalPlies = 0, counter = 0, wiped = 0;
        Parallel.For(0, chases, i =>
        {
            var rng = new Random(17 + i * 104729);
            var strong = PvpBalance.RandomArmy(rng, 4 * 6, new[] { (hunter, 0) }, units.Where(u => u.Item2 <= 6).ToList(), noItems, 0);
            while (strong.Slots.Count > 4)
                strong.Remove(strong.Slots[^1].Column, strong.Slots[^1].Line);
            var lone = new PvpBuild { CommanderId = c };
            var (winner, _, plies, _, left) = PlayCounted(strong, lone, rng.Next(), i % 2 == 0, chasePlies);
            if (winner == null && left[0] == 0) Interlocked.Increment(ref wiped);
            if (winner == Faction.Player)
            {
                Interlocked.Increment(ref caught);
                Interlocked.Add(ref totalPlies, plies);
            }
            else if (winner == Faction.Enemy)
                Interlocked.Increment(ref counter);
        });
        Console.WriteLine($"  {c,-22} attrapé {100.0 * caught / chases,5:0.0}%   (en {totalPlies / (double)Math.Max(1, caught):0} coups en moyenne)"
                          + $"   gagne seul {100.0 * counter / chases,5:0.0}%   chasseurs tous morts {100.0 * wiped / chases,5:0.0}%   s'échappe vraiment {100.0 * (chases - caught - counter - wiped) / chases,5:0.0}%");
    }
    Console.WriteLine($"Durée : {clock.Elapsed:mm\\:ss}");
}

static (Faction? Winner, bool Draw, int Plies, int[] CommanderMoves, int[] Left) PlayCounted(PvpBuild a, PvpBuild b, int seed, bool aStarts, int maxPlies)
{
    var rng = new Random(seed);
    var match = PvpSetup.CreateMatch(mirrored: false, aStarts, seed);
    PvpSetup.PlaceArmy(match, a, Faction.Player);
    PvpSetup.PlaceArmy(match, b, Faction.Enemy);
    var cmd = new int[2];
    var ply = 0;
    for (; ply < maxPlies && !match.IsOver; ply++)
    {
        var actor = match.CurrentTurn;
        var action = PvpBotAi.Choose(match, actor, 1.0, rng) ?? BattleAction.Pass;
        if (match.UnitAt(action.From) is { IsEssential: true })
            cmd[actor == Faction.Player ? 0 : 1]++;
        if (match.Apply(action) == MoveKind.Invalid)
            match.Apply(BattleAction.Pass);
        if (!match.IsOver && match.CurrentTurn != actor)
            match.ApplySeismes(match.CurrentTurn);
        PvpSetup.ResolvePending(match);
    }
    var left = new int[2];
    foreach (var (_, u) in match.Units())
        if (!u.IsEssential && u.Class.Asset != "Basile")
            left[u.Faction == Faction.Player ? 0 : 1]++;
    return (match.Winner, match.IsDraw, ply, cmd, left);
}
