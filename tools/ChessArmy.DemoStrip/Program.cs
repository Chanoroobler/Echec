using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChessArmy.Core.Battle.Config;
using ChessArmy.Core.Campaign;
using ChessArmy.Core.Command.Config;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Map;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// ÉPURATION DU DÉPÔT DÉMO. Le symbole DEMO bride l'accès en code, mais un DLL .NET se patche (dnSpy) :
// sans cette étape, retourner IsDemo suffirait à jouer au jeu complet, tout le contenu étant livré. Ici on
// RETIRE du dossier publié ce que la démo ne doit jamais atteindre : un binaire patché tombe alors sur des
// données vides. On garde l'IDENTITÉ (nom + asset → PNG) de ce qui sert de vitrine (silhouettes du codex,
// commandants « DANS LE JEU COMPLET »), mais plus aucune stat, trait, arbre ni map jouable.
//
// Usage : ChessArmy.DemoStrip <dossier publié>   (lancé par la cible StripDemoContent de ChessArmy.Game)
// Code de sortie ≠ 0 si le résultat ne passe pas la validation : la publication démo échoue alors.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────

// Bornes de la démo : DOIVENT rester alignées sur ChessArmyGame.DemoEndAtPhase / DemoMaxUnitTier.
const int EndAtPhase = 2;
const int MaxUnitTier = 2;
const string DefaultTree = "commandant";   // = CommandTrees.DefaultTreeId (repli d'un commandant sans "tree")

Console.OutputEncoding = Encoding.UTF8;   // accents lisibles dans la sortie de dotnet publish

if (args.Length != 1 || !Directory.Exists(Path.Combine(args[0], "Assets")))
{
    Console.Error.WriteLine("Usage : ChessArmy.DemoStrip <dossier publié contenant Assets/>");
    return 2;
}

var assets = Path.Combine(args[0], "Assets");
var config = Path.Combine(assets, "Config");
var unitsPath = Path.Combine(config, "units.json");
var equipmentPath = Path.Combine(config, "equipment.json");
var treesPath = Path.Combine(config, "commander_trees.json");
var campaignPath = Path.Combine(config, "campaign.json");

var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
var nodeOptions = new JsonNodeOptions { PropertyNameCaseInsensitive = true };   // comme les parseurs du jeu
var writeOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

JsonObject Load(string path) =>
    JsonNode.Parse(File.ReadAllText(path), nodeOptions, docOptions)?.AsObject()
    ?? throw new InvalidDataException($"{path} vide ou illisible.");

void Save(string path, JsonNode node) =>
    // Réécrit SANS les commentaires (qui documentent le contenu complet) : rien à gagner à les livrer.
    File.WriteAllText(path, node.ToJsonString(writeOptions), new UTF8Encoding(false));

// Ne garde que les clés listées (la vitrine) : tout le reste (stats, traits, points, arbre...) disparaît.
void KeepOnly(JsonObject obj, params string[] keep)
{
    foreach (var key in obj.Select(p => p.Key).ToList())
        if (!keep.Contains(key, StringComparer.OrdinalIgnoreCase))
            obj.Remove(key);
}

JsonArray ArrayOf(JsonObject obj, string key) => obj[key] as JsonArray ?? new JsonArray();

string? Str(JsonNode? node, string key) =>
    node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

bool? Bool(JsonNode? node, string key) =>
    node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

var log = new List<string>();

// ── 1. Équipements ───────────────────────────────────────────────────────────────────────────────────
// "demo": false → vitrine du codex seulement (id/nom/rareté/icône), effet neutre (+0 PV) pour rester lisible.
var equipment = Load(equipmentPath);
var demoEquipment = new HashSet<string>(StringComparer.Ordinal);
var strippedEquipment = 0;
foreach (var item in ArrayOf(equipment, "equipments").OfType<JsonObject>())
{
    if (Bool(item, "demo") != false)
    {
        if (Str(item, "id") is { } id)
            demoEquipment.Add(id);
        continue;
    }
    KeepOnly(item, "id", "name", "rarity", "icon", "demo");
    item["stat"] = "Hp";
    item["amount"] = 0;
    item["enemyAllowed"] = false;
    strippedEquipment++;
}
Save(equipmentPath, equipment);
log.Add($"équipements : {strippedEquipment} hors démo vidés, {demoEquipment.Count} gardés");

// ── 2. Commandants jouables en démo (= CommanderSelectScene.IsUnlocked en démo) ──────────────────────
var units = Load(unitsPath);
var commandes = ArrayOf(units, "commandes");
bool IsRole(JsonNode? c, string role) => string.Equals(Str(c, "role"), role, StringComparison.OrdinalIgnoreCase);
var playable = commandes
    .Where(c => IsRole(c, "Commander") && (Bool(c, "unlocked") != false || Bool(c, "demoUnlocked") == true))
    .ToList();
