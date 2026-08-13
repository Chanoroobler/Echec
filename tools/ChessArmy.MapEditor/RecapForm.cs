using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ChessArmy.Core.Campaign;
using ChessArmy.Core.Map;

namespace ChessArmy.MapEditor;

/// <summary>
/// Fenêtre « Récap » : état des lieux de TOUTES les maps d'<c>Assets/Maps</c>, groupées par type et phase,
/// suivi d'une analyse des manques. Rescane le dossier avec le MÊME parseur que le jeu
/// (<see cref="MapLoader.Parse"/>) — le récap reflète donc exactement ce que le jeu charge (brouillons exclus,
/// erreurs de format signalées). Croise avec <c>campaign.json</c> (<see cref="CampaignPlan"/> +
/// <see cref="Run.MissionKindAt"/>) pour connaître les tailles d'escarmouche attendues par phase.
///
/// Modeless : reste ouverte à côté de l'éditeur. Rafraîchie à l'ouverture, par le bouton « Rafraîchir » et
/// après chaque enregistrement (cf. <see cref="MainForm"/>).
/// </summary>
internal sealed class RecapForm : Form
{
    private readonly TileRenderCatalog _catalog;
    private readonly RichTextBox _text = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(30, 32, 38), ForeColor = Color.Gainsboro,
        WordWrap = false, ScrollBars = RichTextBoxScrollBars.Both, DetectUrls = false,
    };
    private readonly Label _summary = new()
    {
        Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 0, 0),
    };

    private readonly Font _mono = new("Consolas", 10f, FontStyle.Regular);
    private readonly Font _bold = new("Consolas", 10f, FontStyle.Bold);

    // Palette du rapport.
    private static readonly Color Fg = Color.Gainsboro;
    private static readonly Color Dim = Color.FromArgb(150, 152, 160);
    private static readonly Color Head = Color.FromArgb(120, 200, 255);
    private static readonly Color Ok = Color.FromArgb(120, 205, 130);
    private static readonly Color Warn = Color.FromArgb(235, 195, 90);
    private static readonly Color Bad = Color.FromArgb(240, 110, 100);

    public RecapForm(TileRenderCatalog catalog)
    {
        _catalog = catalog;
        Text = "Récapitulatif des maps — Chess Army";
        Width = 900;
        Height = 820;
        MinimumSize = new Size(560, 400);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(38, 40, 46);

        _text.Font = _mono;

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 32, 38),
            Padding = new Padding(6, 8, 6, 6), WrapContents = false, AutoScroll = false,
        };
        var refreshBtn = new Button
        {
            Text = "Rafraîchir", AutoSize = true, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 62, 70), ForeColor = Color.Gainsboro, Margin = new Padding(2, 0, 8, 0),
        };
        refreshBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 92, 100);
        refreshBtn.Click += (_, _) => RefreshReport();
        bar.Controls.Add(refreshBtn);
        bar.Controls.Add(_summary);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(bar, 0, 0);
        root.Controls.Add(_text, 0, 1);
        Controls.Add(root);

        RefreshReport();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _mono.Dispose();
        _bold.Dispose();
    }

    /// <summary>Rescane le dossier des maps et réécrit le rapport (conserve la position de défilement au mieux).</summary>
    public void RefreshReport()
    {
        LoadCampaignPlan();
        var (live, drafts, errors) = Scan();

        _text.SuspendLayout();
        _text.Clear();
        Render(live, drafts, errors);
        _text.Select(0, 0);
        _text.ScrollToCaret();
        _text.ResumeLayout();

        _summary.Text =
            $"{live.Count} maps chargées   ·   {drafts.Count} brouillon(s) exclu(s)   ·   {errors.Count} erreur(s)"
            + $"   ·   scanné à {DateTime.Now:HH:mm:ss}";
    }

    // ---------------------------------------------------------------- Collecte
    private sealed record Row(string File, MapData Data);

    /// <summary>
    /// Charge le plan de campagne livré (<c>campaign.json</c>) dans <see cref="CampaignPlan"/> pour que les
    /// tailles attendues par phase soient à jour. Silencieux en cas d'échec (garde le repli codé de Core).
    /// </summary>
    private static void LoadCampaignPlan()
    {
        try
        {
            if (File.Exists(AssetPaths.CampaignJson))
                CampaignPlan.Load(CampaignPlan.FromJson(File.ReadAllText(AssetPaths.CampaignJson)));
        }
        catch { /* campaign.json absent ou invalide : on garde les valeurs par défaut */ }
    }

    private (List<Row> live, List<Row> drafts, List<(string file, string error)> errors) Scan()
    {
        var live = new List<Row>();
        var drafts = new List<Row>();
        var errors = new List<(string, string)>();

        TileCatalog core;
        try { core = TileCatalog.FromJson(_catalog.RawJson); }
        catch (Exception ex) { errors.Add(("(catalogue de tuiles)", ex.Message)); return (live, drafts, errors); }

        if (!Directory.Exists(AssetPaths.MapsDir))
        {
            errors.Add((AssetPaths.MapsDir, "dossier de maps introuvable"));
            return (live, drafts, errors);
        }

        foreach (var path in Directory.EnumerateFiles(AssetPaths.MapsDir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            MapData data;
            try { data = MapLoader.Parse(File.ReadAllText(path), core); }
            catch (Exception ex) { errors.Add((Path.GetFileName(path), FirstLine(ex.Message))); continue; }
            var row = new Row(Path.GetFileName(path), data);
            (data.IsDraft ? drafts : live).Add(row);
        }
        return (live, drafts, errors);
    }

    // ---------------------------------------------------------------- Rendu
    private void Render(List<Row> live, List<Row> drafts, List<(string file, string error)> errors)
    {
        WL("RÉCAPITULATIF DES MAPS", Head, bold: true);
        WL(AssetPaths.MapsDir, Dim);
        WL();

        RenderTotals(live, drafts);
        WL();
        var escGaps = RenderEscarmouches(live);
        WL();
        var bossGaps = RenderBoss(live);
        WL();
        var specGaps = RenderSpeciales(live);
        WL();
        RenderTutoriel(live);
        WL();
        RenderGaps(escGaps, bossGaps, specGaps, drafts, errors, live);
    }

    private void RenderTotals(List<Row> live, List<Row> drafts)
    {
        WL("VUE D'ENSEMBLE", Head, bold: true);
        WL($"  {"Type",-14}{"Jouables",-10}Brouillons", Dim);
        foreach (var type in new[] { CombatType.Escarmouche, CombatType.Speciale, CombatType.Boss, CombatType.Tutoriel })
        {
            var j = live.Count(r => r.Data.Type == type);
            var d = drafts.Count(r => r.Data.Type == type);
            WL($"  {TypeLabel(type),-14}{j,-10}{(d == 0 ? "-" : d.ToString())}");
        }
    }

    /// <summary>
    /// ESCARMOUCHES : groupées par taille (carrées seulement — le jeu ne tire que des maps carrées). Renvoie la
    /// liste des tailles requises par <c>campaign.json</c> sans aucune map (manque bloquant).
    /// </summary>
    private List<(int phase, int size)> RenderEscarmouches(List<Row> live)
    {
        WL("ESCARMOUCHES (tirées par TAILLE, carrées uniquement)", Head, bold: true);

        var esc = live.Where(r => r.Data.Type == CombatType.Escarmouche).ToList();
        var square = esc.Where(r => r.Data.Width == r.Data.Height).ToList();

        // Tailles requises par phase : union des mapSize des missions Escarmouche du plan de campagne.
        var reqByPhase = RequiredEscarmoucheSizes();
        var haveSizes = square.Select(r => r.Data.Width).ToHashSet();

        WL($"  {"Taille",-9}{"Nb",-5}{"Requise en",-14}Maps", Dim);
        foreach (var size in square.Select(r => r.Data.Width).Distinct().OrderBy(s => s))
        {
            var maps = square.Where(r => r.Data.Width == size).ToList();
            var phases = reqByPhase.Where(kv => kv.Value.Contains(size)).Select(kv => kv.Key).ToList();
            var req = phases.Count > 0 ? "phase " + string.Join(",", phases)
                    : size is 9 or 10 ? "variété ph.3"
                    : "non utilisée";
            var reqColor = phases.Count > 0 ? Fg : size is 9 or 10 ? Dim : Warn;
            W($"  {size + "×" + size,-9}{maps.Count,-5}");
            W($"{req,-14}", reqColor);
            WL(string.Join(", ", maps.Select(MapName)), Dim);
        }

        // Manques : une taille requise par une phase sans aucune map carrée.
        var gaps = new List<(int phase, int size)>();
        foreach (var (phase, sizes) in reqByPhase)
            foreach (var size in sizes)
                if (!haveSizes.Contains(size))
                    gaps.Add((phase, size));
        return gaps;
    }

    /// <summary>Phase (1..3) → tailles d'escarmouche attendues, d'après le plan de campagne (missions Escarmouche).</summary>
    private static SortedDictionary<int, SortedSet<int>> RequiredEscarmoucheSizes()
    {
        var map = new SortedDictionary<int, SortedSet<int>>();
        for (var p = 1; p <= Run.PhaseCount; p++)
        {
            var set = new SortedSet<int>();
            for (var m = 1; m <= Run.MissionsPerPhase; m++)
                if (Run.MissionKindAt(p, m) == CombatType.Escarmouche)
                    set.Add(CampaignPlan.For(p, m).MapSize);
            map[p] = set;
        }
        return map;
    }

    /// <summary>BOSS : groupés par phase (le jeu les tire par phase, la taille venant de la map). Renvoie les phases sans map boss.</summary>
    private List<int> RenderBoss(List<Row> live)
    {
        WL("BOSS (tirés par PHASE, taille imposée par la map)", Head, bold: true);
        var boss = live.Where(r => r.Data.Type == CombatType.Boss).ToList();

        WL($"  {"Phase",-9}{"Nb",-5}Maps (taille)", Dim);
        var gaps = new List<int>();
        foreach (var phase in new[] { 1, 2, 3, 0 })
        {
            var maps = boss.Where(r => r.Data.Phase == phase).ToList();
            if (phase == 0 && maps.Count == 0) continue;   // pas de ligne « Toutes » si aucune map boss « toutes phases »
            var label = phase == 0 ? "Toutes" : phase.ToString();
            W($"  {label,-9}{maps.Count,-5}");
            WL(maps.Count == 0 ? "-" : string.Join(", ", maps.Select(NameSize)),
               maps.Count == 0 ? Warn : Dim);
            if (phase is >= 1 and <= 3 && maps.Count == 0) gaps.Add(phase);
        }
        return gaps;
    }

    private static readonly SpecialObjective[] Objectives =
        { SpecialObjective.LibererPaysans, SpecialObjective.ProtegerPaysans, SpecialObjective.SauverPaysans };

    /// <summary>
    /// SPÉCIALES : matrice phase × objectif. Le jeu ne filtre PAS l'objectif : le pool d'une phase = les maps
    /// de cette phase PLUS les « toutes phases » (Phase 0). Renvoie les couples (phase, objectif) non couverts.
    /// </summary>
    private List<(int phase, SpecialObjective obj)> RenderSpeciales(List<Row> live)
    {
        WL("SPÉCIALES (tirées par PHASE, objectif NON filtré, pool commun par phase)", Head, bold: true);
        var spec = live.Where(r => r.Data.Type == CombatType.Speciale).ToList();

        WL($"  {"Phase",-9}{"Libérer",-10}{"Protéger",-10}{"Sauver",-9}Total", Dim);
        foreach (var phase in new[] { 1, 2, 3, 0 })
        {
            var inPhase = spec.Where(r => r.Data.Phase == phase).ToList();
            if (phase == 0 && inPhase.Count == 0) continue;
            var l = inPhase.Count(r => r.Data.Objective == SpecialObjective.LibererPaysans);
            var p = inPhase.Count(r => r.Data.Objective == SpecialObjective.ProtegerPaysans);
            var s = inPhase.Count(r => r.Data.Objective == SpecialObjective.SauverPaysans);
            var label = phase == 0 ? "Toutes" : phase.ToString();
            W($"  {label,-9}{Cell(l),-10}{Cell(p),-10}{Cell(s),-9}{inPhase.Count}");
            WL(phase == 0 ? "   (rejoint le pool de CHAQUE phase)" : "", Dim);
        }

        // Détail (nom, objectif, taille, tours) : peu de maps, on l'affiche pour éclairer la matrice.
        WL();
        WL("  Détail :", Dim);
        foreach (var r in spec.OrderBy(r => r.Data.Phase == 0 ? 9 : r.Data.Phase).ThenBy(r => r.Data.Objective))
        {
            var ph = r.Data.Phase == 0 ? "T" : r.Data.Phase.ToString();
            var tours = r.Data.TurnLimit > 0 ? r.Data.TurnLimit + "t" : "-";
            WL($"    [P{ph}] {ObjLabel(r.Data.Objective),-10} {Dims(r.Data),-7} {tours,-5} {MapName(r)}", Dim);
        }

        // Couverture par phase, en incluant les « toutes phases » (Phase 0) qui rejoignent chaque pool.
        var gaps = new List<(int, SpecialObjective)>();
        for (var phase = 1; phase <= 3; phase++)
            foreach (var obj in Objectives)
            {
                var covered = spec.Any(r => r.Data.Objective == obj && (r.Data.Phase == phase || r.Data.Phase == 0));
                if (!covered) gaps.Add((phase, obj));
            }
        return gaps;
    }

    private void RenderTutoriel(List<Row> live)
    {
        WL("TUTORIEL", Head, bold: true);
        var tuto = live.Where(r => r.Data.Type == CombatType.Tutoriel).ToList();
        if (tuto.Count == 0) { WL("  (aucune)", Warn); return; }
        foreach (var r in tuto)
            WL($"  {NameSize(r)}", Dim);
    }

    // ---------------------------------------------------------------- Analyse des manques
    private void RenderGaps(
        List<(int phase, int size)> escGaps, List<int> bossGaps,
        List<(int phase, SpecialObjective obj)> specGaps, List<Row> drafts,
        List<(string file, string error)> errors, List<Row> live)
    {
        WL("ANALYSE DES MANQUES", Head, bold: true);
        var clean = true;

        if (errors.Count > 0)
        {
            clean = false;
            WL("  Erreurs de format (maps NON chargées par le jeu) :", Bad, bold: true);
            foreach (var (file, error) in errors)
                WL($"    ✗ {file} : {error}", Bad);
        }

        // Escarmouches non carrées : le jeu ne les tirera jamais (pool restreint aux carrées).
        var nonSquare = live.Where(r => r.Data.Type == CombatType.Escarmouche && r.Data.Width != r.Data.Height).ToList();
        if (nonSquare.Count > 0)
        {
            clean = false;
            WL("  Escarmouches NON carrées (jamais tirées, le pool exige width == height) :", Bad);
            foreach (var r in nonSquare)
                WL($"    ✗ {NameSize(r)}", Bad);
        }

        if (escGaps.Count > 0)
        {
            clean = false;
            WL("  Tailles d'escarmouche requises par campaign.json sans map :", Bad);
            foreach (var (phase, size) in escGaps.OrderBy(g => g.phase).ThenBy(g => g.size))
                WL($"    ✗ phase {phase} attend du {size}×{size} : 0 map (repli sur terrain aléatoire)", Bad);
        }

        if (bossGaps.Count > 0)
        {
            clean = false;
            WL("  Phases sans map boss (repli sur une map boss d'une autre phase) :", Warn);
            foreach (var phase in bossGaps)
                WL($"    ! phase {phase} : aucune map boss dédiée", Warn);
        }

        if (specGaps.Count > 0)
        {
            clean = false;
            WL("  Objectifs spéciaux non couverts par phase (pool phase + « toutes phases ») :", Warn);
            foreach (var (phase, obj) in specGaps)
                WL($"    ! phase {phase} : aucune spéciale « {ObjLabel(obj)} »", Warn);
        }

        // Spéciales sans objectif défini (mal configurées : le jeu les charge mais sans règle d'objectif claire).
        var specNoObj = live.Where(r => r.Data.Type == CombatType.Speciale && r.Data.Objective == SpecialObjective.Aucun).ToList();
        if (specNoObj.Count > 0)
        {
            clean = false;
            WL("  Spéciales sans objectif (objective = Aucun) :", Warn);
            foreach (var r in specNoObj)
                WL($"    ! {MapName(r)}", Warn);
        }

        if (drafts.Count > 0)
        {
            WL("  Brouillons EXCLUS du jeu (draft = true, jamais tirés) :", Dim);
            foreach (var r in drafts.OrderBy(r => r.File, StringComparer.OrdinalIgnoreCase))
            {
                var ph = r.Data.Phase == 0 ? "toutes" : "phase " + r.Data.Phase;
                var obj = r.Data.Objective == SpecialObjective.Aucun ? "" : ", " + ObjLabel(r.Data.Objective);
                WL($"    · {MapName(r)} ({TypeLabel(r.Data.Type)}, {ph}{obj}, {Dims(r.Data)})", Dim);
            }
        }

        if (clean)
            WL("  Aucun manque bloquant détecté.", Ok);
    }

    // ---------------------------------------------------------------- Écriture RichText
    private void W(string text, Color? color = null, bool bold = false)
    {
        _text.SelectionStart = _text.TextLength;
        _text.SelectionLength = 0;
        _text.SelectionColor = color ?? Fg;
        _text.SelectionFont = bold ? _bold : _mono;
        _text.AppendText(text);
    }

    private void WL(string text = "", Color? color = null, bool bold = false) => W(text + "\n", color, bold);

    // ---------------------------------------------------------------- Libellés
    private static string MapName(Row r) => Path.GetFileNameWithoutExtension(r.File);
    private static string Dims(MapData d) => d.Width + "×" + d.Height;
    private static string NameSize(Row r) => $"{MapName(r)} ({Dims(r.Data)})";
    private static string Cell(int n) => n == 0 ? "-" : n.ToString();

    private static string TypeLabel(CombatType t) => t switch
    {
        CombatType.Escarmouche => "Escarmouche",
        CombatType.Speciale => "Spéciale",
        CombatType.Boss => "Boss",
        CombatType.Tutoriel => "Tutoriel",
        _ => t.ToString(),
    };

    private static string ObjLabel(SpecialObjective o) => o switch
    {
        SpecialObjective.LibererPaysans => "Libérer",
        SpecialObjective.ProtegerPaysans => "Protéger",
        SpecialObjective.SauverPaysans => "Sauver",
        _ => "Aucun",
    };

    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return i < 0 ? s : s[..i].TrimEnd('\r');
    }
}
