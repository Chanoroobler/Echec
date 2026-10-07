using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ChessArmy.Core.Campaign;
using ChessArmy.Core.Map;
using ChessArmy.Core.Battle;

namespace ChessArmy.MapEditor;

/// <summary>
/// Colonne de droite : tableau des maps ESCARMOUCHE, SPÉCIALES et BOSS du dossier, avec leurs effectifs (ennemis,
/// recrues, coffres). Un clic sur une ligne demande l'ouverture de la map (<see cref="OpenRequested"/>). Lu avec
/// le MÊME parseur que le jeu (<see cref="MapLoader.Parse"/>) : une map illisible n'apparaît pas. Triable en
/// cliquant les en-têtes. Rafraîchi à l'ouverture de l'éditeur, par son bouton et après chaque enregistrement.
/// </summary>
internal sealed class MapListPanel : Panel
{
    private readonly TileRenderCatalog _catalog;
    private readonly Label _count = new()
    {
        AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(8, 7, 0, 0),
    };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, EnableHeadersVisualStyles = false,
        BackgroundColor = Color.FromArgb(45, 47, 54), GridColor = Color.FromArgb(60, 62, 70),
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        ColumnHeadersHeight = 26, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
    };
    private string? _currentPath;
    private bool _filling;

    /// <summary>Le joueur a cliqué une map : chemin complet du fichier à ouvrir.</summary>
    public event Action<string>? OpenRequested;

    public MapListPanel(TileRenderCatalog catalog)
    {
        _catalog = catalog;
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(45, 47, 54);

        var bodyFont = new Font("Segoe UI", 8.5f);
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(45, 47, 54), ForeColor = Color.Gainsboro, Font = bodyFont,
            SelectionBackColor = Color.FromArgb(70, 90, 120), SelectionForeColor = Color.White,
        };
        _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(50, 52, 60) };
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(30, 32, 38), ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            SelectionBackColor = Color.FromArgb(30, 32, 38),
        };
        _grid.RowTemplate.Height = 22;

        AddColumn("Map", "Nom de la map (gris = brouillon, exclu du jeu)", 150, left: true);
        AddColumn("Type", "Esc = escarmouche, Spé = spéciale, Boss = boss", 50);
        AddColumn("Ph", "Phase(s) où la map peut sortir. Spéciale/boss : sa phase (T = toutes). Escarmouche : déduit de campaign.json (une mission escarmouche de la phase demande la taille de son plus petit côté, 8×12 = 8×8 ; 9 et 10 aussi en phase 3) ; « - » = jamais tirée", 54);
        AddColumn("Obj", "Objectif des spéciales : Lib = libérer, Pro = protéger, Sau = sauver", 46);
        AddColumn("Taille", "Largeur × hauteur", 56);
        AddColumn("Enn T/1/2/3", "Ennemis : Total / T1 / T2 / T3. Total = cases de spawn E/D/O (+ B pour un boss, compté dans le total mais pas dans les tiers). Tiers du calque « tiers » (spéciales et boss) ; « - » = pas de tier fixé, la vague suit campaign.json (toujours le cas des escarmouches)", 118);
        AddColumn("Rec", "Recrues (tuiles R)", 44);
        AddColumn("Cof", "Coffres (C et K)", 44);
        AddColumn("Tours", "Limite de tours des spéciales (15 par défaut si la map n'en fixe pas). Vide : sans limite (escarmouche, boss, « sauver »)", 58);
        AddColumn("Min F/N/D", "Paysans à sauver au minimum pour ne pas PERDRE la run, en Facile / Normal / Difficile (quota de la difficulté plafonné aux paysans de la map, comme en jeu). Rouge : le quota dépasse les paysans de la map (plafonné, aucune marge)", 96);
        AddColumn("Terrain", "Éléments de terrain : Ch = chutes (F), Bu = buissons (B), Mi = miradors (tuiles de tour, +portée), Ve = verglas (tuiles glissantes). Vide = aucun", 120);
        _grid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;   // le nom prend la place restante
        // La grille sélectionne d'office la 1re ligne en s'affichant : on remet la sélection sur la map ouverte.
        _grid.HandleCreated += (_, _) => BeginInvoke((Action)HighlightCurrent);
        _grid.VisibleChanged += (_, _) => { if (_grid.Visible && _grid.IsHandleCreated) BeginInvoke((Action)HighlightCurrent); };

        _grid.SortCompare += CompareSlashed;
        _grid.CellClick += (_, e) =>
        {
            if (_filling || e.RowIndex < 0) return;
            if (_grid.Rows[e.RowIndex].Tag is string path && !SamePath(path, _currentPath))
                OpenRequested?.Invoke(path);
        };

        var refresh = new Button
        {
            Text = "Rafraîchir", AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 3, 0, 0),
            BackColor = Color.FromArgb(60, 62, 70), ForeColor = Color.Gainsboro,
        };
        refresh.FlatAppearance.BorderColor = Color.FromArgb(90, 92, 100);
        refresh.Click += (_, _) => RefreshList();

        var title = new Label
        {
            Text = "Maps escarmouche, spéciales et boss", AutoSize = true, ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(6, 7, 0, 0),
        };
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 32, BackColor = Color.FromArgb(30, 32, 38), WrapContents = false,
        };
        header.Controls.Add(title);
        header.Controls.Add(refresh);
        header.Controls.Add(_count);

        Controls.Add(_grid);    // Fill d'abord…
        Controls.Add(header);   // …puis la bande du haut
        RefreshList();
    }

    private void AddColumn(string name, string tip, int width, bool left = false)
    {
        var col = new DataGridViewTextBoxColumn
        {
            HeaderText = name, ToolTipText = tip, Width = width, MinimumWidth = 24,
            SortMode = DataGridViewColumnSortMode.Automatic,
        };
        col.DefaultCellStyle.Alignment = left ? DataGridViewContentAlignment.MiddleLeft : DataGridViewContentAlignment.MiddleCenter;
        col.HeaderCell.Style.Alignment = col.DefaultCellStyle.Alignment;
        _grid.Columns.Add(col);
    }

    /// <summary>Rescane le dossier des maps et remplit le tableau (garde la map courante sélectionnée).</summary>
    public void RefreshList()
    {
        _filling = true;
        try
        {
            _grid.Rows.Clear();
            LoadCampaignPlan();   // tailles d'escarmouche par phase à jour (cf. PhaseText)
            TileCatalog core;
            try { core = TileCatalog.FromJson(_catalog.RawJson); }
            catch { _count.Text = "catalogue illisible"; return; }
            if (!Directory.Exists(AssetPaths.MapsDir)) { _count.Text = "dossier introuvable"; return; }

            var rows = new List<(string Path, MapData Data)>();
            foreach (var path in Directory.EnumerateFiles(AssetPaths.MapsDir, "*.json"))
            {
                try
                {
                    var data = MapLoader.Parse(File.ReadAllText(path), core);
                    if (data.Type is CombatType.Escarmouche or CombatType.Speciale or CombatType.Boss)
                        rows.Add((path, data));
                }
                catch { /* illisible : signalée par le Récap, absente d'ici */ }
            }

            // Spéciales, boss puis escarmouches, chaque groupe par phase, objectif et nom : les maps comparables se suivent.
            foreach (var (path, d) in rows
                         .OrderBy(r => TypeOrder(r.Data.Type))
                         .ThenBy(r => r.Data.Phase == 0 ? 9 : r.Data.Phase)
                         .ThenBy(r => r.Data.Objective)
                         .ThenBy(r => Path.GetFileNameWithoutExtension(r.Path), StringComparer.OrdinalIgnoreCase))
            {
                var recruits = d.Objects.Count(o => o.Kind == MapObjectKind.Recruit);
                var chests = d.Objects.Count(o => o.Kind is MapObjectKind.ChestCommon or MapObjectKind.ChestRare);
                var i = _grid.Rows.Add(
                    Path.GetFileNameWithoutExtension(path),
                    TypeLabel(d.Type),
                    PhaseText(d),
                    ObjLabel(d.Objective),
                    $"{d.Width}×{d.Height}",
                    EnemyText(d), recruits, chests, TurnLimit(d), QuotaText(d, recruits, out var capped), TerrainText(d));   // entiers : le tri des colonnes est numérique
                var row = _grid.Rows[i];
                row.Tag = path;
                if (d.IsDraft)
                    row.DefaultCellStyle.ForeColor = Color.Gray;
                if (d.Type == CombatType.Speciale && recruits == 0)
                    row.Cells[6].Style.ForeColor = Color.FromArgb(240, 110, 100);   // spéciale sans paysan : ingagnable
                if (capped)
                    row.Cells[9].Style.ForeColor = Color.FromArgb(240, 110, 100);   // quota plafonné : il faut TOUS les sauver
            }
            _count.Text = $"{rows.Count} maps";
        }
        finally { _filling = false; }
        HighlightCurrent();
    }

    /// <summary>Map actuellement ouverte dans l'éditeur (sélectionnée dans le tableau ; null = aucune).</summary>
    public void SetCurrent(string? path)
    {
        _currentPath = path;
        HighlightCurrent();
    }

    private void HighlightCurrent()
    {
        _filling = true;
        try
        {
            _grid.CurrentCell = null;
            _grid.ClearSelection();
            foreach (DataGridViewRow row in _grid.Rows)
                if (row.Tag is string p && SamePath(p, _currentPath))
                {
                    row.Selected = true;
                    if (!row.Displayed) _grid.FirstDisplayedScrollingRowIndex = row.Index;
                    break;
                }
        }
        finally { _filling = false; }
    }

    private static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // Limite par défaut d'une spéciale sans « turnLimit » : miroir de GameplayScene.SpecialTurnLimit (à garder aligné).
    private const int DefaultSpecialTurns = 15;

    /// <summary>
    /// Tours de la mission tels que le jeu les applique : la limite de la map (ou le défaut) pour une spéciale
    /// « libérer » / « protéger » ; null (cellule vide) quand il n'y a pas de limite — escarmouche, boss, et
    /// « sauver », une course où la valeur de la map est ignorée.
    /// </summary>
    private static int? TurnLimit(MapData d) =>
        d.Type == CombatType.Speciale && d.Objective != SpecialObjective.SauverPaysans
            ? (d.TurnLimit > 0 ? d.TurnLimit : DefaultSpecialTurns)
            : null;

    /// <summary>
    /// Paysans à sauver au minimum pour ne pas perdre la run, en Facile/Normal/Difficile (« 0/1/2 »), lus dans
    /// <see cref="DifficultySettings"/> (la source du jeu) selon l'objectif et plafonnés aux paysans de la map
    /// comme le fait le jeu. <paramref name="capped"/> : un niveau demandait plus de paysans que la map n'en a.
    /// Vide hors spéciale.
    /// </summary>
    private static string QuotaText(MapData d, int recruits, out bool capped)
    {
        capped = false;
        if (d.Type != CombatType.Speciale || d.Objective == SpecialObjective.Aucun)
            return "";
        var parts = new string[DifficultySettings.AllLevels.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            var s = DifficultySettings.For(DifficultySettings.AllLevels[i]);
            var quota = d.Objective switch
            {
                SpecialObjective.ProtegerPaysans => s.PaysansRequiredProtect,
                SpecialObjective.SauverPaysans => s.PaysansRequiredSave,
                _ => s.PaysansRequired,
            };
            if (quota > recruits) capped = true;
            parts[i] = Math.Min(quota, recruits).ToString();
        }
        return string.Join("/", parts);
    }

    /// <summary>
    /// « Total/T1/T2/T3 ». Total = une unité par case de spawn ennemie (E/D/O) plus le boss (B). Les tiers sont
    /// ceux du calque « tiers » (<see cref="MapData.EnemyTiers"/>, escortes seulement) ; sans calque, « - » : la
    /// composition vient alors de campaign.json selon la mission (toujours le cas des escarmouches).
    /// </summary>
    private static string EnemyText(MapData d)
    {
        var total = d.EnemySpawns.Count + d.BossSpawns.Count;
        if (d.EnemyTiers.Count == 0)
            return $"{total}/-/-/-";
        int t1 = 0, t2 = 0, t3 = 0;
        foreach (var t in d.EnemyTiers)
        {
            if (t == 1) t1++;
            else if (t == 2) t2++;
            else if (t == 3) t3++;
        }
        return $"{total}/{t1}/{t2}/{t3}";
    }

    /// <summary>
    /// Tri des colonnes « nombre/nombre/… » (ennemis, quotas) : on compare nombre par nombre et non comme du
    /// texte, sinon « 8/… » passerait après « 12/… ». Les autres colonnes gardent le tri par défaut.
    /// </summary>
    private static void CompareSlashed(object? sender, DataGridViewSortCompareEventArgs e)
    {
        if (e.CellValue1 is not string a || e.CellValue2 is not string b || !a.Contains('/') || !b.Contains('/'))
            return;
        var pa = a.Split('/');
        var pb = b.Split('/');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = int.TryParse(pa[i], out var x) ? x : -1;   // « - » se range avant 0
            var nb = int.TryParse(pb[i], out var y) ? y : -1;
            if (na != nb)
            {
                e.SortResult = na.CompareTo(nb);
                e.Handled = true;
                return;
            }
        }
        e.SortResult = 0;
        e.Handled = true;
    }

    /// <summary>
    /// Phase(s) où la map peut sortir. Spéciale / boss : la phase de la map (« T » = toutes). ESCARMOUCHE : même
    /// règle que le jeu (GameplayScene.ShuffledEscarmouchePool) — une mission escarmouche de la phase demande la
    /// taille de son PLUS PETIT côté (<see cref="MapData.PoolSize"/>) dans campaign.json, ou phase 3 et 9 / 10.
    /// « - » = jamais tirée (taille demandée nulle part).
    /// </summary>
    private static string PhaseText(MapData d)
    {
        if (d.Type != CombatType.Escarmouche)
            return d.Phase == 0 ? "T" : d.Phase.ToString();
        var size = d.PoolSize;   // plus petit côté : une 8×12 compte comme une 8×8
        var phases = new List<int>();
        for (var p = 1; p <= Run.PhaseCount; p++)
        {
            var fits = p >= 3 && size is 9 or 10;
            for (var m = 1; m <= Run.MissionsIn(p) && !fits; m++)
                fits = Run.MissionKindAt(p, m) == CombatType.Escarmouche && CampaignPlan.For(p, m).MapSize == size;
            if (fits)
                phases.Add(p);
        }
        return phases.Count == 0 ? "-" : string.Join(",", phases);
    }

    /// <summary>Charge campaign.json dans <see cref="CampaignPlan"/> (silencieux : repli sur les valeurs codées de Core).</summary>
    private static void LoadCampaignPlan()
    {
        try
        {
            if (File.Exists(AssetPaths.CampaignJson))
                CampaignPlan.Load(CampaignPlan.FromJson(File.ReadAllText(AssetPaths.CampaignJson)));
        }
        catch { /* campaign.json absent ou invalide : on garde les valeurs par défaut */ }
    }

    /// <summary>
    /// Éléments de terrain notables de la map, avec leur nombre : « Ch2 Bu4 Mi1 Ve6 ». Chutes et buissons sont des
    /// objets ; miradors (tuile qui prête de la portée, <see cref="TileDef.RangeBonus"/>) et verglas (tuile
    /// glissante, <see cref="TileDef.Slides"/>) se lisent sur le terrain. Vide = aucun.
    /// </summary>
    private static string TerrainText(MapData d)
    {
        var chutes = d.Objects.Count(o => o.Kind == MapObjectKind.Chute);
        var bushes = d.Objects.Count(o => o.Kind == MapObjectKind.Bush);
        int miradors = 0, ice = 0;
        for (var c = 0; c < d.Width; c++)
            for (var r = 0; r < d.Height; r++)
            {
                var tile = d.TileAt(new Cell(c, r));
                if (tile.RangeBonus > 0) miradors++;
                if (tile.Slides) ice++;
            }
        var parts = new List<string>(4);
        if (chutes > 0) parts.Add("Ch" + chutes);
        if (bushes > 0) parts.Add("Bu" + bushes);
        if (miradors > 0) parts.Add("Mi" + miradors);
        if (ice > 0) parts.Add("Ve" + ice);
        return string.Join(" ", parts);
    }

    /// <summary>Ordre des groupes dans le tableau : spéciales, boss, puis escarmouches.</summary>
    private static int TypeOrder(CombatType t) => t switch
    {
        CombatType.Speciale => 0,
        CombatType.Boss => 1,
        _ => 2,
    };

    private static string TypeLabel(CombatType t) => t switch
    {
        CombatType.Speciale => "Spé",
        CombatType.Boss => "Boss",
        _ => "Esc",
    };

    private static string ObjLabel(SpecialObjective o) => o switch
    {
        SpecialObjective.LibererPaysans => "Lib",
        SpecialObjective.ProtegerPaysans => "Pro",
        SpecialObjective.SauverPaysans => "Sau",
        _ => "",
    };
}
