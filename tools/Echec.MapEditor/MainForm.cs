using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Echec.Core.Map;

namespace Echec.MapEditor;

/// <summary>
/// Fenêtre principale de l'éditeur : barre d'outils (nouveau/ouvrir/enregistrer, nom, type, taille,
/// zoom), palette à gauche qui change selon le calque actif (Terrain / Spawns / Objets) et
/// <see cref="MapCanvas"/> au centre. À l'enregistrement, la map est re-validée par
/// <c>Echec.Core</c> (mêmes règles que le jeu) avant d'écrire le JSON.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly TileRenderCatalog? _catalog;
    private MapDocument _doc = null!;
    private bool _dirty;

    private readonly MapCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _palette = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(45, 47, 54),
        Padding = new Padding(6), FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
    };
    private readonly TextBox _nameBox = new() { Width = 160 };
    private readonly ComboBox _typeBox = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _widthNum = new() { Minimum = 1, Maximum = 30, Value = 6, Width = 50 };
    private readonly NumericUpDown _heightNum = new() { Minimum = 1, Maximum = 30, Value = 6, Width = 50 };
    private readonly ToolStripStatusLabel _status = new("Prêt.");
    private Button? _selectedPaletteButton;

    public MainForm()
    {
        Text = "Éditeur de maps — Echec";
        Width = 1340;
        Height = 800;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(38, 40, 46);

        try
        {
            _catalog = TileRenderCatalog.Load(AssetPaths.TilesJson, AssetPaths.TilesetsDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible de charger le catalogue de tuiles :\n{AssetPaths.TilesJson}\n\n{ex.Message}",
                "Catalogue introuvable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        BuildUi();

        if (_catalog is { Tiles.Count: > 0 })
        {
            _canvas.Brush = _catalog.Tiles[0].Key;
            NewMap(6, 6);
        }
    }

    private char DefaultTileKey => _catalog is { Tiles.Count: > 0 } ? _catalog.Tiles[0].Key : 'h';

    // ---------------------------------------------------------------- UI
    private void BuildUi()
    {
        _canvas.MapChanged += (_, _) => MarkDirty();

        // Structure en TableLayoutPanel (placement par cellules) : aucune dépendance à l'ordre de
        // docking, donc aucun recouvrement possible entre barre d'outils, panneau gauche et canvas.
        // Colonne gauche : sélecteur de calque (haut) + palette (reste).
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            BackColor = Color.FromArgb(45, 47, 54) };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var layerSel = BuildLayerSelector();
        layerSel.Dock = DockStyle.Fill;
        left.Controls.Add(layerSel, 0, 0);
        left.Controls.Add(_palette, 0, 1);

        // Zone centrale : colonne gauche fixe (220) + canvas.
        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        middle.Controls.Add(left, 0, 0);
        middle.Controls.Add(_canvas, 1, 0);

        var strip = new StatusStrip { BackColor = Color.FromArgb(30, 32, 38), Dock = DockStyle.Fill };
        _status.ForeColor = Color.Gainsboro;
        strip.Items.Add(_status);

        // Racine : 3 rangées empilées (barre / centre / statut).
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var toolbar = BuildToolbar();
        toolbar.Dock = DockStyle.Fill;
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(middle, 0, 1);
        root.Controls.Add(strip, 0, 2);
        Controls.Add(root);

        RebuildPalette();
    }

    private Control BuildToolbar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 50, BackColor = Color.FromArgb(30, 32, 38),
            Padding = new Padding(6, 12, 6, 6), WrapContents = false, AutoScroll = false,
        };

        bar.Controls.Add(ToolButton("Nouveau", (_, _) => NewMap((int)_widthNum.Value, (int)_heightNum.Value)));
        bar.Controls.Add(ToolButton("Ouvrir…", (_, _) => Open()));
        bar.Controls.Add(ToolButton("Enregistrer", (_, _) => Save(false)));
        bar.Controls.Add(ToolButton("Enreg. sous…", (_, _) => Save(true)));

        bar.Controls.Add(Sep());
        bar.Controls.Add(Label("Nom :"));
        _nameBox.TextChanged += (_, _) => { if (_doc is not null) { _doc.Name = _nameBox.Text; MarkDirty(); } };
        bar.Controls.Add(_nameBox);

        bar.Controls.Add(Label("Type :"));
        _typeBox.Items.AddRange(new object[] { "Escarmouche", "Speciale", "Boss" });
        _typeBox.SelectedIndex = 0;
        _typeBox.SelectedIndexChanged += (_, _) => { if (_doc is not null) { _doc.Type = _typeBox.Text; MarkDirty(); } };
        bar.Controls.Add(_typeBox);

        bar.Controls.Add(Sep());
        bar.Controls.Add(Label("Taille :"));
        bar.Controls.Add(_widthNum);
        bar.Controls.Add(Label("×"));
        bar.Controls.Add(_heightNum);
        bar.Controls.Add(ToolButton("Appliquer", (_, _) => ApplySize()));

        bar.Controls.Add(Sep());
        bar.Controls.Add(Label("Zoom :"));
        bar.Controls.Add(ToolButton("−", (_, _) => SetZoom(_canvas.Zoom / 2f)));
        bar.Controls.Add(ToolButton("100%", (_, _) => SetZoom(1f)));
        bar.Controls.Add(ToolButton("+", (_, _) => SetZoom(_canvas.Zoom * 2f)));
        return bar;
    }

    private Control BuildLayerSelector()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.FromArgb(30, 32, 38), Padding = new Padding(4), WrapContents = false,
        };
        AddLayerRadio(panel, "Terrain", EditLayer.Terrain, true);
        AddLayerRadio(panel, "Spawns", EditLayer.Spawns, false);
        AddLayerRadio(panel, "Objets", EditLayer.Objects, false);
        return panel;
    }

    private void AddLayerRadio(Control parent, string text, EditLayer layer, bool selected)
    {
        var rb = new RadioButton
        {
            Text = text, AutoSize = true, Checked = selected, ForeColor = Color.Gainsboro,
            Margin = new Padding(2, 6, 6, 0),
        };
        rb.CheckedChanged += (_, _) =>
        {
            if (!rb.Checked) return;
            _canvas.Layer = layer;
            RebuildPalette();
            _canvas.Invalidate();
        };
        parent.Controls.Add(rb);
    }

    private void RebuildPalette()
    {
        _palette.SuspendLayout();
        foreach (Control c in _palette.Controls) c.Dispose();
        _palette.Controls.Clear();
        _selectedPaletteButton = null;

        switch (_canvas.Layer)
        {
            case EditLayer.Terrain:
                if (_catalog is not null)
                    foreach (var t in _catalog.Tiles)
                        _palette.Controls.Add(TilePaletteButton(t));
                break;
            case EditLayer.Spawns:
                AddBrushButton('P', "Joueur", Color.FromArgb(60, 200, 90));
                AddBrushButton('E', "Ennemi", Color.FromArgb(220, 70, 70));
                AddBrushButton('B', "Boss", Color.FromArgb(170, 90, 220));
                AddBrushButton('.', "Effacer", Color.DimGray);
                break;
            case EditLayer.Objects:
                AddBrushButton('C', "Coffre", Color.FromArgb(230, 190, 60));
                AddBrushButton('K', "Coffre clé", Color.FromArgb(230, 140, 50));
                AddBrushButton('k', "Clé", Color.FromArgb(240, 230, 90));
                AddBrushButton('R', "Recrue", Color.FromArgb(70, 200, 210));
                AddBrushButton('B', "Buisson", Color.FromArgb(90, 160, 80));
                AddBrushButton('.', "Effacer", Color.DimGray);
                break;
        }
        _palette.ResumeLayout();

        // Sélectionne le pinceau courant s'il existe dans la nouvelle palette.
        foreach (Control c in _palette.Controls)
            if (c is Button b && b.Tag is char ch && ch == _canvas.Brush)
            {
                SelectPaletteButton(b);
                break;
            }
    }

    private Button TilePaletteButton(TileInfo tile)
    {
        var btn = new Button
        {
            Size = new Size(60, 82), Margin = new Padding(4), Tag = tile.Key,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 62, 70),
            TextAlign = ContentAlignment.BottomCenter, ForeColor = Color.Gainsboro,
            Text = tile.Key.ToString(), ImageAlign = ContentAlignment.TopCenter,
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(90, 92, 100);
        if (tile.Image is not null)
            btn.Image = ScaleNearest(tile.Image, 50, 62);
        var blocks = (tile.BlocksMove ? "bloque déplacement " : "") + (tile.BlocksFire ? "bloque tir" : "");
        _tips.SetToolTip(btn, $"{tile.Id} ('{tile.Key}')\n{(blocks.Length == 0 ? "libre" : blocks.Trim())}");
        btn.Click += (_, _) => { _canvas.Brush = tile.Key; SelectPaletteButton(btn); };
        return btn;
    }

    private void AddBrushButton(char ch, string label, Color color)
    {
        var btn = new Button
        {
            Size = new Size(96, 44), Margin = new Padding(4), Tag = ch, FlatStyle = FlatStyle.Flat,
            BackColor = color, ForeColor = Color.Black, Text = $"{ch}  {label}",
            TextAlign = ContentAlignment.MiddleCenter,
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(90, 92, 100);
        btn.Click += (_, _) => { _canvas.Brush = ch; SelectPaletteButton(btn); };
        _palette.Controls.Add(btn);
    }

    private void SelectPaletteButton(Button btn)
    {
        if (_selectedPaletteButton is not null)
            _selectedPaletteButton.FlatAppearance.BorderSize = 1;
        _selectedPaletteButton = btn;
        btn.FlatAppearance.BorderColor = Color.Gold;
        btn.FlatAppearance.BorderSize = 3;
    }

    // ---------------------------------------------------------------- Actions
    private void NewMap(int w, int h)
    {
        if (_catalog is null || _catalog.Tiles.Count == 0)
        {
            MessageBox.Show("Aucun catalogue de tuiles chargé — impossible de créer une map.",
                "Catalogue manquant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _doc = MapDocument.NewMap(w, h, DefaultTileKey);
        _doc.Type = _typeBox.Text;
        _nameBox.Text = _doc.Name;
        SyncSizeFields();
        _canvas.SetContent(_doc, _catalog);
        _dirty = false;
        UpdateTitle();
        _status.Text = $"Nouvelle map {w}×{h}.";
    }

    private void Open()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Ouvrir une map",
            Filter = "Maps Echec (*.json)|*.json",
            InitialDirectory = Directory.Exists(AssetPaths.MapsDir) ? AssetPaths.MapsDir : "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (_catalog is null) return;
        try
        {
            _doc = MapDocument.Load(dlg.FileName);
            _nameBox.Text = _doc.Name;
            _typeBox.SelectedItem = _typeBox.Items.Contains(_doc.Type) ? _doc.Type : "Escarmouche";
            SyncSizeFields();
            _canvas.SetContent(_doc, _catalog);
            _dirty = false;
            UpdateTitle();
            _status.Text = $"Ouvert : {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lecture impossible :\n{ex.Message}", "Erreur",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Save(bool saveAs)
    {
        if (_doc is null || _catalog is null) return;
        _doc.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? "map" : _nameBox.Text.Trim();
        _doc.Type = _typeBox.Text;

        // Filet de sécurité : re-valider avec le MÊME code que le jeu avant d'écrire.
        try
        {
            var core = TileCatalog.FromJson(_catalog.RawJson);
            MapLoader.Parse(_doc.ToJson(), core);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"La map n'est pas valide, rien n'a été écrit :\n\n{ex.Message}",
                "Validation échouée", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var path = _doc.FilePath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Enregistrer la map",
                Filter = "Maps Echec (*.json)|*.json",
                InitialDirectory = Directory.Exists(AssetPaths.MapsDir) ? AssetPaths.MapsDir : "",
                FileName = _doc.Name + ".json",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }

        try
        {
            File.WriteAllText(path!, _doc.ToJson());
            _doc.FilePath = path;
            _dirty = false;
            UpdateTitle();
            _status.Text = $"Enregistré : {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Écriture impossible :\n{ex.Message}", "Erreur",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplySize()
    {
        if (_doc is null) return;
        _doc.Resize((int)_widthNum.Value, (int)_heightNum.Value, DefaultTileKey);
        _canvas.UpdateExtent();
        MarkDirty();
        _status.Text = $"Taille : {_doc.Width}×{_doc.Height}.";
    }

    private void SetZoom(float scale)
    {
        _canvas.Zoom = Math.Clamp(scale, 0.25f, 4f);
        _canvas.UpdateExtent();
        _status.Text = $"Zoom : {_canvas.Zoom * 100:0}%";
    }

    // ---------------------------------------------------------------- Helpers
    private void SyncSizeFields()
    {
        _widthNum.Value = Math.Clamp(_doc.Width, (int)_widthNum.Minimum, (int)_widthNum.Maximum);
        _heightNum.Value = Math.Clamp(_doc.Height, (int)_heightNum.Minimum, (int)_heightNum.Maximum);
    }

    private void MarkDirty() { _dirty = true; UpdateTitle(); }

    private void UpdateTitle()
    {
        var file = _doc?.FilePath is { } p ? Path.GetFileName(p) : "(non enregistrée)";
        Text = $"Éditeur de maps — {_doc?.Name} — {file}{(_dirty ? " *" : "")}";
    }

    private readonly ToolTip _tips = new();

    private static Button ToolButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, Margin = new Padding(2, 0, 2, 0),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 62, 70), ForeColor = Color.Gainsboro,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(90, 92, 100);
        b.Click += onClick;
        return b;
    }

    private static Label Label(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(6, 8, 2, 0),
    };

    private static Control Sep() => new Label { Text = "", Width = 12, AutoSize = false };

    private static Bitmap ScaleNearest(Bitmap src, int w, int h)
    {
        var dst = new Bitmap(w, h);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(src, new Rectangle(0, 0, w, h));
        return dst;
    }
}
