using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ChessArmy.MapEditor;

internal enum EditLayer { Terrain, Spawns, Objects }

/// <summary>
/// Zone de dessin de la map : rend les trois calques (terrain découpé des tilesets, spawns, objets)
/// et peint la case survolée au clic. Reproduit le recouvrement 64×80 du jeu (les rangées du bas
/// cachent l'épaisseur des rangées du haut). Clic gauche = pinceau courant, clic droit = efface.
/// PIPETTE : clic MOLETTE (ou Alt+clic gauche) prélève ce qui est posé sous le curseur dans le calque actif.
/// Un simple clic prélève 1 case et en fait le pinceau (cf. <see cref="PickSingle"/> / <see cref="BrushPicked"/>) ;
/// un GLISSER prélève tout le rectangle survolé en TAMPON, reposé ensuite en bloc au clic gauche (cf.
/// <see cref="CaptureStamp"/> / <see cref="StampAt"/> / <see cref="StampPicked"/>).
/// </summary>
internal sealed class MapCanvas : Panel
{
    private MapDocument? _doc;
    private TileRenderCatalog? _catalog;

    private EditLayer _layer = EditLayer.Terrain;
    /// <summary>Calque édité. Changer de calque annule le tampon multi-cases en cours (il était propre au calque).</summary>
    public EditLayer Layer
    {
        get => _layer;
        set { if (_layer != value) { _layer = value; _stamp = null; } }
    }

    /// <summary>Pinceau spécial « main » : aucune tuile — le clic n'écrit RIEN (mode inspection/déplacement).</summary>
    public const string HandBrush = "\0";

    private string _brush = "h";
    /// <summary>Clé peinte au clic (1 ou 2 caractères pour le terrain ; 1 caractère pour spawns/objets).
    /// <see cref="HandBrush"/> = mode main (ne peint pas ; curseur en main).</summary>
    public string Brush
    {
        get => _brush;
        // Choisir un pinceau simple (palette / pipette 1 case) annule le tampon multi-cases.
        set { _brush = value; _stamp = null; Cursor = value == HandBrush ? Cursors.Hand : Cursors.Cross; }
    }

    public float Zoom { get; set; } = 1f;

    /// <summary>Tier courant (1..3) posé AVEC les spawns ENNEMIS (E/D/O) dans le calque tiers.</summary>
    public char Tier { get; set; } = '1';

    /// <summary>Orientation courante posée AVEC les spawns ENNEMIS dans le calque facing : <c>'v'</c> = bas,
    /// <c>'^'</c> = haut, <see cref="MapDocument.EmptyFacing"/> = auto (le jeu décide selon la moitié du plateau).</summary>
    public char Facing { get; set; } = MapDocument.EmptyFacing;

    private static bool IsEnemySpawn(char c) => c is 'E' or 'D' or 'O';
    /// <summary>Spawns qui portent une ORIENTATION forcée (calque facing) : joueur (P) et ennemis (E/D/O), pas le boss.</summary>
    private static bool AcceptsFacing(char c) => c == 'P' || IsEnemySpawn(c);

    private Point _hover = new(-1, -1);

    // Pipette RECTANGULAIRE en cours (Alt+glisser ou molette+glisser sur plusieurs cases) : coins de la
    // sélection tant que le bouton est maintenu ; capturée en tampon au relâchement.
    private bool _picking;
    private Point _pickStart = new(-1, -1);
    private Point _pickEnd = new(-1, -1);

    // Tampon = bloc de cases prélevé (pipette multi-cases), reposé en un bloc au clic gauche. Null = pinceau simple.
    private Stamp? _stamp;

    /// <summary>Dimensions (cases) du tampon multi-cases en cours, ou <c>null</c> si aucun. Pour le statut.</summary>
    public Size? StampSize => _stamp is { } s ? new Size(s.W, s.H) : null;

    public event EventHandler? MapChanged;

    /// <summary>Levé après un prélèvement à la pipette : <see cref="Brush"/> (et, sur le calque Spawns,
    /// <see cref="Tier"/>/<see cref="Facing"/>) viennent d'être remplacés par le contenu d'une case. La vue
    /// (MainForm) s'y abonne pour resynchroniser la surbrillance de la palette.</summary>
    public event EventHandler? BrushPicked;

    /// <summary>Levé quand un TAMPON multi-cases vient d'être prélevé (pipette rectangulaire). La vue met à
    /// jour le statut (« bloc NxM prélevé »).</summary>
    public event EventHandler? StampPicked;

