using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Campaign;
using ChessArmy.Core.Command;
using ChessArmy.Engine;
using ChessArmy.Engine.Input;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.UI;
using ChessArmy.Engine.UI.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.UI;

/// <summary>
/// Écran modal de l'ARBRE DE COMMANDEMENT, ouvert pendant la phase de placement. Dessine l'arbre du
/// commandant courant (<see cref="Run.Tree"/>) : les branches en colonnes, les niveaux du bas vers le
/// haut, reliés en éventail (tout nœud d'un niveau ouvre tout nœud du niveau suivant de SA branche).
///
/// Chaque nœud n'affiche qu'une ICÔNE 32×32 (jamais de texte) et son coût ; le libellé et la description
/// vivent dans strings.csv (<c>tree.&lt;id&gt;</c> / <c>tree.&lt;id&gt;.desc</c>) et n'apparaissent qu'en
/// infobulle au survol / au focus. Toute la règle d'achat est dans <see cref="Run.CanUnlock"/> —
/// cette vue ne fait que présenter et transmettre le clic.
/// </summary>
public sealed class CommandTreeView
{
    // Géométrie de l'arbre (canevas virtuel, jamais mise à l'échelle fractionnaire).
    private const int NodeSize = 64;     // boîte d'un nœud
    private const int IconSize = 32;     // PNG natif, centré dans la boîte
    private const int NodeGapX = 28;     // entre les deux nœuds d'un même niveau
    private const int BranchGap = 76;    // entre deux colonnes
    // Écart d'un niveau au suivant (haut de boîte à haut de boîte). RESSERRÉ dynamiquement entre ces deux
    // bornes quand l'arbre déborde d'un petit canevas — cf. ComputeMetrics (cas 1080p → canevas 540 de haut).
    private const int MaxLevelGap = 100;
    private const int MinLevelGap = 72;
    // Marge écran conservée en haut/bas quand la modale doit s'étendre sur toute la hauteur pour tenir.
    private const int Margin = 8;
    private const int BranchWidth = 2 * NodeSize + NodeGapX;
    // L'en-tête loge : le titre, les points disponibles, puis le rappel des SOURCES de points
    // (une ligne par source : la mission, plus celle propre au commandant).
    private const int HeaderH = 100;
    // Le pied de page loge, DANS CET ORDRE : les libellés de branche, le bouton FERMER, puis l'indice.
    private const int FooterH = 92;
    private const int BranchLabelY = 8;    // sous le bas des nœuds de niveau 1
    private const int CloseButtonY = 28;   // sous les libellés de branche
    private const int CloseButtonH = 34;

    /// <summary>État d'achat d'un nœud — pilote la couleur du cadre et du coût.</summary>
    private enum NodeState
    {
        Owned,        // déjà acheté
        Buyable,      // prérequis satisfait ET assez de points
        TooExpensive, // prérequis satisfait, pas assez de points
        Locked,       // prérequis non satisfait
    }

    private readonly GameContext _ctx;
    private readonly Dictionary<string, Texture2D?> _icons = new();
    private readonly Dictionary<string, Rectangle> _rects = new();

    // Métriques verticales de l'image courante, recalculées en tête de Layout (donc avant tout dessin ou
    // test de survol) : écart entre niveaux (resserré si besoin), hauteur de l'arbre et rectangle du panneau.
    private int _levelGap;
    private int _treeH;
    private Rectangle _panel;

    private float _pulse;

    /// <summary>Manette : niveau focus (1..MaxLevel), ou <see cref="CloseFocusLevel"/> pour le bouton FERMER.</summary>
    private int _focusLevel = 1;
    private int _focusCol;         // manette : rang dans la rangée de ce niveau

    /// <summary>« Niveau » fictif, sous le niveau 1 : le bouton FERMER (atteignable en descendant).</summary>
    private const int CloseFocusLevel = 0;

    public CommandTreeView(GameContext context) => _ctx = context;

    public bool IsOpen { get; private set; }

    public void Open()
    {
        IsOpen = true;
        _focusLevel = 1;
        _focusCol = 0;
        _ctx.Sounds.Play("menu_open");
    }

    public void Close()
    {
        IsOpen = false;
        _ctx.Sounds.Play("menu_close");
    }

