using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ChessArmy.MapEditor;

/// <summary>
/// Édition des zones VERTICALES (murs) d'une tuile, au pixel près : on peint par-dessus l'art de la tuile les
/// pixels qui sont des faces verticales. Le jeu y fait COULER le sang (au lieu de l'y poser). Enregistré dans le
/// masque partagé de l'image (<see cref="TileInfo.MaskPath"/>, ex. <c>TilesMurs_faces.png</c>) : seule la cellule
/// éditée est modifiée, le reste du masque est conservé. Par défaut rien n'est vertical (pas de fichier).
/// L'épaisseur du bord du plateau (16 px du bas) est traitée automatiquement par le jeu : inutile de la peindre.
/// </summary>
internal sealed class FaceMaskForm : Form
{
    private const int Zoom = 6;
    private static readonly Color MaskColor = Color.FromArgb(255, 255, 0, 0);       // couleur écrite dans le masque
    private static readonly Color Overlay = Color.FromArgb(140, 255, 40, 40);   // affichage du masque sur l'art

    private readonly TileInfo _tile;
    private readonly int _tileSize;
    private readonly Bitmap _art;
    private readonly Bitmap _mask;
    private readonly bool _maskExisted;
    private readonly PictureBox _view = new();
    private readonly ComboBox _variant = new();
    private readonly NumericUpDown _brush = new();
    private readonly Label _status = new();
    private int _cellIndex;
    private bool _dirty;

    private Rectangle Cell => _tile.Cells[_cellIndex];

    public FaceMaskForm(TileInfo tile, int tileSize)
    {
        _tile = tile;
        _tileSize = tileSize;
        _art = LoadUnlocked(tile.ArtPath!);
        _maskExisted = File.Exists(tile.MaskPath);
        _mask = _maskExisted ? LoadUnlocked(tile.MaskPath!) : new Bitmap(_art.Width, _art.Height, PixelFormat.Format32bppArgb);
        if (_mask.Width != _art.Width || _mask.Height != _art.Height)
        {
            // Masque d'une autre taille (image agrandie depuis) : on le recopie sur un masque aux bonnes dimensions.
            var fixedMask = new Bitmap(_art.Width, _art.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(fixedMask))
                g.DrawImage(_mask, new Rectangle(0, 0, _mask.Width, _mask.Height));   // taille explicite : pas de mise à l'échelle DPI
            _mask.Dispose();
            _mask = fixedMask;
        }

        Text = $"Zones verticales : {tile.Id}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        BackColor = Color.FromArgb(45, 47, 54);
        ForeColor = Color.Gainsboro;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = true;

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(12) };