    /// <summary>Bloc de cases prélevé à la pipette (calque + valeurs brutes), reposé tel quel au clic.</summary>
    private sealed class Stamp
    {
        public EditLayer Layer;
        public int W, H;
        public string[,]? Tiles;                   // Terrain
        public char[,]? Spawns, Tiers, Facing;     // Spawns (+ tier + orientation)
        public char[,]? Objects;                   // Objets
    }

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

    /// <summary>Taille NATIVE de l'export (px) : largeur = colonnes × TileSize ; hauteur = rangées × TileSize
    /// + l'épaisseur de la dernière rangée (comme l'étendue affichée). Null si rien n'est chargé.</summary>
    public Size? ExportSize()
    {
        if (_doc is null || _catalog is null) return null;
        return new Size(_doc.Width * _catalog.TileSize,
                        _doc.Height * _catalog.TileSize + _catalog.Thickness);
    }

    /// <summary>
    /// Exporte le TERRAIN (+ la GRILLE si <paramref name="drawGrid"/>) en PNG, SANS spawns/objets/tier/survol,
    /// à la taille NATIVE des tuiles (cf. <see cref="ExportSize"/>). Reproduit le recouvrement 64×80 comme en
    /// jeu (dessin de haut en bas : chaque rangée couvre l'épaisseur de celle du dessus ; seule la dernière
    /// garde son épaisseur en bas). Les cases sans art restent TRANSPARENTES. Le zoom d'édition n'affecte pas
    /// l'export. La grille reprend le trait de l'éditeur (blanc semi-transparent, 1 px sur la surface 64×64).
    /// </summary>
    public void ExportTilesPng(string path, bool drawGrid = true)
    {
        if (_doc is null || _catalog is null)
            throw new InvalidOperationException("Aucune map chargée.");

        int ts = _catalog.TileSize;
        int thick = _catalog.Thickness;
        var bmp = new Bitmap(_doc.Width * ts, _doc.Height * ts + thick, PixelFormat.Format32bppArgb);
        try
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;   // pixel art net (1:1 en natif)
                g.PixelOffsetMode = PixelOffsetMode.Half;

                // 1) Terrain, de HAUT en BAS : l'épaisseur d'une rangée est recouverte par la rangée du dessous.
                for (var r = 0; r < _doc.Height; r++)
                    for (var c = 0; c < _doc.Width; c++)
                    {
                        var tile = _catalog.TileForKey(_doc.Tiles[r, c]);
                        if (tile?.Image is not null)
                            g.DrawImage(tile.Image, c * ts, r * ts, ts, ts + thick);
                        // pas de placeholder à l'export : une case sans art reste transparente
                    }

                // 2) Grille sur la surface 64×64 de chaque case (même trait que l'éditeur).
                if (drawGrid)
                {
                    using var gridPen = new Pen(Color.FromArgb(70, 255, 255, 255));
                    for (var r = 0; r < _doc.Height; r++)
                        for (var c = 0; c < _doc.Width; c++)
                            g.DrawRectangle(gridPen, c * ts, r * ts, ts, ts);
                }
            }
            bmp.Save(path, ImageFormat.Png);
        }
        finally { bmp.Dispose(); }
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
                    DrawPlaceholder(g, x, y, surface, key);   // clé inconnue/vide → carré gris étiqueté
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
                if (_doc.Tiers[r, c] != MapDocument.EmptyTier)
                    DrawTier(g, x, y, surface, _doc.Tiers[r, c], active: Layer == EditLayer.Spawns);
                if (_doc.Facing[r, c] != MapDocument.EmptyFacing)
                    DrawFacing(g, x, y, surface, _doc.Facing[r, c], active: Layer == EditLayer.Spawns);
                if (Layer != EditLayer.Terrain || _doc.Objects[r, c] != MapDocument.EmptyObject)
                    DrawObject(g, x, y, surface, _doc.Objects[r, c]);
            }

        // 3) Case survolée.
        if (InGrid(_hover))
        {
            using var hoverPen = new Pen(Color.Gold, 2f);
            g.DrawRectangle(hoverPen, _hover.X * surface, _hover.Y * surface, surface, surface);
        }

        // 4) Rectangle de prélèvement en cours (pipette Alt/molette + glisser).
        if (_picking)
        {
            var (x0, y0, x1, y1) = NormalizeRect(_pickStart, _pickEnd);
            using var pen = new Pen(Color.FromArgb(90, 210, 255), 2f);
            using var fill = new SolidBrush(Color.FromArgb(40, 90, 210, 255));
            float rx = x0 * surface, ry = y0 * surface, rw = (x1 - x0 + 1) * surface, rh = (y1 - y0 + 1) * surface;
            g.FillRectangle(fill, rx, ry, rw, rh);
            g.DrawRectangle(pen, rx, ry, rw, rh);
        }
        // Sinon, empreinte du TAMPON à la case survolée (là où le clic gauche le posera).
        else if (_stamp is { } s && s.Layer == Layer && InGrid(_hover))
        {
            using var pen = new Pen(Color.FromArgb(120, 230, 160), 2f);
            g.DrawRectangle(pen, _hover.X * surface, _hover.Y * surface, s.W * surface, s.H * surface);
        }
    }

    /// <summary>Vrai si la case est dans le plateau chargé.</summary>
    private bool InGrid(Point p) =>
        _doc is not null && p.X >= 0 && p.X < _doc.Width && p.Y >= 0 && p.Y < _doc.Height;

    private void DrawSpawn(Graphics g, float x, float y, float s, char ch)
    {
        if (ch == MapDocument.EmptySpawn || ch == ' ') return;
        var col = ch switch
        {
            'P' => Color.FromArgb(60, 200, 90),
            'E' => Color.FromArgb(220, 70, 70),
            'D' => Color.FromArgb(230, 120, 40),
            'O' => Color.FromArgb(200, 40, 140),
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
            'F' => Color.FromArgb(220, 70, 60),
            _ => Color.Gray,
        };
        int alpha = Layer == EditLayer.Objects ? 220 : 110;
        float badge = s * 0.42f;
        float bx = x + s - badge - 2, by = y + 2;
        using var b = new SolidBrush(Color.FromArgb(alpha, col));
        g.FillEllipse(b, bx, by, badge, badge);
        DrawCentered(g, ch.ToString(), bx, by, badge, badge, badge * 0.6f);
    }

    /// <summary>Petit badge de TIER (1/2/3) en haut-gauche d'une case de spawn ennemi (vert/jaune/rouge).</summary>
    private void DrawTier(Graphics g, float x, float y, float s, char ch, bool active)
    {
        var col = ch switch
        {
            '1' => Color.FromArgb(90, 200, 120),
            '2' => Color.FromArgb(235, 205, 90),
            '3' => Color.FromArgb(230, 110, 90),
            _ => Color.Gray,
        };
        int alpha = active ? 235 : 90;
        float badge = s * 0.4f;
        float bx = x + 2, by = y + 2;
        using var b = new SolidBrush(Color.FromArgb(alpha, col));
        g.FillEllipse(b, bx, by, badge, badge);
        DrawCentered(g, ch.ToString(), bx, by, badge, badge, badge * 0.62f);
    }

    /// <summary>Badge d'ORIENTATION en BAS-GAUCHE d'une case de spawn (joueur ou ennemi) : ▼ = regarde vers le
    /// bas (bleu), ▲ = vers le haut (orange). Grisé si le calque Spawns n'est pas actif.</summary>
    private void DrawFacing(Graphics g, float x, float y, float s, char ch, bool active)
    {
        var (glyph, col) = ch == 'v'
            ? ("▼", Color.FromArgb(80, 170, 235))
            : ("▲", Color.FromArgb(235, 150, 70));
        int alpha = active ? 235 : 90;
        float badge = s * 0.4f;
        float bx = x + 2, by = y + s - badge - 2;
        using var b = new SolidBrush(Color.FromArgb(alpha, col));
        g.FillEllipse(b, bx, by, badge, badge);
        DrawCentered(g, glyph, bx, by, badge, badge, badge * 0.62f);
    }

    private static void DrawPlaceholder(Graphics g, float x, float y, float s, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;   // case vide : rien à dessiner
        using var b = new SolidBrush(Color.FromArgb(60, 60, 70));
        g.FillRectangle(b, x, y, s, s);
        DrawCentered(g, key, x, y, s, s, s * 0.4f);
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
    /// <summary>Geste « pipette » : clic MOLETTE, ou Alt maintenu + clic GAUCHE. Un simple clic prélève 1 case ;
    /// un GLISSER prélève tout le rectangle survolé en un TAMPON (reposé en bloc au clic gauche).</summary>
    private static bool IsPickGesture(MouseEventArgs e) =>
        e.Button == MouseButtons.Middle
        || (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Alt) == Keys.Alt);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (IsPickGesture(e)) { StartPick(e); return; }   // pipette (1 case OU rectangle) : ne peint pas
        PaintCell(e, drag: false);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var cell = CellAt(e.Location);
        if (cell != _hover) { _hover = cell; Invalidate(); }
        if (_picking) { _pickEnd = cell; Invalidate(); return; }   // agrandit le rectangle de prélèvement
        if (e.Button is MouseButtons.Left or MouseButtons.Right) PaintCell(e, drag: true);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_picking) { FinishPick(); _picking = false; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = new Point(-1, -1);
        Invalidate();
    }

    private void PaintCell(MouseEventArgs e, bool drag)
    {
        if (_doc is null) return;
        var cell = CellAt(e.Location);
        if (cell.X < 0 || cell.X >= _doc.Width || cell.Y < 0 || cell.Y >= _doc.Height) return;

        // TAMPON multi-cases actif : le clic GAUCHE le repose en bloc, ancré sur la case cliquée. Posé au CLIC
        // (pas en glissant) pour éviter des recouvrements. Le clic droit garde l'effacement case par case.
        if (_stamp is not null && _stamp.Layer == Layer && e.Button == MouseButtons.Left)
        {
            if (!drag) StampAt(cell.X, cell.Y);
            return;
        }

        if (Brush == HandBrush) return;   // mode main : le clic n'écrit rien
        bool erase = e.Button == MouseButtons.Right;

        // Calque SPAWNS : peindre un ennemi (E/D/O) pose AUSSI le tier courant ; peindre un joueur (P) ou un
        // ennemi pose AUSSI l'orientation courante (calque facing) ; poser un boss ou effacer les efface.
        // Répercuté même si le spawn ne change pas, pour permettre de ne changer QUE le tier ou l'orientation
        // (re-cliquer un spawn après avoir changé la sélection).
        if (Layer == EditLayer.Spawns)
        {
            // Pinceau spawn = 1 caractère (P/E/D/O/B) ; le mode main est déjà écarté ci-dessus.
            var spawn = erase ? MapDocument.EmptySpawn : Brush[0];
            var tier = IsEnemySpawn(spawn) ? Tier : MapDocument.EmptyTier;
            var facing = AcceptsFacing(spawn) ? Facing : MapDocument.EmptyFacing;
            if (_doc.Spawns[cell.Y, cell.X] == spawn && _doc.Tiers[cell.Y, cell.X] == tier
                && _doc.Facing[cell.Y, cell.X] == facing) return;
            _doc.Spawns[cell.Y, cell.X] = spawn;
            _doc.Tiers[cell.Y, cell.X] = tier;
            _doc.Facing[cell.Y, cell.X] = facing;
        }
        else if (Layer == EditLayer.Objects)
        {
            var value = erase ? MapDocument.EmptyObject : Brush[0];
            if (_doc.Objects[cell.Y, cell.X] == value) return;
            _doc.Objects[cell.Y, cell.X] = value;
        }
        else   // Terrain : la clé de tuile (1 ou 2 caractères) ; le clic droit repeint le pinceau (pas d'effacement).
        {
            if (_doc.Tiles[cell.Y, cell.X] == Brush) return;
            _doc.Tiles[cell.Y, cell.X] = Brush;
        }
        Invalidate();
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Pipette (1 case ou rectangle) ----
    private void StartPick(MouseEventArgs e)
    {
        _picking = true;
        _pickStart = _pickEnd = CellAt(e.Location);
        Invalidate();
    }

    /// <summary>Fin du geste de pipette : 1 seule case → pipette simple (pinceau + surbrillance palette), un
    /// rectangle de plusieurs cases → TAMPON du calque actif (reposé ensuite en bloc au clic gauche).</summary>
    private void FinishPick()
    {
        if (_doc is null) return;
        var (x0, y0, x1, y1) = NormalizeRect(_pickStart, _pickEnd);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        if (w <= 1 && h <= 1)
            PickSingle(x0, y0);
        else
            CaptureStamp(x0, y0, w, h);
    }

    /// <summary>
    /// Pipette 1 case : lit ce qui est posé dans le CALQUE ACTIF et en fait le pinceau courant — terrain → clé
    /// de tuile ; spawns → spawn + son tier (ennemis) + son orientation ; objets → objet. Une case VIDE n'est
    /// pas prélevée. Prévient la vue via <see cref="BrushPicked"/> pour resynchroniser la palette.
    /// </summary>
    private void PickSingle(int cx, int cy)
    {
        if (_doc is null || cx < 0 || cx >= _doc.Width || cy < 0 || cy >= _doc.Height) return;

        if (Layer == EditLayer.Spawns)
        {
            var spawn = _doc.Spawns[cy, cx];
            if (spawn == MapDocument.EmptySpawn) return;   // case sans spawn : rien à prélever
            Brush = spawn.ToString();
            if (IsEnemySpawn(spawn) && _doc.Tiers[cy, cx] != MapDocument.EmptyTier)
                Tier = _doc.Tiers[cy, cx];                  // récupère aussi le tier posé sur cet ennemi
            if (AcceptsFacing(spawn))
                Facing = _doc.Facing[cy, cx];               // …et son orientation ('.'/v/^)
        }
        else if (Layer == EditLayer.Objects)
        {
            var obj = _doc.Objects[cy, cx];
            if (obj == MapDocument.EmptyObject) return;     // case sans objet : rien à prélever
            Brush = obj.ToString();
        }
        else   // Terrain
        {
            var key = _doc.Tiles[cy, cx];
            if (string.IsNullOrWhiteSpace(key)) return;     // case sans tuile : rien à prélever
            Brush = key;
        }
        BrushPicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Capture le rectangle (largeur <paramref name="w"/> × hauteur <paramref name="h"/>) du calque
    /// actif en TAMPON, à partir du coin haut-gauche (<paramref name="x0"/>, <paramref name="y0"/>).</summary>
    private void CaptureStamp(int x0, int y0, int w, int h)
    {
        if (_doc is null) return;
        var s = new Stamp { Layer = Layer, W = w, H = h };
        if (Layer == EditLayer.Terrain)
        {
            s.Tiles = new string[h, w];
            for (var r = 0; r < h; r++)
                for (var c = 0; c < w; c++)
                    s.Tiles[r, c] = _doc.Tiles[y0 + r, x0 + c];
        }
        else if (Layer == EditLayer.Spawns)
        {
            s.Spawns = new char[h, w]; s.Tiers = new char[h, w]; s.Facing = new char[h, w];
            for (var r = 0; r < h; r++)
                for (var c = 0; c < w; c++)
                {
                    s.Spawns[r, c] = _doc.Spawns[y0 + r, x0 + c];
                    s.Tiers[r, c] = _doc.Tiers[y0 + r, x0 + c];
                    s.Facing[r, c] = _doc.Facing[y0 + r, x0 + c];
                }
        }
        else
        {
            s.Objects = new char[h, w];
            for (var r = 0; r < h; r++)
                for (var c = 0; c < w; c++)
                    s.Objects[r, c] = _doc.Objects[y0 + r, x0 + c];
        }
        _stamp = s;
        StampPicked?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>Repose le tampon en bloc, coin haut-gauche sur (<paramref name="ax"/>, <paramref name="ay"/>),
    /// rogné aux bords du plateau. Écrit exactement les cases prélevées (cases vides comprises = efface).</summary>
    private void StampAt(int ax, int ay)
    {
        if (_doc is null || _stamp is not { } s || s.Layer != Layer) return;
        bool changed = false;
        for (var r = 0; r < s.H; r++)
            for (var c = 0; c < s.W; c++)
            {
                int x = ax + c, y = ay + r;
                if (x < 0 || x >= _doc.Width || y < 0 || y >= _doc.Height) continue;
                if (s.Layer == EditLayer.Terrain)
                {
                    if (_doc.Tiles[y, x] != s.Tiles![r, c]) { _doc.Tiles[y, x] = s.Tiles[r, c]; changed = true; }
                }
                else if (s.Layer == EditLayer.Spawns)
                {
                    if (_doc.Spawns[y, x] != s.Spawns![r, c] || _doc.Tiers[y, x] != s.Tiers![r, c]
                        || _doc.Facing[y, x] != s.Facing![r, c])
                    {
                        _doc.Spawns[y, x] = s.Spawns![r, c];
                        _doc.Tiers[y, x] = s.Tiers![r, c];
                        _doc.Facing[y, x] = s.Facing![r, c];
                        changed = true;
                    }
                }
                else if (_doc.Objects[y, x] != s.Objects![r, c])
                {
                    _doc.Objects[y, x] = s.Objects[r, c]; changed = true;
                }
            }
        if (changed) { Invalidate(); MapChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Rectangle normalisé (coin haut-gauche → bas-droite) de deux cases, rogné au plateau.</summary>
    private (int X0, int Y0, int X1, int Y1) NormalizeRect(Point a, Point b)
    {
        int x0 = Math.Clamp(Math.Min(a.X, b.X), 0, _doc!.Width - 1);
        int x1 = Math.Clamp(Math.Max(a.X, b.X), 0, _doc.Width - 1);
        int y0 = Math.Clamp(Math.Min(a.Y, b.Y), 0, _doc.Height - 1);
        int y1 = Math.Clamp(Math.Max(a.Y, b.Y), 0, _doc.Height - 1);
        return (x0, y0, x1, y1);
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