    /// <summary>Libère les icônes chargées (appelé par <c>Scene.Unload</c>).</summary>
    public void Unload()
    {
        foreach (var tex in _icons.Values)
            tex?.Dispose();
        _icons.Clear();
    }

    /// <summary>
    /// Traite une image de l'écran ouvert : survol/clic souris, navigation + A à la manette. Le bouton
    /// FERMER et la touche Échap (traitée par la scène) referment. Renvoie vrai si l'écran doit rester
    /// ouvert (faux = le joueur vient de le fermer par un bouton de cette vue).
    /// </summary>
    /// <summary>
    /// <paramref name="canClose"/> faux (tutoriel) : le bouton FERMER et B sont inertes tant que le joueur
    /// n'a pas acheté son nœud — le guide referme l'arbre lui-même.
    /// <paramref name="readOnly"/> vrai (CONSULTATION, ex. écran de sélection du commandant avant la partie) :
    /// on peut naviguer et lire les infobulles, mais aucun achat n'est tenté — pas même le retour sonore de
    /// refus. La <see cref="Run"/> passée peut alors n'être qu'une run d'aperçu servant à porter l'arbre.
    /// </summary>
    public bool Update(Run run, Rectangle area, float dt, bool canClose = true, bool readOnly = false)
    {
        _pulse += dt;
        Layout(run.Tree, area);

        if (_ctx.Input.UsingGamepad)
        {
            if (canClose && _ctx.Input.WasCancelPressed) { Close(); return false; }
            MoveFocus(run.Tree);
            if (_ctx.Input.WasConfirmPressed)
            {
                if (_focusLevel == CloseFocusLevel)
                {
                    if (!canClose) return true;
                    Close();
                    return false;
                }
                if (!readOnly && FocusedNode(run.Tree) is { } focused)
                    TryUnlock(run, focused);
            }
            return true;
        }

        var mouse = _ctx.Input.MousePosition;
        if (_ctx.Input.WasLeftClicked)
        {
            if (CloseButtonRect().Contains(mouse))
            {
                if (!canClose) return true;
                Close();
                return false;
            }
            if (!readOnly && NodeAt(run.Tree, mouse) is { } clicked)
                TryUnlock(run, clicked);
        }
        return true;
    }

    private void TryUnlock(Run run, CommandNode node)
    {
        if (run.Unlock(node))
            _ctx.Sounds.Play("recruit");
        else
            _ctx.Sounds.Play("menu_close");   // achat refusé (points / prérequis) : retour sonore sec
    }

    // ── Navigation manette ───────────────────────────────────────────────────────────────────────
    // Une « rangée » = tous les nœuds d'un niveau, toutes branches confondues, de gauche à droite.
    // Gauche/droite parcourt la rangée ; haut/bas change de niveau en gardant au mieux la colonne.

    private static IReadOnlyList<CommandNode> Row(CommandTree tree, int level) =>
        tree.Nodes.Where(n => n.Level == level).OrderBy(n => n.Branch).ToList();

    private CommandNode? FocusedNode(CommandTree tree)
    {
        if (_focusLevel == CloseFocusLevel)
            return null;   // le focus est sur le bouton FERMER, pas sur un nœud
        var row = Row(tree, _focusLevel);
        return row.Count == 0 ? null : row[System.Math.Clamp(_focusCol, 0, row.Count - 1)];
    }

    private void MoveFocus(CommandTree tree)
    {
        // Bouton FERMER : seul Haut en sort (retour sur la rangée du niveau 1).
        if (_focusLevel == CloseFocusLevel)
        {
            if (_ctx.Input.Nav(NavDir.Up))
                _focusLevel = 1;
            return;
        }

        var row = Row(tree, _focusLevel);
        if (row.Count == 0)
            return;

        if (_ctx.Input.Nav(NavDir.Right)) _focusCol = (_focusCol + 1) % row.Count;
        if (_ctx.Input.Nav(NavDir.Left)) _focusCol = (_focusCol - 1 + row.Count) % row.Count;
        if (_ctx.Input.Nav(NavDir.Up)) _focusLevel = System.Math.Min(CommandTree.MaxLevel, _focusLevel + 1);
        // Bas depuis le niveau 1 : on descend sur le bouton FERMER, juste sous l'arbre.
        if (_ctx.Input.Nav(NavDir.Down)) _focusLevel--;
        if (_focusLevel == CloseFocusLevel)
            return;

        _focusCol = System.Math.Clamp(_focusCol, 0, System.Math.Max(0, Row(tree, _focusLevel).Count - 1));
    }