var playableIds = playable.Select(c => Str(c, "id")).OfType<string>().ToHashSet(StringComparer.Ordinal);
var keptTrees = playable.Select(c => Str(c, "tree") ?? DefaultTree).ToHashSet(StringComparer.Ordinal);
var keptCompanions = playable.Select(c => Str(c, "companion")).OfType<string>().ToHashSet(StringComparer.Ordinal);

// ── 3. Arbres de commandement : seuls ceux des commandants jouables ──────────────────────────────────
var trees = Load(treesPath);
var treeList = ArrayOf(trees, "trees");
for (var i = treeList.Count - 1; i >= 0; i--)
    if (Str(treeList[i], "id") is not { } id || !keptTrees.Contains(id))
        treeList.RemoveAt(i);
Save(treesPath, trees);
log.Add($"arbres : gardés {string.Join(", ", keptTrees)}");

// Classes exclusives amenées par un arbre GARDÉ (effet recruitExclusive) : les autres sont hors démo.
var keptExclusives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
void CollectExclusives(JsonNode? node)
{
    switch (node)
    {
        case JsonObject o:
            if (string.Equals(Str(o, "kind"), "recruitExclusive", StringComparison.OrdinalIgnoreCase) && Str(o, "asset") is { } a)
                keptExclusives.Add(a);
            foreach (var (_, child) in o)
                CollectExclusives(child);
            break;
        case JsonArray arr:
            foreach (var child in arr)
                CollectExclusives(child);
            break;
    }
}
CollectExclusives(treeList);

// ── 4. Classes : au-dessus du plafond de tier, identité seule (nom + asset → PNG de la silhouette) ────
var strippedClasses = 0;
void StripTree(JsonObject? cls, int tier, bool stripAll)
{
    if (cls is null)
        return;
    if (stripAll || tier > MaxUnitTier)
    {
        KeepOnly(cls, "name", "asset", "evolutions");
        strippedClasses++;
    }
    foreach (var evo in ArrayOf(cls, "evolutions").OfType<JsonObject>())
        StripTree(evo, tier + 1, stripAll);
}
foreach (var d in ArrayOf(units, "domaines").OfType<JsonObject>())
    StripTree(d["baseClass"] as JsonObject, 1, stripAll: false);
foreach (var d in ArrayOf(units, "exclusives").OfType<JsonObject>())
    StripTree(d["baseClass"] as JsonObject, 1, stripAll: Str(d["baseClass"], "asset") is not { } a || !keptExclusives.Contains(a));
log.Add($"classes : {strippedClasses} vidées (tier > {MaxUnitTier} ou exclusives hors démo)");

// ── 5. Commandes : commandants/compagnons hors démo en vitrine, boss limités aux phases de la démo ─────
var strippedCommanders = 0;
var droppedBosses = 0;
for (var i = commandes.Count - 1; i >= 0; i--)
{
    if (commandes[i] is not JsonObject c)
        continue;
    if (IsRole(c, "Commander") && !playableIds.Contains(Str(c, "id") ?? ""))
    {
        KeepOnly(c, "role", "id", "domaine", "name", "asset", "difficulty");
        c["unlocked"] = false;
        strippedCommanders++;
    }
    else if (IsRole(c, "Companion") && !keptCompanions.Contains(Str(c, "id") ?? ""))
    {
        KeepOnly(c, "role", "id", "domaine", "name", "asset");
        strippedCommanders++;
    }
    else if (IsRole(c, "Boss"))
    {
        // Boss miroir d'un commandant hors démo : il ne doit jamais sortir en démo.
        if (Str(c, "requiresCommander") is { } need && !playableIds.Contains(need))
        {
            commandes.RemoveAt(i);
            droppedBosses++;
            continue;
        }
        foreach (var key in new[] { "phases", "companionPhases" })
            if (c[key] is JsonObject phases)
                foreach (var phase in phases.Select(p => p.Key).ToList())
                    if (!int.TryParse(phase, out var n) || n > EndAtPhase)
                        phases.Remove(phase);
        if (c["equipmentPool"] is JsonArray pool)
        {
            for (var j = pool.Count - 1; j >= 0; j--)
                if (pool[j]?.GetValue<string>() is not { } id || !demoEquipment.Contains(id))
                    pool.RemoveAt(j);
            if (pool.Count == 0)   // pool vide + equipmentCount = rejeté par le parseur : on retire les deux
            {
                c.Remove("equipmentPool");
                if (c["phases"] is JsonObject ph)
                    foreach (var (_, p) in ph)
                        (p as JsonObject)?.Remove("equipmentCount");
            }
        }
    }
}
Save(unitsPath, units);
log.Add($"commandes : {strippedCommanders} commandants/compagnons en vitrine, {droppedBosses} boss retirés, profils > phase {EndAtPhase} retirés");

