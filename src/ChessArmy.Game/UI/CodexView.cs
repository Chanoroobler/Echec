using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;
using ChessArmy.Engine;
using ChessArmy.Engine.Input;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Persistence;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.UI;
using ChessArmy.Engine.UI.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.UI;

/// <summary>
/// CODEX : écran modal autonome (comme <see cref="CommandTreeView"/>) accessible du menu principal ET du
/// menu pause. Deux onglets :
/// <list type="bullet">
/// <item><b>Pions</b> : une page par domaine (Dame/Fou/Cavalier/Tour) affichant l'ARBORESCENCE des classes
/// (base → branches → feuilles). Un pion est en SILHOUETTE noire tant qu'il n'a jamais été obtenu
/// (méta-progression <see cref="SaveService.IsUnitDiscovered"/>), sinon en clair.</item>
/// <item><b>Équipement</b> : grille de tous les items, même logique de silhouette
/// (<see cref="SaveService.IsEquipmentDiscovered"/>).</item>
/// </list>
/// Ne dépend que du <see cref="GameContext"/> (catalogues statiques <see cref="Domaines"/> / <see cref="Equipments"/>,
/// aucune run requise) : gère souris ET manette, avec ses propres caches de textures libérés par <see cref="Unload"/>.
/// </summary>
public sealed class CodexView
{
    private enum Tab { Units, Equipment }

    // Kinds d'éléments focusables (navigation manette géométrique).
    private enum Kind { TabUnits, TabEquip, DomainePrev, DomaineNext, UnitTile, EquipTile, Close }

    private readonly GameContext _ctx;

    // Cache unique de PNG par chemin relatif à Assets/ (unités, équipements, icônes de domaine). Disposé au Unload.
    private readonly Dictionary<string, Texture2D?> _textures = new();

    /// <summary>Fond du panneau : dégradé vertical tramé BLEU (même style que le menu principal), régénéré au changement de taille.</summary>
    private Texture2D? _background;

    private Tab _tab = Tab.Units;
    private int _domaineIndex;   // 0..Domaines.All.Count-1 (page de l'onglet Pions)
    private float _pulse;

    // Navigation manette : index dans la liste des focusables recalculée à chaque frame.
    private int _focus;
    private readonly List<(Rectangle Rect, Kind Kind, int Data)> _focusables = new();

    public CodexView(GameContext context) => _ctx = context;

    public bool IsOpen { get; private set; }

    public void Open()
    {
        IsOpen = true;
        _tab = Tab.Units;
        _domaineIndex = 0;
        _focus = 0;
        _ctx.Sounds.Play("menu_open");
    }

    public void Close()
    {
        IsOpen = false;
        _ctx.Sounds.Play("menu_close");
    }

    /// <summary>Libère les textures chargées (appelé par <c>Scene.Unload</c>).</summary>
    public void Unload()
    {
        foreach (var tex in _textures.Values)
            tex?.Dispose();
        _textures.Clear();
        _background?.Dispose();
        _background = null;
    }

    /// <summary>
    /// (Re)génère le fond dégradé si absent ou si sa taille a changé. MÊMES paliers que le fond du menu
    /// principal (haut un peu plus clair → presque noir en bas).
    /// </summary>
    private void EnsureBackground(int w, int h)
    {
        if (w <= 0 || h <= 0)
            return;
        if (_background != null && _background.Width == w && _background.Height == h)
            return;
        _background?.Dispose();
        _background = Textures.CreateVerticalDitherGradient(_ctx.GraphicsDevice, w, h,
            Palette.Black4, Palette.Navy2, Palette.Black1);
    }

    // ── Mise en page ─────────────────────────────────────────────────────────────
    // Géométrie déterministe recalculée en tête d'Update et de Draw (état = _tab / _domaineIndex).

    private const int Margin = 8;
    private const int TileFramePad = 6; // marge entre le sprite 64 et le cadre de la tuile
    private const int UnitTileW = 96;   // cadre 76 (sprite 64 + marge) + marges latérales
    private const int UnitTileH = 92;   // cadre 76 + nom dessous
    private const int TreePitchMin = 96;   // pas vertical MIN entre deux tiers de l'arbre
    private const int TreePitchMax = 150;  // pas vertical MAX (au-delà, on CENTRE l'arbre plutôt que de l'étirer)
    private const int EquipTile = 52; // côté d'une tuile d'équipement (icône 32 centrée)
    private const int EquipGap = 10;

    private struct Layout
    {
        public Rectangle Panel;
        public Rectangle TabUnits, TabEquip;
        public Rectangle DomLabel, DomPrev, DomNext;   // sélecteur de domaine (onglet Pions)
        public Rectangle Content;                       // zone des tuiles
        public Rectangle Close;
        public List<(UnitClass Cls, Rectangle Rect)> Units;
        public List<(Rectangle From, Rectangle To)> Links;
        public List<(Equipment Item, Rectangle Rect)> Items;
    }