    // ── Géométrie ────────────────────────────────────────────────────────────────────────────────

    private static int TreeWidth(CommandTree tree) =>
        tree.BranchCount * BranchWidth + System.Math.Max(0, tree.BranchCount - 1) * BranchGap;

    /// <summary>
    /// Bouton FERMER, ancré sous les libellés de branche. Lit les métriques de l'image (calculées par
    /// <see cref="ComputeMetrics"/> en tête de Layout) : à appeler après un Layout de la même image.
    /// </summary>
    private Rectangle CloseButtonRect() =>
        new(_panel.Center.X - 60, _panel.Y + HeaderH + _treeH + CloseButtonY, 120, CloseButtonH);

    /// <summary>
    /// Calcule pour cette image l'écart entre niveaux, la hauteur de l'arbre et le rectangle du panneau,
    /// puis les mémorise. Toujours appelée en tête de <see cref="Layout"/>, donc avant tout dessin/survol.
    ///
    /// Le panneau se centre par défaut dans <paramref name="area"/> — la zone LIBRE réservée par la scène
    /// (sous la frise, à gauche de l'inventaire), pour ne pas paraître décalé par rapport au plateau. Mais
    /// en 1080p le canevas virtuel ne fait que 540 px de haut : l'arbre confortable (4 niveaux) déborde de
    /// cette zone. On étend alors la modale — qui voile déjà tout l'écran — à la pleine hauteur et on
    /// RESSERRE l'écart entre niveaux juste assez pour tout afficher. Aux résolutions où tout tient déjà,
    /// rien ne change (écart = MaxLevelGap, panneau centré sous la frise).
    /// </summary>
    private void ComputeMetrics(CommandTree tree, Rectangle area)
    {
        const int fixedH = HeaderH + FooterH;
        var w = TreeWidth(tree) + 80;
        var comfyPanelH = fixedH + (CommandTree.MaxLevel - 1) * MaxLevelGap + NodeSize;

        // Trop haut pour la zone réservée : on récupère toute la hauteur écran (area.Bottom = bas du canevas).
        var region = comfyPanelH <= area.Height
            ? area
            : new Rectangle(area.X, Margin, area.Width, area.Bottom - 2 * Margin);

        _levelGap = System.Math.Clamp(
            (region.Height - fixedH - NodeSize) / (CommandTree.MaxLevel - 1), MinLevelGap, MaxLevelGap);
        _treeH = (CommandTree.MaxLevel - 1) * _levelGap + NodeSize;

        var panelH = fixedH + _treeH;
        var y = region.Y + System.Math.Max(0, (region.Height - panelH) / 2);
        _panel = new Rectangle(region.X + (region.Width - w) / 2, y, w, panelH);
    }

    /// <summary>(Re)calcule le panneau (<see cref="ComputeMetrics"/>) puis le rectangle de chaque nœud pour
    /// cette image. Clé = id du nœud.</summary>
    private void Layout(CommandTree tree, Rectangle area)
    {
        _rects.Clear();
        ComputeMetrics(tree, area);
        var left = _panel.X + (_panel.Width - TreeWidth(tree)) / 2;
        var top = _panel.Y + HeaderH;

        for (var branch = 0; branch < tree.BranchCount; branch++)
        {
            var bx = left + branch * (BranchWidth + BranchGap);
            for (var level = 1; level <= CommandTree.MaxLevel; level++)
            {
                var nodes = tree.At(branch, level);
                if (nodes.Count == 0)
                    continue;

                // Le niveau 4 (un seul nœud) se centre ; un niveau à deux nœuds occupe toute la colonne.
                var rowW = nodes.Count * NodeSize + (nodes.Count - 1) * NodeGapX;
                var x0 = bx + (BranchWidth - rowW) / 2;
                var y = top + (CommandTree.MaxLevel - level) * _levelGap;   // niveau 1 en bas
                for (var i = 0; i < nodes.Count; i++)
                    _rects[nodes[i].Id] = new Rectangle(x0 + i * (NodeSize + NodeGapX), y, NodeSize, NodeSize);
            }
        }
    }