// ── 6. Maps : ne restent que celles que les phases 1..EndAtPhase peuvent tirer ───────────────────────
var catalog = TileCatalog.FromJson(File.ReadAllText(Path.Combine(assets, "Tiles", "tiles.json")));
IReadOnlyList<IReadOnlyList<MissionSetup>> plan;
try { plan = CampaignPlan.FromJson(File.ReadAllText(campaignPath)); }
catch { plan = CampaignPlan.Defaults(); }
// Tailles d'escarmouche des phases de la démo (les 9×9/10×10 n'entrent au pool qu'en phase 3).
var demoSizes = plan.Take(EndAtPhase).SelectMany(p => p).Select(m => m.MapSize).ToHashSet();

var keptMaps = new List<MapData>();
var removedMaps = new List<string>();
foreach (var file in Directory.GetFiles(Path.Combine(assets, "Maps"), "*.json"))
{
    MapData? map = null;
    try { map = MapLoader.Parse(File.ReadAllText(file), catalog); } catch { /* illisible : ignorée par le jeu */ }
    var reason = map is null ? "illisible"
        : map.IsDraft ? "brouillon"
        : map.IsIceThemed ? "glace"
        : map.Phase > EndAtPhase ? $"phase {map.Phase}"
        : map.Type == CombatType.Escarmouche && !demoSizes.Contains(map.PoolSize) ? $"{map.PoolSize}×{map.PoolSize}"
        : null;
    if (reason is null)
    {
        keptMaps.Add(map!);
        continue;
    }
    File.Delete(file);
    removedMaps.Add($"{Path.GetFileNameWithoutExtension(file)} [{reason}]");
}
log.Add($"maps : {keptMaps.Count} gardées, {removedMaps.Count} retirées ({string.Join(", ", removedMaps)})");

// ── 7. Validation : le dépôt épuré doit rester une démo JOUABLE (mêmes parseurs que le jeu) ──────────
var errors = new List<string>();
var warnings = new List<string>();
try
{
    var unitsJson = File.ReadAllText(unitsPath);
    var domaines = DomaineCatalog.FromJson(unitsJson);
    DomaineCatalog.ExclusivesFromJson(unitsJson);
    var cmds = DomaineCatalog.CommandesFromJson(unitsJson);
    var bosses = DomaineCatalog.BossesFromJson(unitsJson);
    var treeIds = CommandTreeCatalog.FromJson(File.ReadAllText(treesPath)).Select(t => t.Id).ToHashSet();
    EquipmentCatalog.FromJson(File.ReadAllText(equipmentPath));

    if (!treeIds.Contains(DefaultTree))
        errors.Add($"arbre de repli '{DefaultTree}' absent");
    foreach (var c in cmds.Where(c => c.Role == ChessArmy.Core.Battle.CommandeRole.Commander && (c.StartsUnlocked || c.DemoUnlocked)))
        if (!treeIds.Contains(c.TreeId))
            errors.Add($"commandant jouable '{c.Id}' : arbre '{c.TreeId}' absent");
    if (!cmds.Any(c => c.Role == ChessArmy.Core.Battle.CommandeRole.Commander && (c.StartsUnlocked || c.DemoUnlocked)))
        errors.Add("aucun commandant jouable en démo");

    void CheckClass(ChessArmy.Core.Battle.UnitClass cls)
    {
        if (cls.Tier > MaxUnitTier && (cls.MaxHp > 0 || cls.Damage > 0 || cls.Traits.Count > 0))
            errors.Add($"classe '{cls.Name}' (tier {cls.Tier}) garde des stats");
        foreach (var evo in cls.Evolutions)
            CheckClass(evo);
    }
    foreach (var d in domaines)
        CheckClass(d.BaseClass);

    for (var phase = 1; phase <= EndAtPhase; phase++)
    {
        if (!bosses.Any(b => b.SupportsPhase(phase)))
            errors.Add($"aucun boss pour la phase {phase}");
        if (!keptMaps.Any(m => m.Type == CombatType.Boss && (m.Phase == phase || m.Phase == 0)))
            errors.Add($"aucune map de boss pour la phase {phase}");
        var specials = keptMaps.Count(m => m.Type == CombatType.Speciale && (m.Phase == phase || m.Phase == 0));
        if (specials < 2)
            warnings.Add($"phase {phase} : {specials} map(s) spéciale(s) (le choix en propose 2)");
    }
    foreach (var size in demoSizes)
        if (!keptMaps.Any(m => m.Type == CombatType.Escarmouche && m.PoolSize == size))
            warnings.Add($"aucune escarmouche {size}×{size} (terrain aléatoire en repli)");
}
catch (Exception ex)
{
    errors.Add($"données épurées illisibles par le jeu : {ex.Message}");
}

foreach (var line in log)
    Console.WriteLine($"DemoStrip : {line}");
foreach (var w in warnings)
    Console.WriteLine($"DemoStrip : ATTENTION {w}");
foreach (var e in errors)
    Console.Error.WriteLine($"DemoStrip : ERREUR {e}");
return errors.Count == 0 ? 0 : 1;