    private Layout BuildLayout(Viewport vp)
    {
        var panel = new Rectangle(Margin, Margin, vp.Width - 2 * Margin, vp.Height - 2 * Margin);
        var l = new Layout { Panel = panel };

        // Onglets tout en haut (pas de titre « CODEX », pour gagner de la hauteur). Hauteur ≥ celle du texte
        // d'onglet (échelle 2) + marge : inchangée en latin (26), agrandie en chinois (glyphe 12px = 24 à l'échelle 2).
        var tabW = 150;
        var tabH = System.Math.Max(26, _ctx.Font.LineHeight(2) + 8);
        var tabsY = panel.Y + 10;
        l.TabUnits = new Rectangle(panel.Center.X - tabW - 6, tabsY, tabW, tabH);
        l.TabEquip = new Rectangle(panel.Center.X + 6, tabsY, tabW, tabH);

        // Pied : bouton FERMER + indice, gardés À L'INTÉRIEUR du cadre.
        l.Close = new Rectangle(panel.Center.X - 60, panel.Bottom - 46, 120, 26);
        var footerTop = panel.Bottom - 52;

        int contentTop, contentH;
        if (_tab == Tab.Units)
        {
            // Région disponible pour le bloc « sélecteur de domaine + arbre », entre le bas des onglets et le pied.
            // Hauteur du sélecteur ≥ nom de domaine (échelle 2) + marge : 22 en latin, agrandie en chinois.
            var selH = System.Math.Max(22, _ctx.Font.LineHeight(2) + 6);
            const int selGap = 8;
            var regionTop = tabsY + tabH + 6;
            var regionH = footerTop - regionTop;

            // Hauteur NATURELLE du bloc : sélecteur + respiration + arbre à son pas MAX (jamais étiré au-delà).
            var maxTier = MaxTier(Domaines.All[_domaineIndex].BaseClass);
            var treeNaturalH = (maxTier > 1 ? (maxTier - 1) * TreePitchMax : 0) + UnitTileH;
            var blockH = selH + selGap + treeNaturalH;

            // Grand écran (région plus haute que le bloc) : on CENTRE le bloc verticalement ; sinon on cale en
            // haut et l'arbre remplit ce qui reste (cas 1080p, inchangé).
            var centered = regionH > blockH;
            var selY = centered ? regionTop + (regionH - blockH) / 2 : regionTop;

            var selW = 190;
            l.DomLabel = new Rectangle(panel.Center.X - selW / 2, selY, selW, selH);
            l.DomPrev = new Rectangle(l.DomLabel.X - 30, selY, 26, selH);
            l.DomNext = new Rectangle(l.DomLabel.Right + 4, selY, 26, selH);

            contentTop = selY + selH + selGap;
            contentH = centered ? treeNaturalH : footerTop - contentTop;
        }
        else
        {
            contentTop = tabsY + tabH + 12;
            contentH = footerTop - contentTop;
        }

        l.Content = new Rectangle(panel.X + 10, contentTop, panel.Width - 20, contentH);

        if (_tab == Tab.Units)
        {
            l.Units = new List<(UnitClass, Rectangle)>();
            l.Links = new List<(Rectangle, Rectangle)>();
            LayoutTree(Domaines.All[_domaineIndex].BaseClass, l.Content, l.Units, l.Links);
        }
        else
        {
            l.Items = LayoutGrid(Equipments.All, l.Content);
        }
        return l;
    }

    /// <summary>
    /// Dispose l'arbre d'une classe façon « arbre généalogique » : la base en HAUT, ses évolutions dessous,
    /// chaque parent centré sur l'empan de ses feuilles. Gère un nombre quelconque d'évolutions (0/1/2).
    /// </summary>
    private static void LayoutTree(UnitClass root, Rectangle area,
        List<(UnitClass, Rectangle)> tiles, List<(Rectangle, Rectangle)> links)
    {
        var leaves = new List<UnitClass>();
        CollectLeaves(root, leaves);
        var maxTier = MaxTier(root);

        var colW = leaves.Count > 0 ? area.Width / leaves.Count : area.Width;
        var leafX = new Dictionary<UnitClass, int>();
        for (var i = 0; i < leaves.Count; i++)
            leafX[leaves[i]] = area.X + i * colW + colW / 2;

        var rowPitch = maxTier > 1
            ? Math.Clamp((area.Height - UnitTileH) / (maxTier - 1), TreePitchMin, TreePitchMax)
            : 0;

        var rectByClass = new Dictionary<UnitClass, Rectangle>();
        Place(root);

        int Place(UnitClass c)
        {
            int cx;
            if (c.IsLeaf)
            {
                cx = leafX.TryGetValue(c, out var x) ? x : area.Center.X;
            }
            else
            {
                var xs = c.Evolutions.Select(Place).ToList();
                cx = (xs.Min() + xs.Max()) / 2;
            }
            var y = area.Y + (c.Tier - 1) * rowPitch;
            var rect = new Rectangle(cx - UnitTileW / 2, y, UnitTileW, UnitTileH);
            rectByClass[c] = rect;
            tiles.Add((c, rect));
            return cx;
        }

        foreach (var (c, rect) in tiles)
            foreach (var evo in c.Evolutions)
                links.Add((rect, rectByClass[evo]));
    }

    private static void CollectLeaves(UnitClass c, List<UnitClass> acc)
    {
        if (c.IsLeaf) { acc.Add(c); return; }
        foreach (var e in c.Evolutions)
            CollectLeaves(e, acc);
    }

    private static int MaxTier(UnitClass c) =>
        c.IsLeaf ? c.Tier : c.Evolutions.Max(MaxTier);

    private static List<(Equipment, Rectangle)> LayoutGrid(IReadOnlyList<Equipment> items, Rectangle area)
    {
        var list = new List<(Equipment, Rectangle)>();
        var cols = Math.Max(1, Math.Min(12, (area.Width + EquipGap) / (EquipTile + EquipGap)));
        var rows = (items.Count + cols - 1) / cols;
        var gridW = cols * EquipTile + (cols - 1) * EquipGap;
        var startX = area.X + Math.Max(0, (area.Width - gridW) / 2);
        var pitch = EquipTile + EquipGap;
        // Centre verticalement si peu de rangées.
        var gridH = rows * EquipTile + (rows - 1) * EquipGap;
        var startY = area.Y + Math.Max(0, (area.Height - gridH) / 2);

        for (var i = 0; i < items.Count; i++)
        {
            var col = i % cols;
            var row = i / cols;
            list.Add((items[i], new Rectangle(startX + col * pitch, startY + row * pitch, EquipTile, EquipTile)));
        }
        return list;
    }

    // ── Mise à jour ─────────────────────────────────────────────────────────────