    private CommandNode? NodeAt(CommandTree tree, Point p) =>
        tree.Nodes.FirstOrDefault(n => _rects.TryGetValue(n.Id, out var r) && r.Contains(p));

    // ── Rendu ────────────────────────────────────────────────────────────────────────────────────

    /// <summary><paramref name="area"/> = zone où centrer le panneau ; <paramref name="viewport"/> = voile plein canvas.</summary>
    public void Draw(SpriteBatch sb, Viewport viewport, Rectangle area, Run run)
    {
        var tree = run.Tree;
        Layout(tree, area);   // met à jour _panel / _treeH / _levelGap pour cette image

        sb.Begin(samplerState: SamplerState.PointClamp);
        Fill(sb, new Rectangle(0, 0, viewport.Width, viewport.Height), Palette.Black1 * 0.62f);
        _ctx.Style.DrawPanel(sb, _panel);
        // Fond du cadre de commandement ÉCLAIRCI : voile bleu-gris clair sur l'intérieur (inset de 3 pour
        // garder le relief du biseau). Le contenu (titre, nœuds, liens) est dessiné PAR-DESSUS, donc lisible.
        Fill(sb, Inflate(_panel, -3), Palette.Navy1 * 0.55f);

        DrawHeader(sb, _panel, run);
        DrawLinks(sb, tree, run);

        var hovered = _ctx.Input.UsingGamepad ? FocusedNode(tree) : NodeAt(tree, _ctx.Input.MousePosition);
        foreach (var node in tree.Nodes)
            DrawNode(sb, node, run, node == hovered);

        DrawBranchLabels(sb, tree, _panel);
        DrawFooter(sb, _panel);
        if (hovered != null)
            DrawTooltip(sb, hovered, viewport);
        sb.End();
    }

    private void DrawHeader(SpriteBatch sb, Rectangle panel, Run run)
    {
        _ctx.Font.DrawCentered(sb, Loc.T("tree.title"), new Rectangle(panel.X, panel.Y + 12, panel.Width, 24), 3, Palette.Yellow2);
        var points = Loc.T("tree.points", run.CommandPoints);
        DrawPointTotal(sb, _ctx.Font, points, new Rectangle(panel.X, panel.Y + 44, panel.Width, 12),
            run.CommandPoints > 0 ? Palette.Cyan1 : Palette.Blue1);

        // Comment on gagne des points : la mission (commun à tous), puis la source PROPRE au commandant
        // (fusion, ou coup reçu — cf. CommandeDef) — sa clé de texte vit sous l'id de son arbre.
        DrawIncomeLine(sb, panel, 64, Loc.T("tree.income_mission", Run.PointsPerMission));
        if (run.CommanderDef.FusionPoints > 0)
            DrawIncomeLine(sb, panel, 78, Loc.T($"tree.{run.Tree.Id}.income", run.CommanderDef.FusionPoints));
        else if (run.CommanderDef.OnHitPoints > 0)
            DrawIncomeLine(sb, panel, 78,
                Loc.T($"tree.{run.Tree.Id}.income", run.CommanderDef.OnHitPoints, run.CommanderDef.OnHitCap));
    }

    /// <summary>Ligne de gain : en MINUSCULES (preserveCase), pour la détacher des libellés capitalisés de l'UI.</summary>
    private void DrawIncomeLine(SpriteBatch sb, Rectangle panel, int dy, string text) =>
        _ctx.Font.DrawCentered(sb, text, new Rectangle(panel.X, panel.Y + dy, panel.Width, 10), 1, Palette.Blue1,
            preserveCase: true);

    /// <summary>Libellé de chaque branche sous sa colonne (clé <c>tree.&lt;treeId&gt;.branch&lt;N&gt;</c>).</summary>
    private void DrawBranchLabels(SpriteBatch sb, CommandTree tree, Rectangle panel)
    {
        var left = panel.X + (panel.Width - TreeWidth(tree)) / 2;
        var y = panel.Y + HeaderH + _treeH + BranchLabelY;
        for (var branch = 0; branch < tree.BranchCount; branch++)
        {
            var bx = left + branch * (BranchWidth + BranchGap);
            _ctx.Font.DrawCentered(sb, Loc.T($"tree.{tree.Id}.branch{branch}"),
                new Rectangle(bx, y, BranchWidth, 12), 1, Palette.Blue1);
        }
    }

