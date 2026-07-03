using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Echec.MapEditor;

internal enum EditLayer { Terrain, Spawns, Objects }

/// <summary>
/// Zone de dessin de la map : rend les trois calques (terrain découpé des tilesets, spawns, objets)
/// et peint la case survolée au clic. Reproduit le recouvrement 64×80 du jeu (les rangées du bas
/// cachent l'épaisseur des rangées du haut). Clic gauche = pinceau courant, clic droit = efface.
/// </summary>
internal sealed class MapCanvas : Panel
{
    private MapDocument? _doc;
    private TileRenderCatalog? _catalog;

    public EditLayer Layer { get; set; } = EditLayer.Terrain;
    public char Brush { get; set; } = 'h';
    public float Zoom { get; set; } = 1f;

    private Point _hover = new(-1, -1);

    public event EventHandler? MapChanged;

    public MapCanvas()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = Color.FromArgb(30, 32, 38);
    }

    public void SetContent(MapDocument doc, TileRenderCatalog catalog)
    {
        _doc = doc;
        _catalog = catalog;
        UpdateExtent();
        Invalidate();
    }

    public void UpdateExtent()
    {
        if (_doc is null || _catalog is null) return;
        var surface = _catalog.TileSize * Zoom;
        var extra = _catalog.Thickness * Zoom; // épaisseur de la dernière rangée
        AutoScrollMinSize = new Size(
            (int)Math.Ceiling(_doc.Width * surface) + 1,
            (int)Math.Ceiling(_doc.Height * surface + extra) + 1);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_doc is null || _catalog is null) return;

        var g = e.Graphics;
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        float surface = _catalog.TileSize * Zoom;
        float cellH = (_catalog.TileSize + _catalog.Thickness) * Zoom;

        // 1) Terrain — de haut en bas pour que l'épaisseur soit recouverte comme en jeu.
        for (var r = 0; r < _doc.Height; r++)
            for (var c = 0; c < _doc.Width; c++)
            {
                float x = c * surface, y = r * surface;
                var key = _doc.Tiles[r, c];
                var tile = _catalog.TileForKey(key);
                if (tile?.Image is not null)
                    g.DrawImage(tile.Image, x, y, surface, cellH);
                else
                    DrawPlaceholder(g, x, y, surface, key);
            }

        // 2) Grille + overlays sur la surface (64×64) de chaque case.
        using var gridPen = new Pen(Color.FromArgb(70, 255, 255, 255));
        for (var r = 0; r < _doc.Height; r++)
            for (var c = 0; c < _doc.Width; c++)
            {
                float x = c * surface, y = r * surface;
                g.DrawRectangle(gridPen, x, y, surface, surface);

                if (Layer != EditLayer.Terrain || _doc.Spawns[r, c] != MapDocument.EmptySpawn)
                    DrawSpawn(g, x, y, surface, _doc.Spawns[r, c]);
                if (Layer != EditLayer.Terrain || _doc.Objects[r, c] != MapDocument.EmptyObject)
                    DrawObject(g, x, y, surface, _doc.Objects[r, c]);
            }

        // 3) Case survolée.
        if (_hover.X >= 0 && _hover.X < _doc.Width && _hover.Y >= 0 && _hover.Y < _doc.Height)
        {
            using var hoverPen = new Pen(Color.Gold, 2f);
            g.DrawRectangle(hoverPen, _hover.X * surface, _hover.Y * surface, surface, surface);
        }
    }

    private void DrawSpawn(Graphics g, float x, float y, float s, char ch)
    {
        if (ch == MapDocument.EmptySpawn || ch == ' ') return;
        var col = ch switch
        {
            'P' => Color.FromArgb(60, 200, 90),
            'E' => Color.FromArgb(220, 70, 70),
            'B' => Color.FromArgb(170, 90, 220),
            _ => Color.Gray,
        };
        int alpha = Layer == EditLayer.Spawns ? 190 : 90;
        using var b = new SolidBrush(Color.FromArgb(alpha, col));
        g.FillRectangle(b, x + s * 0.15f, y + s * 0.15f, s * 0.7f, s * 0.7f);
        DrawCentered(g, ch.ToString(), x, y, s, s, s * 0.4f);
    }

    private void DrawObject(Graphics g, float x, float y, float s, char ch)
    {
        if (ch == MapDocument.EmptyObject || ch == ' ') return;
        var col = ch switch
        {
            'C' => Color.FromArgb(230, 190, 60),
            'K' => Color.FromArgb(230, 140, 50),
            'k' => Color.FromArgb(240, 230, 90),
            'R' => Color.FromArgb(70, 200, 210),
            'B' => Color.FromArgb(90, 160, 80),
            _ => Color.Gray,
        };
        int alpha = Layer == EditLayer.Objects ? 220 : 110;
        float badge = s * 0.42f;
        float bx = x + s - badge - 2, by = y + 2;
        using var b = new SolidBrush(Color.FromArgb(alpha, col));
        g.FillEllipse(b, bx, by, badge, badge);
        DrawCentered(g, ch.ToString(), bx, by, badge, badge, badge * 0.6f);
    }

    private static void DrawPlaceholder(Graphics g, float x, float y, float s, char key)
    {
        using var b = new SolidBrush(Color.FromArgb(60, 60, 70));
        g.FillRectangle(b, x, y, s, s);
        DrawCentered(g, key.ToString(), x, y, s, s, s * 0.4f);
    }

    private static void DrawCentered(Graphics g, string text, float x, float y, float w, float h, float fontPx)
    {
        if (fontPx < 4) return;
        using var font = new Font("Consolas", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        using var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        using var fg = new SolidBrush(Color.White);
        float tx = x + (w - size.Width) / 2, ty = y + (h - size.Height) / 2;
        g.DrawString(text, font, shadow, tx + 1, ty + 1);
        g.DrawString(text, font, fg, tx, ty);
    }

    // ---- Souris ----
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); PaintCell(e); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var cell = CellAt(e.Location);
        if (cell != _hover) { _hover = cell; Invalidate(); }
        if (e.Button is MouseButtons.Left or MouseButtons.Right) PaintCell(e);
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = new Point(-1, -1);
        Invalidate();
    }

    private void PaintCell(MouseEventArgs e)
    {
        if (_doc is null) return;
        var cell = CellAt(e.Location);
        if (cell.X < 0 || cell.X >= _doc.Width || cell.Y < 0 || cell.Y >= _doc.Height) return;

        bool erase = e.Button == MouseButtons.Right;
        char value = Layer switch
        {
            EditLayer.Spawns => erase ? MapDocument.EmptySpawn : Brush,
            EditLayer.Objects => erase ? MapDocument.EmptyObject : Brush,
            _ => Brush, // le terrain a toujours une tuile ; clic droit = pinceau aussi
        };

        var grid = Layer switch
        {
            EditLayer.Spawns => _doc.Spawns,
            EditLayer.Objects => _doc.Objects,
            _ => _doc.Tiles,
        };
        if (grid[cell.Y, cell.X] == value) return;
        grid[cell.Y, cell.X] = value;
        Invalidate();
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    private Point CellAt(Point mouse)
    {
        if (_catalog is null) return new Point(-1, -1);
        float surface = _catalog.TileSize * Zoom;
        float wx = mouse.X - AutoScrollPosition.X;
        float wy = mouse.Y - AutoScrollPosition.Y;
        return new Point((int)(wx / surface), (int)(wy / surface));
    }
}