        var help = new Label
        {
            AutoSize = true, MaximumSize = new Size(Cell.Width * Zoom, 0), ForeColor = Color.Silver,
            Text = "Peins les pixels qui sont des MURS VERTICAUX : le sang y coulera vers le bas.\n"
                 + "Clic gauche = peindre, clic droit = effacer. Le trait pointillé marque le début de l'épaisseur"
                 + " (déjà gérée par le jeu au bord du plateau).",
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(help);

        var bar = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 8) };
        if (tile.Cells.Count > 1)
        {
            bar.Controls.Add(new Label { Text = "Variante :", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
            _variant.DropDownStyle = ComboBoxStyle.DropDownList;
            _variant.Width = 110;
            for (var i = 0; i < tile.Cells.Count; i++)
                _variant.Items.Add($"Variante {i + 1}");
            _variant.SelectedIndex = 0;
            _variant.SelectedIndexChanged += (_, _) => { _cellIndex = _variant.SelectedIndex; _view.Invalidate(); };
            bar.Controls.Add(_variant);
        }
        bar.Controls.Add(new Label { Text = "Pinceau :", AutoSize = true, Margin = new Padding(12, 7, 4, 0) });
        _brush.Minimum = 1;
        _brush.Maximum = 8;
        _brush.Value = 1;
        _brush.Width = 50;
        bar.Controls.Add(_brush);
        bar.Controls.Add(Btn("Tout effacer", (_, _) => FillCell(false)));
        bar.Controls.Add(Btn("Tout peindre", (_, _) => FillCell(true)));
        if (tile.Cells.Count > 1)
            bar.Controls.Add(Btn("Copier sur les variantes", (_, _) => CopyToVariants()));
        root.Controls.Add(bar);

        _view.Size = new Size(Cell.Width * Zoom, Cell.Height * Zoom);
        _view.BackColor = Color.FromArgb(30, 31, 36);
        _view.Paint += (_, e) => DrawView(e.Graphics);
        _view.MouseDown += (_, e) => PaintAt(e);
        _view.MouseMove += (_, e) => { if (e.Button != MouseButtons.None) PaintAt(e); };
        root.Controls.Add(_view);

        var bottom = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 8, 0, 0) };
        bottom.Controls.Add(Btn("Enregistrer", (_, _) => Save()));
        bottom.Controls.Add(Btn("Fermer", (_, _) => Close()));
        _status.AutoSize = true;
        _status.ForeColor = Color.Gray;
        _status.Margin = new Padding(12, 7, 0, 0);
        _status.Text = Path.GetFileName(tile.MaskPath) + (_maskExisted ? "" : " (sera créé à l'enregistrement)");
        bottom.Controls.Add(_status);
        root.Controls.Add(bottom);

        Controls.Add(root);
        FormClosing += OnClosing;
        KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.S) { Save(); e.Handled = true; } };
    }

    private static Button Btn(string text, EventHandler click)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(70, 72, 80),
            ForeColor = Color.Gainsboro, Margin = new Padding(6, 0, 0, 0),
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(90, 92, 100);
        b.Click += click;
        return b;
    }

    private void DrawView(Graphics g)
    {
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var dest = new Rectangle(0, 0, Cell.Width * Zoom, Cell.Height * Zoom);
        g.DrawImage(_art, dest, Cell, GraphicsUnit.Pixel);

        using (var overlay = new SolidBrush(Overlay))
            for (var y = 0; y < Cell.Height; y++)
                for (var x = 0; x < Cell.Width; x++)
                    if (_mask.GetPixel(Cell.X + x, Cell.Y + y).A > 0)
                        g.FillRectangle(overlay, x * Zoom, y * Zoom, Zoom, Zoom);

        // Limite surface / épaisseur (la partie basse de la cellule, sous la case de 64 px).
        if (_tileSize > 0 && _tileSize < Cell.Height)
        {
            using var pen = new Pen(Color.FromArgb(200, 255, 255, 255)) { DashStyle = DashStyle.Dash };
            g.DrawLine(pen, 0, _tileSize * Zoom, dest.Width, _tileSize * Zoom);
        }
    }

    private void PaintAt(MouseEventArgs e)
    {
        bool? on = e.Button == MouseButtons.Left ? true : e.Button == MouseButtons.Right ? false : null;
        if (on is null)
            return;
        var size = (int)_brush.Value;
        var cx = e.X / Zoom - (size - 1) / 2;
        var cy = e.Y / Zoom - (size - 1) / 2;
        var changed = false;
        for (var y = cy; y < cy + size; y++)
            for (var x = cx; x < cx + size; x++)
                changed |= SetMask(x, y, on.Value);
        if (changed)
        {
            _dirty = true;
            _view.Invalidate();
        }
    }

    /// <summary>Écrit un pixel du masque (coordonnées dans la cellule courante). Vrai s'il a changé.</summary>
    private bool SetMask(int x, int y, bool on)
    {
        if (x < 0 || y < 0 || x >= Cell.Width || y >= Cell.Height)
            return false;
        var px = Cell.X + x;
        var py = Cell.Y + y;
        var was = _mask.GetPixel(px, py).A > 0;
        if (was == on)
            return false;
        _mask.SetPixel(px, py, on ? MaskColor : Color.Transparent);
        return true;
    }

    private void FillCell(bool on)
    {
        for (var y = 0; y < Cell.Height; y++)
            for (var x = 0; x < Cell.Width; x++)
                _dirty |= SetMask(x, y, on);
        _view.Invalidate();
    }

    /// <summary>Recopie le masque de la variante courante sur toutes les autres variantes de la tuile.</summary>
    private void CopyToVariants()
    {
        var src = Cell;
        foreach (var dst in _tile.Cells.Where(c => c != src))
            for (var y = 0; y < Math.Min(src.Height, dst.Height); y++)
                for (var x = 0; x < Math.Min(src.Width, dst.Width); x++)
                {
                    var on = _mask.GetPixel(src.X + x, src.Y + y).A > 0;
                    if ((_mask.GetPixel(dst.X + x, dst.Y + y).A > 0) == on)
                        continue;
                    _mask.SetPixel(dst.X + x, dst.Y + y, on ? MaskColor : Color.Transparent);
                    _dirty = true;
                }
        _status.Text = "Copié sur toutes les variantes (pense à enregistrer).";
    }

    /// <summary>
    /// Enregistre le masque complet de l'image. Masque entièrement vide → aucun fichier (le jeu considère
    /// alors qu'il n'y a pas de zone verticale) : on ne crée pas de fichier vide, et on supprime l'ancien.
    /// </summary>
    private void Save()
    {
        try
        {
            var path = _tile.MaskPath!;
            if (IsEmpty())
            {
                if (File.Exists(path))
                    File.Delete(path);
                _status.Text = "Masque vide : aucune zone verticale (fichier retiré).";
            }
            else
            {
                _mask.Save(path, ImageFormat.Png);
                _status.Text = $"Enregistré : {Path.GetFileName(path)}";
            }
            _dirty = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible d'enregistrer le masque :\n{ex.Message}", "Erreur",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool IsEmpty()
    {
        for (var y = 0; y < _mask.Height; y++)
            for (var x = 0; x < _mask.Width; x++)
                if (_mask.GetPixel(x, y).A > 0)
                    return false;
        return true;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_dirty)
            return;
        var r = MessageBox.Show(this, "Enregistrer les zones verticales modifiées ?", "Zones verticales",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel)
            e.Cancel = true;
        else if (r == DialogResult.Yes)
            Save();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _art.Dispose();
            _mask.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Bitmap LoadUnlocked(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes);
        using var tmp = new Bitmap(ms);
        var copy = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(copy))
            g.DrawImage(tmp, new Rectangle(0, 0, tmp.Width, tmp.Height));   // taille explicite : pas de mise à l'échelle DPI
        return copy;
    }
}