    /// <summary>
    /// Liens en ÉVENTAIL : chaque nœud d'un niveau est relié à chaque nœud du niveau suivant de sa branche
    /// — c'est exactement la règle de prérequis. Le trait s'allume dès que le nœud du BAS est acquis
    /// (le prérequis est alors satisfait pour tout le niveau au-dessus).
    /// </summary>
    private void DrawLinks(SpriteBatch sb, CommandTree tree, Run run)
    {
        for (var level = 1; level < CommandTree.MaxLevel; level++)
            foreach (var lower in tree.Nodes.Where(n => n.Level == level))
                foreach (var upper in tree.Nodes.Where(n => n.Level == level + 1 && n.Branch == lower.Branch))
                    DrawLink(sb, _rects[lower.Id], _rects[upper.Id],
                        run.IsUnlocked(lower.Id) ? Palette.Yellow1 : Palette.Blue2);
    }

    /// <summary>Trait en équerre (montée verticale, palier horizontal, montée finale) entre deux nœuds.</summary>
    private void DrawLink(SpriteBatch sb, Rectangle from, Rectangle to, Color c)
    {
        const int t = 2;
        var midY = (from.Top + to.Bottom) / 2;
        Fill(sb, new Rectangle(from.Center.X - t / 2, midY, t, from.Top - midY), c);
        Fill(sb, new Rectangle(to.Center.X - t / 2, to.Bottom, t, midY - to.Bottom + t), c);
        var x0 = System.Math.Min(from.Center.X, to.Center.X);
        var x1 = System.Math.Max(from.Center.X, to.Center.X);
        Fill(sb, new Rectangle(x0, midY, x1 - x0 + t, t), c);
    }

    private NodeState StateOf(Run run, CommandNode node)
    {
        if (run.IsUnlocked(node.Id))
            return NodeState.Owned;
        if (!run.Tree.PrerequisiteMet(node, run.UnlockedNodes))
            return NodeState.Locked;
        return run.CommandPoints >= node.Cost ? NodeState.Buyable : NodeState.TooExpensive;
    }

    private void DrawNode(SpriteBatch sb, CommandNode node, Run run, bool hovered)
    {
        var rect = _rects[node.Id];
        var state = StateOf(run, node);

        _ctx.Style.DrawRecessed(sb, rect);
        // Fond d'icône ÉCLAIRCI : voile clair sur l'intérieur (inset de 2 pour garder le biseau enfoncé),
        // pour que les icônes 32×32 ressortent mieux que sur le tramage sombre du cadre.
        Fill(sb, Inflate(rect, -2), Palette.Blue1 * 0.6f);

        // Cadre : or (acquis), cyan pulsé (achetable), bleu (trop cher), sombre (verrouillé).
        var border = state switch
        {
            NodeState.Owned => Palette.Yellow1,
            NodeState.Buyable => Color.Lerp(Palette.Cyan1, Palette.Yellow2, Pulse()),
            NodeState.TooExpensive => Palette.Navy1,
            _ => Palette.Black5,
        };
        Border(sb, rect, border, state == NodeState.Owned || state == NodeState.Buyable ? 2 : 1);
        if (hovered)
            Border(sb, Inflate(rect, 3), Palette.Yellow2, 2);

        // Icône 32×32 NATIVE (jamais redimensionnée), assombrie tant que le nœud est hors de portée. Un nœud
        // ACQUIS n'affiche plus son coût : l'icône se recentre alors dans tout le cadre (DrawNodeIcon centre
        // dans la boîte qu'on lui donne) au lieu de rester poussée vers le haut.
        var owned = state == NodeState.Owned;
        var box = owned ? rect : new Rectangle(rect.X, rect.Y + 8, rect.Width, IconSize);
        DrawNodeIcon(sb, node, box, state == NodeState.Locked ? new Color(90, 90, 90) : Color.White);

        // Coût sous l'icône, suivi du jeton de points ; rien pour un nœud déjà acquis (le cadre or suffit).
        if (owned)
            return;
        var color = state switch
        {
            NodeState.Buyable => Palette.White,
            NodeState.TooExpensive => Palette.Purple5,
            _ => Palette.Grey,
        };
        // Le jeton s'assombrit avec le nœud verrouillé, pour ne pas attirer l'œil sur un coût hors de portée.
        var iconTint = state == NodeState.Locked ? new Color(90, 90, 90) : Color.White;
        DrawPointTotal(sb, _ctx.Font, node.Cost.ToString(),
            new Rectangle(rect.X, rect.Bottom - 16, rect.Width, 10), color, iconTint);
    }