    /// <summary>Traite une image ; renvoie <see cref="IsOpen"/> (faux la frame où le joueur ferme).</summary>
    public bool Update(Viewport vp, float dt)
    {
        _pulse += dt;
        var lay = BuildLayout(vp);
        BuildFocusables(lay);
        _focus = Math.Clamp(_focus, 0, Math.Max(0, _focusables.Count - 1));

        // Fermeture : Échap (clavier), B (manette) ou Start.
        if (_ctx.Input.WasKeyPressed(Keys.Escape) || _ctx.Input.WasCancelPressed || _ctx.Input.WasMenuPressed)
        {
            Close();
            return false;
        }

        // Gâchettes/bumpers : bascule d'onglet, quel que soit le périphérique.
        if (_ctx.Input.WasLeftShoulderPressed || _ctx.Input.WasRightShoulderPressed)
        {
            SetTab(_tab == Tab.Units ? Tab.Equipment : Tab.Units);
            return true;
        }

        if (_ctx.Input.UsingGamepad)
        {
            if (_ctx.Input.Nav(NavDir.Up)) MoveFocus(NavDir.Up);
            if (_ctx.Input.Nav(NavDir.Down)) MoveFocus(NavDir.Down);
            if (_ctx.Input.Nav(NavDir.Left)) MoveFocus(NavDir.Left);
            if (_ctx.Input.Nav(NavDir.Right)) MoveFocus(NavDir.Right);
            if (_ctx.Input.WasConfirmPressed && _focus < _focusables.Count)
                Activate(_focusables[_focus].Kind, _focusables[_focus].Data);
            return true;
        }

        // Souris : clic direct sur les contrôles (les tuiles ne servent qu'à l'infobulle au survol).
        if (_ctx.Input.WasLeftClicked)
        {
            var p = _ctx.Input.MousePosition;
            if (lay.Close.Contains(p)) { Close(); return false; }
            if (lay.TabUnits.Contains(p)) SetTab(Tab.Units);
            else if (lay.TabEquip.Contains(p)) SetTab(Tab.Equipment);
            else if (_tab == Tab.Units && lay.DomPrev.Contains(p)) StepDomaine(-1);
            else if (_tab == Tab.Units && lay.DomNext.Contains(p)) StepDomaine(+1);
        }
        return true;
    }

    private void BuildFocusables(Layout lay)
    {
        _focusables.Clear();
        _focusables.Add((lay.TabUnits, Kind.TabUnits, 0));
        _focusables.Add((lay.TabEquip, Kind.TabEquip, 0));
        if (_tab == Tab.Units)
        {
            _focusables.Add((lay.DomPrev, Kind.DomainePrev, 0));
            _focusables.Add((lay.DomNext, Kind.DomaineNext, 0));
            for (var i = 0; i < lay.Units.Count; i++)
                _focusables.Add((lay.Units[i].Rect, Kind.UnitTile, i));
        }
        else
        {
            for (var i = 0; i < lay.Items.Count; i++)
                _focusables.Add((lay.Items[i].Rect, Kind.EquipTile, i));
        }
        _focusables.Add((lay.Close, Kind.Close, 0));
    }

    private void Activate(Kind kind, int data)
    {
        switch (kind)
        {
            case Kind.TabUnits: SetTab(Tab.Units); break;
            case Kind.TabEquip: SetTab(Tab.Equipment); break;
            case Kind.DomainePrev: StepDomaine(-1); break;
            case Kind.DomaineNext: StepDomaine(+1); break;
            case Kind.Close: Close(); break;
            // UnitTile / EquipTile : rien à activer (le focus suffit à afficher l'infobulle).
        }
    }

    private void SetTab(Tab tab)
    {
        if (_tab == tab) { _ctx.Sounds.Play("menu_click"); return; }
        _tab = tab;
        // Garde le focus sur l'onglet choisi (index 0 ou 1) pour un retour visuel cohérent.
        _focus = tab == Tab.Units ? 0 : 1;
        _ctx.Sounds.Play("menu_click");
    }

    private void StepDomaine(int dir)
    {
        var n = Domaines.All.Count;
        _domaineIndex = (_domaineIndex + dir % n + n) % n;
        _ctx.Sounds.Play("menu_click");
    }

    /// <summary>
    /// Navigation par RANGÉES (et non plus « plus proche géométrique »). Les focusables sont regroupés en
    /// rangées par leur Y (onglets, flèches de domaine, chaque tier de l'arbre / chaque ligne de la grille,
    /// puis FERMER). Haut/Bas change de rangée en gardant la colonne la plus proche en X ; Gauche/Droite se
    /// déplace dans la rangée. Ainsi « Bas » depuis le pion de base va bien au tier suivant (et non à FERMER,
    /// qui est pourtant pile en dessous au centre) — cf. retour joueur.
    /// </summary>
    private void MoveFocus(NavDir dir)
    {
        if (_focusables.Count == 0)
            return;
        var rows = BuildFocusRows();
        var (ri, ci) = LocateFocus(rows);
        if (ri < 0)
            return;

        var target = dir switch
        {
            NavDir.Left => ci > 0 ? rows[ri][ci - 1] : -1,
            NavDir.Right => ci < rows[ri].Count - 1 ? rows[ri][ci + 1] : -1,
            NavDir.Up => ri > 0 ? ClosestInRow(rows[ri - 1], _focusables[_focus].Rect.Center.X) : -1,
            NavDir.Down => ri < rows.Count - 1 ? ClosestInRow(rows[ri + 1], _focusables[_focus].Rect.Center.X) : -1,
            _ => -1,
        };

        if (target >= 0 && target != _focus)
        {
            _focus = target;
            _ctx.Sounds.Play("menu_click");
        }
    }