    /// <summary>
    /// Icône d'un nœud, à 32×32 natif centré dans <paramref name="box"/> (repli coloré si le PNG manque).
    /// Public : les cartes de pion s'en servent pour montrer les améliorations actives sur l'unité.
    /// </summary>
    public void DrawNodeIcon(SpriteBatch sb, CommandNode node, Rectangle box, Color tint)
    {
        var icon = new Rectangle(box.Center.X - IconSize / 2, box.Center.Y - IconSize / 2, IconSize, IconSize);
        if (Icon(node.Icon) is { } tex)
            sb.Draw(tex, icon, tint);
        else
            DrawIconFallback(sb, node, icon, tint);
    }

    /// <summary>Côté de l'icône des POINTS de commandement (le jeton doré affiché à côté d'un total).</summary>
    public const int PointIconSize = 8;

    /// <summary>Nom du PNG 8×8 du jeton de points, dans le même dossier que les icônes de nœuds.</summary>
    private const string PointIconName = "point";

    /// <summary>
    /// Dessine le jeton de points de commandement (8×8 natif) dont le coin haut-gauche est à
    /// (<paramref name="x"/>, <paramref name="y"/>). Repli sur un losange plein si le PNG manque.
    /// Public : le panneau de placement s'en sert aussi, à côté de son compteur de points.
    /// </summary>
    public void DrawPointIcon(SpriteBatch sb, int x, int y, Color? tint = null)
    {
        var box = new Rectangle(x, y, PointIconSize, PointIconSize);
        if (Icon(PointIconName) is { } tex)
        {
            sb.Draw(tex, box, tint ?? Color.White);
            return;
        }
        for (var row = 0; row < PointIconSize; row++)
        {
            var half = System.Math.Min(row, PointIconSize - 1 - row) + 1;   // losange plein
            Fill(sb, new Rectangle(x + PointIconSize / 2 - half, y + row, half * 2, 1), Palette.Yellow1);
        }
    }

    /// <summary>
    /// Écrit <paramref name="text"/> suivi du jeton de points, l'ensemble CENTRÉ dans <paramref name="area"/>.
    /// Le jeton est aligné sur la hauteur du texte (7 px de glyphe contre 8 px d'icône) et peut être teinté
    /// (nœud verrouillé) pour rester cohérent avec le libellé.
    /// </summary>
    public void DrawPointTotal(SpriteBatch sb, ITextFont font, string text, Rectangle area, Color color,
        Color? iconTint = null)
    {
        const int gap = 4;
        var textW = font.Measure(text, 1);
        var x = area.X + (area.Width - (textW + gap + PointIconSize)) / 2;
        var y = area.Y + (area.Height - font.GlyphHeight) / 2;
        font.Draw(sb, text, new Vector2(x, y), 1, color);
        // Icône centrée sur la HAUTEUR du texte (police active) : sans ça elle flotte en haut du texte CJK 12px.
        DrawPointIcon(sb, x + textW + gap, y + (font.GlyphHeight - PointIconSize) / 2, iconTint);
    }

    /// <summary>Repli sans PNG : aplat coloré par branche + initiale, pour que l'arbre reste jouable.</summary>
    private void DrawIconFallback(SpriteBatch sb, CommandNode node, Rectangle icon, Color tint)
    {
        var branchColor = node.Branch switch
        {
            0 => Palette.Yellow1,
            1 => Palette.Cyan1,
            _ => Palette.Green1,
        };
        Fill(sb, icon, new Color(branchColor.ToVector3() * tint.ToVector3()));
        Border(sb, icon, Palette.Black1, 1);
        _ctx.Font.DrawCentered(sb, node.Id[..1].ToUpperInvariant(), icon, 2, Palette.Black1);
    }

    /// <summary>Infobulle : libellé + description localisés, posés au-dessus ou sous le nœud selon la place.</summary>
    private void DrawTooltip(SpriteBatch sb, CommandNode node, Viewport vp)
    {
        var name = Loc.T(node.NameKey);
        var lines = Wrap(Capitalize(Loc.T(node.DescKey)), 260);

        // Espacements dérivés de la police ACTIVE (et non figés en 7px) : le titre CJK fait 12px de glyphe
        // (24 à l'échelle 2) contre 7 (14) en latin, sinon il chevauche la description. Reproduit le latin :
        // titleH=14 gap=8 lineH=12 comme avant, et grandit en chinois.
        const int titleTop = 10, gap = 8, botPad = 12;
        var titleH = _ctx.Font.LineHeight(2);
        var lineH = _ctx.Font.GlyphHeight + 5;
        var descTop = titleTop + titleH + gap;

        var w = System.Math.Max(_ctx.Font.Measure(name, 2),
            lines.Count > 0 ? lines.Max(l => _ctx.Font.Measure(l, 1)) : 0) + 24;
        var h = descTop + lines.Count * lineH + botPad;

        var anchor = _rects[node.Id];
        var x = System.Math.Clamp(anchor.Center.X - w / 2, 8, vp.Width - w - 8);
        var y = anchor.Top - h - 10;
        if (y < 8)
            y = anchor.Bottom + 10;   // pas la place au-dessus : on bascule dessous

        var box = new Rectangle(x, y, w, h);
        _ctx.Style.DrawPanel(sb, box);
        _ctx.Font.Draw(sb, name, new Vector2(box.X + 12, box.Y + titleTop), 2, Palette.Yellow2);
        for (var i = 0; i < lines.Count; i++)
            _ctx.Font.Draw(sb, lines[i], new Vector2(box.X + 12, box.Y + descTop + i * lineH), 1, Palette.White,
                preserveCase: true);   // descriptions en minuscules (les libellés/nom restent en capitales)
    }

    private void DrawFooter(SpriteBatch sb, Rectangle panel)
    {
        var btn = CloseButtonRect();
        var focused = _ctx.Input.UsingGamepad && _focusLevel == CloseFocusLevel;
        var hover = !_ctx.Input.UsingGamepad && btn.Contains(_ctx.Input.MousePosition);
        var dy = _ctx.Style.DrawButton(sb, btn, UiStyle.StateOf(hover || focused, hover && _ctx.Input.IsLeftDown));
        var label = btn; label.Offset(0, dy);
        if (focused)
            Border(sb, Inflate(label, 2), Palette.Yellow2, 2);
        _ctx.Font.DrawCentered(sb, Loc.T("tree.close"), label, 1, hover || focused ? Palette.Yellow2 : Palette.White);

        // L'indice suit TOUJOURS le périphérique courant (manette vs clavier/souris).
        var hint = Loc.T(_ctx.Input.UsingGamepad ? "tree.hint_gp" : "tree.hint");
        _ctx.Font.DrawCentered(sb, hint, new Rectangle(panel.X, btn.Bottom + 6, panel.Width, 10), 1, Palette.Blue1);
    }

    /// <summary>Met la PREMIÈRE lettre en capitale (le reste des descriptions reste volontairement en minuscules).</summary>
    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Découpe <paramref name="text"/> en lignes tenant dans <paramref name="maxWidth"/> pixels (échelle 1).</summary>
    private List<string> Wrap(string text, int maxWidth)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (_ctx.Font.Measure(candidate, 1) > maxWidth && line.Length > 0)
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

    /// <summary>PNG 32×32 du nœud (cache ; null si absent → repli coloré).</summary>
    private Texture2D? Icon(string name)
    {
        if (_icons.TryGetValue(name, out var tex))
            return tex;
        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, $"Assets/CommandTree/{name}.png");
        tex = Textures.LoadPngOrNull(_ctx.GraphicsDevice, path);
        _icons[name] = tex;
        return tex;
    }

    /// <summary>Oscillation 0→1→0 pour le cadre des nœuds achetables.</summary>
    private float Pulse() => (float)(System.Math.Sin(_pulse * 4.0) * 0.5 + 0.5);

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