    /// <summary>Regroupe les focusables en rangées par Y (centre), chaque rangée triée gauche→droite.</summary>
    private List<List<int>> BuildFocusRows()
    {
        var order = Enumerable.Range(0, _focusables.Count).ToList();
        order.Sort((a, b) => _focusables[a].Rect.Center.Y.CompareTo(_focusables[b].Rect.Center.Y));
        var rows = new List<List<int>>();
        foreach (var i in order)
        {
            var cy = _focusables[i].Rect.Center.Y;
            if (rows.Count == 0 || Math.Abs(cy - _focusables[rows[^1][0]].Rect.Center.Y) > 24)
                rows.Add(new List<int>());
            rows[^1].Add(i);
        }
        foreach (var row in rows)
            row.Sort((a, b) => _focusables[a].Rect.Center.X.CompareTo(_focusables[b].Rect.Center.X));
        return rows;
    }

    private (int row, int col) LocateFocus(List<List<int>> rows)
    {
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Count; c++)
                if (rows[r][c] == _focus)
                    return (r, c);
        return (-1, -1);
    }

    /// <summary>Index (dans _focusables) de l'élément de la rangée dont le centre X est le plus proche de <paramref name="x"/>.</summary>
    private int ClosestInRow(List<int> row, int x)
    {
        var best = row[0];
        var bestDist = int.MaxValue;
        foreach (var i in row)
        {
            var d = Math.Abs(_focusables[i].Rect.Center.X - x);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // ── Rendu ────────────────────────────────────────────────────────────────────

    public void Draw(SpriteBatch sb, Viewport vp)
    {
        var lay = BuildLayout(vp);
        var gp = _ctx.Input.UsingGamepad;
        var mouse = _ctx.Input.MousePosition;

        var inner = Inflate(lay.Panel, -3);
        EnsureBackground(inner.Width, inner.Height);

        sb.Begin(samplerState: SamplerState.PointClamp);
        Fill(sb, new Rectangle(0, 0, vp.Width, vp.Height), Palette.Black1 * 0.72f);
        _ctx.Style.DrawPanel(sb, lay.Panel);
        if (_background != null)
            sb.Draw(_background, inner, Color.White);   // fond dégradé bleu tramé

        // Kind sous le focus manette : pilote la surbrillance des onglets et des flèches (sans quoi le
        // « curseur » disparaît quand on quitte les tuiles pour un onglet — cf. retour joueur).
        Kind? fk = gp && _focus < _focusables.Count ? _focusables[_focus].Kind : null;

        DrawTab(sb, lay.TabUnits, Loc.T("codex.tab_units"), _tab == Tab.Units, fk == Kind.TabUnits, mouse, gp);
        DrawTab(sb, lay.TabEquip, Loc.T("codex.tab_equipment"), _tab == Tab.Equipment, fk == Kind.TabEquip, mouse, gp);

        // Compteur de découvertes du contenu affiché, dans le coin haut-gauche.
        var (found, total) = Progress();
        _ctx.Font.Draw(sb, Loc.T("codex.progress", found, total),
            new Vector2(lay.Panel.X + 12, lay.Panel.Y + 14), 1, Palette.Blue1);

        // Élément survolé (souris) ou focus (manette) : sert à l'infobulle et à la surbrillance.
        var hoverIndex = HoverIndex(lay, gp, mouse);

        if (_tab == Tab.Units)
        {
            DrawDomaineSelector(sb, lay, mouse, gp, fk);
            foreach (var (from, to) in lay.Links)
                DrawLink(sb, from, to);
            for (var i = 0; i < lay.Units.Count; i++)
                DrawUnitTile(sb, lay.Units[i].Cls, lay.Units[i].Rect,
                    _ctx.Saves.IsUnitDiscovered(lay.Units[i].Cls.Asset), IsTileHighlighted(lay, i, hoverIndex, Kind.UnitTile));
        }
        else
        {
            for (var i = 0; i < lay.Items.Count; i++)
                DrawEquipTile(sb, lay.Items[i].Item, lay.Items[i].Rect,
                    _ctx.Saves.IsEquipmentDiscovered(lay.Items[i].Item.Id), IsTileHighlighted(lay, i, hoverIndex, Kind.EquipTile));
        }

        DrawCloseButton(sb, lay, mouse, gp);

        // Infobulle de détail par-dessus tout le reste.
        DrawTooltip(sb, lay, hoverIndex, vp);
        sb.End();
    }

    private (int found, int total) Progress()
    {
        if (_tab == Tab.Units)
        {
            var all = new List<UnitClass>();
            foreach (var d in Domaines.All)
                Flatten(d.BaseClass, all);
            return (all.Count(c => _ctx.Saves.IsUnitDiscovered(c.Asset)), all.Count);
        }
        return (Equipments.All.Count(e => _ctx.Saves.IsEquipmentDiscovered(e.Id)), Equipments.All.Count);
    }

    private static void Flatten(UnitClass c, List<UnitClass> acc)
    {
        acc.Add(c);
        foreach (var e in c.Evolutions)
            Flatten(e, acc);
    }

    /// <summary>Index dans _focusables de l'élément survolé/focus, ou -1.</summary>
    private int HoverIndex(Layout lay, bool gp, Point mouse)
    {
        if (gp)
            return _focus < _focusables.Count ? _focus : -1;
        for (var i = 0; i < _focusables.Count; i++)
            if (_focusables[i].Rect.Contains(mouse))
                return i;
        return -1;
    }

    private bool IsTileHighlighted(Layout lay, int tileIndex, int hoverIndex, Kind kind)
    {
        if (hoverIndex < 0 || hoverIndex >= _focusables.Count)
            return false;
        var f = _focusables[hoverIndex];
        return f.Kind == kind && f.Data == tileIndex;
    }

    private void DrawTab(SpriteBatch sb, Rectangle r, string label, bool active, bool focusedGp, Point mouse, bool gp)
    {
        var hover = !gp && r.Contains(mouse);
        var dy = _ctx.Style.DrawButton(sb, r, UiStyle.StateOf(hover || active || focusedGp, false));
        var area = r; area.Offset(0, dy);
        _ctx.Font.DrawCentered(sb, label, area, 2, active || focusedGp ? Palette.Yellow2 : Palette.White);
        // Liseré or : plein sur l'onglet ACTIF, épais sur l'onglet sous le focus manette (le « curseur »).
        if (active)
            Border(sb, Inflate(r, 1), Palette.Yellow1, 2);
        if (focusedGp)
            Border(sb, Inflate(r, 3), Palette.Yellow2, 2);
    }

    private void DrawDomaineSelector(SpriteBatch sb, Layout lay, Point mouse, bool gp, Kind? fk)
    {
        var domaine = Domaines.All[_domaineIndex].Id;

        // Nom du domaine centré dans un renfoncement (plus d'icône de déplacement : c'est la carte du pion,
        // au survol, qui montre déjà le motif de déplacement — cf. retour joueur).
        _ctx.Style.DrawRecessed(sb, lay.DomLabel);
        _ctx.Font.DrawCentered(sb, Loc.TOr($"domaine.{domaine}".ToLowerInvariant(), domaine.ToString().ToUpperInvariant()),
            lay.DomLabel, 2, Palette.Cyan1);

        Arrow(sb, lay.DomPrev, "<", mouse, gp, fk == Kind.DomainePrev);
        Arrow(sb, lay.DomNext, ">", mouse, gp, fk == Kind.DomaineNext);
    }

    private void Arrow(SpriteBatch sb, Rectangle r, string glyph, Point mouse, bool gp, bool focusedGp)
    {
        var hover = !gp && r.Contains(mouse);
        var dy = _ctx.Style.DrawButton(sb, r, UiStyle.StateOf(hover || focusedGp, hover && _ctx.Input.IsLeftDown));
        var area = r; area.Offset(0, dy);
        _ctx.Font.DrawCentered(sb, glyph, area, 2, focusedGp ? Palette.Yellow2 : Palette.White);
        if (focusedGp)
            Border(sb, Inflate(r, 3), Palette.Yellow2, 2);
    }

    /// <summary>Tuile d'un pion : sprite (silhouette noire si non découvert) + nom (ou « ??? »).</summary>
    private void DrawUnitTile(SpriteBatch sb, UnitClass cls, Rectangle rect, bool revealed, bool highlighted)
    {
        // Sprite 64×64 NATIF (jamais redimensionné) centré dans un cadre plus grand : une marge tout autour
        // évite que le pion (tête/socle) colle la bordure et paraisse déborder (cf. retour joueur).
        var sprite = new Rectangle(rect.X + (rect.Width - 64) / 2, rect.Y + TileFramePad, 64, 64);
        var frame = Inflate(sprite, TileFramePad);
        _ctx.Style.DrawRecessed(sb, frame);
        Fill(sb, Inflate(frame, -2), Palette.Blue1 * 0.5f);

        var tex = Tex($"Units/{cls.Asset}_front") ?? Tex($"Units/{cls.Asset}");
        if (tex != null)
            sb.Draw(tex, sprite, revealed ? Color.White : Color.Black);   // silhouette noire si non découvert
        else if (!revealed)
            Fill(sb, sprite, Palette.Black1);

        // En démo, les pions au-dessus du plafond de tier sont hors démo : tampon DEMO sur la silhouette.
        if (_ctx.Settings.IsDemo && cls.Tier > ChessArmy.Core.Campaign.Run.MaxUnitTier)
            _ctx.Font.DrawCentered(sb, Loc.T("menu.demo"), sprite, 2, Palette.Yellow2);

        if (highlighted)
            Border(sb, Inflate(frame, 3), Palette.Yellow2, 2);

        var name = revealed ? UnitName(cls) : "???";
        _ctx.Font.DrawCentered(sb, name, new Rectangle(rect.X - 8, frame.Bottom + 3, rect.Width + 16, 10), 1,
            revealed ? Palette.White : Palette.Grey);
    }

    /// <summary>Tuile d'un équipement : icône 32×32 (silhouette noire si non découvert), liseré teinté par rareté.</summary>
    private void DrawEquipTile(SpriteBatch sb, Equipment item, Rectangle rect, bool revealed, bool highlighted)
    {
        _ctx.Style.DrawRecessed(sb, rect);
        Fill(sb, Inflate(rect, -2), Palette.Blue1 * 0.5f);

        var icon = Tex($"Equipment/{item.Icon}");
        var iconBox = new Rectangle(rect.Center.X - 16, rect.Center.Y - 16, 32, 32);
        if (icon != null)
            sb.Draw(icon, iconBox, revealed ? Color.White : Color.Black);
        else if (!revealed)
            Fill(sb, iconBox, Palette.Black1);
        else
        {
            Fill(sb, iconBox, item.GrantsAnyTrait ? Palette.Yellow1 : Palette.Cyan1);
            _ctx.Font.DrawCentered(sb, item.Name.Length > 0 ? item.Name[..1].ToUpperInvariant() : "?", iconBox, 2, Palette.Black1);
        }

        // En démo, tampon DEMO sur les équipements réservés au jeu complet (jamais découvrables en démo).
        if (_ctx.Settings.IsDemo && !item.Demo)
            _ctx.Font.DrawCentered(sb, Loc.T("menu.demo"), iconBox, 2, Palette.Yellow2);

        Border(sb, rect, revealed ? RarityColor(item.Rarity) : Palette.Black5, revealed ? 2 : 1);
        if (highlighted)
            Border(sb, Inflate(rect, 3), Palette.Yellow2, 2);
    }

    /// <summary>Trait en équerre reliant un parent (au-dessus) à une évolution (dessous).</summary>
    private void DrawLink(SpriteBatch sb, Rectangle parent, Rectangle child)
    {
        const int t = 2;
        var midY = (parent.Bottom + child.Top) / 2;
        Fill(sb, new Rectangle(parent.Center.X - t / 2, parent.Bottom, t, midY - parent.Bottom + t), Palette.Blue2);
        Fill(sb, new Rectangle(child.Center.X - t / 2, midY, t, child.Top - midY), Palette.Blue2);
        var x0 = Math.Min(parent.Center.X, child.Center.X);
        var x1 = Math.Max(parent.Center.X, child.Center.X);
        Fill(sb, new Rectangle(x0, midY, x1 - x0 + t, t), Palette.Blue2);
    }

    private void DrawCloseButton(SpriteBatch sb, Layout lay, Point mouse, bool gp)
    {
        var focused = gp && _focus < _focusables.Count && _focusables[_focus].Kind == Kind.Close;
        var hover = !gp && lay.Close.Contains(mouse);
        var dy = _ctx.Style.DrawButton(sb, lay.Close, UiStyle.StateOf(hover || focused, hover && _ctx.Input.IsLeftDown));
        var area = lay.Close; area.Offset(0, dy);
        if (focused)
            Border(sb, Inflate(area, 2), Palette.Yellow2, 2);
        _ctx.Font.DrawCentered(sb, Loc.T("codex.close"), area, 1, hover || focused ? Palette.Yellow2 : Palette.White);

        var hint = Loc.T(gp ? "codex.hint_gp" : "codex.hint");
        _ctx.Font.DrawCentered(sb, hint, new Rectangle(lay.Panel.X, lay.Close.Bottom + 2, lay.Panel.Width, 10), 1, Palette.Blue1);
    }

    // ── Infobulle de détail ──────────────────────────────────────────────────────

    private void DrawTooltip(SpriteBatch sb, Layout lay, int hoverIndex, Viewport vp)
    {
        if (hoverIndex < 0 || hoverIndex >= _focusables.Count)
            return;
        // Les focusables datent du dernier Update ; le layout est reconstruit au Draw. On borne donc l'accès
        // (l'onglet a pu changer dans le même Update, laissant une liste et un layout momentanément désaccordés).
        // Zone de rabat des infobulles = INTÉRIEUR du panneau, AU-DESSUS du pied (bouton FERMER) : elles ne
        // touchent donc jamais la bordure du cadre ni ne recouvrent FERMER (cf. retour « le pion sort du cadre »).
        var bx0 = lay.Panel.X + 6;
        var by0 = lay.Panel.Y + 6;
        var bounds = new Rectangle(bx0, by0, lay.Panel.Right - 6 - bx0, lay.Close.Y - 6 - by0);

        var f = _focusables[hoverIndex];
        if (f.Kind == Kind.UnitTile && lay.Units != null && f.Data < lay.Units.Count)
        {
            var cls = lay.Units[f.Data].Cls;
            // Pion DÉCOUVERT : la MÊME carte qu'en jeu. Non découvert : rien (la silhouette + « ??? » suffisent).
            if (_ctx.Saves.IsUnitDiscovered(cls.Asset))
                DrawUnitCardTooltip(sb, cls, f.Rect, bounds);
        }
        else if (f.Kind == Kind.EquipTile && lay.Items != null && f.Data < lay.Items.Count)
            DrawEquipTooltip(sb, lay.Items[f.Data].Item, f.Rect, bounds);
    }

    // ── Carte de pion (IDENTIQUE à celle du jeu) ──────────────────────────────────
    // Reproduit la disposition de GameplayScene.DrawCardLayout (chemin « découvert », sans équipement /
    // arbre / kills, sans objet sur la carte) : mêmes constantes, mêmes assets, mêmes couleurs.

    private const int CardPad = 12;
    private const int CardTierW = 23, CardTierH = 9;
    private const int CardDomaine = 39;
    private const int CardW = 168, CardH = 340;

    /// <summary>Pose la carte du pion à côté de la tuile, TOUJOURS rabattue dans <paramref name="bounds"/>.</summary>
    private void DrawUnitCardTooltip(SpriteBatch sb, UnitClass c, Rectangle anchor, Rectangle bounds)
    {
        var x = anchor.Right + 8;
        if (x + CardW > bounds.Right)
            x = anchor.X - CardW - 8;   // pas la place à droite : bascule à gauche de la tuile
        x = Math.Clamp(x, bounds.X, Math.Max(bounds.X, bounds.Right - CardW));
        var y = Math.Clamp(anchor.Center.Y - CardH / 2, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - CardH));
        DrawUnitCard(sb, c, Domaines.All[_domaineIndex].Id, new Rectangle(x, y, CardW, CardH));
    }

    private void DrawUnitCard(SpriteBatch sb, UnitClass c, Domaine domaine, Rectangle rect)
    {
        _ctx.Style.DrawPanel(sb, rect);
        var y = rect.Y + CardPad;

        DrawCardTierAndDomaine(sb, c.Tier, domaine, rect);

        // Boîte de titre à la hauteur réelle du nom (14 latin / 24 cjk) : pas de débordement vers l'en-tête.
        // Échelle repliée en 1 si le nom est trop long pour la carte (cf. UnitCardRenderer.TitleScale).
        var name = UnitName(c);
        var nameScale = UnitCardRenderer.TitleScale(_ctx.Font, name, rect.Width, CardPad);
        var titleH = _ctx.Font.LineHeight(nameScale);
        _ctx.Font.DrawCentered(sb, name, new Rectangle(rect.X, y, rect.Width, titleH), nameScale, Palette.White);
        y += titleH + 8;

        var sprite = new Rectangle(rect.X + (rect.Width - 64) / 2, y, 64, 64);
        var front = Tex($"Units/{c.Asset}_front") ?? Tex($"Units/{c.Asset}");
        if (front != null)
            sb.Draw(front, sprite, Color.White);
        y = sprite.Bottom + 6;

        var dom = new Rectangle(rect.X + (rect.Width - CardDomaine) / 2, y, CardDomaine, CardDomaine);
        DrawCardDomaine(sb, domaine, dom);
        y = dom.Bottom + 10;

        var bar = new Rectangle(rect.X + CardPad, y, rect.Width - 2 * CardPad, 14);
        DrawCardHpBar(sb, bar, c.MaxHp);
        y = bar.Bottom + 2;
        _ctx.Font.DrawCentered(sb, $"{c.MaxHp}/{c.MaxHp}", new Rectangle(rect.X, y, rect.Width, 8), 1, Palette.White);
        y += 14;

        y = DrawCardStat(sb, rect, y, "deg", Loc.T("stat.power"), c.Damage.ToString(), Palette.Brown3);
        y = DrawCardStat(sb, rect, y, "dep", Loc.T("stat.movement"), c.MoveRange.ToString(), Palette.Cyan2);
        DrawCardStat(sb, rect, y, "tir", Loc.T("stat.range"), c.AttackRange.ToString(), Palette.Yellow2);

        // Mots-clés (traits) séparés par « | », ancrés en bas et empilés vers le haut.
        var kws = UnitKeywordLabels(c);
        if (kws.Count > 0)
        {
            var lines = Wrap(string.Join(" | ", kws), rect.Width - 2 * CardPad, 1);
            var ty = rect.Bottom - CardPad - lines.Count * 9;
            foreach (var line in lines)
            {
                _ctx.Font.DrawCentered(sb, line, new Rectangle(rect.X, ty, rect.Width, 8), 1, Palette.Cyan1);
                ty += 9;
            }
        }
    }

    /// <summary>Marge haute : icône de TIER + NOM DU DOMAINE, l'ensemble centré (cf. carte de jeu).</summary>
    private void DrawCardTierAndDomaine(SpriteBatch sb, int tier, Domaine domaine, Rectangle rect)
    {
        var name = Loc.TOr($"domaine.{domaine}".ToLowerInvariant(), domaine.ToString().ToUpperInvariant());
        const int gap = 5;
        var nameW = _ctx.Font.Measure(name, 1);
        var startX = rect.X + (rect.Width - (CardTierW + gap + nameW)) / 2;
        DrawCardTier(sb, tier, new Rectangle(startX, rect.Y + 2, CardTierW, CardTierH));
        _ctx.Font.Draw(sb, name, new Vector2(startX + CardTierW + gap, rect.Y + 3), 1, Palette.Cyan1);
    }

    private void DrawCardTier(SpriteBatch sb, int tier, Rectangle area)
    {
        if (Tex($"Icons/tier_{tier}") is { } png)
        {
            sb.Draw(png, area, Color.White);   // 23×9 natif, pas de déformation
            return;
        }
        Fill(sb, Inflate(area, 1), Palette.Black1);
        Fill(sb, area, Palette.Navy2);
        _ctx.Font.DrawCentered(sb, $"T{tier}", area, 1, Palette.White);
    }

    private void DrawCardDomaine(SpriteBatch sb, Domaine domaine, Rectangle area)
    {
        if (Tex($"Icons/domaine_{domaine}".ToLowerInvariant()) is { } png)
        {
            DrawFit(sb, png, area);
            return;
        }
        var color = domaine switch
        {
            Domaine.Cavalier => Palette.Green1,
            Domaine.Tour => Palette.Navy1,
            Domaine.Dame => Palette.Yellow1,
            _ => Palette.Grey,
        };
        Fill(sb, Inflate(area, 1), Palette.Black1);
        Fill(sb, area, color);
        _ctx.Font.DrawCentered(sb, domaine.ToString()[..1].ToUpperInvariant(), area, 2, Palette.Black1);
    }

    private int DrawCardStat(SpriteBatch sb, Rectangle card, int y, string iconKey, string label, string value, Color valueColor)
    {
        const int iconSize = 32;
        var icon = new Rectangle(card.X + CardPad, y, iconSize, iconSize);
        if (Tex($"Icons/stat_{iconKey}") is { } png)
            DrawFit(sb, png, icon);
        else
        {
            Fill(sb, Inflate(icon, 1), Palette.Black1);
            Fill(sb, icon, Palette.Navy2);
            _ctx.Font.DrawCentered(sb, iconKey.ToUpperInvariant()[..1], icon, 2, valueColor);
        }

        _ctx.Font.Draw(sb, label, new Vector2(icon.Right + 8, y + (iconSize - 7) / 2), 1, Palette.Blue1);
        var vw = _ctx.Font.Measure(value, 2);
        _ctx.Font.Draw(sb, value, new Vector2(card.Right - CardPad - vw, y + (iconSize - 14) / 2), 2, valueColor);
        return y + iconSize + 4;
    }

    private void DrawCardHpBar(SpriteBatch sb, Rectangle area, int maxHp)
    {
        if (maxHp <= 0)
            return;
        const int gap = 1;
        for (var i = 0; i < maxHp; i++)
        {
            var left = area.X + (int)Math.Round((double)i * area.Width / maxHp);
            var right = area.X + (int)Math.Round((double)(i + 1) * area.Width / maxHp);
            var w = Math.Max(1, right - left - (i < maxHp - 1 ? gap : 0));
            Fill(sb, new Rectangle(left, area.Y, w, area.Height), Palette.Purple5);   // pion neutre : PV pleins
        }
    }

    /// <summary>Découpe un texte en lignes tenant dans <paramref name="maxWidth"/> px (échelle donnée).</summary>
    private List<string> Wrap(string text, int maxWidth, int scale)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (_ctx.Font.Measure(candidate, scale) > maxWidth && line.Length > 0)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = candidate;
            }
        }
        if (line.Length > 0)
            lines.Add(line);
        return lines.Count > 0 ? lines : new List<string> { "" };
    }

    private void DrawEquipTooltip(SpriteBatch sb, Equipment e, Rectangle anchor, Rectangle bounds)
    {
        var revealed = _ctx.Saves.IsEquipmentDiscovered(e.Id);
        var lines = new List<(string, Color)>();
        if (revealed)
        {
            lines.Add((Loc.T(RarityKey(e.Rarity)), RarityColor(e.Rarity)));
            foreach (var eff in e.Effects)
            {
                if (eff.Trait is { } t)
                    lines.Add((UnitKeywords.For(t).Label, Palette.Cyan1));
                else
                    lines.Add((Loc.T("equip.stat_bonus", eff.Amount, StatLabel(eff.Stat)), Palette.White));
            }
        }
        else
        {
            lines.Add((Loc.T("codex.undiscovered"), Palette.Grey));
        }
        DrawTooltipPanel(sb, revealed ? EquipmentNames.Localized(e) : "???", revealed ? Palette.Yellow2 : Palette.Grey, lines, anchor, bounds);
    }

    private void DrawTooltipPanel(SpriteBatch sb, string title, Color titleColor,
        List<(string Text, Color Color)> lines, Rectangle anchor, Rectangle bounds)
    {
        const int pad = 8;
        // Espacements dérivés de la police active (titre CJK plus haut) ; identiques au latin (titre 20 / ligne 11).
        var lineH = _ctx.Font.GlyphHeight + 4;
        var titleBlock = _ctx.Font.LineHeight(2) + 6;
        var w = _ctx.Font.Measure(title, 2);
        foreach (var (text, _) in lines)
            w = Math.Max(w, _ctx.Font.Measure(text, 1));
        w += 2 * pad;
        var h = pad + titleBlock + lines.Count * lineH + pad;

        var x = Math.Clamp(anchor.Center.X - w / 2, bounds.X, Math.Max(bounds.X, bounds.Right - w));
        var y = anchor.Bottom + 6;
        if (y + h > bounds.Bottom)
            y = Math.Max(bounds.Y, anchor.Top - h - 6);   // pas la place dessous : bascule au-dessus
        y = Math.Clamp(y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - h));

        var box = new Rectangle(x, y, w, h);
        _ctx.Style.DrawPanel(sb, box);
        _ctx.Font.Draw(sb, title, new Vector2(box.X + pad, box.Y + pad), 2, titleColor);
        var ty = box.Y + pad + titleBlock;
        foreach (var (text, color) in lines)
        {
            _ctx.Font.Draw(sb, text, new Vector2(box.X + pad, ty), 1, color, preserveCase: true);
            ty += lineH;
        }
    }

    /// <summary>Libellés des mots-clés (traits) d'une classe, sans doublon (mêmes règles que la carte de jeu).</summary>
    private static List<string> UnitKeywordLabels(UnitClass c)
    {
        var seen = new HashSet<string>();
        var labels = new List<string>();
        void Add(string label)
        {
            if (label.Length > 0 && seen.Add(label))
                labels.Add(label);
        }
        foreach (var t in c.Traits)
            Add(UnitKeywords.For(t).Label);
        if (c.PiercesAllies && !c.Traits.Contains("Franchissement"))
            Add(UnitKeywords.PiercesAllies.Label);
        if (c.MinAttackRange > 1)
            Add(UnitKeywords.DeadZone.Label);
        return labels;
    }

    private static string StatLabel(EquipStat stat) => stat switch
    {
        EquipStat.Hp => Loc.T("stat.hp"),
        EquipStat.Damage => Loc.T("stat.power"),
        EquipStat.MoveRange => Loc.T("stat.movement"),
        EquipStat.AttackRange => Loc.T("stat.range"),
        _ => "",
    };

    private static string RarityKey(EquipmentRarity r) => r switch
    {
        EquipmentRarity.Rare => "equip.rarity.rare",
        EquipmentRarity.Legendary => "equip.rarity.legendary",
        _ => "equip.rarity.common",
    };

    private static Color RarityColor(EquipmentRarity r) => r switch
    {
        EquipmentRarity.Rare => Palette.Cyan1,
        EquipmentRarity.Legendary => Palette.Yellow2,
        _ => Palette.Grey,
    };

    private static string UnitName(UnitClass c) => Loc.TOr("unit." + c.Asset, c.Name).ToUpperInvariant();

    // ── Textures / primitives ─────────────────────────────────────────────────────

    /// <summary>PNG sous <c>Assets/&lt;rel&gt;.png</c> (mis en cache ; null si absent).</summary>
    private Texture2D? Tex(string rel)
    {
        if (_textures.TryGetValue(rel, out var tex))
            return tex;
        tex = Textures.LoadPngOrNull(_ctx.GraphicsDevice, Path.Combine(AppContext.BaseDirectory, $"Assets/{rel}.png"));
        _textures[rel] = tex;
        return tex;
    }

    /// <summary>Dessine un sprite à sa taille native centré dans <paramref name="area"/>, réduit d'un facteur ENTIER si trop grand.</summary>
    private static void DrawFit(SpriteBatch sb, Texture2D sprite, Rectangle area)
    {
        var src = sprite.Width;
        var box = Math.Min(area.Width, area.Height);
        var size = box >= src ? src : src / ((src + box - 1) / box);
        var x = area.X + (area.Width - size) / 2;
        var y = area.Y + (area.Height - size) / 2;
        sb.Draw(sprite, new Rectangle(x, y, size, size), Color.White);
    }

    private void Fill(SpriteBatch sb, Rectangle r, Color c) => sb.Draw(_ctx.Pixel, r, c);

    private void Border(SpriteBatch sb, Rectangle r, Color c, int t)
    {
        Fill(sb, new Rectangle(r.X, r.Y, r.Width, t), c);
        Fill(sb, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
        Fill(sb, new Rectangle(r.X, r.Y, t, r.Height), c);
        Fill(sb, new Rectangle(r.Right - t, r.Y, t, r.Height), c);
    }

    private static Rectangle Inflate(Rectangle r, int by) =>
        new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);
}
